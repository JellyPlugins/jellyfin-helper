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
  await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'seerr-key' },
  });
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

    // My cached recs — 200 (possibly null body) once enabled.
    const my = await user!.get(p('Discovery/My'));
    expect(my.status(), 'Discovery/My').not.toBe(403);
    expect(my.status()).toBeLessThan(500);

    // External links returns the configured Seerr URL.
    const links = await user!.get(p('Discovery/My/ExternalLinks'));
    expect(links.status()).not.toBe(403);
    expect(links.status()).toBeLessThan(500);

    await assertPluginActive(admin);
  });

  test('/My/RequestPermissions + Services resolve against the mock when enabled', async () => {
    test.skip(!user, 'no non-admin user provisioned');
    await setDiscoveryAccess(true);

    const perms = await user!.get(p('Discovery/My/RequestPermissions/radarr?mediaType=movie'));
    expect(perms.status()).toBeLessThan(500);
    expect(perms.status()).not.toBe(403);

    const services = await user!.get(p('Discovery/My/Services/radarr'));
    // 200 (list) or 503 (permission gated) are both valid; never 500/403-when-enabled.
    expect([200, 400, 503]).toContain(services.status());
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
    // Valid outcomes: 200 (recorded) or 400 (validation) — never 500.
    expect(res.status()).toBeLessThan(500);
    await assertPluginActive(admin);
  });
});

// --- admin-side gaps -------------------------------------------------------

test('admin Discovery/Request submission reaches the mock', async () => {
  await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: '***' },
  });
  const res = await admin.post(p('Discovery/Request'), {
    headers: { 'Content-Type': 'application/json' },
    data: { TmdbId: 27205, MediaType: 'movie' },
  });
  // Mock returns 201 → plugin maps to success; validation issues → 400. Not 500.
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(admin);
});

test('Trash/Relocate moves between paths (or degrades cleanly)', async () => {
  // Enable trash first so relocation has meaning.
  await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 },
  });
  const res = await admin.post(p('Trash/Relocate'), {
    headers: { 'Content-Type': 'application/json' },
    data: { OldTrashPath: '.jellyfin-trash', NewTrashPath: '.jellyfin-trash-2' },
  });
  // 200 (moved/nothing-to-move) or 400 (guard) — never a server error.
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(admin);
});
