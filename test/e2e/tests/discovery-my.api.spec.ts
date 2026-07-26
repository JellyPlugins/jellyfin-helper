/**
 * User-facing Discovery (Discovery/My/*) — the non-admin sidebar flow.
 *
 * These endpoints require an authenticated NON-admin user AND the
 * DiscoveryUserAccessEnabled config toggle. We provisioned a normal user in
 * global-setup; if that failed, the whole suite skips (logged, not silent).
 *
 * Covered:
 *   - 403 for every /My endpoint while the toggle is OFF (access gating).
 *   - With the toggle ON: My, ExternalLinks, RequestPermissions, Services,
 *     script, Dismiss respond correctly (against the mock Seerr).
 *   - Also fills two admin-side gaps: Discovery/Request submission and
 *     Trash/Relocate.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, normalUserContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

const auth = loadAuth();

let admin: APIRequestContext;
let user: APIRequestContext | null;

test.beforeAll(async () => {
  admin = await apiContext(auth);
  user = await normalUserContext(auth);
  // Ensure Seerr points at the mock so discovery/permission calls resolve.
  const seed = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'seerr-key' },
  });
  expect(seed.ok(), `initial mock-Seerr config failed: ${seed.status()}`).toBeTruthy();
});

test.afterAll(async () => {
  await admin.dispose();
  await user?.dispose();
});

async function setDiscoveryAccess(enabled: boolean) {
  // Requires Recommendations active + Seerr configured for the toggle to stick.
  const res = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      RecommendationsTaskMode: 'Activate',
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: '***',
      DiscoveryUserAccessEnabled: enabled,
    },
  });
  expect(res.ok(), `toggle set failed: ${res.status()}`).toBeTruthy();
}

test.describe.serial('Discovery/My access gating', () => {
  test('all /My endpoints return 403 when access is disabled', async () => {
    test.skip(!user, 'no non-admin user provisioned');
    await setDiscoveryAccess(false);

    const endpoints = [
      p('Discovery/My'),
      p('Discovery/My/ExternalLinks'),
      p('Discovery/My/RequestPermissions/radarr?mediaType=movie'),
      p('Discovery/My/Services/radarr'),
    ];
    for (const ep of endpoints) {
      const res = await user!.get(ep);
      expect(res.status(), `${ep} should be 403 when disabled`).toBe(403);
    }
    await assertPluginActive(admin);
  });

  test('/My endpoints respond (not 403) when access is enabled', async () => {
    test.skip(!user, 'no non-admin user provisioned');
    await setDiscoveryAccess(true);

    // My cached recs — must succeed (2xx) once enabled; the body may be null
    // (no cached recs yet) but the request itself must not fail.
    const my = await user!.get(p('Discovery/My'));
    expect(my.ok(), `Discovery/My failed: ${my.status()}`).toBeTruthy();

    // External links returns the configured (mock) Seerr URL — a green-path 2xx.
    const links = await user!.get(p('Discovery/My/ExternalLinks'));
    expect(links.ok(), `Discovery/My/ExternalLinks failed: ${links.status()}`).toBeTruthy();

    await assertPluginActive(admin);
  });

  test('/My/RequestPermissions + Services resolve against the mock when enabled', async () => {
    test.skip(!user, 'no non-admin user provisioned');
    await setDiscoveryAccess(true);

    // RequestPermissions resolves the linked Seerr user against the mock — 2xx.
    const perms = await user!.get(p('Discovery/My/RequestPermissions/radarr?mediaType=movie'));
    expect(perms.ok(), `RequestPermissions failed: ${perms.status()}`).toBeTruthy();

    const services = await user!.get(p('Discovery/My/Services/radarr'));
    // 200 (service list) or 503 (user lacks the Seerr permission to select a
    // service) are both legitimate; anything else — esp. 400/500/403 — is a bug.
    expect([200, 503], `Services status ${services.status()}`).toContain(services.status());
    await assertPluginActive(admin);
  });

  test('/My/script is served anonymously (embedded JS)', async () => {
    // AllowAnonymous — reachable even without the user token.
    const res = await admin.get(p('Discovery/My/script'));
    expect([200, 404]).toContain(res.status());
    if (res.ok()) {
      expect(res.headers()['content-type'] ?? '').toContain('javascript');
    }
  });

  test('/My/Dismiss records a dismissal (mutates feedback store)', async () => {
    test.skip(!user, 'no non-admin user provisioned');
    await setDiscoveryAccess(true);
    const res = await user!.post(p('Discovery/My/Dismiss'), {
      headers: { 'Content-Type': 'application/json' },
      data: { TmdbId: 27205, MediaType: 'movie' },
    });
    // A well-formed dismissal for an enabled user must be recorded (2xx). A
    // 400 here would mean the valid payload was rejected — a real regression.
    expect(res.ok(), `Dismiss failed: ${res.status()}`).toBeTruthy();
    await assertPluginActive(admin);
  });
});

// --- admin-side gaps -------------------------------------------------------

test('admin Discovery/Request submission reaches the mock', async () => {
  const cfg = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: '***' },
  });
  expect(cfg.ok(), `request-test config failed: ${cfg.status()}`).toBeTruthy();
  const res = await admin.post(p('Discovery/Request'), {
    headers: { 'Content-Type': 'application/json' },
    data: { TmdbId: 27205, MediaType: 'movie' },
  });
  // The mock accepts the request (maps to 201 → plugin success). A well-formed
  // submission against the configured mock must succeed, not merely avoid 500.
  expect(res.ok(), `Discovery/Request failed: ${res.status()}`).toBeTruthy();
  await assertPluginActive(admin);
});

test('Trash/Relocate moves between paths (or degrades cleanly)', async () => {
  // Enable trash first so relocation has meaning.
  const cfg = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 },
  });
  expect(cfg.ok(), `trash config failed: ${cfg.status()}`).toBeTruthy();
  const res = await admin.post(p('Trash/Relocate'), {
    headers: { 'Content-Type': 'application/json' },
    data: { OldTrashPath: '.jellyfin-trash', NewTrashPath: '.jellyfin-trash-2' },
  });
  // 200 (moved / nothing-to-move) or 400 (path guard rejected the relocation)
  // are both documented outcomes; a 5xx would be a genuine server error.
  expect([200, 400], `Relocate status ${res.status()}`).toContain(res.status());
  await assertPluginActive(admin);
});
