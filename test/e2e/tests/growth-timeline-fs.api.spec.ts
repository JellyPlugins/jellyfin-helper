/** * Behavioral coverage for GrowthTimeline - the storage-growth chart. The existing * trends spec only checks for garbage (no negative/future values). */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, sleep } from '../setup/api-client.ts';
import {
  hasDocker,
  containerMkdir,
  containerWriteFile,
  containerRm,
} from '../setup/fs-assert.ts';

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

/** * Behavioral proof that the cumulative totals are computed from the REAL scanned * filesystem, not served as a placeholder. */
test.describe.serial('GrowthTimeline cumulative totals track real filesystem growth', () => {
  const NEW_DIR = '/media/Movies/Timeline Growth Probe (2024)';
  const NEW_FILE = `${NEW_DIR}/Timeline Growth Probe (2024).mkv`;
  // A distinctive, comfortably-large payload so the delta is unambiguous even if
  // the library size is large; base64 round-trips exact bytes via containerWriteFile.
  const PAYLOAD = 'X'.repeat(4096);
  const PAYLOAD_BYTES = 4096;

  test.beforeAll(() => {
    test.skip(!hasDocker(), 'docker exec unavailable - cannot seed a media file to measure growth');
  });
  test.afterAll(() => {
    // Leave the shared library as we found it so later specs are not perturbed.
    if (hasDocker()) containerRm(NEW_DIR);
  });

  async function forceRefreshTimeline(): Promise<Timeline> {
    let res = await ctx.get(p('GrowthTimeline') + '?forceRefresh=true');
    if (res.status() === 429) {
      await sleep(31_000);
      res = await ctx.get(p('GrowthTimeline') + '?forceRefresh=true');
    }
    expect(res.ok(), `GrowthTimeline forceRefresh status ${res.status()}`).toBeTruthy();
    return (await res.json()) as Timeline;
  }

  function latest(tl: Timeline): Point {
    expect(tl.dataPoints.length, 'the timeline must have data points').toBeGreaterThan(0);
    return tl.dataPoints[tl.dataPoints.length - 1];
  }

  test('adding a known-size media file increases the latest cumulative size and count', async () => {
    // This test deliberately waits out the 30s recompute rate-limit twice, so it
    // needs more than the default 90s per-test budget.
    test.setTimeout(180_000);

    // Baseline: the library already has media (proven by the specs above), so the
    // pre-add totals must themselves be positive - the empty-timeline regression
    // would already trip here.
    const before = latest(await forceRefreshTimeline());
    expect(before.cumulativeFileCount, 'baseline count must be positive').toBeGreaterThan(0);
    expect(before.cumulativeSize, 'baseline size must be positive').toBeGreaterThan(0);

    // Add exactly one new media file of a known size.
    containerMkdir(NEW_DIR);
    containerWriteFile(NEW_FILE, PAYLOAD);

    // Recompute (back off once past the 30s rate limit) and compare.
    await sleep(31_000);
    const after = latest(await forceRefreshTimeline());

    expect(
      after.cumulativeFileCount,
      'the new media file must be reflected in the cumulative file count',
    ).toBeGreaterThan(before.cumulativeFileCount);
    expect(
      after.cumulativeSize,
      'the cumulative size must grow by at least the new file size',
    ).toBeGreaterThanOrEqual(before.cumulativeSize + PAYLOAD_BYTES);
  });
});
