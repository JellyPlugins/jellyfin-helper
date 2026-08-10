/**
 * Adversarial cleanup tests - prove the cleanup stages never delete data OUTSIDE
 * the media library, honour excluded libraries (incl. the trailing trash purge),
 * and document the unlisted-codec false-orphan edge.
 *
 * Requires the container FS; skips loudly when Docker is unreachable.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask, assertPluginActive } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  regenFixtures,
  verifyCanaries,
  containerExists,
  containerFileExists,
  containerDirExists,
  containerMkdir,
  containerWriteFile,
  containerRm,
  execInContainer,
  containerTimestamp,
} from '../setup/fs-assert.ts';

const M = '/media/Movies';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { ExcludedLibraries: '' },
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

async function isolateStage(active: Record<string, unknown>) {
  await putConfig({
    TrickplayTaskMode: 'Deactivate', EmptyMediaFolderTaskMode: 'Deactivate',
    OrphanedSubtitleTaskMode: 'Deactivate', LinkRepairTaskMode: 'Deactivate',
    SeerrCleanupTaskMode: 'Deactivate', RecommendationsTaskMode: 'Deactivate',
    UseTrash: false, OrphanMinAgeDays: 0, ExcludedLibraries: '', ...active,
  });
}

test.describe.serial('cleanup never escapes the media library', () => {
  test.beforeEach(() => {
    ensureCanariesPlanted(); // skips loudly w/o docker; guarantees a canary exists
    regenFixtures();
    // Seed under /config (the plugin's own data mount) rather than /srv: /config is
    // writable by the non-root container UID in CI, whereas /srv lives on the
    // container's root-owned rootfs and the seed silently fails there (skipping the
    // test). /config is still OUTSIDE the media library, so the symlink-escape guard
    // is exercised for real. Distinct subdir from the /config/jfh-canary canary.
    containerWriteFile('/config/jfh-external/secret.mkv', 'EXTERNAL-DATA');
  });

  test.afterEach(() => {
    containerRm(`${M}/Symlink Trap (2020)`);
    expect(verifyCanaries(), 'nothing outside /media may be deleted').toEqual([]);
  });

  test('an orphan-looking folder containing a symlink out of the library does not delete the target', async () => {
    // A folder whose only "content" is a symlink to an external dir. Whatever the
    // cleanup decides about the folder, the EXTERNAL target's data must survive.
    // (The external target is seeded once in beforeEach.)
    // On CI the container runs as a non-root UID; /config is the writable data
    // mount (unlike /srv on the root-owned rootfs). If the external seed still
    // didn't land, skip loudly rather than assert on a phantom file (which would
    // look like a data-loss failure when nothing was ever created).
    test.skip(
      !containerFileExists('/config/jfh-external/secret.mkv'),
      '/config/jfh-external not writable in this environment - cannot seed the external target',
    );

    containerMkdir(`${M}/Symlink Trap (2020)`);
    const linkRes = execInContainer(`ln -s /config/jfh-external "${M}/Symlink Trap (2020)/external"`);
    test.skip(linkRes.code !== 0, 'symlink creation unsupported on this filesystem');

    await isolateStage({
      TrickplayTaskMode: 'Activate', EmptyMediaFolderTaskMode: 'Activate',
      OrphanedSubtitleTaskMode: 'Activate',
    });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // The external target and its data MUST still exist (no follow-the-symlink delete).
    expect(containerFileExists('/config/jfh-external/secret.mkv'), 'external data must survive').toBe(true);
    await assertPluginActive(ctx);
  });

  test('excluded library is fully hands-off: orphan AND its trash both survive', async () => {
    // Exclude the Movies library, seed an orphan + an expired trash entry in it,
    // then run a full Activate cleanup (incl. the trailing trash purge).
    const trash = `${M}/.jellyfin-trash`;
    containerRm(trash);
    const oldTs = containerTimestamp(90);
    containerMkdir(`${trash}/${oldTs}_ExcludedTrash`);
    containerWriteFile(`${trash}/${oldTs}_ExcludedTrash/x.txt`, 'keep');

    await isolateStage({
      ExcludedLibraries: 'Movies',
      TrickplayTaskMode: 'Activate', EmptyMediaFolderTaskMode: 'Activate',
      OrphanedSubtitleTaskMode: 'Activate',
      UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 7,
    });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Orphan in the excluded library is untouched...
    expect(containerExists(`${M}/Ghost Movie (2010)/Ghost Movie (2010).trickplay`)).toBe(true);
    // ...and its expired trash entry is NOT purged (the fix for the unfiltered-purge bug).
    expect(containerDirExists(`${trash}/${oldTs}_ExcludedTrash`), 'excluded-library trash must not be purged').toBe(true);

    containerRm(trash);
    await assertPluginActive(ctx);
  });

  test('emoji / very-long orphan folder name goes through Activate+UseTrash without 500', async () => {
    const emoji = `${M}/😀 Orphan 🎬 ${'x'.repeat(180)}`;
    containerMkdir(emoji);
    containerWriteFile(`${emoji}/note.txt`, 'x'); // non-video → looks orphaned
    await isolateStage({
      EmptyMediaFolderTaskMode: 'Activate',
      UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30,
    });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    expect(verifyCanaries()).toEqual([]);
    containerRm(`${M}/.jellyfin-trash`);
    await assertPluginActive(ctx);
  });
});
