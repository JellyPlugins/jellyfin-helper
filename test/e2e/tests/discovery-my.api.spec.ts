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
  ExcludedLibraries?: string;
  PluginLogLevel?: string;
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
      ExcludedLibraries: c.ExcludedLibraries,
      PluginLogLevel: c.PluginLogLevel,
    };
  }

  // Ensure Seerr points at the mock so discovery/permission calls resolve.
  const seed = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'seerr-key' },
  });
  expect(seed.ok(), `initial mock-Seerr config failed: ${seed.status()}`).toBeTruthy();

  // Raise the plugin log level so the availability-exclusion spec can attach the SeerrDiscovery
  // generator's DEBUG trace to a failure. PUT /Configuration ignores PluginLogLevel by design, so
  // this must go through the dedicated LogLevel endpoint. Best-effort - never fail setup on it.
  await admin
    .put(p('Configuration/LogLevel'), {
      headers: { 'Content-Type': 'application/json' },
      data: { PluginLogLevel: 'DEBUG' },
    })
    .catch(() => undefined);
});

test.afterAll(async () => {
  // Restore the shared Configuration these tests mutated, so later specs run against the original backend state.
  if (Object.keys(configSnapshot).length > 0) {
    const { PluginLogLevel: originalLogLevel, ...restorable } = configSnapshot;
    await admin
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: { ...restorable, SeerrApiKey: API_KEY_MASK },
      })
      .catch(() => undefined);

    // PluginLogLevel is ignored by PUT /Configuration, so the DEBUG level raised in beforeAll must be
    // put back through the dedicated endpoint or later specs inherit DEBUG logging. Best-effort.
    if (originalLogLevel) {
      await admin
        .put(p('Configuration/LogLevel'), {
          headers: { 'Content-Type': 'application/json' },
          data: { PluginLogLevel: originalLogLevel },
        })
        .catch(() => undefined);
    }
  }
  await admin.dispose();
  await user?.dispose();
});

async function setDiscoveryAccess(enabled: boolean) {
  // Requires Recommendations active + Seerr configured for the toggle to stick.
  // ExcludedLibraries must be pristine for the watch profile to be visible to
  // the discovery engine; other specs (hardening) mutate it.
  const res = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      RecommendationsTaskMode: 'Activate',
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: 'seerr-key',
      DiscoveryUserAccessEnabled: enabled,
      ExcludedLibraries: '',
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

  // Ids seeded into the shared admin watch profile, unwound in afterAll so later specs (workers: 1
  // shares one backend) do not inherit a profile this spec created.
  const seededPlayed: string[] = [];
  let seededFavorite: string | null = null;
  const originalGenres = new Map<string, string[] | undefined>();
  // Played items that existed before this spec wiped the admin's watch history to build a clean
  // profile. Restored in afterAll so later specs (shared backend, workers: 1) see the original state.
  const originalPlayed = new Set<string>();
  let originalPlayedCaptured = false;

  test.afterAll(async () => {
    for (const id of seededPlayed) {
      await admin.delete(`/UserPlayedItems/${id}?userId=${auth.userId}`).catch(() => undefined);
    }
    if (seededFavorite) {
      await admin.delete(`/UserFavoriteItems/${seededFavorite}?userId=${auth.userId}`).catch(() => undefined);
    }
    // Restore the played state that existed before the profile wipe. Done after the seeded deletions
    // above so an id that was both pre-existing and re-seeded ends up played, not deleted.
    if (originalPlayedCaptured) {
      for (const id of originalPlayed) {
        await admin.post(`/UserPlayedItems/${id}?userId=${auth.userId}`).catch(() => undefined);
      }
    }
    for (const [itemId, genres] of originalGenres) {
      try {
        const cur = await admin.get(`/Items/${itemId}?userId=${auth.userId}`);
        if (!cur.ok()) continue;
        const dto = (await cur.json()) as { Genres?: string[] };
        dto.Genres = genres ?? [];
        await admin
          .post(`/Items/${itemId}`, {
            headers: { 'Content-Type': 'application/json' },
            data: dto,
          })
          .catch(() => undefined);
      } catch {
        // best-effort restore
      }
    }
  });

  // Discovery requires a user profile with watch history containing genres; without an active profile,
  // no candidates are generated. Since this test suite seeds no playback,
  // it must build its own profile instead of relying on prior test runs.
  // The ffmpeg test fixtures lack genre metadata, which leaves the preference vector empty and causes `SeerrDiscoveryService` to return null and clear the pool.
  // Assigning "Action"—a valid TMDb genre ID—ensures the mock discovery query returns candidates.
  async function seedAdminWatchProfile(): Promise<void> {
    // Ensure a pristine watch profile so leftover playback from other specs (recommendations-ranking)
    // does not dilute the genre vector or change the average-year filter.
    try {
      const existing = await admin.get(`/Items?IncludeItemTypes=Movie&Recursive=true&userId=${auth.userId}&Filters=IsPlayed`);
      if (existing.ok()) {
        const eb = (await existing.json()) as { Items?: Array<{ Id: string }> };
        for (const it of eb.Items ?? []) {
          // Capture what we are about to wipe so afterAll can restore the pre-existing played state.
          originalPlayed.add(it.Id);
          await admin.delete(`/UserPlayedItems/${it.Id}?userId=${auth.userId}`).catch(() => undefined);
        }
        originalPlayedCaptured = true;
      }
    } catch {
      // best-effort
    }

    const res = await admin.get(`/Items?IncludeItemTypes=Movie&Recursive=true&userId=${auth.userId}`);
    expect(res.ok(), `/Items status ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as { Items?: Array<{ Id: string; Name?: string; ProductionYear?: number }> };
    const sorted = (body.Items ?? [])
      .slice()
      .sort((a, b) => (a.Name ?? a.Id).localeCompare(b.Name ?? b.Id));
    const movies = sorted.map((i) => i.Id);
    expect(movies.length, 'need a movie to build a watch profile from').toBeGreaterThan(0);
    // Avoid picking the discover candidates themselves if they ever appear as library items.
    const watched = movies.slice(0, 3);
    for (const id of watched) {
      await assignGenre(id, 'Action');
      const mark = await admin.post(`/UserPlayedItems/${id}?userId=${auth.userId}`);
      expect(mark.ok(), `mark-played ${id}: ${mark.status()}`).toBeTruthy();
      seededPlayed.push(id);
    }
    const fav = await admin.post(`/UserFavoriteItems/${movies[0]}?userId=${auth.userId}`);
    expect([200, 204]).toContain(fav.status());
    seededFavorite = movies[0];

    // Verify the profile is actually visible to the recommendation engine before triggering
    // generation. The discovery task reads the profile through the same WatchHistoryService (no
    // cache) that this endpoint uses, so a green poll here guarantees the task sees the same data.
    // Jellyfin flushes UserData in a batch, so under CI load the played/favorite/genre edits can
    // take several seconds to surface - poll up to ~15s. Critically this must HARD FAIL if the
    // profile never becomes visible: the previous silent break let generation run against an empty
    // profile, which returned an empty pool and surfaced as an unrelated "positive control [] "
    // failure instead of pointing at the real setup race.
    let lastProfile: { GenreDistribution?: Record<string, number>; WatchedMovieCount?: number } = {};
    let profileVisible = false;
    for (let attempt = 0; attempt < 30; attempt++) {
      const wp = await admin.get(p(`Recommendations/WatchProfile/${auth.userId}`));
      if (wp.ok()) {
        lastProfile = (await wp.json()) as typeof lastProfile;
        if ((lastProfile.WatchedMovieCount ?? 0) >= 3 && (lastProfile.GenreDistribution?.Action ?? 0) >= 3) {
          profileVisible = true;
          break;
        }
      }
      await new Promise((r) => setTimeout(r, 500));
    }
    expect(
      profileVisible,
      `watch profile never became visible to the engine (setup race, not a product bug): ` +
        `WatchedMovieCount=${lastProfile.WatchedMovieCount ?? 0}, Action=${lastProfile.GenreDistribution?.Action ?? 0}. ` +
        `Full GenreDistribution=${JSON.stringify(lastProfile.GenreDistribution ?? {})}`,
    ).toBe(true);
  }

  // Set a genre on an item via fetch-modify-save. ItemUpdateController replaces the whole DTO, so
  // we edit the item's own DTO rather than posting a partial body, then persist synchronously (no
  // rescan needed - WatchHistoryService reads item.Genres live at task time).
  async function assignGenre(itemId: string, genre: string): Promise<void> {
    const get = await admin.get(`/Items/${itemId}?userId=${auth.userId}`);
    expect(get.ok(), `fetch item ${itemId}: ${get.status()}`).toBeTruthy();
    const dto = (await get.json()) as { Genres?: string[] };
    if (!originalGenres.has(itemId)) {
      originalGenres.set(itemId, dto.Genres ? [...dto.Genres] : undefined);
    }
    dto.Genres = [genre];
    const post = await admin.post(`/Items/${itemId}`, {
      headers: { 'Content-Type': 'application/json' },
      data: dto,
    });
    expect(post.ok(), `set genre on ${itemId}: ${post.status()}`).toBeTruthy();
  }

  async function readDiscoveryPoolIds(): Promise<number[]> {
    // The admin view returns every user's pool; flatten to the set of surfaced TMDb ids.
    const res = await admin.get(p('Discovery'));
    expect(res.ok(), `Discovery admin list failed: ${res.status()}`).toBeTruthy();
    const pools = (await res.json()) as Array<{
      Recommendations?: Array<{ TmdbId: number; MediaType?: string }>;
    }>;
    return pools.flatMap((pool) => (pool.Recommendations ?? []).map((rec) => rec.TmdbId));
  }

  async function generatedTmdbIds(): Promise<number[]> {
    const run = await runCleanupTask(admin);
    expect(run.LastExecutionResult?.Status).toBe('Completed');
    return readDiscoveryPoolIds();
  }

  // Pull the plugin's own DEBUG log lines for the discovery generator so a failure can point at the
  // real branch (empty candidate gather vs. filter drop vs. an empty-result cache overwrite) instead
  // of a bare "pool was []". Best-effort: never throws, just returns whatever the endpoint yields.
  async function discoveryDebugTail(): Promise<string> {
    const res = await admin.get(p('Logs?source=SeerrDiscovery&minLevel=DEBUG&limit=2000')).catch(() => null);
    if (!res || !res.ok()) return '(SeerrDiscovery logs unavailable)';
    const body = (await res.json().catch(() => null)) as { Entries?: Array<{ Message?: string }> } | null;
    const entries = body?.Entries;
    if (!Array.isArray(entries)) return '(SeerrDiscovery logs not available)';
    return entries.slice(-12).map((l) => l.Message ?? '').join('\n');
  }

  // Generate and require a non-empty pool. runCleanupTask runs the whole weekly umbrella task, whose
  // "Completed" says nothing about whether discovery generated for THIS user: a single transient
  // per-user null makes GenerateForUserAsync drop the user and the generator's final Save([]) wipes a
  // previously-good pool (SeerrDiscoveryService: empty allResults overwrites the cache file). So the
  // positive control must tolerate one bad regeneration and retry, and only fail - with the plugin's
  // own debug tail attached - if the pool stays empty across attempts.
  async function generateExpectingPool(): Promise<number[]> {
    let ids: number[] = [];
    for (let attempt = 0; attempt < 3; attempt++) {
      ids = await generatedTmdbIds();
      if (ids.length > 0) return ids;
    }
    expect(
      ids.length,
      `discovery pool stayed empty across 3 regenerations - the generator produced no ` +
        `candidates for the seeded admin profile. SeerrDiscovery debug tail:\n${await discoveryDebugTail()}`,
    ).toBeGreaterThan(0);
    return ids;
  }

  test('a Seerr-available discover candidate is excluded from generated recommendations', async () => {
    // This test runs the cleanup task twice (before + after arming) plus a profile-visibility
    // poll, each of which can take many seconds under CI load. The suite-wide 90s budget is too
    // tight for that chain, so raise it here rather than globally.
    test.setTimeout(180_000);
    const mock = await pwRequest.newContext();
    try {
      await setDiscoveryAccess(true);
      await seedAdminWatchProfile();

      // Positive control: with the mock pristine, 680 is a normal discover candidate and must
      // surface. This proves the absence assertion below is meaningful and not a vacuous pass on
      // an empty pool. Retry a transient empty regeneration (see generateExpectingPool).
      const reset = await mock.get(`${MOCK_SEERR_PUBLIC}/reset`);
      expect(reset.ok(), `mock reset failed: ${reset.status()}`).toBeTruthy();
      const before = await generateExpectingPool();
      expect(
        before,
        `positive control: ${AVAILABLE_TMDB} must surface before it is armed as available. ` +
          `Pool was non-empty (${before.length} ids: ${before.join(',')}) but lacked ${AVAILABLE_TMDB}. ` +
          `SeerrDiscovery debug tail:\n${await discoveryDebugTail()}`,
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
