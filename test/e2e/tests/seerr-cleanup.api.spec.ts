/**
 * Seerr request-cleanup correctness against the mock. Complements the single
 * existing "count dropped" test with: exact-id deletion, status 2/4/5 protection,
 * DryRun non-deletion, and the age-threshold boundary. Uses the mock's exact-id
 * /count hook — no container FS needed, so this runs everywhere.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask } from '../setup/api-client.ts';

const MOCK = process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';

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

async function resetMock() {
  const m = await pwRequest.newContext();
  const r = await m.get(`${MOCK}/reset`);
  expect(r.ok(), `mock /reset failed: ${r.status()}`).toBeTruthy();
  await m.dispose();
}

async function count(): Promise<{ count: number; ids: number[] }> {
  const m = await pwRequest.newContext();
  const r = await m.get(`${MOCK}/count`);
  expect(r.ok(), `mock /count failed: ${r.status()}`).toBeTruthy();
  const body = (await r.json()) as { count: number; ids: number[] };
  await m.dispose();
  return body;
}

test.describe.serial('Seerr cleanup selects exactly the right requests', () => {
  test.beforeEach(async () => {
    await resetMock();
  });

  test('Activate deletes exactly the expired pending/declined; protects 2/4/5 and recent', async () => {
    const before = await count();
    // Seed = 101(old,pending) 102(old,declined) 103(old,available=4) 104(recent)
    //        105(old,partial=5) 106(old,approved=2) 107(29d,pending) 108(31d,pending)
    expect(before.ids.sort((a, b) => a - b)).toEqual([101, 102, 103, 104, 105, 106, 107, 108]);

    await putConfig({
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: 'e2e-seerr-key',
      SeerrCleanupTaskMode: 'Activate',
      SeerrCleanupAgeDays: 30,
    });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    const after = await count();
    // Deleted: 101, 102 (old + deletable status), 108 (31d > 30d cutoff, pending).
    expect(after.ids).not.toContain(101);
    expect(after.ids).not.toContain(102);
    expect(after.ids).not.toContain(108);
    // Protected by status (available/partial/approved) or recency/age-inside.
    expect(after.ids).toContain(103); // available (4)
    expect(after.ids).toContain(104); // recent
    expect(after.ids).toContain(105); // partially available (5)
    expect(after.ids).toContain(106); // approved (2)
    expect(after.ids).toContain(107); // 29d, inside the 30d cutoff → kept
  });

  test('DryRun deletes nothing on the mock', async () => {
    const before = await count();
    await putConfig({
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: 'e2e-seerr-key',
      SeerrCleanupTaskMode: 'DryRun',
      SeerrCleanupAgeDays: 30,
    });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    const after = await count();
    expect(after.count, 'DryRun must not delete').toBe(before.count);
    expect(after.ids.sort((a, b) => a - b)).toEqual(before.ids.sort((a, b) => a - b));
  });

  test('Phase-1 fetch failure (force-fail key) deletes nothing but completes', async () => {
    const before = await count();
    await putConfig({
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: 'force-fail', // mock 500s every authed call → incomplete snapshot
      SeerrCleanupTaskMode: 'Activate',
      SeerrCleanupAgeDays: 30,
    });
    const result = await runCleanupTask(ctx);
    // The task swallows the failure into result.Failed; it still Completes.
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    const after = await count();
    expect(after.count, 'no deletion on an incomplete snapshot').toBe(before.count);
  });
});
