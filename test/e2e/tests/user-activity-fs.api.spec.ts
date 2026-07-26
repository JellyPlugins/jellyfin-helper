/**
 * Behavioral coverage for UserActivity ("watched" / recently-played). Today only
 * the 503-guard, empty-GUID 400, and unknown-user 404 are tested; nothing seeds
 * real playback. Here we prove the DATA: mark a specific item PLAYED via Jellyfin's
 * own API, rebuild the activity cache (the HelperCleanup task builds it from
 * IUserDataManager), and assert that exact item surfaces as watched — in
 * UserActivity/Latest and in UserActivity/User/{userId} — with a matching play count.
 *
 * Contract notes (verified against the running stack):
 *   - Both routes require an elevated token and return 503 when
 *     RecommendationsTaskMode == Deactivate; we set Activate.
 *   - Latest reads a disk cache; if the task hasn't run it 503s ("not yet
 *     available"), so we run HelperCleanup first.
 *   - Responses are PascalCase (Items[].ItemId/ItemName/TotalPlayCount, ...).
 *
 * Requires the container FS gate only indirectly (it drives the real Jellyfin API);
 * it needs no docker-exec, so it runs wherever the API is reachable.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask } from '../setup/api-client.ts';

interface UserItemActivity { UserId: string; PlayCount: number; Played: boolean }
interface ActivitySummary {
  ItemId: string;
  ItemName: string;
  ItemType: string;
  TotalPlayCount: number;
  UniqueViewers: number;
  MostRecentWatch: string | null;
  UserActivities: UserItemActivity[];
}
interface ActivityResult {
  GeneratedAt: string;
  TotalItemsWithActivity: number;
  TotalUsersAnalyzed: number;
  TotalPlayCount: number;
  Items: ActivitySummary[];
}

let ctx: APIRequestContext;
let auth: ReturnType<typeof loadAuth>;

test.beforeAll(async () => {
  auth = loadAuth();
  ctx = await apiContext(auth);
});
test.afterAll(async () => {
  // Restore a benign task mode so later specs aren't surprised by Activate.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { RecommendationsTaskMode: 'DryRun' },
  }).catch(() => undefined);
  await ctx.dispose();
});

/** Pick one Movie item id from the scanned library. */
async function firstMovieItem(): Promise<{ id: string; name: string }> {
  const res = await ctx.get(`/Items?IncludeItemTypes=Movie&Recursive=true&Limit=1&userId=${auth.userId}`);
  expect(res.ok(), `/Items status ${res.status()}`).toBeTruthy();
  const body = (await res.json()) as { Items?: Array<{ Id: string; Name: string }> };
  const item = body.Items?.[0];
  expect(item, 'the scanned library must have at least one Movie').toBeTruthy();
  return { id: item!.Id, name: item!.Name };
}

test.describe.serial('UserActivity reflects real playback', () => {
  let played: { id: string; name: string };

  test.beforeAll(async () => {
    // Enable the feature so the endpoints don't 503 on the mode gate.
    const cfg = await ctx.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { RecommendationsTaskMode: 'Activate' },
    });
    expect(cfg.ok(), `enable RecommendationsTaskMode failed: ${cfg.status()}`).toBeTruthy();
    // Mark a specific movie as played for the admin user (modern Jellyfin route).
    played = await firstMovieItem();
    const mark = await ctx.post(`/UserPlayedItems/${played.id}?userId=${auth.userId}`);
    expect(mark.ok(), `mark-played status ${mark.status()}`).toBeTruthy();
    // Build the activity cache from Jellyfin's user-data.
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
  });

  test('the played item surfaces in UserActivity/Latest as watched', async () => {
    const res = await ctx.get(p('UserActivity/Latest'));
    expect(res.ok(), `UserActivity/Latest status ${res.status()} (503 = cache not built)`).toBeTruthy();
    const body = (await res.json()) as ActivityResult;

    expect(body.TotalItemsWithActivity, 'at least the one played item').toBeGreaterThanOrEqual(1);
    const mine = body.Items.find((i) => i.ItemId.replace(/-/g, '') === played.id.replace(/-/g, ''));
    expect(mine, `played item "${played.name}" must appear in activity`).toBeTruthy();
    expect(mine!.TotalPlayCount, 'play count reflects the mark-played').toBeGreaterThanOrEqual(1);
    expect(mine!.UniqueViewers, 'the admin user counts as a viewer').toBeGreaterThanOrEqual(1);
    expect(mine!.MostRecentWatch, 'a watch timestamp is recorded').toBeTruthy();
    // The per-user row for the admin is present and marked played.
    const adminRow = mine!.UserActivities.find((u) => u.UserId.replace(/-/g, '') === auth.userId.replace(/-/g, ''));
    expect(adminRow, 'the admin user has an activity row').toBeTruthy();
    expect(adminRow!.Played).toBe(true);
  });

  test('UserActivity/User/{userId} returns the admin\'s watched item', async () => {
    const res = await ctx.get(p(`UserActivity/User/${auth.userId}`));
    expect(res.ok(), `UserActivity/User status ${res.status()}`).toBeTruthy();
    const list = (await res.json()) as ActivitySummary[];
    expect(Array.isArray(list)).toBe(true);
    const mine = list.find((i) => i.ItemId.replace(/-/g, '') === played.id.replace(/-/g, ''));
    expect(mine, 'the played item must appear for this user').toBeTruthy();
    expect(mine!.TotalPlayCount).toBeGreaterThanOrEqual(1);
  });

  test('empty GUID is 400 and an unknown user is 404 (guards intact)', async () => {
    const empty = await ctx.get(p('UserActivity/User/00000000-0000-0000-0000-000000000000'));
    expect(empty.status(), 'empty GUID rejected').toBe(400);
    const unknown = await ctx.get(p('UserActivity/User/11111111-1111-1111-1111-111111111111'));
    expect([200, 404], 'unknown user is 404 or an empty 200').toContain(unknown.status());
    if (unknown.status() === 200) {
      expect((await unknown.json()) as unknown[], 'unknown user yields no activity').toEqual([]);
    }
  });
});
