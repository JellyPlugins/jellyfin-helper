/** * Reparse-point / symlink guards in the cleanup STAGES (not the trash purge). */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask, assertPluginActive } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  regenFixtures,
  verifyCanaries,
  containerExists,
  containerFileExists,
  containerDirExists,
  containerIsSymlink,
  containerMkdir,
  containerWriteFile,
  containerRm,
  execInContainer,
} from '../setup/fs-assert.ts';

const M = '/media/Movies';

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

/** Activate exactly one cleanup stage; Deactivate everything else. UseTrash off. */
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

/** Create a symlink in the container; skip the test loudly if the FS can't. */
function makeSymlink(target: string, link: string): void {
  const res = execInContainer(`ln -s ${JSON.stringify(target)} ${JSON.stringify(link)}`);
  test.skip(res.code !== 0, 'symlink creation unsupported on this filesystem');
}

test.describe.serial('cleanup stages refuse reparse points but still delete genuine orphans', () => {
  test.beforeEach(() => {
    ensureCanariesPlanted(); // skips loudly w/o docker; guarantees a canary exists
    regenFixtures();
  });

  test.afterEach(() => {
    // Blanket containment guarantee: nothing outside /media was touched.
    expect(verifyCanaries(), 'canary files outside /media must be intact').toEqual([]);
  });

  test('trickplay: a symlinked orphan .trickplay is skipped, while the real orphan is removed', async () => {
    // A .trickplay directory that is itself a SYMLINK, with no matching video -> looks exactly like an orphan the trickplay stage would delete, but the reparse-point guard must skip it (deleting a symlinked dir could nuke a linked tree).
    const realTarget = `${M}/Link Target Dir (2099)`;
    containerMkdir(realTarget);
    containerWriteFile(`${realTarget}/keep.txt`, 'KEEP');

    containerMkdir(`${M}/Symlinked Trick (2020)`);
    makeSymlink(realTarget, `${M}/Symlinked Trick (2020)/Symlinked Trick (2020).trickplay`);

    await isolateStage({ TrickplayTaskMode: 'Activate' });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Guard held: the symlink node survives and still points at its target's data.
    expect(
      containerIsSymlink(`${M}/Symlinked Trick (2020)/Symlinked Trick (2020).trickplay`),
      'the symlinked .trickplay must survive as a link (reparse-point guard)',
    ).toBe(true);
    expect(containerFileExists(`${realTarget}/keep.txt`), 'the link target data must survive').toBe(true);

    // Straight-line case still works: the real, non-symlink orphan is removed.
    expect(
      containerExists(`${M}/Ghost Movie (2010)/Ghost Movie (2010).trickplay`),
      'the genuine orphan .trickplay must still be deleted',
    ).toBe(false);

    containerRm(`${M}/Symlinked Trick (2020)`);
    containerRm(realTarget);
    await assertPluginActive(ctx);
  });

  test('subtitle: a symlinked orphan subtitle is skipped, while the real orphan subtitle is removed', async () => {
    // Mixed Bag (2018) has a video, so the subtitle stage processes the dir. We add TWO orphan subtitles to it: one a real file, one a SYMLINK.
    const subTarget = `${M}/Sub Link Target (2099)/target.srt`;
    containerMkdir(`${M}/Sub Link Target (2099)`);
    containerWriteFile(subTarget, 'LINKED-SUB');

    makeSymlink(subTarget, `${M}/Mixed Bag (2018)/Symlinked Orphan (2001).en.srt`);

    await isolateStage({ OrphanedSubtitleTaskMode: 'Activate' });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Guard held: the symlinked subtitle survives as a link; its target survives.
    expect(
      containerIsSymlink(`${M}/Mixed Bag (2018)/Symlinked Orphan (2001).en.srt`),
      'the symlinked orphan subtitle must survive as a link',
    ).toBe(true);
    expect(containerFileExists(subTarget), 'the subtitle link target must survive').toBe(true);

    // Straight-line case still works: the genuine (real-file) orphan subtitle is removed,
    // and the dir's own valid video + matching sub survive.
    expect(
      containerFileExists(`${M}/Mixed Bag (2018)/Ghost Subtitle (2001).en.srt`),
      'the genuine orphan subtitle must still be deleted',
    ).toBe(false);
    expect(containerFileExists(`${M}/Mixed Bag (2018)/Mixed Bag (2018).mkv`)).toBe(true);
    expect(containerFileExists(`${M}/Mixed Bag (2018)/Mixed Bag (2018).en.srt`)).toBe(true);

    containerRm(`${M}/Sub Link Target (2099)`);
    await assertPluginActive(ctx);
  });

  test('empty-folder: an unresolved symlinked subtree protects the folder, while a genuine orphan is removed', async () => {
    // A folder that would look empty/orphaned (no video anywhere in its own tree) BUT contains a symlinked subdirectory.
    const linkTarget = '/config/jfh-empty-link-target';
    containerWriteFile(`${linkTarget}/data.txt`, 'TARGET');
    test.skip(
      !containerFileExists(`${linkTarget}/data.txt`),
      '/config/jfh-empty-link-target not writable in this environment - cannot seed the link target',
    );

    containerMkdir(`${M}/Unresolved Folder (2020)`);
    // A non-video file so it looks like an empty/metadata-only orphan...
    containerWriteFile(`${M}/Unresolved Folder (2020)/readme.txt`, 'x');
    // ...plus a symlinked subdirectory that makes its status unprovable.
    makeSymlink(linkTarget, `${M}/Unresolved Folder (2020)/linked`);

    await isolateStage({ EmptyMediaFolderTaskMode: 'Activate' });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Guard held: the folder with the unresolved link survives untouched.
    expect(
      containerDirExists(`${M}/Unresolved Folder (2020)`),
      'a folder with an unresolved symlinked subtree must be kept (orphan status unproven)',
    ).toBe(true);
    expect(
      containerIsSymlink(`${M}/Unresolved Folder (2020)/linked`),
      'the symlinked subdir must survive as a link',
    ).toBe(true);
    expect(containerFileExists(`${linkTarget}/data.txt`), 'the link target data must survive').toBe(true);

    // Straight-line case still works: the genuine empty/orphan folder is removed.
    expect(
      containerExists(`${M}/Lonely Sub (2012)`),
      'the genuine orphan folder must still be deleted',
    ).toBe(false);

    containerRm(`${M}/Unresolved Folder (2020)`);
    containerRm(linkTarget);
    await assertPluginActive(ctx);
  });
});
