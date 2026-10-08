/**
 * Shared watch-profile seeding for the Trakt e2e specs.
 *
 * Trakt personal recommendations reuse the Seerr-backed external-candidate scorer
 * (SeerrDiscoveryService.ScoreExternalCandidatesAsync), which returns null unless the requesting user has a
 * watch profile with a non-empty genre-preference vector. Link state (GetMyTrakt's `Linked`) only reflects
 * whether a token exists, so a linked user with no profile legitimately yields an empty result. The
 * official-plugin spec needs this seed before asserting a populated grid.
 *
 * The ffmpeg fixtures carry no genre metadata, so seeding assigns a valid genre ("Action") to a few movies
 * and marks them played/favorited for the NON-admin user, then polls until the engine sees the profile.
 * Admin credentials edit any user's playback via ?userId=. Teardown unwinds every mutation so later specs
 * (workers: 1 shares one backend) do not inherit the profile.
 */
import { expect, type APIRequestContext } from '@playwright/test';

/** The genre assigned to the seeded movies; the profile is considered visible once the engine counts >= 3. */
const SEED_GENRE = 'Action';
const SEED_COUNT = 3;

/** Records every mutation a seed made, so the caller can unwind it verbatim in afterAll. */
export interface WatchProfileSeed {
  readonly played: string[];
  readonly favorite: string | null;
  readonly originalGenres: Map<string, string[] | undefined>;
}

/**
 * Seed a genre watch profile for the non-admin user and return the handle used to unwind it. `adminUserId`
 * is the admin's own id (used to read item DTOs with a user context); `normalUserId` is the non-admin whose
 * profile is being built. Hard-fails on a setup race so it surfaces as itself, not as a confusing empty grid.
 */
export async function seedNormalUserWatchProfile(
  admin: APIRequestContext,
  adminUserId: string,
  normalUserId: string,
): Promise<WatchProfileSeed> {
  const originalGenres = new Map<string, string[] | undefined>();

  const res = await admin.get(`/Items?IncludeItemTypes=Movie&Recursive=true&userId=${adminUserId}`);
  expect(res.ok(), `/Items status ${res.status()}`).toBeTruthy();
  const body = (await res.json()) as { Items?: Array<{ Id: string; Name?: string }> };
  const movies = (body.Items ?? [])
    .slice()
    .sort((a, b) => (a.Name ?? a.Id).localeCompare(b.Name ?? b.Id))
    .map((i) => i.Id);
  expect(movies.length, 'need a movie to build a watch profile from').toBeGreaterThan(0);

  // The seeded movies are independent of each other, so assign + mark them concurrently.
  const played = await Promise.all(
    movies.slice(0, SEED_COUNT).map(async (id) => {
      await assignGenre(admin, adminUserId, originalGenres, id, SEED_GENRE);
      const mark = await admin.post(`/UserPlayedItems/${id}?userId=${normalUserId}`);
      expect(mark.ok(), `mark-played ${id}: ${mark.status()}`).toBeTruthy();
      return id;
    }),
  );
  const fav = await admin.post(`/UserFavoriteItems/${movies[0]}?userId=${normalUserId}`);
  expect([200, 204]).toContain(fav.status());
  const favorite = movies[0];

  // The profile edits flush asynchronously; poll until the engine sees them (hard-fail on timeout so a
  // setup race surfaces as itself, not as a confusing empty-grid later).
  let lastProfile: { GenreDistribution?: Record<string, number>; WatchedMovieCount?: number } = {};
  let visible = false;
  for (let attempt = 0; attempt < 30; attempt++) {
    const wp = await admin.get(`/JellyfinHelper/Recommendations/WatchProfile/${normalUserId}`);
    if (wp.ok()) {
      lastProfile = (await wp.json()) as typeof lastProfile;
      if ((lastProfile.WatchedMovieCount ?? 0) >= SEED_COUNT && (lastProfile.GenreDistribution?.[SEED_GENRE] ?? 0) >= SEED_COUNT) {
        visible = true;
        break;
      }
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  expect(
    visible,
    `normal-user watch profile never became visible (setup race, not a product bug): ` +
      `WatchedMovieCount=${lastProfile.WatchedMovieCount ?? 0}, ${SEED_GENRE}=${lastProfile.GenreDistribution?.[SEED_GENRE] ?? 0}.`,
  ).toBe(true);

  return { played, favorite, originalGenres };
}

/** Unwind every mutation a seed made. Best-effort: later specs must not inherit the profile. */
export async function clearNormalUserWatchProfile(
  admin: APIRequestContext,
  adminUserId: string,
  normalUserId: string,
  seed: WatchProfileSeed,
): Promise<void> {
  // Each unwind is independent (distinct items), so run them concurrently. Best-effort: later specs
  // must not inherit the profile.
  await Promise.all(
    seed.played.map((id) => admin.delete(`/UserPlayedItems/${id}?userId=${normalUserId}`).catch(() => undefined)),
  );
  if (seed.favorite) {
    await admin.delete(`/UserFavoriteItems/${seed.favorite}?userId=${normalUserId}`).catch(() => undefined);
  }
  await Promise.all(
    [...seed.originalGenres].map(async ([itemId, genres]) => {
      try {
        const cur = await admin.get(`/Items/${itemId}?userId=${adminUserId}`);
        if (!cur.ok()) return;
        const dto = (await cur.json()) as { Genres?: string[] };
        dto.Genres = genres ?? [];
        await admin.post(`/Items/${itemId}`, { headers: { 'Content-Type': 'application/json' }, data: dto }).catch(() => undefined);
      } catch {
        // best-effort restore
      }
    }),
  );
}

/**
 * Set a single genre on an item via fetch-modify-save. ItemUpdateController replaces the whole DTO, so we
 * edit the item's own DTO rather than posting a partial body, and remember the original genres for teardown.
 */
async function assignGenre(
  admin: APIRequestContext,
  adminUserId: string,
  originalGenres: Map<string, string[] | undefined>,
  itemId: string,
  genre: string,
): Promise<void> {
  const get = await admin.get(`/Items/${itemId}?userId=${adminUserId}`);
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
