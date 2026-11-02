/**
 * Discovery native tab (home page): the regression guard for the tab the plugin
 * now renders itself. The script is injected into index.html at request time and
 * the tab is registered against Jellyfin's own tab strip. The specs assert that injection, the tab
 * registration, the deep link, and the reload path, and that a user without
 * access never gets an entry point at all.
 */
import { test, expect, type Page } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { apiContext, authHeader, normalUserContext, loadAuth, p } from '../setup/api-client.ts';
import { disableDiscoveryUserAccess, ensureDiscoveryConfigured } from '../setup/discovery-config.ts';

// Only the Trakt layout spec needs the official plugin: the Trakt probe reports
// enabled only when that plugin is present.
const OFFICIAL_TRAKT = process.env.JFH_E2E_EXTERNAL_PLUGINS === '1';

test.beforeEach(async ({ page }, testInfo) => {
  const layout = String(testInfo.project.metadata.discoveryLayout ?? 'modern');
  await page.addInitScript(value => localStorage.setItem('layout', value), layout);
});

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
// tab this spec guards against. A load error shares the message markup, so it is
// rejected separately instead of counting as a rendered panel.
async function expectDiscoveryRendered(page: Page): Promise<void> {
  const panel = page.locator('#jfh-tab-panel-discovery');
  await expect(panel).toBeVisible({ timeout: 15_000 });
  await expect(panel.locator('.jfh-discovery-grid, .jfh-discovery-msg').first()).toBeVisible({ timeout: 15_000 });
  await expect(panel.locator('.jfh-discovery-error'), 'Discovery must not render a load error').toHaveCount(0);
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

async function openSidebar(page: Page, modern: boolean) {
  const navigation = page.locator('.jfhelper-nav-discovery').filter({ visible: true });
  if (modern) {
    const drawer = page.locator('.MuiDrawer-paper').filter({ visible: true });
    const toggle = page.getByRole('button', { name: /menu/i }).filter({ visible: true }).first();
    await expect(drawer.or(toggle).first()).toBeVisible({ timeout: 15_000 });
    if (await drawer.count() === 0) await toggle.click({ timeout: 15_000 });
  } else {
    await page.locator('.mainDrawerButton').filter({ visible: true }).first().click({ timeout: 15_000 });
  }
  await expect(navigation, 'Discovery must be present in the active drawer').toHaveCount(1, { timeout: 15_000 });
  await expect(navigation).toBeVisible();
  return navigation;
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
    await expect(page).not.toHaveURL(/jfhTab=discovery/);
    await expectDiscoveryHidden(page);
    await page.goForward();
    await expect(page).toHaveURL(/jfhTab=discovery/);
    await expectDiscoveryRendered(page);
  });

  test('a clean browser session gets Discovery after interactive login without refreshing', async ({ page }, testInfo) => {
    const auth = loadAuth();
    const user = auth.normalUser ?? auth;
    const password = auth.normalUser
      ? process.env.JELLYFIN_USER_PASS ?? 'E2eUserPass1!'
      : process.env.JELLYFIN_ADMIN_PASS ?? 'E2ePassw0rd!';
    const base = auth.baseUrl.replace(/\/$/, '');
    await page.goto(`${base}/web/index.html`, { waitUntil: 'domcontentloaded' });
    const login = page.locator('#loginPage').filter({ visible: true });
    const username = login.locator('#txtManualName');
    const userCards = login.locator('#divUsers .card');
    const addServer = page.locator('#selectServerPage .btnAddServer').filter({ visible: true });
    const serverHost = page.locator('#txtServerHost').filter({ visible: true });
    await expect.poll(async () => {
      if (await username.isVisible() || await userCards.first().isVisible()) return 'login';
      if (await serverHost.isVisible()) return 'server-address';
      if (await addServer.isVisible()) return 'server-selection';
      return 'loading';
    }, { timeout: 15_000, message: 'Jellyfin must expose its initial connection or login screen' }).not.toBe('loading');
    if (await addServer.isVisible()) {
      await addServer.click({ timeout: 15_000 });
      await expect(serverHost).toBeEditable({ timeout: 15_000 });
    }
    if (await serverHost.isVisible()) {
      await expect(serverHost).toBeEditable();
      await serverHost.fill(base, { timeout: 15_000 });
      await page.locator('.addServerForm .button-submit').filter({ visible: true }).click({ timeout: 15_000 });
    }
    await expect(login).toBeVisible({ timeout: 15_000 });
    await expect.poll(async () => await username.isVisible() || await userCards.first().isVisible(), {
      timeout: 15_000, message: 'Jellyfin must show either its manual login form or its user choices',
    }).toBe(true);
    if (!await username.isVisible()) await login.locator('.btnManual').click({ timeout: 15_000 });
    await expect(page.locator('#jfh-tab-link-discovery, #jfh-tab-btn-discovery')).toHaveCount(0);
    await expect(page.locator('.jfhelper-nav-discovery')).toHaveCount(0);
    await expect(username).toBeEditable();
    await username.fill(user.userName, { timeout: 15_000 });
    await login.locator('#txtManualPassword').fill(password, { timeout: 15_000 });
    const [authentication] = await Promise.all([
      page.waitForResponse(response => new URL(response.url()).pathname.toLowerCase().endsWith('/users/authenticatebyname') && response.request().method() === 'POST', { timeout: 15_000 }),
      login.locator('.manualLoginForm .button-submit').click({ timeout: 15_000 }),
    ]);
    expect(authentication.ok(), 'the interactive login must be accepted by Jellyfin').toBe(true);
    await expect(page).toHaveURL(/#\/home(?:\.html)?(?:\?|$)/);
    // Check the drawer before any native-tab activation can conceal a missing first-login entry.
    const navigation = await openSidebar(page, testInfo.project.metadata.discoveryLayout === 'modern');
    await expect(navigation).toHaveAttribute('aria-label', 'Discover');
    await navigation.click();
    await expectDiscoveryRendered(page);
    await expect(discoveryEntry(page)).toHaveCount(1);
    await openDiscovery(page);
    await expectDiscoveryRendered(page);
    await page.evaluate(() => (window as any).Dashboard.logout());
    await expect(page).toHaveURL(/#\/(?:login|selectserver)/);
    await expect(page.locator('#jfh-tab-link-discovery, #jfh-tab-btn-discovery, #jfh-tab-panel-discovery')).toHaveCount(0);
    await expect(page.locator('.jfhelper-nav-discovery, .jellyfinHelperSection')).toHaveCount(0);
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

  test('owns exactly one native panel with matching button and panel positions', async ({ page }) => {
    await openHome(page);
    await openDiscovery(page);
    await expectDiscoveryRendered(page);

    const ownership = await page.evaluate(() => {
      const panel = document.getElementById('jfh-tab-panel-discovery');
      const button = document.getElementById('jfh-tab-btn-discovery');
      // The experimental layout keeps the strip button in a hidden header, so only
      // rendered elements count as an entry point.
      const isVisible = (el: Element | null) => {
        if (!el) return false;
        const style = window.getComputedStyle(el);
        if (style.display === 'none' || style.visibility === 'hidden') return false;
        const rect = el.getBoundingClientRect();
        return rect.width > 0 && rect.height > 0;
      };
      return {
        panels: document.querySelectorAll('#jfh-tab-panel-discovery').length,
        panelClasses: panel?.className ?? '',
        panelIndex: Number(panel?.dataset.index),
        buttonIndex: Number(button?.dataset.index),
        panelPosition: panel ? Array.from(panel.parentElement?.querySelectorAll(':scope > .tabContent.pageTabContent') ?? []).indexOf(panel) : -1,
        buttonPosition: button ? Array.from(button.parentElement?.querySelectorAll(':scope > .emby-tab-button') ?? []).indexOf(button) : -1,
        // Only one entry point may be visible: the strip button on the stable layout,
        // the header link on the experimental one. The hidden twin is not clickable.
        buttons: Array.from(document.querySelectorAll('#jfh-tab-btn-discovery')).filter(isVisible).length,
        links: Array.from(document.querySelectorAll('#jfh-tab-link-discovery')).filter(isVisible).length,
      };
    });
    expect(ownership.panels, 'exactly one Discovery panel exists').toBe(1);
    expect(ownership.panelClasses, 'the panel is a native Jellyfin tab panel').toContain('tabContent');
    expect(ownership.panelClasses).toContain('pageTabContent');
    expect(ownership.panelIndex).toBe(ownership.buttonIndex);
    expect(ownership.panelPosition).toBe(ownership.panelIndex);
    expect(ownership.buttonPosition).toBe(ownership.buttonIndex);
    expect(ownership.buttons + ownership.links, 'exactly one entry point exists').toBe(1);
  });

  test('signing out removes Discovery without clicking its entry again', async ({ page }) => {
    const auth = loadAuth();
    const user = auth.normalUser ?? auth;
    const password = auth.normalUser
      ? process.env.JELLYFIN_USER_PASS ?? 'E2eUserPass1!'
      : process.env.JELLYFIN_ADMIN_PASS ?? 'E2ePassw0rd!';
    // A separate device session prevents logout from invalidating credentials shared by other tests.
    const response = await page.request.post(`${auth.baseUrl}/Users/AuthenticateByName`, {
      headers: { Authorization: authHeader().replace('jfh-e2e-device', randomUUID()) },
      data: { Username: user.userName, Pw: password },
    });
    expect(response.ok()).toBeTruthy();
    const session = await response.json() as { AccessToken: string; User: { Id: string } };
    await openHomeAs(page, session.AccessToken, session.User.Id);
    await openDiscovery(page);
    await expectDiscoveryRendered(page);
    await page.evaluate(() => (window as any).Dashboard.logout());
    await expect(page).toHaveURL(/#\/(?:login|selectserver)/);
    await expect(page.locator('#jfh-tab-btn-discovery, #jfh-tab-link-discovery, #jfh-tab-panel-discovery')).toHaveCount(0);
    await expect(page.locator('.jfhelper-nav-discovery, .jellyfinHelperSection')).toHaveCount(0);
  });

  test('selecting Home clears the Discovery deep link before reload', async ({ page }) => {
    await openHome(page);
    await openDiscovery(page);
    await expectDiscoveryRendered(page);
    await clickHome(page);
    await expectDiscoveryHidden(page);
    await expect(page).not.toHaveURL(/jfhTab=discovery/);
    await page.reload();
    await expect(discoveryEntry(page)).toHaveCount(1);
    await expectDiscoveryHidden(page);
  });

  test('reopening Discovery updates card details from a fresh response', async ({ page }) => {
    let title = 'Before refresh';
    await page.route('**/JellyfinHelper/Discovery/My', route => route.fulfill({
      json: { Recommendations: [{ TmdbId: 123, MediaType: 'movie', Title: title, Score: 0.8, Genres: [] }] },
    }));
    await openHome(page);
    await openDiscovery(page);
    await expect(page.locator('#jfh-tab-panel-discovery .jfh-discovery-card-title')).toHaveText(title);
    await clickHome(page);
    await expectDiscoveryHidden(page);
    title = 'After refresh';
    await openDiscovery(page);
    await expect(page.locator('#jfh-tab-panel-discovery .jfh-discovery-card-title')).toHaveText(title);
  });

  test('an empty result still exposes the available Discovery navigation', async ({ page }) => {
    await page.route('**/JellyfinHelper/Discovery/My', route => route.fulfill({ json: { Recommendations: [] } }));
    await openHome(page);
    await openDiscovery(page);
    await expectDiscoveryRendered(page);
    await expect(page.locator('.jfhelper-nav-discovery')).toHaveCount(1);
    await expect(page.locator('#jfh-tab-panel-discovery .jfh-discovery-grid')).toHaveCount(0);
  });

  for (const width of [320, 390, 640]) {
    test('sidebar navigation stays compact inside a ' + width + 'px viewport', async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 844 });
      await openHome(page);
      await expect(discoveryEntry(page)).toHaveCount(1);
      const modern = testInfo.project.metadata.discoveryLayout === 'modern';
      const navigation = await openSidebar(page, modern);
      await expect(navigation).toHaveAttribute('aria-label', 'Discover');
      await navigation.evaluate(element => { (element as HTMLElement).style.fontSize = '150%'; });
      await expect.poll(async () => {
        const box = await navigation.boundingBox();
        return box ? box.x : -Infinity;
      }).toBeGreaterThanOrEqual(-1);
      const row = await navigation.boundingBox();
      expect(row).not.toBeNull();
      expect(row!.x + row!.width).toBeLessThanOrEqual(width + 1);
      if (modern) {
        const geometry = await navigation.evaluate(element => {
          const icon = element.querySelector('.jfh-discovery-navigation-icon')!.getBoundingClientRect();
          const text = element.querySelector('.jfh-discovery-navigation-text')!.getBoundingClientRect();
          return { gap: text.left - icon.right, fontSize: Number.parseFloat(getComputedStyle(element).fontSize) };
        });
        expect(geometry.gap).toBeGreaterThanOrEqual(0);
        expect(geometry.gap).toBeLessThanOrEqual(geometry.fontSize);
      }
    });
  }

  test('revoking access removes previously rendered cards and entry points', async ({ page }) => {
    const admin = await apiContext(loadAuth());
    try {
      await openHome(page);
      await openDiscovery(page);
      await expectDiscoveryRendered(page);
      await clickHome(page);
      await expectDiscoveryHidden(page);
      await disableDiscoveryUserAccess(admin);
      const denied = page.waitForResponse(response =>
        new URL(response.url()).pathname === p('Discovery/My') && response.status() === 403);
      await discoveryEntry(page).click();
      await denied;
      await expect(page.locator('#jfh-tab-btn-discovery, #jfh-tab-link-discovery, #jfh-tab-panel-discovery')).toHaveCount(0);
      await expect(page.locator('.jfhelper-nav-discovery')).toHaveCount(0);
    } finally {
      await ensureDiscoveryConfigured(admin);
      await admin.dispose();
    }
  });

  test('a cold dashboard navigation to Home initializes Discovery', async ({ page }) => {
    const auth = loadAuth();
    const base = await openHomeAs(page, auth.token, auth.userId);
    await page.goto(`${base}/web/index.html#/dashboard`);
    await expect(page).toHaveURL(/#\/dashboard/);
    await page.waitForFunction(() => typeof (window as any).Emby?.Page?.show === 'function');
    await page.evaluate(() => (window as any).Emby.Page.show('/home'));
    await expect(discoveryEntry(page)).toHaveCount(1);
    await openDiscovery(page);
    await expectDiscoveryRendered(page);
  });
});

/**
 * Negative counterpart: with the user-access toggle OFF, nothing is injected or
 * registered at all. Injection is gated on the toggle, so the shell is served
 * untouched and no tab button, header link, or panel can exist.
 * Runs in its own describe so its config toggling is bracketed and restored.
 */
test.describe('Discovery native tab - access disabled', () => {
  test.beforeAll(async () => {
    const auth = loadAuth();
    const admin = await apiContext(auth);
    const probe = (await normalUserContext(auth)) ?? admin;
    try {
      // Turn the user-access toggle OFF with the shared known-good payload, then poll until the user-facing
      // read actually 403s so the UI assertion below is not racing an un-propagated toggle.
      await disableDiscoveryUserAccess(admin);
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

  test('no script, tab, link or panel is registered while access is off', async ({ page }) => {
    const base = await openHome(page);

    // Injection is gated on the toggle, so the shell must be served untouched. Asserting
    // the response instead of a 403 keeps this independent of how the SPA probes the API.
    const html = await (await page.request.get(`${base}/web/index.html`)).text();
    expect(html, 'no Discovery script is injected while access is off').not.toContain(
      'plugin="Jellyfin Helper"',
    );

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


test.describe('Discovery native tab - logged out', () => {
  test('no discovery entry point is rendered on the login page', async ({ page }) => {
    const auth = loadAuth();
    const base = auth.baseUrl.replace(/\/$/, '');
    await page.goto(`${base}/web/index.html#/login`);
    await expect(page.locator('#jfh-tab-btn-discovery, #jfh-tab-link-discovery')).toHaveCount(0, { timeout: 10_000 });
    await expect(page.locator('.jfhelper-nav-discovery')).toHaveCount(0);
  });
});

test.describe('Discovery native tab - sidebar entry point', () => {
  test('opens Discovery from the first Home sidebar without activating the native tab first', async ({ page }, testInfo) => {
    await openHome(page);
    const navigation = await openSidebar(page, testInfo.project.metadata.discoveryLayout === 'modern');
    await navigation.click();
    await expectDiscoveryRendered(page);
    await expect(page.locator('.jfhelper-nav-discovery')).toHaveCount(1);
  });
});
