/**
 * Discovery native tab (home page): the regression guard for the tab the plugin
 * now renders itself. The script is injected into index.html at request time and
 * the tab is registered against Jellyfin's own tab strip, so neither Custom Tabs
 * nor File Transformation is involved. The specs assert that injection, the tab
 * registration, the deep link, and the reload path, and that a user without
 * access never gets an entry point at all.
 */
import { test, expect, type Page } from '@playwright/test';
import { apiContext, normalUserContext, loadAuth, p } from '../setup/api-client.ts';
import { ensureDiscoveryConfigured } from '../setup/discovery-config.ts';

// Only the Trakt layout spec needs the official plugin: the Trakt probe reports
// enabled only when that plugin is present.
const OFFICIAL_TRAKT = process.env.JFH_E2E_EXTERNAL_PLUGINS === '1';

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

async function openHomeAs(page: Page, token: string, userId: string): Promise<string> {
  const auth = loadAuth();
  const base = auth.baseUrl.replace(/\/$/, '');
  const infoRes = await page.request.get(`${base}/System/Info/Public`);
  const { Id: serverId } = (await infoRes.json()) as { Id: string };

  await page.addInitScript(seedCredentials, { base, token, userId, serverId });
  await page.goto(`${base}/web/index.html#/home`);
  return base;
}

// Use the normal user when provisioned (matches the real user-facing flow);
// fall back to the admin token otherwise.
async function openHome(page: Page): Promise<string> {
  const auth = loadAuth();
  return openHomeAs(page, auth.normalUser?.token ?? auth.token, auth.normalUser?.userId ?? auth.userId);
}

// The stable layout renders the tab strip button; the Jellyfin 12 experimental
// layout hides the strip and gets the header-tray link instead. Exactly one of
// the two is visible, so a single locator covers both layouts.
function discoveryEntry(page: Page) {
  return page.locator('#jfh-tab-btn-discovery, #jfh-tab-link-discovery').filter({ visible: true });
}

async function openDiscovery(page: Page): Promise<void> {
  await expect(discoveryEntry(page), 'the Discovery tab is registered').toHaveCount(1, { timeout: 20_000 });
  await discoveryEntry(page).click();
  await expect(page.locator('#jfh-tab-panel-discovery')).toHaveClass(/is-active/, { timeout: 15_000 });
}

// The panel must reach a terminal state: the result grid or the explicit
// no-results message. A visible but empty panel (a stuck spinner) is the blank
// tab this spec guards against.
async function expectDiscoveryRendered(page: Page): Promise<void> {
  const panel = page.locator('#jfh-tab-panel-discovery');
  await expect(panel).toBeVisible({ timeout: 15_000 });
  await expect(panel.locator('.jfh-discovery-grid, .jfh-discovery-msg').first()).toBeVisible({ timeout: 15_000 });
}

async function expectDiscoveryHidden(page: Page): Promise<void> {
  await expect(page.locator('#jfh-tab-panel-discovery')).toBeHidden({ timeout: 15_000 });
}

async function clickHome(page: Page): Promise<void> {
  // In the JF12 modern header the Home tab (index 0) is the FIRST anchor with a
  // bare href="#/" (styled as a tab, labeled with the server name, no stable
  // text) - NOT #/home or #/home?tab=0, which do not exist. The legacy tab strip
  // button is the fallback for the stable layout.
  const modernHome = page.locator('header.MuiAppBar-root a[href="#/"]').filter({ visible: true });
  if (await modernHome.count()) {
    await modernHome.first().click();
    return;
  }
  const stripHome = page.locator('.emby-tabs-slider .emby-tab-button[data-index="0"]').filter({ visible: true });
  if (await stripHome.count()) {
    await stripHome.first().click();
    return;
  }
  await page.locator('.headerTabs button').filter({ visible: true }).first().click();
}

test.describe('Discovery native tab (home page)', () => {
  // The ui-setup project enables discovery access once, but subsequent configuration saves (via API or Settings-tab) overwrite this with `DiscoveryUserAccessEnabled=false`,
  // causing a 403 during the home page mount and render assertion timeouts. Re-assert it here immediately before the spec runs, so a setup race fails loudly.
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

  test('index.html is served with exactly one injected script tag', async ({ page }) => {
    const base = await openHome(page);
    const html = await (await page.request.get(`${base}/web/index.html`)).text();
    const tags = html.match(/<script[^>]*plugin="Jellyfin Helper"[^>]*><\/script>/g) ?? [];
    expect(tags, 'the plugin injects its script exactly once').toHaveLength(1);
    expect(tags[0], 'the injected tag loads the combined Discovery script').toContain('Discovery/My/script');
  });

  test('renders on first open and never goes blank across repeated navigation', async ({ page }) => {
    await openHome(page);
    await openDiscovery(page);
    await expectDiscoveryRendered(page);

    // Hammer the navigation: away to Home, back to Discovery. The panel must be
    // filled every single time, with no blank frame. Each step synchronizes on
    // an observable state change rather than a fixed wait.
    for (let i = 0; i < 20; i++) {
      await clickHome(page);
      await expectDiscoveryHidden(page);
      await openDiscovery(page);
      await expectDiscoveryRendered(page);
    }

    // Browser back/forward is a distinct navigation path (popstate/hashchange).
    await page.goBack();
    await expectDiscoveryHidden(page);
    await page.goForward();
    await expectDiscoveryRendered(page);
  });

  // F5 guard: with the Discovery tab active, a hard reload restores the
  // ?tab=N&jfhTab=discovery deep link and the tab must fill itself without any
  // further navigation.
  test('renders after a hard reload with the Discovery tab active (no manual nav)', async ({ page }) => {
    await openHome(page);
    await openDiscovery(page);
    await expectDiscoveryRendered(page);

    expect(page.url(), 'clicking Discovery should deep-link the tab into the URL').toContain('jfhTab=discovery');

    await page.reload();

    // No openDiscovery here: the panel must fill itself from the restored deep link.
    await expectDiscoveryRendered(page);
  });

  test('owns exactly one panel and needs no third party tab plugin', async ({ page }) => {
    await openHome(page);
    await openDiscovery(page);
    await expectDiscoveryRendered(page);

    const ownership = await page.evaluate(() => {
      const panel = document.getElementById('jfh-tab-panel-discovery');
      return {
        // Custom Tabs stamped every panel it created; none may exist now.
        customTabPanels: document.querySelectorAll('[id^="customTab_"]').length,
        panels: document.querySelectorAll('#jfh-tab-panel-discovery').length,
        panelClasses: panel?.className ?? '',
        buttons: document.querySelectorAll('#jfh-tab-btn-discovery').length,
        links: document.querySelectorAll('#jfh-tab-link-discovery').length,
      };
    });
    expect(ownership.customTabPanels, 'no Custom Tabs panel is involved').toBe(0);
    expect(ownership.panels, 'exactly one Discovery panel exists').toBe(1);
    expect(ownership.panelClasses, 'the panel is a native Jellyfin tab panel').toContain('tabContent');
    expect(ownership.panelClasses).toContain('pageTabContent');
    expect(ownership.buttons + ownership.links, 'exactly one entry point exists').toBe(1);
  });
});

/**
 * Negative counterpart: with the user-access toggle OFF, the tab must not be
 * registered at all. discovery-sidebar.js gets a 403 from /Discovery/My and bails
 * before registering, so neither a tab button, a header link, nor a panel exists.
 * Runs in its own describe so its config toggling is bracketed and restored.
 */
test.describe('Discovery native tab - access disabled', () => {
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

  test('no tab, link or panel is registered while access is off', async ({ page }) => {
    // Arm the response wait before navigating so the 403 that ends the bootstrap
    // is observed, not raced.
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
    await openHome(page);
    await bailResponse;

    await expect(page.locator('#jfh-tab-btn-discovery, #jfh-tab-link-discovery')).toHaveCount(0);
    await expect(page.locator('#jfh-tab-panel-discovery')).toHaveCount(0);
  });
});

/**
 * Unlinked-user tab layout: with Trakt sourcing on and the official Trakt plugin present, a user WITHOUT a Trakt
 * link must not see a "Trakt for you" tab (it would only render a not-linked message), but must still see the
 * local "For you" grid and the login-free "Trakt trending" tab. The e2e admin is never seeded into the official
 * plugin's TraktUsers (global-setup seeds only the normal user), so the admin is a real unlinked user here.
 */
test.describe('Discovery native tab - unlinked Trakt user', () => {
  test.skip(!OFFICIAL_TRAKT, 'official Trakt plugin not staged (JFH_E2E_EXTERNAL_PLUGINS!=1)');

  test.beforeAll(async () => {
    const auth = loadAuth();
    const admin = await apiContext(auth);
    try {
      // Trakt sourcing on + Seerr configured so the tab bar renders and the probe is not access-gated.
      await ensureDiscoveryConfigured(admin);
      await expect
        .poll(async () => (await admin.get(p('Discovery/My/Trakt'))).status(), { timeout: 15_000 })
        .not.toBe(403);
    } finally {
      await admin.dispose();
    }
  });

  test('unlinked user sees For you + Trakt trending but not Trakt for you', async ({ page }) => {
    const auth = loadAuth();
    await openHomeAs(page, auth.token, auth.userId);
    await openDiscovery(page);

    // The tab bar renders once the Trakt availability probe settles (enabled by presence, not by link state).
    const tabs = page.locator('#jfh-tab-panel-discovery .jfh-discovery-tabs');
    await expect(tabs).toBeVisible({ timeout: 20_000 });

    // Match the tab's full trimmed label exactly: "For you" is a substring of "Trakt for you", so a loose
    // hasText would conflate the local and personal tabs and hide the very regression under test.
    const tabButton = (label: string) =>
      tabs.locator('.jfh-discovery-tab', { hasText: new RegExp(`^\\s*${label}\\s*$`) });

    // For you (local) and Trakt trending (no login) are present; Trakt for you (personal) is absent because the
    // admin is unlinked. The hidden personal button is proven by its total absence, not merely being invisible.
    await expect(tabButton('For you')).toBeVisible();
    await expect(tabButton('Trakt trending')).toBeVisible();
    await expect(tabButton('Trakt for you')).toHaveCount(0);

    // Exactly two tabs, so the layout is the clean 2-segment bar, never a gap where the personal tab would sit.
    await expect(tabs.locator('.jfh-discovery-tab')).toHaveCount(2);
  });
});
