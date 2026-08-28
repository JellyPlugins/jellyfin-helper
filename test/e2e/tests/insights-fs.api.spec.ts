/** * Behavioral coverage for LibraryInsights - the "largest media dirs" and "recently * added/changed" views. */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p } from '../setup/api-client.ts';

interface InsightEntry {
  Name: string;
  Size: number;
  CreatedUtc: string;
  ModifiedUtc: string;
  LibraryName: string;
  CollectionType: string;
  ChangeType: string;
}
interface InsightsResult {
  Largest: InsightEntry[];
  LargestTotalSize: number;
  Recent: InsightEntry[];
  RecentTotalCount: number;
  LibrarySizes: Record<string, number>;
  ComputedAtUtc: string;
}

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

async function getInsights(): Promise<InsightsResult> {
  const res = await ctx.get(p('LibraryInsights'));
  expect(res.ok(), `LibraryInsights status ${res.status()}`).toBeTruthy();
  return (await res.json()) as InsightsResult;
}

test.describe('LibraryInsights reflects real scanned data', () => {
  test('Largest is sorted by size descending, entries are real /media dirs, and total is the sum', async () => {
    const insights = await getInsights();
    expect(insights.Largest.length, 'Largest should list media dirs').toBeGreaterThan(0);

    let runningSum = 0;
    for (let i = 0; i < insights.Largest.length; i++) {
      const e = insights.Largest[i];
      expect(e.Size, `entry ${e.Name} has a positive size`).toBeGreaterThan(0);
      expect(e.Name, `entry ${i} has a name`).toBeTruthy();
      expect(e.CollectionType, `entry ${e.Name} has a collection type`).toBeTruthy();
      if (i > 0) {
        expect(
          insights.Largest[i - 1].Size,
          `Largest must be sorted desc (index ${i - 1} >= ${i})`,
        ).toBeGreaterThanOrEqual(e.Size);
      }
      runningSum += e.Size;
    }
    // LargestTotalSize is the aggregate of the listed entries.
    expect(insights.LargestTotalSize, 'LargestTotalSize == sum of Largest entry sizes').toBe(runningSum);
  });

  test('a known generated movie appears among the largest entries', async () => {
    const insights = await getInsights();
    const names = insights.Largest.map((e) => e.Name);
    // gen-media.sh always creates these movie dirs; at least one must rank in the
    // top set (they are among the larger real clips: 4K/1080p).
    const knownLarge = ['Nebula Drift (2021)', 'Aurora Skies (2019)', 'Old Reel (1998)', 'Test Show'];
    expect(
      knownLarge.some((n) => names.some((have) => have.includes(n.replace(/ \(\d+\)$/, '')))),
      `at least one known fixture in ${JSON.stringify(names)}`,
    ).toBe(true);
  });

  test('Recent lists items with a valid ChangeType and a coherent total count', async () => {
    const insights = await getInsights();
    // The library was scanned recently, so Recent should be populated.
    expect(insights.Recent.length, 'Recent should list recently-added items').toBeGreaterThan(0);
    expect(
      insights.RecentTotalCount,
      'RecentTotalCount >= the (possibly capped) Recent list length',
    ).toBeGreaterThanOrEqual(insights.Recent.length);
    for (const e of insights.Recent) {
      expect(['added', 'changed'], `ChangeType of ${e.Name} is added|changed`).toContain(e.ChangeType);
      expect(new Date(e.ModifiedUtc).getTime(), `${e.Name} has a valid ModifiedUtc`).toBeGreaterThan(0);
    }
  });

  test('LibrarySizes has a positive Movies entry', async () => {
    const insights = await getInsights();
    const movieKey = Object.keys(insights.LibrarySizes).find((k) => /movie/i.test(k));
    expect(movieKey, 'a Movies library entry should exist in LibrarySizes').toBeTruthy();
    expect(insights.LibrarySizes[movieKey!], 'Movies library has positive total size').toBeGreaterThan(0);
  });
});
