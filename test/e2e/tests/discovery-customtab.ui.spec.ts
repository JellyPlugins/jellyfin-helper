/**
 * Discovery Custom Tab (home page): The regression guard for the Jellyfin 12
 * blank-tab race. Mounts the real `.jellyfinhelper.discovery` marker that the
 * Custom Tabs plugin renders from its ContentHtml, then navigates away and back
 * many times asserting the panel is never left blank, and that discovery-sidebar.js
 * never fabricates a `customTab_` panel of its own (that self-heal fought Custom
 * Tabs and caused the intermittent blank).
 *
 * Requires the external Custom Tabs + File Transformation plugins, staged by
 * run.sh. When they are absent (JFH_E2E_EXTERNAL_PLUGINS!=1) the whole file skips.
 */
import { test, expect, type Page } from '@playwright/test';
import { loadAuth } from '../setup/api-client.ts';

const EXTERNAL_PLUGINS = process.env.JFH_E2E_EXTERNAL_PLUGINS === '1';

interface Cred {
  base: string;
  token: string;
  userId: string;
  serverId: string;
}

// Seed Jellyfin's web credentials before any page script runs so the SPA
// auto-logs-in and lands on the real home page (not the plugin dashboard).
function seedCredentials(c: Cred): void {
  localStorage.setItem(
    'jellyfin_credentials',
    JSON.stringify({
      Servers: [
        {
          Id: c.serverId,
          Name: 'jfh-e2e',
          ManualAddress: c.base,
          LocalAddress: c.base,
          RemoteAddress: c.base,
          AccessToken: c.token,
          UserId: c.userId,
          DateLastAccessed: Date.now(),
          LastConnectionMode: 2,
          IsLocalServer: true,
        },
      ],
    }),
  );
  localStorage.setItem('enableAutoLogin', 'true');
}

async function openHome(page: Page): Promise<string> {
  const auth = loadAuth();
  const base = auth.baseUrl.replace(/\/$/, '');
  const infoRes = await page.request.get(`${base}/System/Info/Public`);
  const { Id: serverId } = (await infoRes.json()) as { Id: string };

  // Use the normal user when provisioned (matches the real user-facing flow);
  // fall back to the admin token otherwise.
  const token = auth.normalUser?.token ?? auth.token;
  const userId = auth.normalUser?.userId ?? auth.userId;

  await page.addInitScript(seedCredentials, { base, token, userId, serverId });
  await page.goto(`${base}/web/index.html#/home`);
  return base;
}

// Click the Custom Tab labelled "Seerr Discovery" across the Modern MUI header
// and the legacy tab bar.
async function clickDiscoveryTab(page: Page): Promise<void> {
  const modern = page.locator('header.MuiAppBar-root a', { hasText: 'Seerr Discovery' });
  const legacy = page.locator('.headerTabs button', { hasText: 'Seerr Discovery' });
  if (await modern.count()) {
    await modern.first().click();
  } else {
    await legacy.first().click();
  }
}

async function clickHomeTab(page: Page): Promise<void> {
  const modernHome = page.locator('header.MuiAppBar-root a[href$="?tab=0"], header.MuiAppBar-root a[href="#/home"]');
  const legacyHome = page.locator('.headerTabs button').first();
  if (await modernHome.count()) {
    await modernHome.first().click();
  } else {
    await legacyHome.click();
  }
}

// The marker must end up populated by discovery-sidebar.js: either the grid, or
// the explicit "no results" message. A blank marker (no .jfh-discovery-container)
// is the failure this spec guards against.
async function expectDiscoveryRendered(page: Page): Promise<void> {
  const content = page.locator('.jellyfinhelper.discovery .jfh-discovery-container');
  await expect(content.first()).toBeVisible({ timeout: 15_000 });
}

// Observable "we left the tab" condition: Custom Tabs hides non-active panels,
// so the discovery content is no longer visible once Home is active. Used to
// synchronize navigation instead of a fixed wait.
async function expectDiscoveryHidden(page: Page): Promise<void> {
  const content = page.locator('.jellyfinhelper.discovery .jfh-discovery-container');
  await expect(content.first()).toBeHidden({ timeout: 15_000 });
}

test.describe('Discovery custom tab (home page)', () => {
  test.skip(!EXTERNAL_PLUGINS, 'Custom Tabs / File Transformation not staged (JFH_E2E_EXTERNAL_PLUGINS!=1)');

  test('renders on first open and never goes blank across repeated navigation', async ({ page }) => {
    await openHome(page);

    // The Custom Tabs plugin needs a moment to inject the tab into the header.
    await expect(
      page.locator('header.MuiAppBar-root a', { hasText: 'Seerr Discovery' })
        .or(page.locator('.headerTabs button', { hasText: 'Seerr Discovery' })),
    ).toBeVisible({ timeout: 20_000 });

    await clickDiscoveryTab(page);
    await expectDiscoveryRendered(page);

    // Hammer the navigation: away to Home, back to Discovery. The panel must be
    // filled every single time, with no blank frame. Each step synchronizes on
    // an observable state change rather than a fixed wait.
    for (let i = 0; i < 20; i++) {
      await clickHomeTab(page);
      await expectDiscoveryHidden(page);
      await clickDiscoveryTab(page);
      await expectDiscoveryRendered(page);
    }

    // Browser back/forward is a distinct navigation path (popstate/hashchange).
    await page.goBack();
    await expectDiscoveryHidden(page);
    await page.goForward();
    await expectDiscoveryRendered(page);
  });

  test('discovery-sidebar.js never fabricates its own customTab_ panel', async ({ page }) => {
    await openHome(page);
    await expect(
      page.locator('header.MuiAppBar-root a', { hasText: 'Seerr Discovery' })
        .or(page.locator('.headerTabs button', { hasText: 'Seerr Discovery' })),
    ).toBeVisible({ timeout: 20_000 });
    await clickDiscoveryTab(page);
    await expectDiscoveryRendered(page);

    // Every discovery marker must live inside a Custom-Tabs-owned panel, and the
    // panel count must match the configured custom-tab count (exactly one). If
    // our script fabricated a competing customTab_ node, there would be a marker
    // outside a [data-index] panel or a duplicate panel.
    const strayMarkers = await page.evaluate(() => {
      const markers = Array.from(document.querySelectorAll('.jellyfinhelper.discovery'));
      return markers.filter((m) => !m.closest('[id^="customTab_"]')).length;
    });
    expect(strayMarkers, 'a discovery marker exists outside a Custom Tabs panel').toBe(0);

    const panelCount = await page.locator('[id^="customTab_"]').count();
    expect(panelCount, 'exactly one custom-tab panel should exist').toBe(1);
  });
});
