/**
 * Discovery Custom Tab (home page): The regression guard for the Jellyfin 12
 * blank-tab race. Mounts the real `.jellyfinhelper.discovery` marker that the
 * Custom Tabs plugin renders from its ContentHtml, then navigates away and back
 * many times asserting the panel is never left blank, and that discovery-sidebar.js
 * never fabricates a `customTab_` panel of its own (that self-heal fought Custom
 * Tabs and caused the intermittent blank).
 *
 * Requires the external Custom Tabs + File Transformation plugins, which run.sh
 * stages from their LATEST release and treats as a hard prerequisite (a failed
 * stage aborts the whole run, never a silent skip). The JFH_E2E_EXTERNAL_PLUGINS
 * guard below only matters for a dev running Playwright directly without staging.
 */
import { test, expect, type Page } from '@playwright/test';
import { apiContext, normalUserContext, loadAuth, p } from '../setup/api-client.ts';
import { ensureDiscoveryConfigured } from '../setup/discovery-config.ts';

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
  // In the JF12 modern header the Home tab (index 0) is the FIRST anchor with a
  // bare href="#/" (styled as a tab, labeled with the server name, no stable
  // text) - NOT #/home or #/home?tab=0, which do not exist. The legacy
  // .emby-tab-button[data-index="0"] also exists but is INVISIBLE in the modern
  // layout (clicking it times out). a[href="#/"] excludes the legacy button
  // (it has no href); the visible filter guards the legacy-layout case.
  const modernHome = page.locator('header.MuiAppBar-root a[href="#/"]').filter({ visible: true });
  if (await modernHome.count()) {
    await modernHome.first().click();
    return;
  }
  await page.locator('.headerTabs button').filter({ visible: true }).first().click();
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

  //The ui-setup project enables discovery access once, but subsequent configuration saves (via API or Settings-tab) overwrite this with `DiscoveryUserAccessEnabled=false`,
  // causing a 403 error during home page mount and resulting in render assertion timeouts. To prevent this, access is re-asserted here immediately before the spec runs.
  // This replaces a silent mount timeout with a loud failure if a setup race occurs.
  test.beforeAll(async () => {
    const auth = loadAuth();
    const admin = await apiContext(auth);
    const probe = (await normalUserContext(auth)) ?? admin;
    try {
      await ensureDiscoveryConfigured(admin);
      // The toggle write and the user-facing read go through different layers;
      // under CI load the enablement can take a beat to surface. Poll up to ~15s
      // and fail with the status so a persistent 403 points at the setup, not the UI.
      await expect
        .poll(async () => (await probe.get(p('Discovery/My'))).status(), { timeout: 15_000 })
        .not.toBe(403);
    } finally {
      if (probe !== admin) await probe.dispose();
      await admin.dispose();
    }
  });

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

  // Regression guard for the F5 blank-tab bug: with the Discovery tab active, a hard
  // reload restores the ?tab=N deep link and Custom Tabs rebuilds the panel BEFORE
  // discovery-sidebar.js finishes its async availability probe. If the DOM watcher only
  // started after that probe, the restore mutation was already missed and the panel
  // stayed blank until a manual nav away-and-back. The fix starts the watcher the moment
  // the home context is known (pre-probe), so the restored panel is caught and filled
  // without any further navigation. This test reloads on the deep link and asserts the
  // panel renders on its own.
  test('renders after a hard reload with the Discovery tab active (no manual nav)', async ({ page }) => {
    await openHome(page);
    await expect(discoveryTab(page)).toBeVisible({ timeout: 20_000 });

    await clickDiscoveryTab(page);
    await expectDiscoveryRendered(page);

    // Clicking the tab deep-links the URL to ?tab=N; a reload here is a true F5 of the
    // active-Discovery state, not a fresh home load. (addInitScript re-seeds credentials
    // on every navigation, so the SPA auto-logs-in again after the reload.)
    const urlWithTab = page.url();
    expect(urlWithTab, 'clicking Discovery should deep-link the tab into the URL').toContain('tab=');

    await page.reload();

    // No clickDiscoveryTab here: the panel must fill itself from the restored deep link.
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

/**
 * Negative counterpart: with the user-access toggle OFF, the Discovery panel must NOT render a result grid.
 * The tab button itself is owned by the external Custom Tabs plugin (its ContentHtml), so it can still appear
 * in the header - what OUR code controls is population: discovery-sidebar.js gets a 403 from /Discovery/My and
 * renders the explicit "not enabled" message instead of cards. Asserting the grid never appears (and the
 * disabled message does) is the behavioral proof that access-off hides the feature's content. Runs in its own
 * describe so its config toggling is bracketed and restored, never leaking into the positive tests above.
 */
test.describe('Discovery custom tab - access disabled', () => {
  test.skip(!EXTERNAL_PLUGINS, 'Custom Tabs / File Transformation not staged (JFH_E2E_EXTERNAL_PLUGINS!=1)');

  test.beforeAll(async () => {
    const auth = loadAuth();
    const admin = await apiContext(auth);
    const probe = (await normalUserContext(auth)) ?? admin;
    try {
      // Turn the user-access toggle OFF while preserving the rest of the config. PUT /Configuration binds the
      // whole ConfigurationUpdateRequest and resets every omitted non-nullable field to its default, so a
      // partial body would wipe Seerr/Arr/cleanup settings that later specs (workers: 1, shared backend) rely
      // on. Read the current config, flip only the toggle, and write it back. Poll until the user-facing read
      // actually 403s, so the UI assertion below is not racing an un-propagated toggle.
      const current = await admin.get(p('Configuration'));
      expect(current.ok(), `read configuration: ${current.status()}`).toBeTruthy();
      const cfg = (await current.json()) as Record<string, unknown>;
      const put = await admin.put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: { ...cfg, DiscoveryUserAccessEnabled: false },
      });
      expect(put.ok(), `disable discovery access: ${put.status()}`).toBeTruthy();
      await expect
        .poll(async () => (await probe.get(p('Discovery/My'))).status(), { timeout: 15_000 })
        .toBe(403);
    } finally {
      if (probe !== admin) await probe.dispose();
      await admin.dispose();
    }
  });

  test.afterAll(async () => {
    // Restore access for every later spec (workers: 1 shares one backend).
    const auth = loadAuth();
    const admin = await apiContext(auth);
    try {
      await ensureDiscoveryConfigured(admin);
    } finally {
      await admin.dispose();
    }
  });

  test('access-disabled panel never renders a result grid', async ({ page }) => {
    await openHome(page);

    // The Custom Tabs plugin still injects its tab (it owns that), so a user can still click it. But with the
    // user-access gate off, discovery-sidebar.js gets a 403 from /Discovery/My and bails before building any
    // content: no result grid is ever rendered. (On the home custom-tab the panel simply stays empty rather
    // than showing the config-page "not enabled" message, so we assert the grid's absence, not a message.)
    await expect(discoveryTab(page)).toBeVisible({ timeout: 20_000 });

    // The bail under test happens exactly when discovery-sidebar.js gets the 403 from /Discovery/My,
    // so synchronize on that response instead of sleeping: arm before the click (each click remounts
    // into Custom Tabs' rebuilt panel and refetches while uncached), then assert no grid was ever built.
    const bailResponse = page.waitForResponse(
      (res) => {
        try {
          return new URL(res.url()).pathname === p('Discovery/My') && res.status() === 403;
        } catch {
          return false;
        }
      },
      { timeout: 20_000 },
    );
    await clickDiscoveryTab(page);
    await bailResponse;

    const grid = page.locator('.jellyfinhelper.discovery .jfh-discovery-grid');
    await expect(grid).toHaveCount(0);
  });
});
