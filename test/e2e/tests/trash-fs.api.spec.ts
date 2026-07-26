/**
 * Behavioural filesystem tests for the trash bin: move-to-trash and date-based
 * retention purge — both entirely unverified by the shape/counter tests today.
 *
 * Requires the container FS (docker exec); skips loudly when unavailable.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask } from '../setup/api-client.ts';
import {
  hasDocker,
  regenFixtures,
  containerExists,
  containerDirExists,
  containerLs,
  containerFindCount,
  containerMkdir,
  containerWriteFile,
  containerRm,
  containerTimestamp,
  verifyCanaries,
} from '../setup/fs-assert.ts';

const M = '/media/Movies';
const TRASH = `${M}/.jellyfin-trash`;

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

test.describe.serial('trash move + retention purge', () => {
  test.beforeEach(() => {
    test.skip(!hasDocker(), 'docker exec unavailable — cannot inspect container FS');
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
    // reads the name, not real mtimes — deterministic, not flaky).
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
});
