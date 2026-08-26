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

const ADMIN_USER = process.env.JELLYFIN_ADMIN_USER ?? 'e2eadmin';
const ADMIN_PASS = process.env.JELLYFIN_ADMIN_PASS ?? 'E2ePassw0rd!';

/**
 * Log into the Jellyfin web UI and open the Jellyfin Helper config dashboard.
 *
 * Primary path: seed localStorage with the admin token so the SPA treats us as
 * already signed in. Fallback: if the web client does not recognise the seeded
 * credentials (format changes across RC releases), detect the login form and
 * authenticate interactively.
 */
export async function openDashboard(page: Page): Promise<void> {
  const auth = loadAuth();
  const base = auth.baseUrl.replace(/\/$/, '');

  // Fetch the live server Id. The web client matches Servers[].Id against this
  // value; a mismatch drops the user to the login page.
  const infoRes = await page.request.get(`${base}/System/Info/Public`);
  const info = (await infoRes.json()) as { Id: string; ServerName?: string };
  const serverId = info.Id;

  // Runs in the BROWSER so it must be self-contained (no Node closures).
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

  // Re-seed on every document load so the value is present before the
  // credentialProvider constructor reads it.
  await page.addInitScript(seedCreds, seedArg);

  await page.goto(`${base}/web/index.html`);
  await page.evaluate(seedCreds, seedArg);

  // Full navigation to the plugin config page. The page is registered under
  // the plugin name with a space ("Jellyfin Helper").
  const configUrl = `${base}/web/index.html#!/configurationpage?name=${encodeURIComponent('Jellyfin Helper')}`;
  await page.goto(configUrl);
  await page.reload();

  const tabBar = page.locator('.tab-bar');

  // Fast path: localStorage seeding worked and the plugin shell mounted.
  const appeared = await tabBar.isVisible().catch(() => false);
  if (appeared) return;

  try {
    await expect(tabBar).toBeVisible({ timeout: 12_000 });
    return;
  } catch {
    // Seeded credentials were not accepted; fall through to login fallback.
  }

  // Fallback: authenticate through the login form if the SPA shows one.
  const loginVisible = await page
    .locator('#loginPage, #manualLoginForm, input#txtManualName')
    .first()
    .isVisible({ timeout: 5_000 })
    .catch(() => false);

  if (loginVisible) {
    const userInput = page.locator('input#txtManualName, #loginPage input[type="text"]').first();
    const passInput = page.locator('input#txtManualPassword, #loginPage input[type="password"]').first();
    const submitBtn = page.locator('button#btnManualLogin, #loginPage button.raised').first();

    await userInput.fill(ADMIN_USER);
    await passInput.fill(ADMIN_PASS);
    await submitBtn.click();

    await page.waitForFunction(
      () => !window.location.hash.includes('login') && !window.location.pathname.includes('login'),
      { timeout: 20_000 },
    );

    await page.evaluate((url) => {
      window.location.hash = new URL(url).hash;
    }, configUrl);
  } else {
    // Not on login and no tab-bar yet. Try hash without the bang as a route
    // format fallback (#!/ vs #/).
    const altUrl = configUrl.replace('#!/', '#/');
    await page.evaluate((url) => {
      window.location.hash = new URL(url).hash;
    }, altUrl);
  }

  await expect(tabBar).toBeVisible({ timeout: 20_000 });
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
