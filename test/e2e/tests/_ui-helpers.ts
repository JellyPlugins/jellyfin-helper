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
  // belonging to an unknown server, so it drops to the login page — fetch the
  // real Id and use it.
  const infoRes = await page.request.get(`${base}/System/Info/Public`);
  const serverId = ((await infoRes.json()) as { Id: string }).Id;

  // Visit root first so localStorage is scoped to the right origin.
  await page.goto(`${base}/web/index.html`);

  await page.evaluate(
    ({ base, token, userId, serverId }) => {
      const creds = {
        Servers: [
          {
            manualAddress: base,
            Id: serverId,
            AccessToken: token,
            UserId: userId,
            DateLastAccessed: Date.now(),
            LastConnectionMode: 1,
          },
        ],
      };
      localStorage.setItem('jellyfin_credentials', JSON.stringify(creds));
      localStorage.setItem('enableAutoLogin', 'true');
    },
    { base, token: auth.token, userId: auth.userId, serverId },
  );

  // Navigate straight to the plugin config page. The page is registered under
  // the plugin's Name ("Jellyfin Helper", with a space) — the un-encoded
  // "JellyfinHelper" 404s and the shell never mounts.
  await page.goto(`${base}/web/index.html#!/configurationpage?name=${encodeURIComponent('Jellyfin Helper')}`);

  // The shell is injected asynchronously; wait for the tab bar to exist.
  await expect(page.locator('.tab-bar')).toBeVisible({ timeout: 30_000 });
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
