import { type Page, expect } from '@playwright/test';
import { loadAuth } from '../setup/api-client.ts';

interface SeedArgs {
  base: string;
  token: string;
  userId: string;
  serverId: string;
}

/**
 * Seeds auth state into localStorage before page scripts execute.
 * Using LastConnectionMode = 2 (Manual) ensures the web client connects
 * directly via ManualAddress.
 */
function seedCredentials(a: SeedArgs): void {
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
}

/**
 * Pre-authenticates the session and opens the plugin configuration page.
 */
export async function openDashboard(page: Page): Promise<void> {
  const auth = loadAuth();
  const base = auth.baseUrl.replace(/\/$/, '');

  const infoRes = await page.request.get(`${base}/System/Info/Public`);
  const { Id: serverId } = (await infoRes.json()) as { Id: string };

  const seedArg: SeedArgs = {
    base,
    token: auth.token,
    userId: auth.userId,
    serverId,
  };

  await page.addInitScript(seedCredentials, seedArg);

  const configUrl = `${base}/web/index.html#!/configurationpage?name=${encodeURIComponent('Jellyfin Helper')}`;
  await page.goto(configUrl);

  const tabBar = page.locator('.tab-bar');

  try {
    await expect(tabBar).toBeVisible({ timeout: 12_000 });
  } catch {
    await page.evaluate((url) => {
      window.location.hash = new URL(url).hash;
    }, configUrl);

    await expect(tabBar).toBeVisible({ timeout: 15_000 });
  }
}

/**
 * Switches to the target tab and waits for its panel to become active.
 */
export async function switchTab(page: Page, tab: string): Promise<void> {
  await page.locator(`.tab-btn[data-tab="${tab}"]`).click();
  await expect(page.locator(`#tab-${tab}`)).toHaveClass(/active/, { timeout: 15_000 });
}

/**
 * Captures uncaught browser errors and page exceptions.
 */
export function trackConsoleErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') {
      errors.push(msg.text());
    }
  });
  page.on('pageerror', (err) => errors.push(err.message));
  return errors;
}

export { PLUGIN_GUID } from '../setup/api-client.ts';