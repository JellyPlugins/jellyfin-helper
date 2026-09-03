/** * User-facing Discovery (Discovery/My/*) - the non-admin sidebar flow. * * These endpoints require an authenticated NON-admin user AND the * DiscoveryUserAccessEnabled config toggle. */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, normalUserContext, requireNormalUser, loadAuth, p, assertPluginActive, runCleanupTask, API_KEY_MASK } from '../setup/api-client.ts';

// MaxVisiblePerUser cap enforced by GetMyDiscoveryResults (SeerrDiscoveryService).
const MAX_VISIBLE_PER_USER = 10;

// The mock is published to the host on loopback; the plugin container reaches it as mock-seerr.
const MOCK_SEERR_PUBLIC = process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';

const auth = loadAuth();

let admin: APIRequestContext;
let user: APIRequestContext | null;

// Snapshot of the shared-backend Configuration fields these tests mutate, so we can restore them in afterAll and not leak state into later specs that assume a pristine Seerr/Trash config (the state-bleed pattern already fixed elsewhere).
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
  // the API key with the mask sentinel; re-sending it preserves the stored key (no wipe).
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
  // Restore the shared Configuration these tests mutated, so later specs run against the original backend state.
  if (Object.keys(configSnapshot).length > 0) {
    await admin
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: { ...configSnapshot, SeerrApiKey: API_KEY_MASK },
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
      SeerrApiKey: API_KEY_MASK,
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

    // My cached recs - must succeed (2xx) once enabled; the body may be null
    // (no cached recs yet) but the request itself must not fail.
    const my = await user!.get(p('Discovery/My'));
    expect(my.ok(), `Discovery/My failed: ${my.status()}`).toBeTruthy();

    // External links returns the configured (mock) Seerr URL - a green-path 2xx.
    const links = await user!.get(p('Discovery/My/ExternalLinks'));
    expect(links.ok(), `Discovery/My/ExternalLinks failed: ${links.status()}`).toBeTruthy();

    await assertPluginActive(admin);
  });

  test('/My/RequestPermissions + Services resolve against the mock when enabled', async () => {
    requireNormalUser(user);
    await setDiscoveryAccess(true);

    // RequestPermissions resolves the linked Seerr user against the mock - 2xx.
    const perms = await user!.get(p('Discovery/My/RequestPermissions/radarr?mediaType=movie'));
    expect(perms.ok(), `RequestPermissions failed: ${perms.status()}`).toBeTruthy();

    const services = await user!.get(p('Discovery/My/Services/radarr'));
    // 200 (service list) or 503 (user lacks the Seerr permission to select a
    // service) are both legitimate; anything else - esp. 400/500/403 - is a bug.
    expect([200, 503], `Services status ${services.status()}`).toContain(services.status());
    await assertPluginActive(admin);
  });

  test('/My/script is served anonymously (embedded JS, no auth header)', async () => {
    // [AllowAnonymous] - must be reachable with NO Authorization header at all (the sidebar script loads before the user is known).
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
    // 400 here would mean the valid payload was rejected - a real regression.
    expect(res.ok(), `Dismiss failed: ${res.status()}`).toBeTruthy();
    await assertPluginActive(admin);
  });
});

test('admin Discovery/Request submission reaches the mock', async () => {
  const cfg = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: API_KEY_MASK },
  });
  expect(cfg.ok(), `request-test config failed: ${cfg.status()}`).toBeTruthy();
  const res = await admin.post(p('Discovery/Request'), {
    headers: { 'Content-Type': 'application/json' },
    data: { TmdbId: 27205, MediaType: 'movie' },
  });
  // The mock accepts the request (maps to 201 -> plugin success). A well-formed
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

// --- Discovery/My cached-result FILTERING (not just the empty-cache path) ---- The /My tests above only ever hit the empty-cache branch (body may be null).
test.describe.serial('Discovery/My cached-result filtering', () => {
  test('populated cache is filtered and capped for the requesting user', async () => {
    requireNormalUser(user);
    // Enable access (sets RecommendationsTaskMode:Activate + Seerr) then generate.
    await setDiscoveryAccess(true);
    const run = await runCleanupTask(admin);
    expect(run.LastExecutionResult?.Status).toBe('Completed');

    const res = await user!.get(p('Discovery/My'));
    expect(res.ok(), `Discovery/My failed: ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as {
      Recommendations?: Array<{ TmdbId: number; MediaType?: string; AlreadyRequested?: boolean }>;
    } | null;

    // The generation may or may not surface items for this specific linked user depending on the mock's discover pool; both are valid, but if it DID populate we must see the filter/cap invariants hold (not the vacuous empty-cache path).
    if (body && Array.isArray(body.Recommendations)) {
      expect(
        body.Recommendations.length,
        `visible pool must be capped at MaxVisiblePerUser=${MAX_VISIBLE_PER_USER}`,
      ).toBeLessThanOrEqual(MAX_VISIBLE_PER_USER);
      // The filter excludes AlreadyRequested items - none may appear in the served pool.
      for (const rec of body.Recommendations) {
        expect(rec.AlreadyRequested ?? false, `TmdbId ${rec.TmdbId} leaked despite AlreadyRequested`).toBe(false);
      }
    }
    await assertPluginActive(admin);
  });
});

// --- Seerr availability exclusion (Fix: honour mediaInfo.status) ------------- A discover candidate Seerr reports as already available (mediaInfo.status 4/5) must never surface as a discovery recommendation, even though nothing in Radarr/Sonarr or the Jellyfin library tracks it. Pulp Fiction (tmdbId 680) is the only discover candidate not already excluded by the Arr/library sources, so arming it isolates the mediaInfo.status filter.
test.describe.serial('Discovery availability exclusion', () => {
  const AVAILABLE_TMDB = 680;

  // Discovery only surfaces candidates for a user whose watch history yields a non-empty genre
  // preference vector; with no active profile the generation returns an empty pool. The suite
  // seeds no playback, so this spec must establish its own profile rather than depend on another
  // spec (recommendations-ranking) having marked items played first in the same worker.
  async function seedAdminWatchProfile(): Promise<void> {
    const res = await admin.get(`/Items?IncludeItemTypes=Movie&Recursive=true&userId=${auth.userId}`);
    expect(res.ok(), `/Items status ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as { Items?: Array<{ Id: string }> };
    const movies = (body.Items ?? []).map((i) => i.Id);
    expect(movies.length, 'need a movie to build a watch profile from').toBeGreaterThan(0);
    for (const id of movies.slice(0, 3)) {
      const mark = await admin.post(`/UserPlayedItems/${id}?userId=${auth.userId}`);
      expect(mark.ok(), `mark-played ${id}: ${mark.status()}`).toBeTruthy();
    }
    const fav = await admin.post(`/UserFavoriteItems/${movies[0]}?userId=${auth.userId}`);
    expect([200, 204]).toContain(fav.status());
  }

  async function generatedTmdbIds(): Promise<number[]> {
    const run = await runCleanupTask(admin);
    expect(run.LastExecutionResult?.Status).toBe('Completed');
    // The admin view returns every user's pool; flatten to the set of surfaced TMDb ids.
    const res = await admin.get(p('Discovery'));
    expect(res.ok(), `Discovery admin list failed: ${res.status()}`).toBeTruthy();
    const pools = (await res.json()) as Array<{
      Recommendations?: Array<{ TmdbId: number; MediaType?: string }>;
    }>;
    return pools.flatMap((pool) => (pool.Recommendations ?? []).map((rec) => rec.TmdbId));
  }

  test('a Seerr-available discover candidate is excluded from generated recommendations', async () => {
    const mock = await pwRequest.newContext();
    try {
      await setDiscoveryAccess(true);
      await seedAdminWatchProfile();

      // Positive control: with the mock pristine, 680 is a normal discover candidate and must
      // surface. This proves the absence assertion below is meaningful and not a vacuous pass on
      // an empty pool.
      const reset = await mock.get(`${MOCK_SEERR_PUBLIC}/reset`);
      expect(reset.ok(), `mock reset failed: ${reset.status()}`).toBeTruthy();
      const before = await generatedTmdbIds();
      expect(
        before,
        `positive control: ${AVAILABLE_TMDB} must surface before it is armed as available`,
      ).toContain(AVAILABLE_TMDB);

      // Arm 680 as fully available (status 5) on the discover payload, then regenerate.
      const arm = await mock.post(`${MOCK_SEERR_PUBLIC}/seed-available-candidate`, {
        headers: { 'Content-Type': 'application/json' },
        data: { tmdbId: AVAILABLE_TMDB, status: 5 },
      });
      expect(arm.ok(), `arm available candidate failed: ${arm.status()}`).toBeTruthy();

      const after = await generatedTmdbIds();
      expect(
        after,
        `TmdbId ${AVAILABLE_TMDB} leaked despite Seerr reporting it available`,
      ).not.toContain(AVAILABLE_TMDB);

      await assertPluginActive(admin);
    } finally {
      // Disarm so later specs see the pristine discover pool.
      await mock.get(`${MOCK_SEERR_PUBLIC}/reset`).catch(() => undefined);
      await mock.dispose();
    }
  });
});
