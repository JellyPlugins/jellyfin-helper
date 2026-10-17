import { afterAll, describe, expect, test } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);

const pt = (date: string, size = 1, count = 1) => ({ date, cumulativeSize: size, cumulativeFileCount: count });

describe('advanceBucketDate and bucketStartDate', () => {
  test.each([
    ['2026-01-31T00:00:00.000Z', 'daily', '2026-02-01'],
    ['2026-01-01T00:00:00.000Z', 'weekly', '2026-01-08'],
    ['2026-01-31T00:00:00.000Z', 'monthly', '2026-03-03'],
    ['2024-02-29T00:00:00.000Z', 'yearly', '2025-03-01'],
  ])('advance %s by %s', (date, gran, expected) => {
    expect(ui.advanceBucketDate(date, gran).toISOString().slice(0, 10)).toBe(expected);
  });

  test('unknown granularity advances monthly', () => {
    expect(ui.advanceBucketDate('2026-01-15T00:00:00.000Z', 'nope').toISOString().slice(0, 7)).toBe('2026-02');
  });

  test('weekly buckets start on monday', () => {
    expect(ui.bucketStartDate('2026-05-17T12:00:00.000Z', 'weekly').toISOString().slice(0, 10)).toBe('2026-05-11');
    expect(ui.bucketStartDate('2026-05-11T00:00:00.000Z', 'weekly').toISOString().slice(0, 10)).toBe('2026-05-11');
  });

  test('monthly and yearly snaps', () => {
    expect(ui.bucketStartDate('2026-05-17T00:00:00.000Z', 'monthly').toISOString().slice(0, 10)).toBe('2026-05-01');
    expect(ui.bucketStartDate('2026-05-17T00:00:00.000Z', 'yearly').toISOString().slice(0, 10)).toBe('2026-01-01');
    expect(ui.bucketStartDate('2026-05-17T15:00:00.000Z', 'daily').toISOString()).toBe('2026-05-17T00:00:00.000Z');
  });
});

describe('interpolateDataPoints', () => {
  test('fills daily gaps with carried values', () => {
    const out = ui.interpolateDataPoints([pt('2026-01-01T00:00:00.000Z', 10, 1), pt('2026-01-04T00:00:00.000Z', 40, 4)], 'daily');
    expect(out.map((p: any) => p.date.slice(0, 10))).toEqual(['2026-01-01', '2026-01-02', '2026-01-03', '2026-01-04']);
    expect(out[1].cumulativeSize).toBe(10);
    expect(out[3].cumulativeSize).toBe(40);
  });

  test('short series pass through', () => {
    expect(ui.interpolateDataPoints([], 'daily')).toEqual([]);
    const one = [pt('2026-01-01T00:00:00.000Z')];
    expect(ui.interpolateDataPoints(one, 'daily')).toBe(one);
  });

  test('adjacent points gain nothing', () => {
    const pts = [pt('2026-01-01T00:00:00.000Z', 1, 1), pt('2026-01-02T00:00:00.000Z', 2, 2)];
    expect(ui.interpolateDataPoints(pts, 'daily')).toHaveLength(2);
  });

  test('a dense series hitting the point cap truncates and warns', () => {
    const start = Date.UTC(2000, 0, 1);
    const pts = Array.from({ length: 20000 }, (_, i) => pt(new Date(start + i * 86400000).toISOString(), i, i));
    const out = ui.interpolateDataPoints(pts, 'daily');
    expect(out).toHaveLength(20000);
    expect(out.at(-1).cumulativeSize).toBe(19999);
    // Match the exact message: console.warn is not cleared between tests, so a bare
    // toHaveBeenCalled() could be satisfied by an unrelated earlier warning.
    expect(console.warn).toHaveBeenCalledWith(
      '[JellyfinHelper] Trend timeline truncated to 20000 points (granularity: daily).',
    );
  });

  test('a gap larger than the cap truncates while keeping the final point', () => {
    const out = ui.interpolateDataPoints(
      [pt('2000-01-01T00:00:00.000Z', 1, 1), pt('2060-01-01T00:00:00.000Z', 2, 2)],
      'daily',
    );
    expect(out).toHaveLength(19999);
    expect(out.at(-1).cumulativeSize).toBe(2);
  });
});

describe('projectToGranularity and pickLevelForSpan', () => {
  test('keeps the last point per bucket', () => {
    const pts = [pt('2026-05-11T00:00:00.000Z', 1, 1), pt('2026-05-12T00:00:00.000Z', 5, 2), pt('2026-05-19T00:00:00.000Z', 9, 3)];
    const out = ui.projectToGranularity(pts, 'weekly');
    expect(out).toHaveLength(2);
    expect(out[0].cumulativeSize).toBe(5);
  });

  test('daily level returns the input', () => {
    const pts = [pt('2026-05-11T00:00:00.000Z'), pt('2026-05-12T00:00:00.000Z')];
    expect(ui.projectToGranularity(pts, 'daily')).toBe(pts);
  });

  test.each([
    [2000, 'yearly'],
    [1826, 'yearly'],
    [400, 'monthly'],
    [366, 'monthly'],
    [100, 'weekly'],
    [91, 'weekly'],
    [90, 'daily'],
    [0, 'daily'],
  ])('span %s days', (span, level) => {
    expect(ui.pickLevelForSpan(span)).toBe(level);
  });
});

describe('computeNiceYScale', () => {
  test('zero peak still yields a positive scale', () => {
    const { yMax, ticks } = ui.computeNiceYScale(0);
    expect(yMax).toBeGreaterThan(0);
    expect(ticks.length).toBeGreaterThan(0);
  });

  test('peak fits inside the scale with clean ticks', () => {
    const { yMax, ticks } = ui.computeNiceYScale(5 * 1024 ** 3);
    expect(yMax).toBeGreaterThanOrEqual(5 * 1024 ** 3);
    expect(ticks[0]).toBe(0);
    expect(ticks[ticks.length - 1]).toBeGreaterThanOrEqual(Math.round(yMax));
  });

  test('small peaks stay in bytes', () => {
    expect(ui.computeNiceYScale(500).yMax).toBeLessThan(1024);
  });

  test('a denormal peak still yields a positive scale', () => {
    const { yMax, ticks } = ui.computeNiceYScale(Number.MIN_VALUE);
    expect(yMax).toBeGreaterThan(0);
    expect(ticks.length).toBeGreaterThan(0);
  });
});

describe('collectVisiblePoints', () => {
  test('pads the window with neighbors', () => {
    const pts = [pt('2026-01-01T00:00:00.000Z'), pt('2026-01-05T00:00:00.000Z'), pt('2026-01-10T00:00:00.000Z')];
    const out = ui.collectVisiblePoints(pts, new Date('2026-01-04T00:00:00.000Z').getTime(), new Date('2026-01-06T00:00:00.000Z').getTime());
    expect(out.map((p: any) => p.date.slice(0, 10))).toEqual(['2026-01-01', '2026-01-05', '2026-01-10']);
  });

  test('empty window falls back to the last point', () => {
    const pts = [pt('2026-01-01T00:00:00.000Z')];
    const out = ui.collectVisiblePoints(pts, 0, 1);
    expect(out).toHaveLength(1);
  });

  test('an empty projection renders nothing', () => {
    expect(ui.collectVisiblePoints([], 0, 1)).toHaveLength(0);
  });
});

describe('deltas and zoom math', () => {
  test.each([[5, 'diff-up'], [-3, 'diff-down'], [0, 'diff-neutral']])('class for %s', (d, cls) => {
    expect(ui.trendDeltaClass(d)).toBe(cls);
  });

  test.each([[5, '+'], [-5, '-'], [0, '±']])('sign for %s', (d, sign) => {
    expect(ui.trendDeltaSign(d, '-')).toBe(sign);
  });

  test('wheel zoom factor grows with delta and caps', () => {
    expect(ui.wheelZoomFactor(false, -100, 100)).toBeLessThan(1);
    expect(ui.wheelZoomFactor(false, 100, 100)).toBeGreaterThan(1);
    expect(ui.wheelZoomFactor(false, -10000, 10000)).toBe(ui.wheelZoomFactor(false, -200, 200));
    expect(ui.wheelZoomFactor(true, -100, 100)).toBeGreaterThan(ui.wheelZoomFactor(false, -100, 100));
  });

  test('pinch zoom-out follows the gentler pinch curve', () => {
    expect(ui.wheelZoomFactor(true, 100, 100)).toBeGreaterThan(1);
    expect(ui.wheelZoomFactor(true, 100, 100)).toBeLessThan(ui.wheelZoomFactor(false, 100, 100));
  });

  test('zoom limits at domain edges', () => {
    const ctx = { chartState: { startTime: 0, endTime: 100 }, domainStart: 0, domainEnd: 100, minSpanMs: 10 };
    expect(ui.wheelHitsZoomLimit(ctx, 5)).toBe(true);
    expect(ui.wheelHitsZoomLimit({ ...ctx, chartState: { startTime: 0, endTime: 50 } }, 5)).toBe(false);
    expect(ui.wheelHitsZoomLimit({ ...ctx, chartState: { startTime: 0, endTime: 10 } }, -5)).toBe(true);
  });

  test('clampTrendX stays inside the plot', () => {
    const g = { padL: 10, padR: 10, width: 100 };
    expect(ui.clampTrendX(-5, g)).toBe(10);
    expect(ui.clampTrendX(500, g)).toBe(90);
    expect(ui.clampTrendX(50, g)).toBe(50);
  });
});

describe('insights', () => {
  test('empty largest and recent render empty states', () => {
    expect(ui.buildLargestTree({ Largest: [] })).toContain('trend-empty');
    expect(ui.buildRecentTree({ Recent: [] })).toContain('trend-empty');
  });

  test('largest groups by library with movies first', () => {
    const html = ui.buildLargestTree({
      Largest: [
        { LibraryName: 'Shows', CollectionType: 'tvshows', Name: 's', Size: 5 },
        { LibraryName: 'Movies', CollectionType: 'movies', Name: 'm', Size: 10 },
      ],
    });
    expect(html.indexOf('Movies')).toBeLessThan(html.indexOf('Shows'));
    expect(html).toContain('10.00 B');
  });

  test('negative and garbage sizes count as zero', () => {
    expect(ui.sumPositiveSizes([{ Size: -5 }, { Size: 'x' }, { Size: 7 }])).toBe(7);
  });

  test('recent rows badge added vs changed', () => {
    const added = ui.buildRecentItemRow({ ChangeType: 'added', Name: 'n', Size: 1, CreatedUtc: '2026-01-02T00:00:00.000Z' });
    const changed = ui.buildRecentItemRow({ ChangeType: 'changed', Name: 'n', Size: 1, ModifiedUtc: '2026-01-03T00:00:00.000Z' });
    expect(added).toContain('insight-badge-added');
    expect(changed).toContain('insight-badge-changed');
    expect(added).toContain('2026');
  });

  test('grouping falls back to unknown', () => {
    expect(Object.keys(ui.groupByLibrary([{ Name: 'x' }]))).toEqual(['Unknown']);
  });

  test.each([[undefined, 2], [[], 2]])('sort order without items', (items, order) => {
    expect(ui.insightLibrarySortOrder('L', { L: items })).toBe(order);
  });

  test('sort order prefers movies over shows over rest', () => {
    const grouped = {
      S: [{ CollectionType: 'tvshows' }],
      M: [{ CollectionType: 'movies' }],
      X: [{ CollectionType: 'music' }],
    };
    expect(ui.insightLibrarySortOrder('M', grouped)).toBe(0);
    expect(ui.insightLibrarySortOrder('S', grouped)).toBe(1);
    expect(ui.insightLibrarySortOrder('X', grouped)).toBe(2);
  });

  test('badges map collection types to distinct icons', () => {
    const movies = ui.getInsightTypeBadge('movies');
    const shows = ui.getInsightTypeBadge('tvshows');
    expect(movies).toContain('<svg');
    expect(movies).not.toBe(shows);
    expect(ui.getInsightTypeBadge(undefined)).toBe(ui.getInsightTypeBadge('unknown-thing'));
  });

  test('insight dates are UTC and dash on garbage', () => {
    expect(ui.formatInsightDate('2026-01-02T00:00:00.000Z')).toContain('2026');
    expect(ui.formatInsightDate('')).toBe('-');
    expect(ui.formatInsightDate('nope')).toBe('-');
  });

  test('panel toggle flips visibility and aria', () => {
    document.body.innerHTML =
      '<div id="p1"></div><div id="p2" class="visible"></div>' +
      '<button id="b1" aria-expanded="false"></button><button id="b2" aria-expanded="true"></button>';
    const b1 = document.getElementById('b1')!;
    const b2 = document.getElementById('b2')!;
    ui.toggleInsightPanel('p1', 'p2', b1, b2);
    expect(document.getElementById('p1')!.classList.contains('visible')).toBe(true);
    expect(document.getElementById('p2')!.classList.contains('visible')).toBe(false);
    expect(b1.getAttribute('aria-expanded')).toBe('true');
    expect(b2.getAttribute('aria-expanded')).toBe('false');
  });

  test('panel toggle tolerates missing nodes and buttons', () => {
    document.body.innerHTML = '<div id="p1"></div>';
    ui.toggleInsightPanel('missing-show', 'p1', null, null);
    expect(document.getElementById('p1')!.classList.contains('visible')).toBe(false);
    ui.toggleInsightPanel('p1', 'missing-hide', null, null);
    expect(document.getElementById('p1')!.classList.contains('visible')).toBe(true);
  });

  test('largest rows treat garbage and non-positive sizes as zero', () => {
    const html = ui.buildLargestTree({
      Largest: [
        { LibraryName: 'Mixed', CollectionType: 'movies', Name: 'a', Size: 'x' },
        { LibraryName: 'Mixed', CollectionType: 'movies', Name: 'b', Size: -5 },
      ],
    });
    expect(html.match(/insight-tree-name/g)).toHaveLength(2);
    expect(html).toContain('0 B');
  });

  test('recent rows treat garbage sizes as zero', () => {
    const html = ui.buildRecentItemRow({ ChangeType: 'added', Name: 'n', Size: 'x', CreatedUtc: '2026-01-02T00:00:00.000Z' });
    expect(html).toContain('0 B');
    expect(html).toContain('insight-badge-added');
  });

  test('recent tree groups two libraries and sorts movies first', () => {
    const html = ui.buildRecentTree({
      Recent: [
        { LibraryName: 'Shows', CollectionType: 'tvshows', Name: 's', Size: 5, ChangeType: 'added', CreatedUtc: '2026-01-02T00:00:00.000Z' },
        { LibraryName: 'Movies', CollectionType: 'movies', Name: 'm', Size: 10, ChangeType: 'changed', ModifiedUtc: '2026-01-03T00:00:00.000Z' },
      ],
    });
    expect(html.indexOf('Movies')).toBeLessThan(html.indexOf('Shows'));
  });

  test('sort order falls back when no collection type is set', () => {
    expect(ui.insightLibrarySortOrder('L', { L: [{ Name: 'x' }] })).toBe(2);
  });

  test('grouping keeps same-library entries in one bucket', () => {
    const grouped = ui.groupByLibrary([{ LibraryName: 'A', Name: 'x' }, { LibraryName: 'A', Name: 'y' }]);
    expect(grouped.A).toHaveLength(2);
  });

  test('music libraries get their own badge icon', () => {
    expect(ui.getInsightTypeBadge('music')).not.toBe(ui.getInsightTypeBadge('unknown-thing'));
    expect(ui.getInsightTypeBadge('MUSIC')).toBe(ui.getInsightTypeBadge('music'));
  });
});

describe('formatGranularityLabel', () => {
  test('yearly is the plain year', () => {
    expect(ui.formatGranularityLabel('2026-05-17T00:00:00.000Z', 'yearly')).toBe('2026');
  });

  test('garbage yields a dash', () => {
    expect(ui.formatGranularityLabel('nope', 'daily')).toBe('-');
  });

  test('month labels carry the year', () => {
    expect(ui.formatGranularityLabel('2026-05-17T00:00:00.000Z', 'monthly')).toContain('2026');
  });

  test('unknown granularity falls back to the full date', () => {
    const out = ui.formatGranularityLabel('2026-05-17T00:00:00.000Z', 'quarterly');
    expect(out).toContain('2026');
    expect(out).not.toBe('-');
  });
});
