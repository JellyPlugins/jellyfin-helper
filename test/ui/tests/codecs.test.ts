import { afterAll, describe, expect, test, vi } from 'vitest';
import { ajaxRouter, flush } from '../src/apiStub.js';
import { freshConfigPage, loadConfigPage, saveCoverage } from '../src/loader.js';

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

  test('tooltip clamps overflow and viewport edges', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    c.innerHTML = ui.renderDonutSvg(
      { h264: 2 },
      [{ LibraryName: 'Movies', VideoCodecs: { h264: 2 } }],
      'VideoCodecs',
      'tipx',
    );
    (c as HTMLElement).getBoundingClientRect = () => ({ left: 50, right: 150, top: 50, bottom: 150, width: 100, height: 100, x: 50, y: 50, toJSON() {} }) as DOMRect;
    const tip = c.querySelector('.donut-tooltip') as HTMLElement;
    tip.getBoundingClientRect = () => ({ left: -10, right: 500, top: -10, bottom: 500, width: 510, height: 510, x: -10, y: -10, toJSON() {} }) as DOMRect;
    const seg = c.querySelector('[data-segment-id="tipx_0"]') as HTMLElement;
    ui.showDonutTooltip(c, { clientX: 10, clientY: 10 }, seg);
    expect(tip.style.left).toBe('-46px');
    expect(tip.style.top).toBe('-46px');
  });

  test('segment trigger and panel-open guards', () => {
    const bare = document.createElement('div');
    expect(() => ui.triggerCodecRowForSegment(bare, false)).not.toThrow();
    const box = document.createElement('div');
    box.className = 'chart-box';
    document.body.appendChild(box);
    const noCodec = document.createElement('div');
    box.appendChild(noCodec);
    ui.triggerCodecRowForSegment(noCodec, false);
    const noMatch = document.createElement('div');
    noMatch.dataset.codec = 'nope';
    box.appendChild(noMatch);
    expect(ui.isSegmentPanelOpen(noMatch)).toBe(false);
    ui.triggerCodecRowForSegment(noMatch, false);
    expect(ui.isSegmentPanelOpen({} as any)).toBe(false);
  });

  test('breakdown toggle click and keyboard keep the active row', () => {
    document.body.innerHTML =
      '<div class="codec-breakdown">' +
      '<button data-breakdown-toggle data-expanded="true" data-total="3"></button>' +
      '<div class="codec-clickable codec-row-active" data-breakdown-hidden></div>' +
      '<div class="codec-clickable" data-breakdown-hidden></div>' +
      '</div>';
    const btn = document.querySelector('[data-breakdown-toggle]') as HTMLElement;
    const rows = document.querySelectorAll('[data-breakdown-hidden]') as NodeListOf<HTMLElement>;
    ui.attachCodecClickHandlers();
    btn.click();
    expect(rows[0].style.display).toBe('');
    expect(rows[1].style.display).toBe('none');
    expect(btn.dataset.expanded).toBe('false');
    const e = new window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    btn.dispatchEvent(e);
    expect(e.defaultPrevented).toBe(true);
    expect(btn.dataset.expanded).toBe('true');
  });

  test('collapsed row sync skips expanded boxes', () => {
    document.body.innerHTML =
      '<div class="codec-breakdown"><button data-breakdown-toggle data-expanded="true"></button>' +
      '<div class="codec-clickable codec-row-active" data-breakdown-hidden></div></div>' +
      '<div class="codec-breakdown"><button data-breakdown-toggle data-expanded="false"></button>' +
      '<div class="codec-clickable codec-row-active" data-breakdown-hidden></div>' +
      '<div class="codec-clickable" data-breakdown-hidden></div></div>';
    ui.attachCodecClickHandlers();
    document.querySelector('.codec-breakdown .codec-clickable')!.dispatchEvent(new window.Event('click', { bubbles: true }));
    const second = document.querySelectorAll('.codec-breakdown')[1];
    const hidden = second.querySelectorAll('[data-breakdown-hidden]') as NodeListOf<HTMLElement>;
    expect(hidden[0].style.display).toBe('');
    expect(hidden[1].style.display).toBe('none');
  });

  test('drilldown before any data renders an empty panel', () => {
    const local = freshConfigPage();
    document.body.innerHTML =
      '<div id="tab-codecs"><div class="charts-row"><div class="chart-box" data-chart-id="videoCodecs">' +
      local.renderCodecBreakdown({ h264: 1 }, {}, 'videoCodecs') + '</div></div>' +
      '<div class="file-tree-panel" id="codecDetail_videoCodecs"></div></div>';
    local.attachCodecClickHandlers();
    (document.querySelector('.codec-clickable') as HTMLElement).click();
    expect(document.getElementById('codecDetail_videoCodecs')!.textContent).toBe('');
  });

  test('watched username row resolves from WatchedByUserPaths', () => {
    const local = freshConfigPage();
    local.fillCodecsData({
      Movies: [{
        LibraryName: 'Movies',
        VideoCodecs: { h264: 1 },
        VideoCodecPaths: { h264: ['/m/a.mkv'] },
        WatchedByUserPaths: { alice: ['/m/a.mkv'] },
        WatchedByUserSizes: { alice: 1 },
      }],
      TvShows: [], Other: [], Music: [], Books: [], Libraries: [],
      MovieRootPaths: ['/m'], TvShowRootPaths: [], OtherRootPaths: [], MusicRootPaths: [], BookRootPaths: [],
    });
    document.body.innerHTML =
      '<div id="tab-codecs"><div class="charts-row"><div class="chart-box" data-chart-id="watched">' +
      local.renderCodecBreakdown({ alice: 1 }, {}, 'watched') + '</div></div>' +
      '<div class="file-tree-panel" id="codecDetail_watched"></div></div>';
    local.attachCodecClickHandlers();
    const row = document.querySelector('.codec-clickable[data-codec="alice"]') as HTMLElement;
    row.click();
    expect(document.getElementById('codecDetail_watched')!.textContent).toContain('a.mkv');
  });

  test('video bitrate card renders from tier data', () => {
    const local = freshConfigPage();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    local.fillCodecsData({
      Movies: [{
        LibraryName: 'Movies',
        VideoBitrateTiers: { low: 1 },
        VideoBitrateTierSizes: { low: 100 },
      }],
      TvShows: [], Other: [], Music: [], Books: [], Libraries: [],
      MovieRootPaths: ['/m'], TvShowRootPaths: [], OtherRootPaths: [], MusicRootPaths: [], BookRootPaths: [],
    });
    expect(document.querySelector('.chart-box[data-chart-id="videoBitrate"]')).not.toBeNull();
  });
});

function codecRect(left: number, top: number, width = 100, height = 50): DOMRect {
  return { left, right: left + width, top, bottom: top + height, width, height, x: left, y: top, toJSON() {} } as DOMRect;
}

function codecCard(id: string, r: DOMRect): HTMLElement {
  const card = document.createElement('div');
  card.className = 'chart-box';
  card.dataset.chartId = id;
  card.getBoundingClientRect = () => r;
  const handle = document.createElement('span');
  handle.className = 'codec-card-handle';
  card.appendChild(handle);
  return card;
}

describe('drag engine and order persistence edges', () => {
  test('begin, move, reset and teardown manage the clone', () => {
    const p = freshConfigPage();
    ajaxRouter({});
    document.body.innerHTML = '';
    const host = document.createElement('div');
    const row = document.createElement('div');
    row.className = 'charts-row';
    const a = codecCard('a', codecRect(0, 0));
    const b = codecCard('b', codecRect(110, 0));
    row.append(a, b);
    host.appendChild(row);
    document.body.appendChild(host);
    const state: any = {
      handle: a.querySelector('.codec-card-handle'), card: a, chartsRow: row, pointerId: 1,
      isTouch: false, startX: 0, startY: 0, lastClientX: 5, lastClientY: 5,
      active: false, armTimer: null, clone: null, moved: false, autoScrollRaf: 0, autoScrollSpeed: 0,
    };
    p.beginCodecDrag(state);
    expect(state.active).toBe(true);
    expect(document.querySelector('.codec-drag-clone')).not.toBeNull();
    p.beginCodecDrag(state);
    p.moveCodecCloneToPointer(state);
    p.moveCodecCloneToPointer({ clone: null } as any);
    p.teardownCodecDrag(state);
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
    expect(state.active).toBe(false);

    p.attachCodecCardDragHandlers(host);
    const handle = a.querySelector('.codec-card-handle') as HTMLElement;
    handle.dispatchEvent(Object.assign(new window.Event('pointerdown', { bubbles: true }), {
      pointerType: 'mouse', button: 0, clientX: 5, clientY: 5, pointerId: 9,
    }));
    window.dispatchEvent(Object.assign(new window.Event('pointermove'), { pointerId: 9, clientX: 30, clientY: 30 }));
    expect(document.querySelector('.codec-drag-clone')).not.toBeNull();
    p.resetCodecDragForRerender();
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
  });

  test('begin recovers when the card cannot be measured', () => {
    const p = freshConfigPage();
    const card = codecCard('a', codecRect(0, 0));
    card.getBoundingClientRect = () => { throw new Error('detached'); };
    document.body.appendChild(card);
    const state: any = {
      handle: card.querySelector('.codec-card-handle'), card, chartsRow: document.body, pointerId: 1,
      isTouch: false, startX: 0, startY: 0, lastClientX: 0, lastClientY: 0,
      active: false, armTimer: null, clone: null, moved: false, autoScrollRaf: 0, autoScrollSpeed: 0,
    };
    expect(() => p.beginCodecDrag(state)).not.toThrow();
    expect(state.active).toBe(false);
  });

  test('pointer move cancels touch arming and gates on the mouse lift threshold', () => {
    const p = freshConfigPage();
    const row = document.createElement('div');
    row.className = 'charts-row';
    const a = codecCard('a', codecRect(0, 0));
    row.appendChild(a);
    document.body.appendChild(row);
    const touchState: any = {
      handle: a.querySelector('.codec-card-handle'), card: a, chartsRow: row, pointerId: 1,
      isTouch: true, startX: 0, startY: 0, lastClientX: 0, lastClientY: 0,
      active: false, armTimer: setTimeout(() => undefined, 500), clone: null, moved: false, autoScrollRaf: 0, autoScrollSpeed: 0,
    };
    p.onCodecDragPointerMove({ pointerId: 1, clientX: 100, clientY: 100, preventDefault() {} } as any, touchState);
    expect(touchState.armTimer).toBeNull();
    const mouseState: any = {
      handle: a.querySelector('.codec-card-handle'), card: a, chartsRow: row, pointerId: 2,
      isTouch: false, startX: 0, startY: 0, lastClientX: 0, lastClientY: 0,
      active: false, armTimer: null, clone: null, moved: false, autoScrollRaf: 0, autoScrollSpeed: 0,
    };
    p.onCodecDragPointerMove({ pointerId: 2, clientX: 2, clientY: 0, preventDefault() {} } as any, mouseState);
    expect(mouseState.active).toBe(false);
    p.onCodecDragPointerMove({ pointerId: 2, clientX: 60, clientY: 10, preventDefault() {} } as any, mouseState);
    expect(mouseState.active).toBe(true);
    p.teardownCodecDrag(mouseState);
  });

  test('pointer move returns when arming failed', () => {
    const p = freshConfigPage();
    const card = codecCard('a', codecRect(0, 0));
    card.getBoundingClientRect = () => { throw new Error('detached'); };
    document.body.appendChild(card);
    const state: any = {
      handle: card.querySelector('.codec-card-handle'), card, chartsRow: document.body, pointerId: 1,
      isTouch: false, startX: 0, startY: 0, lastClientX: 0, lastClientY: 0,
      active: false, armTimer: null, clone: null, moved: false, autoScrollRaf: 0, autoScrollSpeed: 0,
    };
    expect(() => p.onCodecDragPointerMove({ pointerId: 1, clientX: 30, clientY: 30, preventDefault() {} } as any, state)).not.toThrow();
    expect(state.active).toBe(false);
  });

  test('pointer up commits, saves and clears arm timers', async () => {
    const p = freshConfigPage();
    const calls = ajaxRouter({});
    const row = document.createElement('div');
    row.className = 'charts-row';
    const a = codecCard('a', codecRect(0, 0));
    const b = codecCard('b', codecRect(110, 0));
    row.append(a, b);
    document.body.appendChild(row);
    const state: any = {
      handle: a.querySelector('.codec-card-handle'), card: a, chartsRow: row, pointerId: 1,
      isTouch: true, startX: 0, startY: 0, lastClientX: 0, lastClientY: 0,
      active: true, armTimer: setTimeout(() => undefined, 500), clone: null, moved: true, autoScrollRaf: 0, autoScrollSpeed: 0,
    };
    p.onCodecDragPointerUp({ pointerId: 1 } as any, state);
    expect(state.armTimer).toBeNull();
    expect(state.active).toBe(false);
    await flush();
    expect(calls.some((c) => String(c.url).includes('CodecCardOrder'))).toBe(true);
  });

  test('queued saves flush once the active save settles', async () => {
    const p = freshConfigPage();
    const calls = ajaxRouter({ 'CodecCardOrder': { CodecCardOrder: ['c'] } });
    p.saveCodecCardOrder(['a'], null, ['b']);
    p.saveCodecCardOrder(['c'], null, ['b']);
    await flush();
    await flush();
    const puts = calls.filter((c) => String(c.url).includes('CodecCardOrder'));
    expect(puts).toHaveLength(2);
  });

  test('failed saves roll back and clear the queue', async () => {
    const p = freshConfigPage();
    const calls = ajaxRouter({}, ['CodecCardOrder']);
    const before = p.orderedCodecCardIds({ containers: 1, resolutions: 1, watched: 1 });
    p.saveCodecCardOrder(['a'], null, ['b']);
    p.saveCodecCardOrder(['c'], null, ['b']);
    await flush();
    await flush();
    // The queued save is dropped after the failure.
    expect(calls.filter((c) => String(c.url).includes('CodecCardOrder'))).toHaveLength(1);
    expect(p.orderedCodecCardIds({ containers: 1, resolutions: 1, watched: 1 })).toEqual(before);
  });

  test('attach handles without charts-row and consume contextmenu', () => {
    const p = freshConfigPage();
    document.body.innerHTML = '';
    const empty = document.createElement('div');
    expect(() => p.attachCodecCardDragHandlers(empty)).not.toThrow();
    const host = document.createElement('div');
    const row = document.createElement('div');
    row.className = 'charts-row';
    const a = codecCard('a', codecRect(0, 0));
    row.appendChild(a);
    host.appendChild(row);
    document.body.appendChild(host);
    p.attachCodecCardDragHandlers(host);
    const handle = a.querySelector('.codec-card-handle')!;
    const ev = new window.Event('contextmenu', { bubbles: true, cancelable: true });
    handle.dispatchEvent(ev);
    expect(ev.defaultPrevented).toBe(true);
  });

  test('reorderFullOrder rebases new ids onto the full order', () => {
    const p = freshConfigPage();
    expect(p.reorderFullOrder(['a', 'b', 'c'], ['c', 'a', 'z'])).toEqual(['c', 'b', 'a', 'z']);
    expect(p.reorderFullOrder(['a', 'b', 'c'], ['b'])).toEqual(['a', 'b', 'c']);
  });

  test('groupCardsIntoRows groups by top and grows to the tallest card', () => {
    const p = freshConfigPage();
    const rows = p.groupCardsIntoRows([
      { el: {}, left: 200, right: 300, top: 0, bottom: 10, cx: 250 },
      { el: {}, left: 0, right: 100, top: 0, bottom: 40, cx: 50 },
      { el: {}, left: 0, right: 100, top: 50, bottom: 60, cx: 50 },
    ] as any);
    expect(rows).toHaveLength(2);
    expect(rows[0].bottom).toBe(40);
    expect((rows[0].cards[0] as any).left).toBe(0);
  });

  test('findSlotIndex covers ends, row bands and nearest centre', () => {
    const p = freshConfigPage();
    const rows = [
      { top: 0, bottom: 10, cards: [{ left: 10, right: 40, cx: 25 }, { left: 60, right: 90, cx: 75 }] },
      { top: 20, bottom: 30, cards: [{ left: 10, right: 40, cx: 25 }] },
    ] as any;
    expect(p.findSlotIndex(rows, 3, 50, -5)).toBe(0);
    expect(p.findSlotIndex(rows, 3, 50, 100)).toBe(3);
    expect(p.findSlotIndex(rows, 3, 100, 25)).toBe(3);
    expect(p.findSlotIndex(rows, 3, 25, 25)).toBe(2);
    expect(p.findSlotIndex(rows, 3, 70, 5)).toBe(1);
    expect(p.findSlotIndex(rows, 3, 25, 18)).toBe(2);
    expect(p.colsOf(rows)).toBe(2);
  });

  test('reorderCodecCardTo bails on tiny grids and settled slots', () => {
    const p = freshConfigPage();
    const row = document.createElement('div');
    row.className = 'charts-row';
    const a = codecCard('a', codecRect(0, 0));
    row.appendChild(a);
    document.body.appendChild(row);
    const single: any = { card: a, chartsRow: row, moved: false };
    p.reorderCodecCardTo(single, 100, 100);
    expect(single.moved).toBe(false);
    const b = codecCard('b', codecRect(110, 0));
    row.appendChild(b);
    const state: any = { card: b, chartsRow: row, moved: false };
    p.reorderCodecCardTo(state, 5, 100);
    expect(state.moved).toBe(false);
  });

  test('flip honors reduced motion and settles pending transforms', () => {
    const p = freshConfigPage();
    vi.stubGlobal('matchMedia', () => ({ matches: true }));
    const row = document.createElement('div');
    row.className = 'charts-row';
    const a = codecCard('a', codecRect(0, 0));
    const b = codecCard('b', codecRect(110, 0));
    row.append(a, b);
    document.body.appendChild(row);
    let mutated = false;
    p.flipCodecReorder(row, a, () => { mutated = true; });
    expect(mutated).toBe(true);
    b.style.transform = 'translate(5px, 0)';
    b.style.transition = 'transform 160ms ease';
    (b as any)._codecFlipTimer = setTimeout(() => undefined, 160);
    p.settleCodecFlip([b]);
    expect(b.style.transform).toBe('');
    expect((b as any)._codecFlipTimer).toBe(0);
    vi.unstubAllGlobals();
  });

  test('auto scroll runs, reverses and stops with the drag', () => {
    const p = freshConfigPage();
    (window as any).scrollBy = () => undefined;
    const row = document.createElement('div');
    row.className = 'charts-row';
    const a = codecCard('a', codecRect(0, 0));
    const b = codecCard('b', codecRect(110, 0));
    row.append(a, b);
    document.body.appendChild(row);
    const state: any = {
      handle: a.querySelector('.codec-card-handle'), card: a, chartsRow: row, pointerId: 1,
      lastClientX: 5, lastClientY: 5, active: true, armTimer: null, clone: null,
      autoScrollRaf: 0, autoScrollSpeed: 0,
    };
    p.updateCodecAutoScroll(state);
    expect(state.autoScrollSpeed).toBeLessThan(0);
    vi.advanceTimersByTime(20);
    state.lastClientY = 100000;
    state.lastClientX = 5;
    p.updateCodecAutoScroll(state);
    state.autoScrollSpeed = 0;
    vi.advanceTimersByTime(20);
    p.teardownCodecDrag(state);
    expect(state.autoScrollSpeed).toBe(0);
  });

  test('keyboard arrows reorder and focus stays on the handle', () => {
    const p = freshConfigPage();
    document.body.innerHTML = '';
    const host = document.createElement('div');
    const row = document.createElement('div');
    row.className = 'charts-row';
    const a = codecCard('a', codecRect(0, 0));
    const b = codecCard('b', codecRect(110, 0));
    const c = codecCard('c', codecRect(220, 0));
    row.append(a, b, c);
    host.appendChild(row);
    document.body.appendChild(host);
    p.attachCodecCardDragHandlers(host);
    const handle = b.querySelector('.codec-card-handle') as HTMLElement;
    handle.setAttribute('aria-grabbed', 'true');
    handle.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true, cancelable: true }));
    const boxes = [...row.querySelectorAll('.chart-box[data-chart-id]')];
    expect(boxes[0]).toBe(b);
    expect(handle.getAttribute('aria-grabbed')).toBe('true');
  });
});
