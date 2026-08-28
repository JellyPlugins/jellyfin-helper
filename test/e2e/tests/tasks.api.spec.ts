/** * Scheduled task behaviour across all modes. * * Architecture reminder (from the source map): there is ONE Jellyfin scheduled * task - `HelperCleanup` - that orchestrates 8 stages. */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask, assertPluginActive } from '../setup/api-client.ts';

// The mock Seerr is reachable from the host on localhost; inside compose it's
// mock-seerr. Tests run on the host, so use the published port.
const MOCK_SEERR_PUBLIC = process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';

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

async function cleanupStats(): Promise<{ TotalItemsDeleted: number; TotalBytesFreed: number }> {
  const res = await ctx.get(p('CleanupStatistics'));
  expect(res.ok()).toBeTruthy();
  return res.json();
}

// Run these in order: DryRun first (asserts nothing deleted), then Activate.
test.describe.serial('HelperCleanup across modes', () => {
  test('all stages Deactivate → task completes, no side effects', async () => {
    await putConfig({
      TrickplayTaskMode: 'Deactivate',
      EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate',
      LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate',
      RecommendationsTaskMode: 'Deactivate',
      UseTrash: false,
    });

    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    await assertPluginActive(ctx);
  });

  test('all cleanup stages DryRun → completes, reports but deletes nothing', async () => {
    const before = await cleanupStats();

    await putConfig({
      TrickplayTaskMode: 'DryRun',
      EmptyMediaFolderTaskMode: 'DryRun',
      OrphanedSubtitleTaskMode: 'DryRun',
      LinkRepairTaskMode: 'DryRun',
      UseTrash: false,
    });

    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // DryRun must not change the persisted deletion counters.
    const after = await cleanupStats();
    expect(after.TotalItemsDeleted).toBe(before.TotalItemsDeleted);
    await assertPluginActive(ctx);
  });

  test('cleanup stages Activate (permanent delete) → completes, counters rise', async () => {
    const before = await cleanupStats();

    await putConfig({
      TrickplayTaskMode: 'Activate',
      EmptyMediaFolderTaskMode: 'Activate',
      OrphanedSubtitleTaskMode: 'Activate',
      LinkRepairTaskMode: 'Activate',
      OrphanMinAgeDays: 0, // don't let age gating skip our fresh fixtures
      UseTrash: false,
    });

    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // The orphaned trickplay folder + orphaned subtitle should have been removed,
    // so the cumulative counter must have increased.
    const after = await cleanupStats();
    expect(after.TotalItemsDeleted).toBeGreaterThan(before.TotalItemsDeleted);
    await assertPluginActive(ctx);
  });

  test('Activate with UseTrash → cleanup completes and trash summary is coherent (non-negative)', async () => {
    // Re-generate fixtures first would be ideal, but a second Activate run with
    // trash enabled should still complete cleanly even if little remains.
    await putConfig({
      TrickplayTaskMode: 'Activate',
      EmptyMediaFolderTaskMode: 'Activate',
      OrphanedSubtitleTaskMode: 'Activate',
      LinkRepairTaskMode: 'DryRun',
      UseTrash: true,
      TrashFolderPath: '.jellyfin-trash',
      TrashRetentionDays: 30,
      OrphanMinAgeDays: 0,
    });

    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Trash summary endpoint must respond and report a coherent (non-negative) size.
    const summary = await ctx.get(p('Trash/Summary'));
    expect(summary.ok()).toBeTruthy();
    const body = (await summary.json()) as { TotalSize: number; TotalItems: number };
    expect(body.TotalSize).toBeGreaterThanOrEqual(0);
    expect(body.TotalItems).toBeGreaterThanOrEqual(0);
    await assertPluginActive(ctx);
  });

  test('Recommendations Deactivate is safe after being active (playlist purge path)', async () => {
    // First enable recs so there may be playlists, then deactivate to hit the
    // "remove all recommendation playlists" branch - must complete cleanly.
    await putConfig({ RecommendationsTaskMode: 'Activate', SyncRecommendationsToPlaylist: true });
    let result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    await putConfig({ RecommendationsTaskMode: 'Deactivate' });
    result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    await assertPluginActive(ctx);
  });

  test('Seerr cleanup Activate against mock → completes, deletes expired requests', async () => {
    // Reset the mock to a known set, then read the starting count. The mock is a hard dependency of this test - if it's unreachable we must fail loudly, not skip the deletion assertions and let the test pass vacuously.
    const mock = await pwRequest.newContext();
    try {
      const reset = await mock.get(`${MOCK_SEERR_PUBLIC}/reset`);
      expect(reset.ok(), `mock Seerr /reset failed: ${reset.status()}`).toBeTruthy();
      const beforeRes = await mock.get(`${MOCK_SEERR_PUBLIC}/count`);
      expect(beforeRes.ok(), `mock Seerr /count failed: ${beforeRes.status()}`).toBeTruthy();
      const before = (await beforeRes.json()) as { count: number };
      expect(before.count, 'mock should start with seeded requests').toBeGreaterThan(0);

      // Point Seerr at the mock and enable cleanup with an age that makes the
      // 2023-dated mock requests expired.
      await putConfig({
        SeerrUrl: 'http://mock-seerr:5055',
        SeerrApiKey: 'e2e-seerr-key',
        SeerrCleanupTaskMode: 'Activate',
        SeerrCleanupAgeDays: 30,
      });

      const result = await runCleanupTask(ctx);
      expect(result.LastExecutionResult?.Status).toBe('Completed');

      // Verify the expired requests were actually deleted from the mock: ids 101 (old/pending) and 102 (old/declined) should be gone; 103 (available, protected) and 104 (recent) should remain.
      const afterRes = await mock.get(`${MOCK_SEERR_PUBLIC}/count`);
      expect(afterRes.ok(), `mock Seerr /count failed: ${afterRes.status()}`).toBeTruthy();
      const after = (await afterRes.json()) as { count: number; ids: number[] };
      expect(after.count, 'expired requests should have been deleted').toBeLessThan(before.count);
      expect(after.ids, 'protected available request must survive').toContain(103);
      expect(after.ids, 'recent request must survive').toContain(104);
    } finally {
      await mock.dispose().catch(() => undefined);
    }
    await assertPluginActive(ctx);
  });
});
