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

// The "Seerr Discovery" tab in the header. Jellyfin 12.2 renders BOTH a legacy
// `.emby-tab-button` (hidden) and a modern MUI `<a>` (visible) with the same
// id/text in the DOM, so an unfiltered locator matches two elements and trips
// Playwright strict mode. Scope to the visible one - that is the tab a user
// actually sees and clicks regardless of which header layout is active.
function discoveryTab(page: Page) {
  return page
    .locator('header.MuiAppBar-root a, .headerTabs button')
    .filter({ hasText: 'Seerr Discovery' })
    .filter({ visible: true });
}

// Click the Custom Tab labelled "Seerr Discovery" (visible header variant).
async function clickDiscoveryTab(page: Page): Promise<void> {
  await discoveryTab(page).first().click();
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

// The marker must end up populated by discovery-sidebar.js with a terminal
// state: either the result grid or the explicit no-results message. A visible
// but empty container (e.g. a stuck spinner) is NOT acceptable - that is the
// blank-tab regression this spec guards against.
async function expectDiscoveryRendered(page: Page): Promise<void> {
  const grid = page.locator('.jellyfinhelper.discovery .jfh-discovery-grid');
  const empty = page.locator('.jellyfinhelper.discovery .jfh-discovery-msg');
  await expect(grid.first().or(empty.first())).toBeVisible({ timeout: 15_000 });
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
    await expect(discoveryTab(page)).toBeVisible({ timeout: 20_000 });

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
    await expect(discoveryTab(page)).toBeVisible({ timeout: 20_000 });
    await clickDiscoveryTab(page);
    await expectDiscoveryRendered(page);

    // Every discovery marker must live inside a Custom-Tabs-owned panel, and
    // exactly one such panel may exist. Ownership is asserted structurally, not
    // by count alone: Custom Tabs mounts its panel inside <main> (Modern) or next
    // to #favoritesTab (legacy) and stamps it with data-index. A panel our own
    // (removed) self-heal would have fabricated is detectable as a marker outside
    // a customTab_ node, a panel missing data-index, or a panel mounted nowhere
    // Custom Tabs would place it.
    const ownership = await page.evaluate(() => {
      const markers = Array.from(document.querySelectorAll('.jellyfinhelper.discovery'));
      const stray = markers.filter((m) => !m.closest('[id^="customTab_"]')).length;
      const panels = Array.from(document.querySelectorAll('[id^="customTab_"]'));
      const favoritesTab = document.getElementById('favoritesTab');
      // The legacy run of Custom-Tabs-owned panels starts directly after #favoritesTab.
      const legacyRun = new Set();
      let sibling = favoritesTab?.nextElementSibling ?? null;
      while (sibling && /^customTab_/.test(sibling.id || '')) {
        legacyRun.add(sibling);
        sibling = sibling.nextElementSibling;
      }
      const unowned = panels.filter((p) => {
        const hasIndex = p.hasAttribute('data-index');
        // Owned means inside <main> (Modern) or the legacy run above. A document-wide
        // #favoritesTab lookup would pass every panel, so position is checked, not presence.
        const placedByCustomTabs = !!p.closest('main') || legacyRun.has(p);
        return !hasIndex || !placedByCustomTabs;
      }).length;
      return { stray, panelCount: panels.length, unowned };
    });
    expect(ownership.stray, 'a discovery marker exists outside a Custom Tabs panel').toBe(0);
    expect(ownership.panelCount, 'exactly one custom-tab panel should exist').toBe(1);
    expect(ownership.unowned, 'the panel is not structurally owned by Custom Tabs').toBe(0);
  });
});
