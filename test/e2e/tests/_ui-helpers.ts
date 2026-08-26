/**
 * Shared UI helpers: authenticate the Jellyfin web client in the browser and
 * open the Jellyfin Helper config dashboard, which is rendered entirely by JS.
 *
 * The dashboard injects its shell into #statsResult and switches tabs via
 * `.tab-btn[data-tab="..."]` -> `#tab-{name}.active`. We assert on stable IDs /
 * data-attributes / classes, NEVER on localized text.
 */
import { type Page, expect } from '@playwright/test';
import { loadAuth } from '../setup/api-client.ts';

/** Admin credentials for the UI login fallback (same as global-setup). */
const ADMIN_USER = process.env.JELLYFIN_ADMIN_USER ?? 'e2eadmin';
const ADMIN_PASS = process.env.JELLYFIN_ADMIN_PASS ?? 'E2ePassw0rd!';

/**
 * Log into the Jellyfin web UI and open the Jellyfin Helper config dashboard.
 *
 * Strategy (resilient across JF web-client versions):
 *   1. Seed localStorage with jellyfin_credentials (fast path — avoids the
 *      login form entirely if the web client recognises the format).
 *   2. Navigate to the plugin config page and wait for `.tab-bar`.
 *   3. If `.tab-bar` doesn't appear within a short window, detect whether
 *      the browser is showing a login screen and fall back to a real UI login.
 *   4. After login, navigate to the config page and wait again.
 *
 * The fallback makes the suite survive localStorage format changes across
 * Jellyfin RC releases without requiring immediate test patches.
 */
export async function openDashboard(page: Page): Promise<void> {
  const auth = loadAuth();
  const base = auth.baseUrl.replace(/\/$/, '');

  // The JF web client matches Servers[].Id against the live server's Id from
  // /System/Info/Public. A hardcoded Id makes it treat the stored token as
  // belonging to an unknown server, so it drops to the login page - fetch the
  // real Id and use it.
  const infoRes = await page.request.get(`${base}/System/Info/Public`);
  const info = (await infoRes.json()) as { Id: string; ServerName?: string };
  const serverId = info.Id;

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
            Name: 'jfh-e2e',
            ManualAddress: a.base,
            LocalAddress: a.base,
            RemoteAddress: a.base,
            AccessToken: a.token,
            UserId: a.userId,
            DateLastAccessed: Date.now(),
            LastConnectionMode: 2,
            IsLocalServer: true,
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
  const tabBarVisible = await tabBar.isVisible().catch(() => false);

  if (!tabBarVisible) {
    // Give the SPA a moment to boot and settle before checking further.
    try {
      await expect(tabBar).toBeVisible({ timeout: 12_000 });
      return; // Fast path succeeded.
    } catch {
      // Fall through to diagnostics + fallback login.
    }

    // ─── DIAGNOSTIC: log current state so CI output reveals what went wrong ───
    const currentUrl = page.url();
    // eslint-disable-next-line no-console
    console.log(`[openDashboard] tab-bar not visible. URL: ${currentUrl}`);
    const lsState = await page.evaluate(() => {
      const o: Record<string, string> = {};
      for (let i = 0; i < localStorage.length; i++) {
        const k = localStorage.key(i)!;
        o[k] = localStorage.getItem(k)!.slice(0, 200);
      }
      return JSON.stringify(o);
    });
    // eslint-disable-next-line no-console
    console.log(`[openDashboard] localStorage snapshot: ${lsState.slice(0, 600)}`);
    await page.screenshot({ path: 'test-results/debug-openDashboard-fallback.png', fullPage: true });

    // ─── FALLBACK: detect login screen and authenticate via the UI ───
    const loginIndicator = page.locator(
      '#loginPage, [data-testid="login"], #manualLoginForm, input#txtManualName, ' +
        'button#btnManualLogin, .inputContainer input[type="text"]',
    );
    const onLoginScreen = await loginIndicator.first().isVisible({ timeout: 5_000 }).catch(() => false);

    if (onLoginScreen) {
      // eslint-disable-next-line no-console
      console.log('[openDashboard] Login screen detected — performing UI login as fallback');

      // Fill credentials. The selectors cover known Jellyfin web-client variants.
      const userInput = page.locator('input#txtManualName, #loginPage input[type="text"]').first();
      const passInput = page.locator('input#txtManualPassword, #loginPage input[type="password"]').first();
      const submitBtn = page.locator(
        'button#btnManualLogin, #loginPage .raised.submit, #loginPage button.raised',
      ).first();

      await userInput.fill(ADMIN_USER);
      await passInput.fill(ADMIN_PASS);
      await submitBtn.click();

      // Wait until we leave the login page (URL changes away from login/session).
      await page.waitForFunction(
        () => !window.location.hash.includes('login') && !window.location.pathname.includes('login'),
        { timeout: 20_000 },
      );
      // eslint-disable-next-line no-console
      console.log(`[openDashboard] Logged in. Now at: ${page.url()}`);

      // Navigate to the config page.
      await page.evaluate((url) => {
        window.location.hash = new URL(url).hash;
      }, configUrl);
    } else {
      // Not on login, not showing tab-bar — maybe we're on the dashboard home
      // and just need a hash nudge, or the route format changed.
      // eslint-disable-next-line no-console
      console.log('[openDashboard] Not on login screen — trying alternative route formats');

      // Try without the hashbang (#!/ -> #/)
      const altUrl = configUrl.replace('#!/', '#/');
      await page.evaluate((url) => {
        window.location.hash = new URL(url).hash;
      }, altUrl);
    }

    // Final wait for the plugin shell to mount.
    await expect(tabBar).toBeVisible({ timeout: 20_000 });
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

export { PLUGIN_GUID } from '../setup/api-client.ts';
