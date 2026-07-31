/**
 * Shared UI helpers: authenticate the Jellyfin web client in the browser and
 * open the Jellyfin Helper config dashboard, which is rendered entirely by JS.
 *
 * The dashboard injects its shell into #statsResult and switches tabs via
 * `.tab-btn[data-tab="..."]` → `#tab-{name}.active`. We assert on stable IDs /
 * data-attributes / classes, NEVER on localized text.
 */
import { type Page, expect } from '@playwright/test';
import { loadAuth, PLUGIN_GUID } from '../setup/api-client.ts';

/**
 * Log into the Jellyfin web UI by injecting credentials the way the web client
 * stores them, then navigate to the plugin config page.
 *
 * Jellyfin's web client keeps the server + token in localStorage under
 * "jellyfin_credentials". We seed that so the SPA treats us as logged in.
 */
export async function openDashboard(page: Page): Promise<void> {
  const auth = loadAuth();
  const base = auth.baseUrl.replace(/\/$/, '');

  // The JF web client matches Servers[].Id against the live server's Id from
  // /System/Info/Public. A hardcoded Id makes it treat the stored token as
  // belonging to an unknown server, so it drops to the login page - fetch the
  // real Id and use it.
  const infoRes = await page.request.get(`${base}/System/Info/Public`);
  const serverId = ((await infoRes.json()) as { Id: string }).Id;

  // Field names + the sign-in gate are taken from jellyfin-apiclient
  // (connectToServer): State = AccessToken && enableAutoLogin !== false
  // ? "SignedIn" : "ServerSignIn". Addresses on reconnect are read from the
  // PascalCase ManualAddress/LocalAddress (lowercase manualAddress is ignored),
  // and enableAutoLogin must already be set before boot.
  //
  // This runs in the BROWSER, so it must be a pure function of its argument -
  // it cannot close over Node-scope variables (base/auth/serverId).
  const seedArg = { base, token: auth.token, userId: auth.userId, serverId };
  const seedCreds = (a: { base: string; token: string; userId: string; serverId: string }) => {
    localStorage.setItem(
      'jellyfin_credentials',
      JSON.stringify({
        Servers: [
          {
            Id: a.serverId,
            ManualAddress: a.base,
            LocalAddress: a.base,
            AccessToken: a.token,
            UserId: a.userId,
            DateLastAccessed: 1,
            LastConnectionMode: 1,
          },
        ],
      }),
    );
    localStorage.setItem('enableAutoLogin', 'true');
  };

  // Re-seed on EVERY document load (runs before any page script) so the value
  // is present the instant the credentialProvider constructor reads it - this
  // covers the initial load and the config-page load below without a race.
  await page.addInitScript(seedCreds, seedArg);

  // First load establishes the origin + runs the init script. Also seed
  // explicitly in case init-script timing differs across browsers.
  await page.goto(`${base}/web/index.html`);
  await page.evaluate(seedCreds, seedArg);

  // Navigate to the plugin config page as a FULL document load so
  // ServerConnections is constructed fresh against the (now seeded) store and
  // resolves to SignedIn instead of redirecting to /session/login. The page is
  // registered under the plugin's Name ("Jellyfin Helper", with a space) - the
  // un-encoded "JellyfinHelper" 404s and the shell never mounts.
  const configUrl = `${base}/web/index.html#!/configurationpage?name=${encodeURIComponent('Jellyfin Helper')}`;
  // A plain goto to a URL that differs only in the hash from the current one
  // is treated as a same-document nav and won't re-boot the SPA. We're coming
  // from #/ (home) here, but force a reload afterwards to be certain the app
  // re-initializes from the seeded credentials and lands on the config route.
  await page.goto(configUrl);
  await page.reload();

  // If the app briefly lands on the dashboard home after auto-login instead of
  // the deep-linked config route, nudge it back to the config page (a hash nav
  // is enough here - the SPA is already booted + signed in).
  const tabBar = page.locator('.tab-bar');
  try {
    await expect(tabBar).toBeVisible({ timeout: 20_000 });
  } catch {
    await page.evaluate((url) => {
      window.location.hash = new URL(url).hash;
    }, configUrl);
    // The shell is injected asynchronously; wait for the tab bar to exist.
    await expect(tabBar).toBeVisible({ timeout: 15_000 });
  }
}

/** Switch to a tab by its data-tab value and assert the panel becomes active. */
export async function switchTab(page: Page, tab: string): Promise<void> {
  await page.locator(`.tab-btn[data-tab="${tab}"]`).click();
  await expect(page.locator(`#tab-${tab}`)).toHaveClass(/active/, { timeout: 15_000 });
}

/** Collect uncaught console errors so a test can fail if the UI throws. */
export function trackConsoleErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') errors.push(msg.text());
  });
  page.on('pageerror', (err) => errors.push(err.message));
  return errors;
}

export { PLUGIN_GUID };
