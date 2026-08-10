/**
 * Behavioural filesystem tests for the cleanup stages - the "does it delete the
 * RIGHT thing and keep the WRONG thing" proof the counter-only tests can't give.
 *
 * Every current cleanup assertion elsewhere leans on the shared
 * CleanupStatistics.TotalItemsDeleted counter (which any stage can bump) or a
 * task-Completed status. Here we ISOLATE one stage (Activate it, Deactivate the
 * rest) and assert the actual on-disk outcome via `docker exec`, so a regression
 * that deletes everything - or nothing - is caught.
 *
 * Requires the container FS (docker exec). When Docker is unreachable from the
 * test host these skip LOUDLY rather than pass vacuously.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask, assertPluginActive } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  regenFixtures,
  containerExists,
  containerDirExists,
  containerFileExists,
  verifyCanaries,
} from '../setup/fs-assert.ts';

const M = '/media/Movies';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  // Restore a benign shared state so later specs (e.g. smoke) don't inherit a
  // Deactivated recommendations/activity backend from this destructive suite.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { RecommendationsTaskMode: 'DryRun', UseTrash: false, OrphanMinAgeDays: 30 },
  }).catch(() => undefined);
  await ctx.dispose();
});

async function putConfig(body: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: body,
  });
  expect(res.ok(), `config update failed: ${res.status()}`).toBeTruthy();
}

/** Activate exactly one cleanup stage; Deactivate everything else. */
async function isolateStage(active: Record<string, unknown>) {
  await putConfig({
    TrickplayTaskMode: 'Deactivate',
    EmptyMediaFolderTaskMode: 'Deactivate',
    OrphanedSubtitleTaskMode: 'Deactivate',
    LinkRepairTaskMode: 'Deactivate',
    SeerrCleanupTaskMode: 'Deactivate',
    RecommendationsTaskMode: 'Deactivate',
    UseTrash: false,
    OrphanMinAgeDays: 0,
    ...active,
  });
}

// These run serially and each regenerates the (destructive-run-consuming)
// fixtures first, so ordering can't make one test poison the next.
test.describe.serial('cleanup deletes the right thing, keeps the wrong thing', () => {
  test.beforeEach(() => {
    ensureCanariesPlanted(); // skips loudly w/o docker; guarantees a canary exists
    regenFixtures();
  });

  test.afterEach(() => {
    // No misuse here, but assert the library-external canaries are pristine as a
    // blanket guarantee that cleanup never escaped /media.
    expect(verifyCanaries(), 'canary files outside /media must be intact').toEqual([]);
  });

  test('trickplay: orphan removed, valid (video-backed) trickplay survives', async () => {
    await isolateStage({ TrickplayTaskMode: 'Activate' });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Orphan (no matching video) → gone.
    expect(containerExists(`${M}/Ghost Movie (2010)/Ghost Movie (2010).trickplay`)).toBe(false);
    // Valid (matching video present) → survives, with its tile intact.
    expect(containerDirExists(`${M}/Valid Trick (2020)/Valid Trick (2020).trickplay`)).toBe(true);
    expect(containerFileExists(`${M}/Valid Trick (2020)/Valid Trick (2020).mkv`)).toBe(true);
    await assertPluginActive(ctx);
  });

  test('trickplay: non-video same-basename companion does not save the folder', async () => {
    await isolateStage({ TrickplayTaskMode: 'Activate' });
    await runCleanupTask(ctx);
    // "Sub Only" has a same-named .srt but NO video → the .trickplay is still orphaned.
    expect(containerExists(`${M}/Sub Only (2020)/Sub Only (2020).trickplay`)).toBe(false);
    // The subtitle itself is untouched (trickplay stage doesn't handle subtitles).
    expect(containerFileExists(`${M}/Sub Only (2020)/Sub Only (2020).en.srt`)).toBe(true);
  });

  test('subtitle: genuine orphan removed, valid + matching subs survive', async () => {
    await isolateStage({ OrphanedSubtitleTaskMode: 'Activate' });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Orphan .srt in a dir that DOES contain a video → deleted (the real delete path).
    expect(containerFileExists(`${M}/Mixed Bag (2018)/Ghost Subtitle (2001).en.srt`)).toBe(false);
    // The dir's own video + its matching sub survive.
    expect(containerFileExists(`${M}/Mixed Bag (2018)/Mixed Bag (2018).mkv`)).toBe(true);
    expect(containerFileExists(`${M}/Mixed Bag (2018)/Mixed Bag (2018).en.srt`)).toBe(true);
    // A different valid subtitle elsewhere also survives.
    expect(containerFileExists(`${M}/Copper Canyon (2015)/Copper Canyon (2015).en.srt`)).toBe(true);
  });

  test('subtitle: video-less dir is skipped entirely (all its subs survive)', async () => {
    // Deactivate the empty-folder stage too, so survival is attributable to the
    // subtitle stage's "no video in dir → skip" rule, not to nothing running.
    await isolateStage({ OrphanedSubtitleTaskMode: 'Activate' });
    await runCleanupTask(ctx);
    expect(containerFileExists(`${M}/Lonely Sub (2012)/Lonely Sub (2012).en.srt`)).toBe(true);
  });

  test('subtitle: multi-language subs survive, non-language ".DTS" orphan removed', async () => {
    await isolateStage({ OrphanedSubtitleTaskMode: 'Activate' });
    await runCleanupTask(ctx);
    const base = `${M}/Polyglot (2016)/Polyglot (2016)`;
    for (const suf of ['en.srt', 'es.forced.srt', 'de.sdh.srt', 'zh-Hans.srt', 'pt-BR.ass']) {
      expect(containerFileExists(`${base}.${suf}`), `${suf} should survive`).toBe(true);
    }
    // ".DTS" is not a language/flag → treated as orphan and removed.
    expect(containerFileExists(`${base}.DTS.srt`)).toBe(false);
    // False-orphan title ending in a language token → kept by the fallback.
    expect(
      containerFileExists(`${M}/Interview with the en (2004)/Interview with the en (2004).srt`),
    ).toBe(true);
  });

  test('empty-folder: orphan dir removed, video-in-tree protects whole folder', async () => {
    await isolateStage({ EmptyMediaFolderTaskMode: 'Activate' });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Lonely Sub: has a file (subtitle) but no video anywhere → orphaned → removed.
    expect(containerExists(`${M}/Lonely Sub (2012)`)).toBe(false);
    // Mixed Keep: a nested video protects the whole top-level folder.
    expect(containerDirExists(`${M}/Mixed Keep (2016)`)).toBe(true);
    expect(containerFileExists(`${M}/Mixed Keep (2016)/Season 01/Mixed Keep S01E01.mkv`)).toBe(true);
  });

  test('empty-folder: metadata-only and audio-only folders survive', async () => {
    await isolateStage({ EmptyMediaFolderTaskMode: 'Activate' });
    await runCleanupTask(ctx);
    // Wanted placeholder (poster + nfo, no media) → kept.
    expect(containerDirExists(`${M}/Wanted Placeholder (2027)`)).toBe(true);
    // Audio-only folder in a video library → kept (music guard).
    expect(containerFileExists(`${M}/Soundtrack Only (2016)/track.mp3`)).toBe(true);
  });

  test('empty-folder: broken .strm-only folder survives (strm classified as video)', async () => {
    // LinkRepair Deactivated so only the empty-folder stage runs.
    await isolateStage({ EmptyMediaFolderTaskMode: 'Activate' });
    await runCleanupTask(ctx);
    expect(containerFileExists(`${M}/Broken Link (2020)/Broken Link (2020).strm`)).toBe(true);
  });

  test('DryRun deletes nothing on disk (all orphans survive, no trash created)', async () => {
    await putConfig({
      TrickplayTaskMode: 'DryRun',
      EmptyMediaFolderTaskMode: 'DryRun',
      OrphanedSubtitleTaskMode: 'DryRun',
      LinkRepairTaskMode: 'DryRun',
      SeerrCleanupTaskMode: 'Deactivate',
      RecommendationsTaskMode: 'Deactivate',
      UseTrash: false,
      OrphanMinAgeDays: 0,
    });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Every orphan fixture is still on disk after a DryRun.
    expect(containerExists(`${M}/Ghost Movie (2010)/Ghost Movie (2010).trickplay`)).toBe(true);
    expect(containerFileExists(`${M}/Mixed Bag (2018)/Ghost Subtitle (2001).en.srt`)).toBe(true);
    expect(containerDirExists(`${M}/Lonely Sub (2012)`)).toBe(true);
    // No trash directory was created by a DryRun.
    expect(containerExists(`${M}/.jellyfin-trash`)).toBe(false);
  });

  test('permanent delete removes orphans without creating a trash folder; valid media survives', async () => {
    await putConfig({
      TrickplayTaskMode: 'Activate',
      EmptyMediaFolderTaskMode: 'Activate',
      OrphanedSubtitleTaskMode: 'Activate',
      LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate',
      RecommendationsTaskMode: 'Deactivate',
      UseTrash: false,
      OrphanMinAgeDays: 0,
    });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    expect(containerExists(`${M}/Ghost Movie (2010)/Ghost Movie (2010).trickplay`)).toBe(false);
    expect(containerExists(`${M}/Lonely Sub (2012)`)).toBe(false);
    // UseTrash:false → nothing was routed to trash.
    expect(containerExists(`${M}/.jellyfin-trash`)).toBe(false);
    // Valid media untouched.
    expect(containerFileExists(`${M}/Aurora Skies (2019)/Aurora Skies (2019).mkv`)).toBe(true);
    expect(containerFileExists(`${M}/Copper Canyon (2015)/Copper Canyon (2015).en.srt`)).toBe(true);
  });

  test('age gating keeps a too-new orphan, then removes it once the gate is 0', async () => {
    // High min-age → fresh orphan is too new to delete → survives.
    await isolateStage({ TrickplayTaskMode: 'Activate', OrphanMinAgeDays: 3650 });
    await runCleanupTask(ctx);
    expect(containerExists(`${M}/Ghost Movie (2010)/Ghost Movie (2010).trickplay`)).toBe(true);

    // Drop the gate to 0 → now eligible → removed.
    await isolateStage({ TrickplayTaskMode: 'Activate', OrphanMinAgeDays: 0 });
    await runCleanupTask(ctx);
    expect(containerExists(`${M}/Ghost Movie (2010)/Ghost Movie (2010).trickplay`)).toBe(false);
  });
});
