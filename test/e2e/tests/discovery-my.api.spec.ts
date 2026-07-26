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
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, normalUserContext, requireNormalUser, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

const auth = loadAuth();

let admin: APIRequestContext;
let user: APIRequestContext | null;

// Snapshot of the shared-backend Configuration fields these tests mutate, so we
// can restore them in afterAll and not leak state into later specs that assume a
// pristine Seerr/Trash config (the state-bleed pattern already fixed elsewhere).
interface ConfigSnapshot {
  SeerrUrl?: string;
  DiscoveryUserAccessEnabled?: boolean;
  RecommendationsTaskMode?: string;
  UseTrash?: boolean;
  TrashFolderPath?: string;
  TrashRetentionDays?: number;
}
let configSnapshot: ConfigSnapshot = {};

test.beforeAll(async () => {
  admin = await apiContext(auth);
  user = await normalUserContext(auth);

  // Capture the pre-test config so afterAll can put it back verbatim. GET masks
  // the API key as '***'; re-sending '***' preserves the stored key (no wipe).
  const current = await admin.get(p('Configuration'));
  if (current.ok()) {
    const c = (await current.json()) as ConfigSnapshot;
    configSnapshot = {
      SeerrUrl: c.SeerrUrl,
      DiscoveryUserAccessEnabled: c.DiscoveryUserAccessEnabled,
      RecommendationsTaskMode: c.RecommendationsTaskMode,
      UseTrash: c.UseTrash,
      TrashFolderPath: c.TrashFolderPath,
      TrashRetentionDays: c.TrashRetentionDays,
    };
  }

  // Ensure Seerr points at the mock so discovery/permission calls resolve.
  const seed = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'seerr-key' },
  });
  expect(seed.ok(), `initial mock-Seerr config failed: ${seed.status()}`).toBeTruthy();
});

test.afterAll(async () => {
  // Restore the shared Configuration these tests mutated, so later specs run
  // against the original backend state. SeerrApiKey is intentionally omitted:
  // sending '***' would preserve the current key, and the snapshot never had the
  // plaintext anyway; leaving it out keeps whatever key is stored now.
  if (Object.keys(configSnapshot).length > 0) {
    await admin
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: { ...configSnapshot, SeerrApiKey: '***' },
      })
      .catch(() => undefined);
  }
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
    requireNormalUser(user);
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
    requireNormalUser(user);
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
    requireNormalUser(user);
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

  test('/My/script is served anonymously (embedded JS, no auth header)', async () => {
    // [AllowAnonymous] — must be reachable with NO Authorization header at all
    // (the sidebar script loads before the user is known). Use a bare context so
    // anonymity is actually exercised, not an admin token.
    const anon = await pwRequest.newContext({ baseURL: auth.baseUrl });
    try {
      const res = await anon.get(p('Discovery/My/script'));
      expect(res.status(), 'anonymous script must not be 401/403').not.toBe(401);
      expect(res.status()).not.toBe(403);
      expect(res.ok(), `Discovery/My/script anonymous failed: ${res.status()}`).toBeTruthy();
      expect(res.headers()['content-type'] ?? '').toContain('javascript');
    } finally {
      await anon.dispose();
    }
  });

  test('/My/Dismiss records a dismissal (mutates feedback store)', async () => {
    requireNormalUser(user);
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
