import { afterAll, describe, expect, test } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown>, failures: string[] = []) {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      if (failures.some((f) => String(opts.url).includes(f))) return Promise.reject(new Error('down'));
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
}

async function flush() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

const timeline = () => ({
  granularity: 'daily',
  dataPoints: [
    { date: '2026-01-01T00:00:00.000Z', cumulativeSize: 100, cumulativeFileCount: 1 },
    { date: '2026-01-02T00:00:00.000Z', cumulativeSize: 200, cumulativeFileCount: 2 },
    { date: '2026-01-03T00:00:00.000Z', cumulativeSize: 300, cumulativeFileCount: 3 },
  ],
});

describe('renderTrendChart', () => {
  test('short timelines render the empty state', () => {
    for (const tl of [null, {}, { dataPoints: [] }, { dataPoints: [timeline().dataPoints[0]] }]) {
      const out = ui.renderTrendChart(tl);
      expect(out.chartState).toBeNull();
      expect(out.html).toContain('trend-empty');
    }
  });

  test('a real timeline renders svg with points', () => {
    const out = ui.renderTrendChart(timeline());
    expect(out.html).toContain('<svg');
    expect(out.chartState.fullDaily.length).toBe(3);
    expect(out.chartState.startTime).toBeLessThanOrEqual(out.chartState.endTime);
  });
});

describe('loadTrendData', () => {
  test('paints the chart into its container', async () => {
    ajaxRouter({ GrowthTimeline: timeline() });
    document.body.innerHTML = '<div id="trendChartContainer"></div>';
    ui.loadTrendData(false);
    await flush();
    expect(document.getElementById('trendChartContainer')!.innerHTML).toContain('<svg');
  });

  test('failure paints the error state', async () => {
    ajaxRouter({}, ['GrowthTimeline']);
    document.body.innerHTML = '<div id="trendChartContainer"></div>';
    ui.loadTrendData(false);
    await flush();
    expect(document.getElementById('trendChartContainer')!.textContent).toMatch(/could not load/i);
  });

  test('missing container does not throw', async () => {
    ajaxRouter({ GrowthTimeline: timeline() });
    document.body.innerHTML = '';
    expect(() => ui.loadTrendData(false)).not.toThrow();
    await flush();
    // No chart container means nothing to render into: the body stays empty.
    expect(document.body.children).toHaveLength(0);
  });
});

describe('insights cards', () => {
  const insights = () => ({
    LargestTotalSize: 300,
    Largest: [{ LibraryName: 'Movies', CollectionType: 'movies', Name: 'big.mkv', Size: 300 }],
    Recent: [{ LibraryName: 'Movies', CollectionType: 'movies', Name: 'new.mkv', Size: 10, ChangeType: 'added', CreatedUtc: '2026-01-02T00:00:00.000Z' }],
  });

  test('cards render with toggleable panels', async () => {
    ajaxRouter({ LibraryInsights: insights() });
    document.body.innerHTML = '<div id="insightsContainer"></div>';
    ui.loadInsightsData();
    await flush();
    expect(document.getElementById('insightLargestBtn')).not.toBeNull();
    (document.getElementById('insightLargestBtn') as HTMLButtonElement).click();
    expect(document.getElementById('insightLargestPanel')!.classList.contains('visible')).toBe(true);
    expect(document.getElementById('insightsContainer')!.textContent).toContain('big.mkv');
  });

  test('failure paints the error state', async () => {
    ajaxRouter({}, ['LibraryInsights']);
    document.body.innerHTML = '<div id="insightsContainer"></div>';
    ui.loadInsightsData();
    await flush();
    expect(document.getElementById('insightsContainer')!.textContent).toMatch(/could not load/i);
  });
});
