/**
 * Behavioural filesystem tests for the trash bin: move-to-trash and date-based
 * retention purge - both entirely unverified by the shape/counter tests today.
 *
 * Requires the container FS (docker exec); skips loudly when unavailable.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  regenFixtures,
  containerExists,
  containerDirExists,
  containerLs,
  containerFindCount,
  containerMkdir,
  containerWriteFile,
  containerRm,
  containerTimestamp,
  containerIsSymlink,
  execInContainer,
  verifyCanaries,
  q,
} from '../setup/fs-assert.ts';

const M = '/media/Movies';
const TRASH = `${M}/.jellyfin-trash`;

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
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

test.describe.serial('trash move + retention purge', () => {
  test.beforeEach(() => {
    ensureCanariesPlanted(); // skips loudly w/o docker; guarantees a canary exists
    regenFixtures();
    containerRm(TRASH); // start each test from a clean trash
  });

  test.afterEach(() => {
    expect(verifyCanaries(), 'canary files outside /media must be intact').toEqual([]);
  });

  test('UseTrash moves the orphan INTO trash (source gone, timestamped copy present)', async () => {
    await putConfig({
      TrickplayTaskMode: 'Activate',
      EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate',
      LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate',
      RecommendationsTaskMode: 'Deactivate',
      UseTrash: true,
      TrashFolderPath: '.jellyfin-trash',
      TrashRetentionDays: 30,
      OrphanMinAgeDays: 0,
    });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Source removed from the library path.
    expect(containerExists(`${M}/Ghost Movie (2010)/Ghost Movie (2010).trickplay`)).toBe(false);
    // A timestamped copy appears in trash, contents intact.
    const entries = containerLs(TRASH);
    const moved = entries.filter((e) => /^\d{8}-\d{6}_Ghost Movie \(2010\)\.trickplay$/.test(e));
    expect(moved.length, `trash entries: ${entries.join(', ')}`).toBe(1);
    expect(containerFindCount(`${TRASH}/${moved[0]}`)).toBeGreaterThan(0);
    // Valid media not swept into trash.
    expect(containerExists(`${TRASH}/Copper Canyon (2015)`)).toBe(false);
  });

  test('retention purges an expired trash entry and keeps a fresh one', async () => {
    await putConfig({
      TrickplayTaskMode: 'Deactivate',
      EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate',
      LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate',
      RecommendationsTaskMode: 'Deactivate',
      UseTrash: true,
      TrashFolderPath: '.jellyfin-trash',
      TrashRetentionDays: 7,
      OrphanMinAgeDays: 0,
    });
    // Seed two trash entries with names whose TIMESTAMP encodes their age (purge
    // reads the name, not real mtimes - deterministic, not flaky).
    const oldTs = containerTimestamp(30);
    const freshTs = containerTimestamp(0);
    containerMkdir(`${TRASH}/${oldTs}_OldOrphan`);
    containerWriteFile(`${TRASH}/${oldTs}_OldOrphan/x.txt`, 'old');
    containerMkdir(`${TRASH}/${freshTs}_NewOrphan`);
    containerWriteFile(`${TRASH}/${freshTs}_NewOrphan/y.txt`, 'new');

    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    expect(containerDirExists(`${TRASH}/${oldTs}_OldOrphan`), 'expired entry should be purged').toBe(false);
    expect(containerDirExists(`${TRASH}/${freshTs}_NewOrphan`), 'fresh entry should survive').toBe(true);
  });

  test('retentionDays<=0 disables purge; enabling it (1) then purges the expired entry', async () => {
    const oldTs = containerTimestamp(30);

    // Retention disabled → expired entry preserved.
    await putConfig({
      TrickplayTaskMode: 'Deactivate', EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate', LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate', RecommendationsTaskMode: 'Deactivate',
      UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 0, OrphanMinAgeDays: 0,
    });
    containerMkdir(`${TRASH}/${oldTs}_Persist`);
    containerWriteFile(`${TRASH}/${oldTs}_Persist/x.txt`, 'keep');
    await runCleanupTask(ctx);
    expect(containerDirExists(`${TRASH}/${oldTs}_Persist`), 'retention=0 must not purge').toBe(true);

    // Enable retention → now the expired entry is purged.
    await putConfig({ UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 1 });
    await runCleanupTask(ctx);
    expect(containerDirExists(`${TRASH}/${oldTs}_Persist`), 'retention=1 must purge expired').toBe(false);
  });

  test('non-timestamped foreign entries in trash are never purged', async () => {
    await putConfig({
      TrickplayTaskMode: 'Deactivate', EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate', LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate', RecommendationsTaskMode: 'Deactivate',
      UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 1, OrphanMinAgeDays: 0,
    });
    containerMkdir(`${TRASH}/not-a-timestamp-folder`);
    containerWriteFile(`${TRASH}/not-a-timestamp-folder/keep.txt`, 'keep');
    await runCleanupTask(ctx);
    // Name doesn't parse as a trash timestamp → left alone.
    expect(containerDirExists(`${TRASH}/not-a-timestamp-folder`)).toBe(true);
  });

  test('an expired symlinked trash entry is unlinked, but its target survives (reparse-point = link-only delete)', async () => {
    // Regression guard for the data-loss risk in PurgeExpiredTrash: a trash entry
    // that is a symlink/junction (reparse point) must be removed as the LINK ONLY -
    // never recursively followed into the target. If a regression flipped to the
    // else-branch (Directory.Delete(dir, recursive:true)) it would wipe the
    // target's real contents.
    await putConfig({
      TrickplayTaskMode: 'Deactivate', EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate', LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate', RecommendationsTaskMode: 'Deactivate',
      UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 7, OrphanMinAgeDays: 0,
    });

    // A real target dir OUTSIDE the trash, holding data that must survive.
    const target = `${M}/Symlink Target (2099)`;
    containerRm(target);
    containerWriteFile(`${target}/keep.mkv`, 'PRECIOUS-TARGET-DATA');

    // An EXPIRED, timestamp-named trash entry that is a symlink to that target.
    const oldTs = containerTimestamp(30);
    const linkEntry = `${TRASH}/${oldTs}_LinkedOrphan`;
    containerMkdir(TRASH);
    const ln = execInContainer(`ln -s ${q(target)} ${q(linkEntry)}`);
    test.skip(ln.code !== 0, 'symlink creation unsupported on this filesystem');
    expect(containerIsSymlink(linkEntry), 'precondition: the trash entry is a symlink').toBe(true);

    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // The link entry is gone...
    expect(containerExists(linkEntry), 'expired symlink entry must be unlinked').toBe(false);
    // ...but the target directory and its data are byte-for-byte intact.
    expect(containerDirExists(target), 'symlink target dir must survive').toBe(true);
    expect(containerExists(`${target}/keep.mkv`), 'target data must survive the link-only delete').toBe(true);

    containerRm(target);
  });
});

/**
 * DELETE /Trash/Folders GREEN PATH - the on-disable bulk removal actually wipes the
 * per-library trash folders on disk and reports a non-zero Deleted count.
 *
 * Elsewhere this endpoint is only ever seen in its NEGATIVE branches: authz.api.spec
 * asserts non-admin denial, and trash-abuse.api.spec asserts an absolute /config
 * path is refused (and then runs against the safe default with NOTHING seeded, so it
 * proves a no-op, not a removal). The scheduled PurgeExpiredTrash path (above) is a
 * different code path (timestamp-name retention, not the endpoint's
 * Directory.Delete(recursive) loop). So the success branch - seed real trash, DELETE,
 * assert Deleted>0 AND the folders are gone - had no coverage.
 */
test.describe.serial('DELETE /Trash/Folders bulk removal (green path)', () => {
  test.beforeEach(() => {
    ensureCanariesPlanted(); // skips loudly w/o docker; guarantees a canary exists
    regenFixtures();
    containerRm(TRASH);
  });

  test.afterEach(() => {
    containerRm(TRASH);
    expect(verifyCanaries(), 'canary files outside /media must be intact').toEqual([]);
  });

  test('deletes seeded per-library trash folders on disk and reports Deleted>0', async () => {
    // Default (relative/blank) trash path → the endpoint targets <library>/.jellyfin-trash,
    // gated to stay under the library root. Seed a real trash folder with content.
    await putConfig({ UseTrash: true, TrashFolderPath: '.jellyfin-trash' });

    const entryA = `${TRASH}/${containerTimestamp(1)}_DoomedA`;
    const entryB = `${TRASH}/${containerTimestamp(2)}_DoomedB`;
    containerMkdir(entryA);
    containerWriteFile(`${entryA}/a.txt`, 'doomed-a');
    containerMkdir(entryB);
    containerWriteFile(`${entryB}/b.mkv`, 'doomed-b');
    expect(containerDirExists(TRASH), 'precondition: trash folder seeded').toBe(true);
    expect(containerFindCount(TRASH), 'precondition: trash has content').toBeGreaterThan(0);

    const res = await ctx.delete(p('Trash/Folders'));
    expect(res.ok(), `DELETE /Trash/Folders failed: ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as { Deleted: number; Failed: number };
    expect(body.Deleted, 'at least one trash folder must be reported deleted').toBeGreaterThanOrEqual(1);
    expect(body.Failed, 'no deletion should fail on a writable seeded folder').toBe(0);

    // The whole trash folder is gone from disk...
    expect(containerDirExists(TRASH), 'trash folder must be removed from disk').toBe(false);
    expect(containerExists(entryA), 'trashed entry A must be gone').toBe(false);
    expect(containerExists(entryB), 'trashed entry B must be gone').toBe(false);
    // ...but the library root that CONTAINED it is untouched.
    expect(containerDirExists(M), 'library root must survive the trash wipe').toBe(true);
    expect(containerFindCount(M), 'library media must survive the trash wipe').toBeGreaterThan(0);
  });

  test('is a clean no-op when there is no trash folder to delete', async () => {
    await putConfig({ UseTrash: true, TrashFolderPath: '.jellyfin-trash' });
    expect(containerDirExists(TRASH), 'precondition: no trash folder').toBe(false);

    const res = await ctx.delete(p('Trash/Folders'));
    expect(res.ok(), `DELETE /Trash/Folders failed: ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as { Deleted: number; Failed: number };
    // Nothing on disk to remove → Deleted 0, Failed 0, no error, library intact.
    expect(body.Deleted).toBe(0);
    expect(body.Failed).toBe(0);
    expect(containerDirExists(M), 'library root must survive a no-op delete').toBe(true);
  });
});
