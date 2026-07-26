/**
 * Behavioral coverage for GrowthTimeline — the storage-growth chart. The existing
 * trends spec only checks for garbage (no negative/future values). Here we prove
 * the timeline reflects the REAL scanned library with correct, coherent data.
 *
 * Why not "add a file and watch it grow": the timeline is append-only and buckets
 * by scan date, and its size/count come from a media-extension scan of what was
 * already captured — a file added mid-run does not deterministically produce a
 * fresh delta within one recompute. So we assert the invariants that DO hold:
 *   - The series is non-empty and its cumulative file count is monotonically
 *     non-decreasing (the defining property of a cumulative growth curve).
 *   - The latest cumulative totals are positive and consistent with a library
 *     that has media (bytes > 0, file count > 0).
 *   - totalDirectoriesScanned is positive and no point is future-dated.
 *
 * GrowthTimeline serializes camelCase (dataPoints/cumulativeSize/cumulativeFileCount,
 * the last a long) and rate-limits the compute path to once/30s (429 + Retry-After);
 * without forceRefresh it serves the disk cache. We read the cached timeline and
 * back off once if a compute happens to be throttled.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, sleep } from '../setup/api-client.ts';

interface Point { date: string; cumulativeSize: number; cumulativeFileCount: number }
interface Timeline {
  granularity: string;
  dataPoints: Point[];
  totalDirectoriesScanned: number;
}

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

async function getTimeline(): Promise<Timeline> {
  let res = await ctx.get(p('GrowthTimeline'));
  if (res.status() === 429) {
    await sleep(31_000);
    res = await ctx.get(p('GrowthTimeline'));
  }
  expect(res.ok(), `GrowthTimeline status ${res.status()}`).toBeTruthy();
  return (await res.json()) as Timeline;
}

test.describe('GrowthTimeline reflects the scanned library', () => {
  test('cumulative file count is monotonically non-decreasing across the series', async () => {
    const tl = await getTimeline();
    expect(tl.dataPoints.length, 'the timeline has data points after a scan').toBeGreaterThan(0);
    for (let i = 1; i < tl.dataPoints.length; i++) {
      expect(
        tl.dataPoints[i].cumulativeFileCount,
        `cumulative file count never decreases (index ${i})`,
      ).toBeGreaterThanOrEqual(tl.dataPoints[i - 1].cumulativeFileCount);
      expect(
        tl.dataPoints[i].cumulativeSize,
        `cumulative size never decreases (index ${i})`,
      ).toBeGreaterThanOrEqual(tl.dataPoints[i - 1].cumulativeSize);
    }
  });

  test('the latest cumulative totals are positive for a library that has media', async () => {
    const tl = await getTimeline();
    expect(tl.dataPoints.length, 'the timeline has data points after a scan').toBeGreaterThan(0);
    const last = tl.dataPoints[tl.dataPoints.length - 1];
    expect(last.cumulativeFileCount, 'the library has media, so count > 0').toBeGreaterThan(0);
    expect(last.cumulativeSize, 'the library has bytes, so size > 0').toBeGreaterThan(0);
    expect(tl.totalDirectoriesScanned, 'directories were scanned').toBeGreaterThan(0);
  });

  test('no data point is dated in the future', async () => {
    const tl = await getTimeline();
    const cutoff = Date.now() + 86_400_000; // allow a day of clock skew
    for (const pt of tl.dataPoints) {
      expect(new Date(pt.date).getTime(), 'no future-dated points').toBeLessThanOrEqual(cutoff);
    }
  });
});
