/**
 * User-facing Trakt discovery (Discovery/My/Trakt*) end to end against the mock-trakt server.
 * Exercises the device-link state machine (start -> pending poll -> arm -> linked), the personal and
 * trending surfaces, and the TraktEnabled gate. Requires an authenticated non-admin user and TraktEnabled.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, normalUserContext, loadAuth, p, API_KEY_MASK } from '../setup/api-client.ts';

// The mock is published to the host on loopback; the plugin container reaches it as mock-trakt.
const MOCK_TRAKT_PUBLIC = process.env.MOCK_TRAKT_PUBLIC_URL ?? 'http://localhost:9100';

const auth = loadAuth();
let admin: APIRequestContext;
let user: APIRequestContext | null;

// Snapshot of the shared-backend Configuration fields this spec mutates, so afterAll can put them
// back verbatim and not leak Trakt/Seerr state into later specs that assume a pristine config.
interface ConfigSnapshot {
  TraktEnabled?: boolean;
  TraktClientId?: string;
  SeerrUrl?: string;
  DiscoveryUserAccessEnabled?: boolean;
  RecommendationsTaskMode?: string;
}
let snapshot: ConfigSnapshot = {};

// Non-admin watch-profile seed state, unwound in afterAll so later specs (workers: 1 shares one
// backend) do not inherit a profile this spec created on the normal user.
const seededPlayed: string[] = [];
let seededFavorite: string | null = null;
const originalGenres = new Map<string, string[] | undefined>();

/** Drive a mock-trakt test hook from the host. */
async function traktHook(path: string): Promise<void> {
  const ctx = await pwRequest.newContext();
  try {
    await ctx.post(`${MOCK_TRAKT_PUBLIC}${path}`);
  } finally {
    await ctx.dispose();
  }
}

/**
 * Set a genre on an item via fetch-modify-save. ItemUpdateController replaces the whole DTO, so we
 * edit the item's own DTO rather than posting a partial body. Mirrors discovery-my.api.spec.ts.
 */
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

/**
 * Seed a genre watch profile for the NON-admin user. Trakt personal scoring reuses the shared
 * external-candidate scorer (SeerrDiscoveryService.ScoreExternalCandidatesAsync), which returns null
 * — making GetMyTrakt report Linked=false — unless the requesting user has a watch profile whose
 * genre-preference vector is non-empty. The ffmpeg fixtures carry no genre metadata, so we assign a
 * valid genre ("Action") and mark a few items played for the normal user, then poll until the engine
 * sees the profile. Admin credentials can edit any user's playback via ?userId=.
 */
async function seedNormalUserWatchProfile(normalUserId: string): Promise<void> {
  const res = await admin.get(`/Items?IncludeItemTypes=Movie&Recursive=true&userId=${auth.userId}`);
  expect(res.ok(), `/Items status ${res.status()}`).toBeTruthy();
  const body = (await res.json()) as { Items?: Array<{ Id: string; Name?: string }> };
  const movies = (body.Items ?? [])
    .slice()
    .sort((a, b) => (a.Name ?? a.Id).localeCompare(b.Name ?? b.Id))
    .map((i) => i.Id);
  expect(movies.length, 'need a movie to build a watch profile from').toBeGreaterThan(0);

  for (const id of movies.slice(0, 3)) {
    await assignGenre(id, 'Action');
    const mark = await admin.post(`/UserPlayedItems/${id}?userId=${normalUserId}`);
    expect(mark.ok(), `mark-played ${id}: ${mark.status()}`).toBeTruthy();
    seededPlayed.push(id);
  }
  const fav = await admin.post(`/UserFavoriteItems/${movies[0]}?userId=${normalUserId}`);
  expect([200, 204]).toContain(fav.status());
  seededFavorite = movies[0];

  // The profile edits flush asynchronously; poll until the engine sees them (hard-fail on timeout so
  // a setup race surfaces as itself, not as a confusing Linked=false later).
  let lastProfile: { GenreDistribution?: Record<string, number>; WatchedMovieCount?: number } = {};
  let visible = false;
  for (let attempt = 0; attempt < 30; attempt++) {
    const wp = await admin.get(p(`Recommendations/WatchProfile/${normalUserId}`));
    if (wp.ok()) {
      lastProfile = (await wp.json()) as typeof lastProfile;
      if ((lastProfile.WatchedMovieCount ?? 0) >= 3 && (lastProfile.GenreDistribution?.Action ?? 0) >= 3) {
        visible = true;
        break;
      }
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  expect(
    visible,
    `normal-user watch profile never became visible (setup race, not a product bug): ` +
      `WatchedMovieCount=${lastProfile.WatchedMovieCount ?? 0}, Action=${lastProfile.GenreDistribution?.Action ?? 0}.`,
  ).toBe(true);
}

test.beforeAll(async () => {
  admin = await apiContext(auth);
  user = await normalUserContext(auth);

  const current = await admin.get(p('Configuration'));
  if (current.ok()) {
    const c = (await current.json()) as ConfigSnapshot;
    snapshot = {
      TraktEnabled: c.TraktEnabled,
      TraktClientId: c.TraktClientId,
      SeerrUrl: c.SeerrUrl,
      DiscoveryUserAccessEnabled: c.DiscoveryUserAccessEnabled,
      RecommendationsTaskMode: c.RecommendationsTaskMode,
    };
  }

  // Enable Trakt (pointed at the mock via container env) AND configure Seerr at the mock with
  // Recommendations active: the Trakt personal surface reuses the Seerr-backed external scorer, which
  // returns null (-> Linked=false) unless Seerr is configured. Mirrors discovery-my.api.spec.ts.
  const seed = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      DiscoveryUserAccessEnabled: true,
      RecommendationsTaskMode: 'Activate',
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: 'seerr-key',
      TraktClientId: 'mock-trakt-client-id',
      TraktClientSecret: 'mock-trakt-client-secret',
    },
  });
  expect(seed.ok(), `enable Trakt failed: ${seed.status()}`).toBeTruthy();

  // The device-flow spec links as the non-admin user; its personal recommendations only score (and
  // thus report Linked=true) once that user has a genre watch profile.
  if (user) {
    await seedNormalUserWatchProfile(auth.normalUser!.userId);
  }
});

test.afterAll(async () => {
  // Unwind the non-admin watch profile this spec created.
  for (const id of seededPlayed) {
    await admin.delete(`/UserPlayedItems/${id}?userId=${auth.normalUser?.userId}`).catch(() => undefined);
  }
  if (seededFavorite) {
    await admin.delete(`/UserFavoriteItems/${seededFavorite}?userId=${auth.normalUser?.userId}`).catch(() => undefined);
  }
  for (const [itemId, genres] of originalGenres) {
    try {
      const cur = await admin.get(`/Items/${itemId}?userId=${auth.userId}`);
      if (!cur.ok()) continue;
      const dto = (await cur.json()) as { Genres?: string[] };
      dto.Genres = genres ?? [];
      await admin.post(`/Items/${itemId}`, { headers: { 'Content-Type': 'application/json' }, data: dto }).catch(() => undefined);
    } catch {
      // best-effort restore
    }
  }

  // Restore the Configuration fields this spec mutated. GET masked the Seerr key, so re-send the mask
  // sentinel to preserve whatever key was stored before this spec ran. TraktEnabled is derived server-side
  // from the credentials, so restoring the client id (+ clearing the secret this spec set) is what resets it.
  if (Object.keys(snapshot).length > 0) {
    const restoredTraktId = snapshot.TraktClientId ?? '';
    await admin
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: {
          TraktClientId: restoredTraktId,
          // Only the mock secret existed before restore; GET never returns it. Clear it when the original
          // had no client id so the derived enabled flag matches the pre-spec state.
          TraktClientSecret: restoredTraktId === '' ? '' : API_KEY_MASK,
          SeerrUrl: snapshot.SeerrUrl ?? '',
          SeerrApiKey: API_KEY_MASK,
          DiscoveryUserAccessEnabled: snapshot.DiscoveryUserAccessEnabled ?? false,
          RecommendationsTaskMode: snapshot.RecommendationsTaskMode ?? 'Deactivate',
        },
      })
      .catch(() => undefined);
  }
  await admin.dispose();
  if (user) {
    await user.dispose();
  }
});

test.beforeEach(async () => {
  await traktHook('/reset');
});

test('personal Trakt starts unlinked, then links through the device flow', async () => {
  expect(user, 'normal user required').toBeTruthy();

  // Before linking the personal endpoint reports Linked=false (the UI shows the connect panel).
  const before = await user!.get(p('Discovery/My/Trakt'));
  expect(before.ok()).toBeTruthy();
  expect((await before.json()).Linked).toBe(false);

  // Start the device flow.
  const start = await user!.post(p('Discovery/My/Trakt/Device/Start'), { headers: { 'Content-Type': 'application/json' }, data: {} });
  expect(start.ok(), `device start: ${start.status()}`).toBeTruthy();
  const device = await start.json();
  expect(device.user_code).toBeTruthy();

  // Poll once while the mock is still pending.
  const pending = await user!.post(p('Discovery/My/Trakt/Device/Poll'), {
    headers: { 'Content-Type': 'application/json' },
    data: { DeviceCode: device.device_code },
  });
  expect(pending.ok()).toBeTruthy();
  expect((await pending.json()).Status).toBe('Pending');

  // Arm the mock to approve, poll again (respecting the per-user 5s poll throttle).
  await traktHook('/arm-linked');
  await new Promise((r) => setTimeout(r, 6000));
  const linked = await user!.post(p('Discovery/My/Trakt/Device/Poll'), {
    headers: { 'Content-Type': 'application/json' },
    data: { DeviceCode: device.device_code },
  });
  expect(linked.ok()).toBeTruthy();
  expect((await linked.json()).Status).toBe('Linked');

  // After linking the personal endpoint returns a scored result envelope.
  const after = await user!.get(p('Discovery/My/Trakt'));
  expect(after.ok()).toBeTruthy();
  expect((await after.json()).Linked).toBe(true);
});

test('trending is available without linking', async () => {
  expect(user, 'normal user required').toBeTruthy();
  const trending = await user!.get(p('Discovery/My/Trakt/Trending'));
  expect(trending.ok(), `trending: ${trending.status()}`).toBeTruthy();
});

test('disconnect clears the link', async () => {
  expect(user, 'normal user required').toBeTruthy();
  await ensureLinkedForDisconnect();
  const res = await user!.post(p('Discovery/My/Trakt/Device/Disconnect'), { headers: { 'Content-Type': 'application/json' }, data: {} });
  expect(res.ok()).toBeTruthy();
  expect((await res.json()).Success).toBe(true);

  // The link is actually gone: the personal endpoint reports Linked:false again.
  const after = await user!.get(p('Discovery/My/Trakt'));
  expect(after.ok()).toBeTruthy();
  expect((await after.json()).Linked).toBe(false);
});

/**
 * Links the normal user unless already linked: a redundant start right after
 * the link test would hit the per-minute throttle and 429 here.
 */
async function ensureLinkedForDisconnect(): Promise<void> {
  const cur = await user!.get(p('Discovery/My/Trakt'));
  expect(cur.ok()).toBeTruthy();
  if ((await cur.json()).Linked === true) {
    return;
  }
  const start = await user!.post(p('Discovery/My/Trakt/Device/Start'), { headers: { 'Content-Type': 'application/json' }, data: {} });
  expect(start.ok(), `device start: ${start.status()}`).toBeTruthy();
  const device = await start.json();
  await traktHook('/arm-linked');
  const linked = await user!.post(p('Discovery/My/Trakt/Device/Poll'), {
    headers: { 'Content-Type': 'application/json' },
    data: { DeviceCode: device.device_code },
  });
  expect(linked.ok()).toBeTruthy();
  expect((await linked.json()).Status).toBe('Linked');
}

test('Trakt endpoints are gated by the discovery-access toggle', async () => {
  expect(user, 'normal user required').toBeTruthy();

  // Turn user-level discovery access OFF. Trakt tabs are a feature of the Discovery sidebar, so every
  // /Discovery/My/Trakt* endpoint must now 403 even though Trakt itself stays configured (secret kept via
  // the mask sentinel). This is the contract the sidebar probes to hide the Trakt tabs.
  const off = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      DiscoveryUserAccessEnabled: false,
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: API_KEY_MASK,
      TraktClientId: 'mock-trakt-client-id',
      TraktClientSecret: API_KEY_MASK,
    },
  });
  expect(off.ok(), `disable discovery access failed: ${off.status()}`).toBeTruthy();

  try {
    const endpoints: Array<[string, 'get' | 'post']> = [
      ['Discovery/My/Trakt', 'get'],
      ['Discovery/My/Trakt/Trending', 'get'],
      ['Discovery/My/Trakt/Device/Start', 'post'],
      ['Discovery/My/Trakt/Device/Poll', 'post'],
      ['Discovery/My/Trakt/Device/Disconnect', 'post'],
    ];
    for (const [ep, method] of endpoints) {
      const res = method === 'get'
        ? await user!.get(p(ep))
        : await user!.post(p(ep), { headers: { 'Content-Type': 'application/json' }, data: {} });
      expect(res.status(), `${ep} should be 403 when discovery access is off`).toBe(403);
    }
  } finally {
    // Restore access so later tests (and other specs on the shared backend) see the enabled state.
    await admin
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: {
          DiscoveryUserAccessEnabled: true,
          RecommendationsTaskMode: 'Activate',
          SeerrUrl: 'http://mock-seerr:5055',
          SeerrApiKey: API_KEY_MASK,
          TraktClientId: 'mock-trakt-client-id',
          TraktClientSecret: API_KEY_MASK,
        },
      })
      .catch(() => undefined);
  }
});
