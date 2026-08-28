/** * Recommendations -> playlist lifecycle. The named "Deactivate purges playlists" * and "sync-off purges" branches are only status-checked today, so a no-op purge * passes. */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask, sleep } from '../setup/api-client.ts';
import { hasDocker, execInContainer } from '../setup/fs-assert.ts';

const PLAYLIST_PREFIX = '🎬 Recommended for';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

async function putConfig(body: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: body,
  });
  expect(res.ok(), `config update failed: ${res.status()}`).toBeTruthy();
}

/** Count Jellyfin playlists whose name starts with the managed prefix. */
async function managedPlaylistCount(): Promise<number> {
  const res = await ctx.get('/Items?IncludeItemTypes=Playlist&Recursive=true');
  // Fail loudly on a transient 401/500 rather than mapping it to 0 - a swallowed
  // error here would read as "no playlists" and make the purge assertion pass falsely.
  expect(res.ok(), `playlist query failed: ${res.status()}`).toBeTruthy();
  const body = (await res.json()) as { Items?: Array<{ Name?: string }> };
  return (body.Items ?? []).filter((i) => (i.Name ?? '').startsWith(PLAYLIST_PREFIX)).length;
}

test.describe.serial('recommendations playlist create → purge', () => {
  test.beforeAll(async () => {
    // Start from a clean slate: a prior run (or the earlier full suite against a persisted /config) can leave managed playlists on disk, which would make the create/purge deltas ambiguous.
    await putConfig({ RecommendationsTaskMode: 'Deactivate', SyncRecommendationsToPlaylist: false });
    let baseline = await managedPlaylistCount();
    for (let i = 0; i < 10 && baseline > 0; i++) {
      await runCleanupTask(ctx);
      await sleep(1500);
      baseline = await managedPlaylistCount();
    }
    expect(baseline, 'beforeAll could not purge pre-existing managed playlists').toBe(0);
  });

  test('Activate+sync creates playlists; Deactivate purges them', async () => {
    // Baseline is clean after beforeAll (asserted there); re-check defensively.
    expect(await managedPlaylistCount(), 'baseline should have no managed playlists').toBe(0);

    await putConfig({
      RecommendationsTaskMode: 'Activate',
      SyncRecommendationsToPlaylist: true,
      // Isolate: no filesystem stages.
      TrickplayTaskMode: 'Deactivate',
      EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate',
      LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate',
    });
    let result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    await sleep(2000); // let Jellyfin persist the playlist items

    const created = await managedPlaylistCount();
    test.skip(created === 0, 'engine produced no recommendation playlists on this library - purge assertion would be vacuous');

    // Deactivate -> the purge branch must remove every managed playlist.
    await putConfig({ RecommendationsTaskMode: 'Deactivate' });
    result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Poll: playlist deletion + library refresh can lag behind task completion.
    let remaining = created;
    for (let i = 0; i < 10 && remaining > 0; i++) {
      await sleep(1500);
      remaining = await managedPlaylistCount();
    }
    expect(remaining, 'Deactivate must purge managed playlists').toBe(0);
  });

  test('Activate writes the recommendation cache file; DryRun does not', async () => {
    test.skip(!hasDocker(), 'docker exec unavailable - cannot inspect the cache file');
    const cache = '/config/data/jellyfin-helper-recommendations-latest.json';

    // Activate -> cache present and non-empty.
    execInContainer(`rm -f ${cache}`);
    await putConfig({
      RecommendationsTaskMode: 'Activate',
      SyncRecommendationsToPlaylist: false,
      TrickplayTaskMode: 'Deactivate', EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate', LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate',
    });
    let result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);
    expect(execInContainer(`test -s ${cache}`).code, 'Activate should persist a non-empty cache').toBe(0);

    // DryRun -> cache must NOT be (re)written.
    execInContainer(`rm -f ${cache}`);
    await putConfig({ RecommendationsTaskMode: 'DryRun' });
    result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);
    expect(execInContainer(`test -e ${cache}`).code, 'DryRun must not persist the cache').not.toBe(0);
  });
});
