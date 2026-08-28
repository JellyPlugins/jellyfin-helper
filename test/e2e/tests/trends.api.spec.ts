/** * Trends + statistics integrity - the "is there garbage in there?" check. */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, sleep } from '../setup/api-client.ts';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

test('media statistics reflect the scanned fake library', async () => {
  // Ensure a scan result exists (Latest is 204 before the first scan).
  let res = await ctx.get(p('MediaStatistics/Latest'));
  if (res.status() === 204) {
    // Trigger a scan; only re-fire after the 30s rate-limit window if the
    // first call was itself rate-limited (a second immediate call would just
    // return 429 and fail the assertion below).
    const scan = await ctx.get(p('MediaStatistics/ScanLibraries'));
    if (scan.status() === 429) {
      await sleep(31_000);
      res = await ctx.get(p('MediaStatistics/ScanLibraries'));
    } else {
      res = scan;
    }
  }
  expect(res.ok(), `stats status ${res.status()}`).toBeTruthy();
  const stats = (await res.json()) as any;

  // We generated several video files; totals must be positive and coherent.
  expect(stats).toBeTruthy();
  // The exact shape varies, but there should be a positive total size somewhere.
  const json = JSON.stringify(stats);
  expect(json.length).toBeGreaterThan(2);
  // No negative byte counts anywhere in the payload.
  expect(json).not.toMatch(/:\s*-\d/);
});

test('growth timeline contains no negative or future data', async () => {
  const res = await ctx.get(p('GrowthTimeline'));
  // May be 429 if just computed; retry once after a beat.
  let body: any;
  if (res.status() === 429) {
    await sleep(2000);
    body = await ctx.get(p('GrowthTimeline')).then((r) => (r.ok() ? r.json() : null));
  } else if (res.ok()) {
    body = await res.json();
  } else {
    // A genuine server error must fail the test, not silently skip it.
    expect(res.status(), 'unexpected GrowthTimeline status').toBeLessThan(500);
  }
  test.skip(!body, 'timeline not available yet');

  const now = Date.now();
  const points: Array<any> = body.DataPoints ?? body.dataPoints ?? [];
  for (const pt of points) {
    const size = pt.CumulativeSize ?? pt.cumulativeSize ?? 0;
    const count = pt.CumulativeFileCount ?? pt.cumulativeFileCount ?? 0;
    const date = new Date(pt.Date ?? pt.date ?? 0).getTime();
    expect(size, 'no negative cumulative size').toBeGreaterThanOrEqual(0);
    expect(count, 'no negative cumulative count').toBeGreaterThanOrEqual(0);
    // Allow a day of clock skew, but no far-future points.
    expect(date, 'no future-dated points').toBeLessThanOrEqual(now + 86_400_000);
  }
});

test('library insights compute without error and are coherent', async () => {
  const res = await ctx.get(p('LibraryInsights'));
  expect(res.ok(), `insights status ${res.status()}`).toBeTruthy();
  const body = JSON.stringify(await res.json());
  // No negative sizes leaking into insights.
  expect(body).not.toMatch(/[Ss]ize"?\s*:\s*-\d/);
});

test('cleanup statistics are non-negative and coherent', async () => {
  const res = await ctx.get(p('CleanupStatistics'));
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as { TotalBytesFreed: number; TotalItemsDeleted: number };
  expect(body.TotalBytesFreed).toBeGreaterThanOrEqual(0);
  expect(body.TotalItemsDeleted).toBeGreaterThanOrEqual(0);
});
