import { afterAll, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);

function ajaxStub(routes: Record<string, unknown> = {}) {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
}

function seedCodecs() {
  ajaxStub({ 'JellyfinHelper/Configuration': {} });
  ui.fillCodecsData({
    Movies: [
      {
        LibraryName: 'Movies',
        VideoCodecs: { h264: 2 },
        VideoCodecPaths: { h264: ['/m/a.mkv', '/m/b.mkv'] },
        VideoBitrates: { '/m/a.mkv': 8 },
      },
    ],
    TvShows: [],
    Other: [],
    Music: [],
    Books: [],
    Libraries: [{ LibraryName: 'Movies' }],
    MovieRootPaths: ['/m'],
    TvShowRootPaths: [],
    OtherRootPaths: [],
    ContainerFormats: { mkv: 2 },
  });
}

describe('polarToCartesian and describeArc', () => {
  test('origin math', () => {
    const p = ui.polarToCartesian(10, 20, 5, 0);
    expect(p.x).toBeCloseTo(15);
    expect(p.y).toBeCloseTo(20);
  });

  test('small arc uses the small-arc flag', () => {
    expect(ui.describeArc(0, 0, 10, 5, 0, 1)).toContain('0 0 1');
  });

  test('large arc flips the flag', () => {
    expect(ui.describeArc(0, 0, 10, 5, 0, 4)).toContain('0 1 1');
  });

  test('full circle splits into two paths', () => {
    const path = ui.describeArc(0, 0, 10, 5, 0, 2 * Math.PI);
    expect(path.split('M ')).toHaveLength(3);
  });
});

describe('renderDonutSvg', () => {
  test('empty data renders the placeholder', () => {
    expect(ui.renderDonutSvg({}, [], 'P', 'c1')).toContain('No data');
    expect(ui.renderDonutSvg({ a: 0 }, [], 'P', 'c1')).toContain('No data');
  });

  test('segments sort by value with unknown last', () => {
    const html = ui.renderDonutSvg({ unknown: 5, h264: 10, hevc: 7 }, [], 'P', 'c1');
    const order = [...html.matchAll(/data-codec="([^"]+)"/g)].map((m) => m[1]);
    expect(order).toEqual(['h264', 'hevc', 'unknown']);
  });

  test('labels are escaped', () => {
    expect(ui.renderDonutSvg({ '<x>': 3 }, [], 'P', 'c1')).toContain('&lt;x>');
  });
});

describe('renderCodecBreakdown', () => {
  test('empty dict renders nothing', () => {
    expect(ui.renderCodecBreakdown({}, {}, 'c1')).toBe('');
  });

  test('rows hide behind the show-more toggle', () => {
    document.body.innerHTML = ui.renderCodecBreakdown({ a: 2, b: 1 }, { a: 100, b: 50 }, 'c1');
    expect(document.querySelectorAll('[data-breakdown-hidden]')).toHaveLength(2);
    const btn = document.querySelector('[data-breakdown-toggle]') as HTMLElement;
    ui.toggleCodecBreakdown(btn);
    expect(btn.dataset.expanded).toBe('true');
    expect(btn.textContent).toBe('Show less');
    ui.toggleCodecBreakdown(btn);
    expect(btn.dataset.expanded).toBe('false');
  });

  test('toggle without a breakdown parent is a no-op', () => {
    document.body.innerHTML = '<button id="lonely"></button>';
    expect(() => ui.toggleCodecBreakdown(document.getElementById('lonely')!)).not.toThrow();
  });
});

describe('codec path collectors', () => {
  const data = {
    Movies: [{ Codecs: { h264: ['/m/a.mkv'] } }],
    TvShows: [{ Codecs: { h264: ['/t/b.mkv'] } }],
    Other: [{ Codecs: { hevc: ['/o/c.mkv'] } }],
    MovieRootPaths: ['/m'],
    TvShowRootPaths: ['/t'],
    OtherRootPaths: ['/o'],
  };

  test('category filter limits the sections', () => {
    const out = ui.collectCodecPaths(data, 'Codecs', 'h264', { movies: true });
    expect(out.movies).toEqual(['/m/a.mkv']);
    expect(out.tvShows).toEqual([]);
    expect(out.rootPaths.movies).toEqual(['/m']);
  });

  test('no filter collects everywhere', () => {
    const out = ui.collectCodecPaths(data, 'Codecs', 'h264', null);
    expect(out.movies.length + out.tvShows.length).toBe(2);
  });

  test('track labels merge first-wins per path', () => {
    const merged = ui.collectTrackLabelMeta(
      { Movies: [{ AudioTrackLabels: { '/a': ['en', 'de'], '/b': ['fr'] } }], TvShows: [], Other: [] },
      'audio',
    );
    expect(merged).toEqual({ '/a': 'en, de', '/b': 'fr' });
  });

  test('resolution dimensions merge across groups', () => {
    const merged = ui.collectResolutionDimensions({
      Movies: [{ ResolutionDimensions: { '/a': '1920x800' } }],
      TvShows: [],
      Other: [{ ResolutionDimensions: { '/b': '3840x2160' } }],
    });
    expect(merged).toEqual({ '/a': '1920x800', '/b': '3840x2160' });
  });

  test('drilldown meta resolves per chart', () => {
    expect(ui.collectDrilldownMeta('nope')).toBeNull();
  });

  test('watched counts sum per user', () => {
    const out = ui.countWatchedUsers([
      { WatchedByUserPaths: { alice: ['/a', '/b'], bob: [] } },
      { WatchedByUserPaths: { alice: ['/c'] } },
      {},
    ]);
    expect(out).toEqual({ alice: 3 });
  });
});

describe('card order helpers', () => {
  test('reorder keeps absent slots and appends new ids', () => {
    expect(ui.reorderFullOrder(['a', 'b', 'c'], ['c', 'a'])).toEqual(['c', 'b', 'a']);
    const fresh = ui.reorderFullOrder([], ['b', 'a']);
    expect(fresh.slice(-2)).toEqual(['b', 'a']);
  });

  test('ordered ids follow the canonical order by default', () => {
    expect(ui.orderedCodecCardIds({ watched: 1, containers: 1, resolutions: 1 })).toEqual([
      'containers',
      'resolutions',
      'watched',
    ]);
  });

  test('unknown ids never surface', () => {
    expect(ui.orderedCodecCardIds({ nope: 1 })).toEqual([]);
  });
});

describe('persisted card order', () => {
  test('server order wins over canonical, second load is free', async () => {
    ajaxStub({ 'JellyfinHelper/Configuration': { CodecCardOrder: ['watched', 'containers'] } });
    const ajaxSpy = vi.spyOn((globalThis as any).ApiClient, 'ajax');
    ui.loadCodecCardOrderOnce();
    await flush();
    expect(ui.orderedCodecCardIds({ watched: 1, containers: 1, resolutions: 1 })[0]).toBe('watched');
    // The cache guard must short-circuit: a second call issues no further request.
    const callsAfterFirst = ajaxSpy.mock.calls.length;
    expect(callsAfterFirst).toBeGreaterThan(0);
    ui.loadCodecCardOrderOnce();
    await flush();
    expect(ajaxSpy.mock.calls).toHaveLength(callsAfterFirst);
    ajaxSpy.mockRestore();
  });
});

describe('drag geometry helpers', () => {
  const rows = (cards: any[]) => ui.groupCardsIntoRows(cards);

  test('groups by row with one pixel tolerance', () => {
    const r = rows([
      { top: 0, bottom: 10, left: 20 },
      { top: 0, bottom: 10, left: 0 },
      { top: 50, bottom: 60, left: 0 },
    ]);
    expect(r).toHaveLength(2);
    expect(r[0].cards.map((c: any) => c.left)).toEqual([0, 20]);
    expect(ui.colsOf(r)).toBe(2);
  });

  test('pointer above the grid selects the first slot', () => {
    const r = rows([{ top: 100, bottom: 110, left: 0, right: 10, cx: 5 }]);
    expect(ui.findSlotIndex(r, 1, 5, 50)).toBe(0);
  });

  test('pointer below the grid appends', () => {
    const r = rows([{ top: 0, bottom: 10, left: 0, right: 10, cx: 5 }]);
    expect(ui.findSlotIndex(r, 1, 5, 500)).toBe(1);
  });

  test('reduced motion never throws', () => {
    expect(() => ui.prefersReducedMotion()).not.toThrow();
  });
});

describe('keyboard reorder list ops', () => {
  function rowDom() {
    document.body.innerHTML =
      '<div id="row">' +
      ['a', 'b', 'c'].map((id) => `<div class="chart-box" data-chart-id="${id}"><span class="codec-card-handle" tabindex="0"></span></div>`).join('') +
      '</div>';
    return document.getElementById('row')!;
  }

  test('index lookup and restore', () => {
    const row = rowDom();
    const cards = [...row.querySelectorAll('.chart-box')];
    expect(ui.indexOfCard(row, cards[1])).toBe(1);
    expect(ui.indexOfCard(row, document.createElement('div'))).toBe(-1);
    ui.restoreCardToIndex(row, cards[0], 5);
    expect([...row.querySelectorAll('.chart-box')].pop()).toBe(cards[0]);
  });

  test('move announces the new position', () => {
    const row = rowDom();
    const cards = [...row.querySelectorAll('.chart-box')];
    ui.moveCardInList(row, cards[0], 1);
    expect([...row.querySelectorAll('.chart-box')][1]).toBe(cards[0]);
    expect(document.getElementById('codecCardLiveRegion')!.textContent).toContain('2');
  });

  test('out-of-range moves are ignored', () => {
    const row = rowDom();
    const cards = [...row.querySelectorAll('.chart-box')];
    ui.moveCardInList(row, cards[0], -1);
    expect([...row.querySelectorAll('.chart-box')][0]).toBe(cards[0]);
    ui.announceCodec('hello');
    expect(document.getElementById('codecCardLiveRegion')!.textContent).toBe('hello');
  });
});

describe('donut tooltip and drilldown', () => {
  test('tooltip shows codec totals and hides again', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    c.innerHTML = ui.renderDonutSvg(
      { h264: 2 },
      [{ LibraryName: 'Movies', VideoCodecs: { h264: 2 } }],
      'VideoCodecs',
      'tip1',
    );
    const seg = c.querySelector('[data-segment-id="tip1_0"]') as HTMLElement;
    ui.showDonutTooltip(c, { clientX: 10, clientY: 10 }, seg);
    const tip = c.querySelector('.donut-tooltip')!;
    expect(tip.classList.contains('visible')).toBe(true);
    expect(tip.textContent).toContain('h264');
    ui.hideDonutTooltip(c);
    expect(tip.classList.contains('visible')).toBe(false);
  });

  test('unknown segments render nothing', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    c.innerHTML = ui.renderDonutSvg({ h264: 2 }, [], 'VideoCodecs', 'tip1');
    expect(() => ui.showDonutTooltip(c, { clientX: 1, clientY: 1 }, { dataset: { segmentId: 'nope' } })).not.toThrow();
    expect(c.querySelector('.donut-tooltip')!.classList.contains('visible')).toBe(false);
  });

  test('missing tooltip element is a no-op', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const seg = document.createElement('div');
    seg.dataset.segmentId = 'tip1_0';
    expect(() => ui.showDonutTooltip(document.getElementById('c')!, { clientX: 1, clientY: 1 }, seg)).not.toThrow();
    expect(() => ui.hideDonutTooltip(document.getElementById('c')!)).not.toThrow();
  });

  test('segment click opens the drilldown and marks the row', () => {
    seedCodecs();
    document.body.innerHTML =
      '<div class="chart-box">' +
      ui.renderCodecBreakdown({ h264: 2 }, {}, 'videoCodecs') +
      '<div class="file-tree-panel" id="codecDetail_videoCodecs"></div></div>';
    ui.attachCodecClickHandlers();
    const seg = document.createElement('div');
    seg.dataset.segmentId = 'x';
    seg.dataset.codec = 'h264';
    document.querySelector('.chart-box')!.appendChild(seg);
    ui.triggerCodecRowForSegment(seg, false);
    expect(document.querySelector('.codec-clickable')!.classList.contains('codec-row-active')).toBe(true);
    expect(document.getElementById('codecDetail_videoCodecs')!.textContent).toContain('a.mkv');
    expect(ui.isSegmentPanelOpen(seg)).toBe(true);
  });
});
