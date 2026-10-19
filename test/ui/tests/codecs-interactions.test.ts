import { afterAll, afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage } from '../src/loader.js';

afterAll(saveCoverage);

function ajaxStub(routes: Record<string, unknown> = {}) {
  const calls: any[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      calls.push(opts);
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) {
          if (typeof value === 'function') return (value as any)(opts);
          return Promise.resolve(value);
        }
      }
      return Promise.resolve({});
    },
  };
  return calls;
}

function codecsHost(ui: any) {
  document.body.innerHTML = '<div id="tab-codecs"><div id="codecsContent"></div></div>';
  return ui;
}

function richData() {
  return {
    Movies: [
      {
        LibraryName: 'Movies',
        VideoCodecs: { h264: 3, hevc: 1 },
        VideoCodecPaths: { h264: ['/m/a.mkv', '/m/b.mkv', '/m/c.mkv'], hevc: ['/m/d.mkv'] },
        VideoCodecSizes: { h264: 3000, hevc: 1000 },
        Resolutions: { '1080p': 3, '2160p': 1 },
        ResolutionPaths: { '1080p': ['/m/a.mkv'], '2160p': ['/m/d.mkv'] },
        ResolutionDimensions: { '/m/a.mkv': '1920x1080', '/m/d.mkv': '3840x2160' },
        ContainerFormats: { mkv: 4 },
        AudioLanguages: { en: 3, de: 1 },
        AudioLanguagePaths: { en: ['/m/a.mkv'], de: ['/m/b.mkv'] },
        AudioTrackLabels: { '/m/a.mkv': ['English', 'Director'], '/m/b.mkv': ['Deutsch'] },
        SubtitleLanguages: { en: 2 },
        SubtitleLanguagePaths: { en: ['/m/a.mkv'] },
        SubtitleTrackLabels: { '/m/a.mkv': ['English SDH'] },
        WatchedByUserPaths: { alice: ['/m/a.mkv', '/m/b.mkv'], bob: ['/m/c.mkv'] },
        WatchedByUserSizes: { alice: 2000, bob: 1000 },
        WatchedTiers: { 'Never watched': 1 },
        WatchedTierPaths: { 'Never watched': ['/m/d.mkv'] },
        WatchedTierSizes: { 'Never watched': 1000 },
      },
    ],
    TvShows: [],
    Other: [],
    Music: [{ LibraryName: 'Music', MusicAudioCodecs: { flac: 2 }, MusicAudioCodecSizes: { flac: 500 } }],
    Books: [{ LibraryName: 'Books', BookFormats: { epub: 5 }, BookFormatSizes: { epub: 100 } }],
    Libraries: [{ LibraryName: 'Movies', ContainerFormats: { mkv: 4 }, ContainerSizes: { mkv: 4000 } }],
    MovieRootPaths: ['/m'],
    TvShowRootPaths: [],
    OtherRootPaths: [],
    MusicRootPaths: [],
    BookRootPaths: [],
  };
}

function keydown(el: Element, key: string) {
  const e = new window.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
  el.dispatchEvent(e);
  return e;
}

function pointerEvt(type: string, props: Record<string, unknown> = {}) {
  const e = new window.Event(type, { bubbles: true, cancelable: true });
  Object.assign(e, { pointerId: 1, pointerType: 'mouse', button: 0, clientX: 0, clientY: 0 }, props);
  return e;
}

function stubGrid() {
  const cards = [...document.querySelectorAll('#codecsContent .chart-box[data-chart-id]')] as HTMLElement[];
  cards.forEach((card, i) => {
    const col = i % 2;
    const row = Math.floor(i / 2);
    const left = col * 200;
    const top = row * 100;
    const rect = { left, right: left + 180, top, bottom: top + 80, width: 180, height: 80, x: left, y: top, toJSON() {} };
    card.getBoundingClientRect = () => rect as DOMRect;
  });
  const chartsRow = document.querySelector('#codecsContent .charts-row') as HTMLElement;
  chartsRow.getBoundingClientRect = () => ({ left: 0, right: 420, top: 0, bottom: 400, width: 420, height: 400, x: 0, y: 0, toJSON() {} }) as DOMRect;
  vi.stubGlobal('matchMedia', () => ({ matches: true }));
  return cards;
}

function domRect(l: number, t: number, r: number, b: number): DOMRect {
  return { left: l, top: t, right: r, bottom: b, width: r - l, height: b - t, x: l, y: t, toJSON() {} } as DOMRect;
}

function fakeDragState(over: Record<string, any> = {}) {
  return {
    handle: document.createElement('span'),
    card: document.createElement('div'),
    chartsRow: document.createElement('div'),
    pointerId: 1,
    isTouch: false,
    startX: 0,
    startY: 0,
    lastClientX: 0,
    lastClientY: 0,
    active: false,
    armTimer: null,
    clone: null,
    moved: false,
    autoScrollRaf: 0,
    autoScrollSpeed: 0,
    move() {},
    touchMove() {},
    up() {},
    ...over,
  };
}

beforeEach(() => {
  document.body.innerHTML = '';
  (window as any).scrollTo = () => undefined;
  (window as any).scrollBy = () => undefined;
  (Element.prototype as any).scrollIntoView = () => undefined;
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('showDonutTooltip edge overflow', () => {
  const ui = freshConfigPage();

  function tooltipHost() {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    c.innerHTML = ui.renderDonutSvg({ h264: 2 }, [], 'VideoCodecs', 'tip1');
    return {
      container: c.querySelector('.donut-container') as HTMLElement,
      tip: c.querySelector('.donut-tooltip') as HTMLElement,
      seg: c.querySelector('[data-segment-id="tip1_0"]') as HTMLElement,
    };
  }

  test('pointer near the right edge flips the tooltip to the left of the cursor', () => {
    const { container, tip, seg } = tooltipHost();
    container.getBoundingClientRect = () => domRect(0, 0, 100, 100);
    let first = true;
    tip.getBoundingClientRect = () => (first ? (first = false, domRect(0, 0, 200, 50)) : domRect(10, 10, 110, 60));
    ui.showDonutTooltip(container, { clientX: 90, clientY: 10 }, seg);
    expect(tip.style.left).toBe('-122px');
  });

  test('pointer near the bottom edge lifts the tooltip above the cursor', () => {
    const { container, tip, seg } = tooltipHost();
    container.getBoundingClientRect = () => domRect(0, 0, 1000, 40);
    let first = true;
    tip.getBoundingClientRect = () => (first ? (first = false, domRect(0, 0, 100, 200)) : domRect(10, 10, 110, 210));
    ui.showDonutTooltip(container, { clientX: 500, clientY: 30 }, seg);
    expect(tip.style.top).toBe('-182px');
  });
});

describe('isSegmentPanelOpen guards', () => {
  const ui = freshConfigPage();

  test('detached segment reports closed', () => {
    const seg = document.createElement('div');
    seg.dataset.codec = 'h264';
    expect(ui.isSegmentPanelOpen(seg)).toBe(false);
  });

  test('attached segment with no active row reports closed', () => {
    document.body.innerHTML =
      '<div class="chart-box">' + ui.renderCodecBreakdown({ h264: 2 }, {}, 'videoCodecs') + '</div>';
    const box = document.querySelector('.chart-box')!;
    const seg = document.createElement('div');
    seg.dataset.codec = 'h264';
    box.appendChild(seg);
    expect(ui.isSegmentPanelOpen(seg)).toBe(false);

    const ghost = document.createElement('div');
    ghost.dataset.codec = 'unknown';
    box.appendChild(ghost);
    expect(ui.isSegmentPanelOpen(ghost)).toBe(false);
  });

  test('segment whose row is active reports open', () => {
    document.body.innerHTML =
      '<div class="chart-box">' + ui.renderCodecBreakdown({ h264: 2 }, {}, 'videoCodecs') + '</div>';
    const box = document.querySelector('.chart-box')!;
    box.querySelector('.codec-clickable')!.classList.add('codec-row-active');
    const seg = document.createElement('div');
    seg.dataset.codec = 'h264';
    box.appendChild(seg);
    expect(ui.isSegmentPanelOpen(seg)).toBe(true);
  });
});

describe('triggerCodecRowForSegment guards', () => {
  const ui = freshConfigPage();

  test('detached segment without a chart-box ancestor is ignored', () => {
    const seg = document.createElement('div');
    seg.dataset.codec = 'h264';
    expect(() => ui.triggerCodecRowForSegment(seg, false)).not.toThrow();
    expect(document.querySelectorAll('.codec-row-active')).toHaveLength(0);
  });

  test('segment without data-codec is ignored', () => {
    document.body.innerHTML =
      '<div class="chart-box">' + ui.renderCodecBreakdown({ h264: 2 }, {}, 'videoCodecs') + '</div>';
    const box = document.querySelector('.chart-box')!;
    const seg = document.createElement('div');
    box.appendChild(seg);
    expect(() => ui.triggerCodecRowForSegment(seg, false)).not.toThrow();
    expect(document.querySelectorAll('.codec-row-active')).toHaveLength(0);
  });

  test('attached segment with data-codec activates the matching row', () => {
    document.body.innerHTML =
      '<div class="chart-box">' + ui.renderCodecBreakdown({ h264: 2 }, {}, 'videoCodecs')
      + '<div class="file-tree-panel" id="codecDetail_videoCodecs"></div></div>';
    const box = document.querySelector('.chart-box')!;
    ui.attachCodecClickHandlers();
    const seg = document.createElement('div');
    seg.dataset.codec = 'h264';
    box.appendChild(seg);
    ui.triggerCodecRowForSegment(seg, false);
    expect(box.querySelector('.codec-clickable')!.classList.contains('codec-row-active')).toBe(true);
  });
});

describe('breakdown collapse sync', () => {
  const ui = freshConfigPage();

  function twoBreakdowns() {
    document.body.innerHTML =
      '<div class="codec-breakdown" id="bd1">'
      + '<div class="codec-row codec-clickable" data-codec="a" style="display:;" data-breakdown-hidden="1"></div>'
      + '<button type="button" class="codec-show-more" data-breakdown-toggle="1" data-expanded="true">Show less</button>'
      + '</div>'
      + '<div class="codec-breakdown" id="bd2">'
      + '<div class="codec-row codec-clickable" data-codec="b" style="display:;" data-breakdown-hidden="1"></div>'
      + '<button type="button" class="codec-show-more" data-breakdown-toggle="1" data-expanded="false">Show all</button>'
      + '</div>';
  }

  test('expanded breakdowns are skipped, collapsed ones mirror the active row', () => {
    twoBreakdowns();
    ui.attachCodecClickHandlers();
    const row1 = document.querySelector('#bd1 .codec-clickable')!;
    row1.dispatchEvent(new window.Event('click', { bubbles: true }));
    expect((document.querySelector('#bd1 .codec-clickable') as HTMLElement).style.display).toBe('');
    expect((document.querySelector('#bd2 .codec-clickable') as HTMLElement).style.display).toBe('none');
  });

  test('collapsing keeps the active row visible', () => {
    document.body.innerHTML = ui.renderCodecBreakdown({ a: 3, b: 2, c: 1 }, { a: 100, b: 50, c: 1 }, 'videoCodecs');
    const rows = [...document.querySelectorAll('.codec-clickable')] as HTMLElement[];
    rows[1].classList.add('codec-row-active');
    const btn = document.querySelector('[data-breakdown-toggle]') as HTMLElement;
    ui.toggleCodecBreakdown(btn);
    expect(btn.dataset.expanded).toBe('true');
    ui.toggleCodecBreakdown(btn);
    expect(btn.dataset.expanded).toBe('false');
    expect(btn.textContent).toBe('Show all 3');
    expect(rows[1].style.display).toBe('');
    expect(rows[0].style.display).toBe('none');
    expect(rows[2].style.display).toBe('none');
  });
});

describe('attachCodecClickHandlers bindings', () => {
  const ui = freshConfigPage();

  test('click on the Show-all toggle expands and collapses the breakdown', () => {
    document.body.innerHTML = ui.renderCodecBreakdown({ a: 3, b: 2 }, {}, 'videoCodecs');
    ui.attachCodecClickHandlers();
    const btn = document.querySelector('[data-breakdown-toggle]') as HTMLElement;
    btn.click();
    expect(btn.dataset.expanded).toBe('true');
    expect(btn.textContent).toBe('Show less');
    expect(btn.dataset.total).toBe('2');
    btn.click();
    expect(btn.dataset.expanded).toBe('false');
    expect(btn.textContent).toBe('Show all 2');
  });

  test('Enter and Space on the toggle expand it and prevent default', () => {
    document.body.innerHTML = ui.renderCodecBreakdown({ a: 3, b: 2 }, {}, 'videoCodecs');
    ui.attachCodecClickHandlers();
    const btn = document.querySelector('[data-breakdown-toggle]') as HTMLElement;
    const enter = keydown(btn, 'Enter');
    expect(enter.defaultPrevented).toBe(true);
    expect(btn.dataset.expanded).toBe('true');
    const space = keydown(btn, ' ');
    expect(space.defaultPrevented).toBe(true);
    expect(btn.dataset.expanded).toBe('false');
  });

  test('row click before any scan data renders an empty panel without throwing', () => {
    document.body.innerHTML =
      '<div class="chart-box">' + ui.renderCodecBreakdown({ h264: 2 }, {}, 'videoCodecs')
      + '<div class="file-tree-panel" id="codecDetail_videoCodecs"></div></div>';
    ui.attachCodecClickHandlers();
    const row = document.querySelector('.codec-clickable') as HTMLElement;
    expect(() => row.click()).not.toThrow();
    expect(row.classList.contains('codec-row-active')).toBe(true);
    expect(document.getElementById('codecDetail_videoCodecs')!.innerHTML).toBe('');
  });

  test('watched rows read per-user paths while Never watched reads tier paths', async () => {
    codecsHost(ui);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();
    const watched = document.querySelector('.chart-box[data-chart-id="watched"]')!;
    const aliceRow = [...watched.querySelectorAll('.codec-clickable')].find(
      (r) => (r as HTMLElement).dataset.codec === 'alice',
    ) as HTMLElement;
    aliceRow.click();
    expect(document.getElementById('codecDetail_watched')!.textContent).toContain('a.mkv');
    const neverRow = [...watched.querySelectorAll('.codec-clickable')].find(
      (r) => (r as HTMLElement).dataset.codec === 'Never watched',
    ) as HTMLElement;
    neverRow.click();
    expect(document.getElementById('codecDetail_watched')!.textContent).toContain('d.mkv');
    expect(document.getElementById('codecDetail_watched')!.textContent).not.toContain('a.mkv');
  });
});

describe('donut hover touch guards', () => {
  const ui = freshConfigPage();

  async function render() {
    codecsHost(ui);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();
    const vc = document.querySelector('.chart-box[data-chart-id="videoCodecs"]')!;
    return {
      container: vc.querySelector('.donut-container') as HTMLElement,
      h264: [...vc.querySelectorAll('.donut-segment')].find(
        (s) => (s as HTMLElement).dataset.codec === 'h264',
      ) as HTMLElement,
      hevc: [...vc.querySelectorAll('.donut-segment')].find(
        (s) => (s as HTMLElement).dataset.codec === 'hevc',
      ) as HTMLElement,
    };
  }

  const tap = (path: Element) =>
    path.dispatchEvent(Object.assign(new window.Event('touchend', { bubbles: true, cancelable: true }), {
      changedTouches: [{ clientX: 20, clientY: 20 }],
    }));
  const hover = (path: Element, type: string) =>
    path.dispatchEvent(Object.assign(new window.Event(type), { clientX: 30, clientY: 30 }));

  test('mouseenter within the touch window is suppressed', async () => {
    const { container, h264, hevc } = await render();
    tap(h264.querySelector('path')!);
    expect(container.querySelector('.donut-tooltip')!.classList.contains('visible')).toBe(true);
    hover(hevc.querySelector('path')!, 'mouseenter');
    expect(container.querySelector('.donut-tooltip')!.textContent).toContain('h264');
    expect(container.querySelector('.donut-tooltip')!.textContent).not.toContain('hevc');
    await vi.advanceTimersByTimeAsync(900);
    hover(hevc.querySelector('path')!, 'mouseenter');
    expect(container.querySelector('.donut-tooltip')!.textContent).toContain('hevc');
  });

  test('mousemove within the touch window is suppressed', async () => {
    const { container, h264, hevc } = await render();
    tap(h264.querySelector('path')!);
    hover(hevc.querySelector('path')!, 'mousemove');
    expect(container.querySelector('.donut-tooltip')!.textContent).toContain('h264');
    expect(container.querySelector('.donut-tooltip')!.textContent).not.toContain('hevc');
    await vi.advanceTimersByTimeAsync(900);
    hover(hevc.querySelector('path')!, 'mousemove');
    expect(container.querySelector('.donut-tooltip')!.textContent).toContain('hevc');
  });

  test('click within the touch window does not toggle another segment', async () => {
    const { h264, hevc } = await render();
    tap(h264.querySelector('path')!);
    expect(h264.classList.contains('donut-segment-hover')).toBe(true);
    hevc.querySelector('path')!.dispatchEvent(new window.Event('click', { bubbles: true }));
    expect(hevc.classList.contains('donut-segment-hover')).toBe(false);
    const hevcRow = [...hevc.closest('.chart-box')!.querySelectorAll('.codec-clickable')].find(
      (r) => (r as HTMLElement).dataset.codec === 'hevc',
    )!;
    expect(hevcRow.classList.contains('codec-row-active')).toBe(false);
    await vi.advanceTimersByTimeAsync(900);
    hevc.querySelector('path')!.dispatchEvent(new window.Event('click', { bubbles: true }));
    expect(hevcRow.classList.contains('codec-row-active')).toBe(true);
  });

  test('tapping a second segment clears the first segment highlight', async () => {
    const { h264, hevc } = await render();
    tap(h264.querySelector('path')!);
    expect(h264.classList.contains('donut-segment-hover')).toBe(true);
    tap(hevc.querySelector('path')!);
    expect(h264.classList.contains('donut-segment-hover')).toBe(false);
    expect(hevc.classList.contains('donut-segment-hover')).toBe(true);
  });
});

describe('fillCodecsData descriptors', () => {
  test('videoBitrate card renders from VideoBitrateTiers', async () => {
    const ui = freshConfigPage();
    codecsHost(ui);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData({
      Movies: [{
        LibraryName: 'Movies',
        VideoBitrateTiers: { Low: 2, High: 1 },
        VideoBitrateTierSizes: { Low: 200, High: 100 },
        VideoBitrateTierPaths: { Low: ['/m/a.mkv', '/m/b.mkv'], High: ['/m/c.mkv'] },
      }],
      TvShows: [],
      Other: [],
      Music: [],
      Books: [],
      Libraries: [],
      MovieRootPaths: ['/m'],
      TvShowRootPaths: [],
      OtherRootPaths: [],
      MusicRootPaths: [],
      BookRootPaths: [],
    });
    await flush();
    const card = document.querySelector('.chart-box[data-chart-id="videoBitrate"]');
    expect(card).not.toBeNull();
    expect(card!.querySelectorAll('.donut-segment')).toHaveLength(2);
  });

  test('watched tooltip lists every contributing library', async () => {
    const ui = freshConfigPage();
    codecsHost(ui);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData({
      Movies: [{ LibraryName: 'Movies', WatchedByUserPaths: { alice: ['/m/a.mkv'] } }],
      TvShows: [{ LibraryName: 'TV', WatchedByUserPaths: { alice: ['/t/e.mkv'] } }],
      Other: [],
      Music: [],
      Books: [],
      Libraries: [],
      MovieRootPaths: ['/m'],
      TvShowRootPaths: ['/t'],
      OtherRootPaths: [],
      MusicRootPaths: [],
      BookRootPaths: [],
    });
    await flush();
    const container = document
      .querySelector('.chart-box[data-chart-id="watched"] .donut-container') as HTMLElement;
    const seg = [...container.querySelectorAll('.donut-segment')].find(
      (s) => (s as HTMLElement).dataset.codec === 'alice',
    ) as HTMLElement;
    ui.showDonutTooltip(container, { clientX: 5, clientY: 5 }, seg);
    const tip = container.querySelector('.donut-tooltip')!;
    expect(tip.textContent).toContain('Movies');
    expect(tip.textContent).toContain('TV');
  });
});

describe('loadCodecCardOrderOnce generation guard', () => {
  function captureGet() {
    let getCb: ((cfg: any) => void) | null = null;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: (opts: any) => {
        if (opts.type === 'GET') {
          return { then(cb: any) { getCb = cb; return Promise.resolve(); } };
        }
        return Promise.resolve({});
      },
    };
    return () => getCb;
  }

  test('a GET that lands before any commit applies the persisted order', async () => {
    const ui = freshConfigPage();
    codecsHost(ui);
    const getCb = captureGet();
    ui.fillCodecsData(richData());
    getCb()!({ CodecCardOrder: ['watched', 'containers'] });
    await flush();
    const row = document.querySelector('#codecsContent .charts-row')!;
    const ids = [...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    expect(ids[0]).toBe('watched');
    expect(ids[1]).toBe('containers');
  });

  test('a GET issued before a commit is stale and is ignored', async () => {
    const ui = freshConfigPage();
    codecsHost(ui);
    const getCb = captureGet();
    ui.fillCodecsData(richData());
    const row = document.querySelector('#codecsContent .charts-row')!;
    const before = [...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    ui.commitCodecCardOrderFromDom(row, row.querySelector('.chart-box')!);
    getCb()!({ CodecCardOrder: ['watched', 'containers'] });
    await flush();
    const liveRow = document.querySelector('#codecsContent .charts-row')!;
    const after = [...liveRow.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    expect(after).toEqual(before);
    expect(after[0]).toBe('containers');
  });
});

describe('card order save queueing', () => {
  test('a save during an in-flight save queues exactly one follow-up PUT', async () => {
    const ui = freshConfigPage();
    codecsHost(ui);
    const putBodies: any[] = [];
    let putCb: ((resp: any) => void) | null = null;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: (opts: any) => {
        if (opts.type === 'PUT') {
          putBodies.push(JSON.parse(opts.data));
          return { then(cb: any) { putCb = cb; return Promise.resolve(); } };
        }
        return Promise.resolve({});
      },
    };
    ui.fillCodecsData(richData());
    await flush();
    const row = document.querySelector('#codecsContent .charts-row')!;
    const firstCard = row.querySelector('.chart-box') as HTMLElement;
    const secondCard = row.querySelectorAll('.chart-box')[1] as HTMLElement;

    ui.saveCodecCardOrder(['watched', 'containers'], firstCard, null);
    expect(putBodies).toHaveLength(1);
    ui.saveCodecCardOrder(['videoCodecs', 'resolutions'], secondCard, null);
    expect(putBodies).toHaveLength(1);

    putCb!({});
    await flush();
    expect(putBodies).toHaveLength(2);
    expect(putBodies[1].codecCardOrder).toEqual(['videoCodecs', 'resolutions']);

    putCb!({});
    await flush();
    expect(putBodies).toHaveLength(2);
  });

  test('feedback on a detached card is skipped without throwing', () => {
    const ui = freshConfigPage();
    const detached = document.createElement('div');
    expect(() => ui.showCodecSaveFeedback(detached, true)).not.toThrow();
    expect(document.querySelectorAll('.codec-save-icon')).toHaveLength(0);
  });

  test('feedback on a live card shows a success icon', () => {
    const ui = freshConfigPage();
    document.body.innerHTML = '<div class="chart-box" data-chart-id="watched"></div>';
    const card = document.querySelector('.chart-box')!;
    ui.showCodecSaveFeedback(card, true);
    const icon = card.querySelector('.codec-save-icon')!;
    expect(icon.classList.contains('codec-save-icon--visible')).toBe(true);
    expect(icon.classList.contains('codec-save-icon--error')).toBe(false);
  });
});

describe('commitCodecCardOrderFromDom guards', () => {
  function putCounter() {
    let puts = 0;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: (opts: any) => {
        if (opts.type === 'PUT') puts++;
        return Promise.resolve({});
      },
    };
    return () => puts;
  }

  test('detached charts row persists nothing', () => {
    const ui = freshConfigPage();
    const puts = putCounter();
    const detachedRow = document.createElement('div');
    expect(() => ui.commitCodecCardOrderFromDom(detachedRow, document.createElement('div'))).not.toThrow();
    expect(puts()).toBe(0);
  });

  test('attached row with no cards persists nothing', () => {
    const ui = freshConfigPage();
    const puts = putCounter();
    document.body.innerHTML = '<div class="charts-row"></div>';
    const row = document.querySelector('.charts-row')!;
    expect(() => ui.commitCodecCardOrderFromDom(row, document.createElement('div'))).not.toThrow();
    expect(puts()).toBe(0);
  });
});

describe('reorderFullOrder base fallback', () => {
  const ui = freshConfigPage();

  test('empty previous order seeds visible ids onto canonical slots', () => {
    const out = ui.reorderFullOrder([], ['watched', 'containers']);
    expect(out[0]).toBe('watched');
    expect(out[6]).toBe('containers');
    expect(out).toHaveLength(11);
    expect(out).toEqual(expect.arrayContaining([
      'containers', 'resolutions', 'videoCodecs', 'watched',
    ]));
  });

  test('null previous order behaves like an empty one', () => {
    const out = ui.reorderFullOrder(null, ['watched']);
    expect(out[0]).toBe('containers');
    expect(out[6]).toBe('watched');
  });
});

describe('attachCodecCardDragHandlers guards', () => {
  const ui = freshConfigPage();

  test('container without a charts row binds nothing', () => {
    document.body.innerHTML = '<div id="host"><span class="codec-card-handle"></span></div>';
    const host = document.getElementById('host')!;
    expect(() => ui.attachCodecCardDragHandlers(host)).not.toThrow();
    const handle = host.querySelector('.codec-card-handle')!;
    handle.dispatchEvent(pointerEvt('pointerdown', { clientX: 1, clientY: 1 }));
    window.dispatchEvent(pointerEvt('pointermove', { clientX: 50, clientY: 50 }));
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
  });

  test('handle without a chart-box parent is skipped', () => {
    document.body.innerHTML =
      '<div id="host"><div class="charts-row"><span class="codec-card-handle"></span></div></div>';
    const host = document.getElementById('host')!;
    expect(() => ui.attachCodecCardDragHandlers(host)).not.toThrow();
    const handle = host.querySelector('.codec-card-handle')!;
    handle.dispatchEvent(pointerEvt('pointerdown', { clientX: 1, clientY: 1 }));
    window.dispatchEvent(pointerEvt('pointermove', { clientX: 50, clientY: 50 }));
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
  });

  test('context menu on a handle is prevented', async () => {
    codecsHost(ui);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();
    const handle = document.querySelector('#codecsContent .codec-card-handle')!;
    const e = new window.Event('contextmenu', { bubbles: true, cancelable: true });
    handle.dispatchEvent(e);
    expect(e.defaultPrevented).toBe(true);
  });
});

describe('drag re-render teardown', () => {
  test('a live drag is torn down when a re-render replaces the grid', async () => {
    const ui = freshConfigPage();
    codecsHost(ui);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();
    stubGrid();
    const handle = document.querySelector('#codecsContent .codec-card-handle')!;
    handle.dispatchEvent(pointerEvt('pointerdown', { clientX: 10, clientY: 10 }));
    window.dispatchEvent(pointerEvt('pointermove', { clientX: 40, clientY: 40 }));
    expect(document.querySelector('.codec-drag-clone')).not.toBeNull();
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
  });
});

describe('beginCodecDrag guards', () => {
  const ui = freshConfigPage();

  test('an already active drag is not started twice', () => {
    const handle = document.createElement('span');
    const clone = document.createElement('div');
    clone.className = 'codec-drag-clone';
    document.body.appendChild(clone);
    const state = fakeDragState({ handle, active: true, clone });
    ui.beginCodecDrag(state);
    expect(state.clone).toBe(clone);
    expect(document.querySelectorAll('.codec-drag-clone')).toHaveLength(1);
    expect(handle.getAttribute('aria-grabbed')).toBeNull();
  });

  test('a layout failure tears the drag down and a later attempt still arms', () => {
    const card = document.createElement('div');
    const handle = document.createElement('span');
    card.getBoundingClientRect = () => { throw new Error('layout boom'); };
    const state = fakeDragState({ card, handle });
    expect(() => ui.beginCodecDrag(state)).not.toThrow();
    expect(state.active).toBe(false);
    expect(card.classList.contains('codec-dragging-source')).toBe(false);
    card.getBoundingClientRect = () => domRect(0, 0, 100, 80);
    ui.beginCodecDrag(state);
    expect(state.active).toBe(true);
    expect(state.clone).not.toBeNull();
    expect(handle.getAttribute('aria-grabbed')).toBe('true');
  });
});

describe('clone and move guards', () => {
  const ui = freshConfigPage();

  test('moveCodecCloneToPointer without a clone is a no-op', () => {
    const state = fakeDragState({ clone: null, grabOffsetX: 5, grabOffsetY: 5, lastClientX: 50, lastClientY: 60 });
    expect(() => ui.moveCodecCloneToPointer(state)).not.toThrow();
  });

  test('moveCodecCloneToPointer positions the clone at the pointer', () => {
    const clone = document.createElement('div');
    const state = fakeDragState({ clone, grabOffsetX: 10, grabOffsetY: 20, lastClientX: 110, lastClientY: 220 });
    ui.moveCodecCloneToPointer(state);
    expect(clone.style.transform).toBe('translate(100px, 200px) scale(1.02)');
  });

  test('a pointer move whose drag fails to arm does not preventDefault or clone', () => {
    const card = document.createElement('div');
    card.getBoundingClientRect = () => { throw new Error('layout boom'); };
    const state = fakeDragState({ card, isTouch: false, active: false, startX: 0, startY: 0 });
    let prevented = false;
    ui.onCodecDragPointerMove(
      { clientX: 50, clientY: 50, pointerId: 1, preventDefault: () => { prevented = true; } } as any,
      state,
    );
    expect(state.active).toBe(false);
    expect(prevented).toBe(false);
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
  });
});

describe('reorderCodecCardTo guards', () => {
  const ui = freshConfigPage();

  function twoCardRow() {
    document.body.innerHTML =
      '<div class="charts-row">'
      + '<div class="chart-box" data-chart-id="containers"></div>'
      + '<div class="chart-box" data-chart-id="videoCodecs"></div>'
      + '</div>';
    const row = document.querySelector('.charts-row') as HTMLElement;
    const [a, b] = [...row.querySelectorAll('.chart-box')] as HTMLElement[];
    a.getBoundingClientRect = () => domRect(0, 0, 180, 80);
    b.getBoundingClientRect = () => domRect(200, 0, 380, 80);
    return { row, a, b };
  }

  test('a grid with fewer than two cards is never reordered', () => {
    document.body.innerHTML =
      '<div class="charts-row"><div class="chart-box" data-chart-id="containers"></div></div>';
    const row = document.querySelector('.charts-row') as HTMLElement;
    const card = row.querySelector('.chart-box') as HTMLElement;
    const state = fakeDragState({ chartsRow: row, card, lastClientX: 500, lastClientY: 500 });
    expect(() => ui.reorderCodecCardTo(state, 500, 500)).not.toThrow();
    expect(row.children).toHaveLength(1);
  });

  test('a pointer resting in the source slot is a no-op that does not mark a move', () => {
    const { row, a } = twoCardRow();
    const state = fakeDragState({ chartsRow: row, card: a, lastClientX: 250, lastClientY: 20 });
    ui.reorderCodecCardTo(state, 250, 20);
    expect([...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId))
      .toEqual(['containers', 'videoCodecs']);
    expect(state.moved).toBe(false);
  });
});

describe('flip settle and reduced motion', () => {
  const ui = freshConfigPage();

  function flipFixture() {
    const row = document.createElement('div');
    const a = document.createElement('div');
    a.className = 'chart-box';
    a.dataset.chartId = 'a';
    const b = document.createElement('div');
    b.className = 'chart-box';
    b.dataset.chartId = 'b';
    row.append(a, b);
    document.body.appendChild(row);
    let shifted = false;
    b.getBoundingClientRect = () => (shifted ? domRect(180, 0, 360, 80) : domRect(0, 0, 180, 80));
    const mutate = () => { shifted = true; row.appendChild(a); };
    return { row, a, b, mutate };
  }

  test('reduced motion mutates in place with no transform and no animation', () => {
    const { row, a, b, mutate } = flipFixture();
    vi.stubGlobal('matchMedia', () => ({ matches: true }));
    ui.flipCodecReorder(row, a, mutate);
    expect([...row.querySelectorAll('.chart-box')].map((x) => (x as HTMLElement).dataset.chartId)).toEqual(['b', 'a']);
    expect(b.style.transform).toBe('');
    expect(b.style.transition).toBe('');
  });

  test('settleCodecFlip clears a pending timer and resets the transform', () => {
    const { b } = flipFixture();
    b.style.transform = 'translate(10px, 0px)';
    ui.settleCodecFlip([b]);
    expect(b.style.transform).toBe('');
    expect(b.style.transition).toBe('none');
    b.style.transform = 'translate(10px, 0px)';
    b.style.transition = 'none';
    (b as any)._codecFlipTimer = 42;
    ui.settleCodecFlip([b]);
    expect((b as any)._codecFlipTimer).toBe(0);
    expect(b.style.transform).toBe('');
  });

  test('FLIP animates shifted siblings and cleans up after the glide', async () => {
    const { row, a, b, mutate } = flipFixture();
    const rafCbs: FrameRequestCallback[] = [];
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => { rafCbs.push(cb); return rafCbs.length; });
    vi.stubGlobal('cancelAnimationFrame', () => undefined);
    ui.flipCodecReorder(row, a, mutate);
    expect(b.style.transform).toBe('translate(-180px, 0px)');
    expect(rafCbs).toHaveLength(1);
    rafCbs[0](0);
    expect(b.style.transition).toBe('transform 160ms ease');
    expect(b.style.transform).toBe('');
    expect((b as any)._codecFlipTimer).not.toBe(0);
    await vi.advanceTimersByTimeAsync(170);
    expect(b.style.transition).toBe('');
    expect((b as any)._codecFlipTimer).toBe(0);
  });
});

describe('updateCodecAutoScroll speed calc', () => {
  const ui = freshConfigPage();

  test('pointer in the bottom band scrolls down at full speed', () => {
    const rafCbs: FrameRequestCallback[] = [];
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => { rafCbs.push(cb); return rafCbs.length; });
    vi.stubGlobal('cancelAnimationFrame', () => undefined);
    const scrollSpy = vi.spyOn(window, 'scrollBy');
    const state: any = {
      active: true,
      lastClientX: 100,
      lastClientY: 768,
      autoScrollRaf: 0,
      autoScrollSpeed: 0,
      chartsRow: document.createElement('div'),
    };
    ui.updateCodecAutoScroll(state);
    expect(state.autoScrollSpeed).toBe(12);
    expect(rafCbs).toHaveLength(1);
    rafCbs[0](0);
    expect(scrollSpy).toHaveBeenCalledWith(0, 12);
  });
});

describe('drag teardown paths', () => {
  const ui = freshConfigPage();

  test('pointer up clears a pending long-press arm timer', async () => {
    const card = document.createElement('div');
    const handle = document.createElement('span');
    let lateArm = false;
    const armId = window.setTimeout(() => { lateArm = true; }, 450);
    const state = fakeDragState({ card, handle, armTimer: armId, isTouch: true, active: false });
    ui.onCodecDragPointerUp({ pointerId: 1 } as any, state);
    expect(state.armTimer).toBeNull();
    await vi.advanceTimersByTimeAsync(500);
    expect(lateArm).toBe(false);
  });

  test('a touch drop stamps the compat guard so synthetic hover is suppressed', async () => {
    codecsHost(ui);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();
    const vc = document.querySelector('.chart-box[data-chart-id="videoCodecs"]') as HTMLElement;
    const card = vc.querySelector('.chart-box') ?? vc;
    const state = fakeDragState({
      card: vc,
      handle: vc.querySelector('.codec-card-handle') as HTMLElement,
      chartsRow: vc.closest('.charts-row') as HTMLElement,
      isTouch: true,
      active: true,
      moved: true,
    });
    ui.onCodecDragPointerUp({ pointerId: 1 } as any, state);
    expect(state.active).toBe(false);
    vc.querySelector('.donut-segment path')!
      .dispatchEvent(Object.assign(new window.Event('mouseenter'), { clientX: 5, clientY: 5 }));
    expect(vc.querySelector('.donut-container .donut-tooltip')!.classList.contains('visible')).toBe(false);
    await vi.advanceTimersByTimeAsync(900);
    vc.querySelector('.donut-segment path')!
      .dispatchEvent(Object.assign(new window.Event('mouseenter'), { clientX: 5, clientY: 5 }));
    expect(vc.querySelector('.donut-container .donut-tooltip')!.classList.contains('visible')).toBe(true);
  });

  test('teardown clears the arm timer so a stale long-press cannot fire late', async () => {
    const card = document.createElement('div');
    const handle = document.createElement('span');
    const clone = document.createElement('div');
    let lateArm = false;
    const armId = window.setTimeout(() => { lateArm = true; }, 450);
    const state = fakeDragState({ card, handle, armTimer: armId, clone, active: true });
    ui.teardownCodecDrag(state);
    expect(state.armTimer).toBeNull();
    await vi.advanceTimersByTimeAsync(500);
    expect(lateArm).toBe(false);
  });

  test('teardown removes the clone and resets the card and handle', () => {
    const card = document.createElement('div');
    card.className = 'codec-dragging-source';
    const handle = document.createElement('span');
    handle.setAttribute('aria-grabbed', 'true');
    handle.style.touchAction = 'none';
    const clone = document.createElement('div');
    clone.className = 'codec-drag-clone';
    document.body.appendChild(clone);
    const state = fakeDragState({ card, handle, clone, active: true });
    ui.teardownCodecDrag(state);
    expect(state.clone).toBeNull();
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
    expect(card.classList.contains('codec-dragging-source')).toBe(false);
    expect(handle.getAttribute('aria-grabbed')).toBe('false');
    expect(handle.style.touchAction).toBe('');
  });
});

describe('keyboard reorder edges', () => {
  async function render() {
    const ui = freshConfigPage();
    codecsHost(ui);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();
    return ui;
  }

  test('Escape restores the grabbed card to its origin slot', async () => {
    const ui = await render();
    const row = document.querySelector('#codecsContent .charts-row')!;
    const ids = () => [...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    const origin = ids();
    const handle = row.querySelectorAll('.chart-box .codec-card-handle')[1]!;
    keydown(handle, 'Enter');
    keydown(handle, 'ArrowDown');
    expect(ids()[2]).toBe(origin[1]);
    keydown(handle, 'Escape');
    expect(ids()).toEqual(origin);
    expect(handle.getAttribute('aria-grabbed')).toBe('false');
    expect(document.getElementById('codecCardLiveRegion')!.textContent).toBe('Reorder cancelled.');
  });

  test('ArrowUp moves the grabbed card one slot up and announces the position', async () => {
    const ui = await render();
    const row = document.querySelector('#codecsContent .charts-row')!;
    const ids = () => [...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    const second = row.querySelectorAll('.chart-box')[1] as HTMLElement;
    const handle = second.querySelector('.codec-card-handle')!;
    keydown(handle, 'Enter');
    keydown(handle, 'ArrowUp');
    expect(ids()[0]).toBe(second.dataset.chartId);
    expect(document.getElementById('codecCardLiveRegion')!.textContent).toBe('Moved to position 1');
  });

  test('grabbing a card creates the aria-live region', async () => {
    const ui = await render();
    const handle = document.querySelector('#codecsContent .codec-card-handle')!;
    keydown(handle, 'Enter');
    const live = document.getElementById('codecCardLiveRegion');
    expect(live).not.toBeNull();
    expect(live!.getAttribute('aria-live')).toBe('polite');
    expect(live!.textContent).toContain('Card grabbed');
    keydown(handle, 'Enter');
  });
});

describe('drag geometry helpers', () => {
  const ui = freshConfigPage();

  test('groupCardsIntoRows grows the row bottom past the first card height', () => {
    const rows = ui.groupCardsIntoRows([
      { top: 0, bottom: 10, left: 0, right: 100 },
      { top: 0.5, bottom: 20, left: 200, right: 300 },
      { top: 50, bottom: 60, left: 0, right: 100 },
    ]);
    expect(rows).toHaveLength(2);
    expect(rows[0].bottom).toBe(20);
    expect(rows[0].cards).toHaveLength(2);
    expect(rows[1].bottom).toBe(60);
  });

  test('findSlotIndex picks the nearest card inside the band row', () => {
    const rows = [
      { top: 0, bottom: 10, cards: [{ left: 0, right: 100, cx: 50 }, { left: 200, right: 300, cx: 250 }] },
      { top: 100, bottom: 110, cards: [{ left: 0, right: 100, cx: 50 }] },
    ];
    expect(ui.findSlotIndex(rows, 3, 240, 50)).toBe(1);
    expect(ui.findSlotIndex(rows, 3, 60, 50)).toBe(0);
  });

  test('findSlotIndex appends past a short last row and clamps outside the grid', () => {
    const rows = [
      { top: 0, bottom: 10, cards: [{ left: 0, right: 100, cx: 50 }, { left: 200, right: 300, cx: 250 }] },
      { top: 100, bottom: 110, cards: [{ left: 0, right: 100, cx: 50 }] },
    ];
    expect(ui.findSlotIndex(rows, 3, 2000, 105)).toBe(3);
    expect(ui.findSlotIndex(rows, 3, 50, 500)).toBe(3);
    expect(ui.findSlotIndex(rows, 3, 50, -10)).toBe(0);
  });
});
