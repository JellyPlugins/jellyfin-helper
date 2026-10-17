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

function controlledAjax() {
  const calls: Array<{ resolve: (v: unknown) => void; reject: (e: unknown) => void }> = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: () => new Promise((resolve, reject) => calls.push({ resolve, reject })),
  };
  return calls;
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
    expect(out.chartState.fullDaily).toHaveLength(3);
    expect(out.chartState.startTime).toBeLessThanOrEqual(out.chartState.endTime);
  });

  test('a timeline without granularity defaults to daily', () => {
    const out = ui.renderTrendChart({ dataPoints: timeline().dataPoints });
    expect(out.chartState).not.toBeNull();
    expect(out.chartState.fullDaily).toHaveLength(3);
  });

  test('an unknown granularity warns and still renders', () => {
    const out = ui.renderTrendChart({ ...timeline(), granularity: 'quarterly' });
    expect(out.html).toContain('<svg');
    // Match the exact message: console.warn is not cleared between tests, so a bare
    // toHaveBeenCalled() could be satisfied by an unrelated earlier warning.
    expect(console.warn).toHaveBeenCalledWith(
      '[JellyfinHelper] Unknown granularity "quarterly", falling back to "daily".',
    );
  });

  test('an all-zero series trims to its tail points', () => {
    const zero = (d: string) => ({ date: d, cumulativeSize: 0, cumulativeFileCount: 0 });
    const out = ui.renderTrendChart({
      granularity: 'daily',
      dataPoints: [zero('2026-01-01T00:00:00.000Z'), zero('2026-01-02T00:00:00.000Z'), zero('2026-01-03T00:00:00.000Z')],
    });
    expect(out.chartState).not.toBeNull();
    expect(out.chartState.fullDaily).toHaveLength(2);
  });

  test('two points on one date render a zero-span chart', () => {
    const d = '2026-01-01T00:00:00.000Z';
    const out = ui.renderTrendChart({
      granularity: 'daily',
      dataPoints: [
        { date: d, cumulativeSize: 10, cumulativeFileCount: 1 },
        { date: d, cumulativeSize: 20, cumulativeFileCount: 2 },
      ],
    });
    expect(out.chartState).not.toBeNull();
    expect(out.chartState.startTime).toBe(out.chartState.endTime);
    expect(out.html).toContain('<svg');
  });

  test('a shrinking series still renders its svg', () => {
    const out = ui.renderTrendChart({
      granularity: 'daily',
      dataPoints: [
        { date: '2026-01-01T00:00:00.000Z', cumulativeSize: 300, cumulativeFileCount: 3 },
        { date: '2026-01-02T00:00:00.000Z', cumulativeSize: 200, cumulativeFileCount: 2 },
        { date: '2026-01-03T00:00:00.000Z', cumulativeSize: 100, cumulativeFileCount: 1 },
      ],
    });
    expect(out.chartState).not.toBeNull();
    expect(out.html).toContain('<svg');
  });

  test('a sub-day dense series drops its data dots', () => {
    const start = Date.UTC(2026, 0, 1);
    const dataPoints = Array.from({ length: 600 }, (_, i) => ({
      date: new Date(start + i * 3600000).toISOString(),
      cumulativeSize: (i + 1) * 1024,
      cumulativeFileCount: i + 1,
    }));
    const out = ui.renderTrendChart({ granularity: 'daily', dataPoints });
    expect(out.chartState).not.toBeNull();
    expect(out.html).not.toContain('<circle');
  });

  // Positive control for the test below: without it, a jsdom that never surfaces custom
  // properties would make the "falls back" assertions pass even with the sanitizer gone.
  test('safe theme colors are honored', () => {
    document.documentElement.style.setProperty('--color-primary-light', 'rgba(1,2,3,0.5)');
    document.documentElement.style.setProperty('--color-primary', '#123456');
    try {
      const out = ui.renderTrendChart(timeline());
      expect(out.html).toContain('rgba(1,2,3,0.5)');
      expect(out.html).toContain('#123456');
    } finally {
      document.documentElement.style.removeProperty('--color-primary-light');
      document.documentElement.style.removeProperty('--color-primary');
    }
  });

  test('unsafe theme colors fall back to the defaults', () => {
    document.documentElement.style.setProperty('--color-primary-light', 'url(javascript:evil)');
    document.documentElement.style.setProperty('--color-primary', 'var(--evil)');
    try {
      const out = ui.renderTrendChart(timeline());
      expect(out.html).toContain('rgba(0,164,220,0.15)');
      expect(out.html).toContain('#00a4dc');
    } finally {
      document.documentElement.style.removeProperty('--color-primary-light');
      document.documentElement.style.removeProperty('--color-primary');
    }
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

  test('failure without a container does not throw', async () => {
    ajaxRouter({}, ['GrowthTimeline']);
    document.body.innerHTML = '';
    ui.loadTrendData(false);
    await flush();
    expect(document.body.children).toHaveLength(0);
  });

  test('a short timeline paints the empty state without interaction wiring', async () => {
    ajaxRouter({ GrowthTimeline: { granularity: 'daily', dataPoints: [timeline().dataPoints[0]] } });
    document.body.innerHTML = '<div id="trendChartContainer"></div>';
    ui.loadTrendData(false);
    await flush();
    const container = document.getElementById('trendChartContainer')!;
    expect(container.textContent).toMatch(/not enough historical data/i);
    expect(container.querySelector('.trend-chart')).toBeNull();
  });

  test('a stale trend response is ignored', async () => {
    const calls = controlledAjax();
    document.body.innerHTML = '<div id="trendChartContainer"></div>';
    ui.loadTrendData(false);
    ui.loadTrendData(false);
    expect(calls).toHaveLength(2);
    calls[0].resolve(timeline());
    await flush();
    expect(document.getElementById('trendChartContainer')!.innerHTML).not.toContain('<svg');
    calls[1].resolve(timeline());
    await flush();
    expect(document.getElementById('trendChartContainer')!.innerHTML).toContain('<svg');
  });

  test('a stale trend failure is ignored', async () => {
    const calls = controlledAjax();
    document.body.innerHTML = '<div id="trendChartContainer"></div>';
    ui.loadTrendData(false);
    ui.loadTrendData(false);
    calls[0].reject(new Error('down'));
    await flush();
    expect(document.getElementById('trendChartContainer')!.textContent).not.toMatch(/could not load/i);
    calls[1].resolve(timeline());
    await flush();
    expect(document.getElementById('trendChartContainer')!.innerHTML).toContain('<svg');
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

  test('failure without a container does not throw', async () => {
    ajaxRouter({}, ['LibraryInsights']);
    document.body.innerHTML = '';
    ui.loadInsightsData();
    await flush();
    expect(document.body.children).toHaveLength(0);
  });

  test('the recent card toggles its own panel', async () => {
    ajaxRouter({ LibraryInsights: insights() });
    document.body.innerHTML = '<div id="insightsContainer"></div>';
    ui.loadInsightsData();
    await flush();
    (document.getElementById('insightLargestBtn') as HTMLButtonElement).click();
    (document.getElementById('insightRecentBtn') as HTMLButtonElement).click();
    expect(document.getElementById('insightRecentPanel')!.classList.contains('visible')).toBe(true);
    expect(document.getElementById('insightLargestPanel')!.classList.contains('visible')).toBe(false);
  });

  test('cards render even when their toggle buttons are missing', () => {
    document.body.innerHTML = '<div id="insightsContainer"></div>';
    const orig = document.getElementById.bind(document);
    (document as any).getElementById = (id: string) =>
      (id === 'insightLargestBtn' || id === 'insightRecentBtn' ? null : orig(id));
    try {
      ui.renderInsightCards(insights());
    } finally {
      (document as any).getElementById = orig;
    }
    expect(document.querySelectorAll('.insight-card')).toHaveLength(2);
  });

  test('a stale insights response is ignored', async () => {
    const calls = controlledAjax();
    document.body.innerHTML = '<div id="insightsContainer"></div>';
    ui.loadInsightsData();
    ui.loadInsightsData();
    calls[0].resolve(insights());
    await flush();
    expect(document.getElementById('insightsContainer')!.textContent).not.toContain('big.mkv');
    calls[1].resolve(insights());
    await flush();
    expect(document.getElementById('insightsContainer')!.textContent).toContain('big.mkv');
  });

  test('a stale insights failure is ignored', async () => {
    const calls = controlledAjax();
    document.body.innerHTML = '<div id="insightsContainer"></div>';
    ui.loadInsightsData();
    ui.loadInsightsData();
    calls[0].reject(new Error('down'));
    await flush();
    expect(document.getElementById('insightsContainer')!.textContent).not.toMatch(/could not load/i);
    calls[1].resolve(insights());
    await flush();
    expect(document.getElementById('insightsContainer')!.textContent).toContain('big.mkv');
  });
});
