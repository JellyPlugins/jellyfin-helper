import { afterAll, afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);

// Captures every ApiClient.ajax call so PUT persistence can be asserted on the
// request body, not just on the eventual DOM feedback.
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

// Scaffold the real container fillCodecsData renders into, matching the live tab.
function codecsHost(bridge: any = ui) {
  document.body.innerHTML = '<div id="tab-codecs"><div id="codecsContent"></div></div>';
  return bridge;
}

// Realistic multi-card payload: enough distinct chart dicts so fillCodecsData
// emits several populated cards with drag handles and donuts.
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

beforeEach(() => {
  document.body.innerHTML = '';
  // jsdom implements neither; the panel-open and auto-scroll paths call them.
  (window as any).scrollTo = () => undefined;
  (Element.prototype as any).scrollIntoView = () => undefined;
});

// stubGrid() overrides matchMedia to force reduced motion. Undo it after every
// test so later suites keep jsdom's real value and do not depend on run order.
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('fillCodecsData full render', () => {
  test('renders a card per populated chart with handles and donuts', async () => {
    codecsHost();
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();

    const row = document.querySelector('#codecsContent .charts-row')!;
    expect(row).not.toBeNull();
    const ids = [...row.querySelectorAll('.chart-box[data-chart-id]')].map(
      (b) => (b as HTMLElement).dataset.chartId,
    );
    expect(ids).toEqual(expect.arrayContaining([
      'containers', 'resolutions', 'videoCodecs', 'watched',
      'audioLanguages', 'subtitleLanguages', 'musicAudioCodecs', 'bookFormats',
    ]));
    // Charts without data stay out of the grid entirely.
    expect(ids).not.toContain('dynamicRanges');
    expect(ids).not.toContain('videoBitrate');
    // Canonical order places containers and resolutions before videoCodecs.
    expect(ids.indexOf('containers')).toBeLessThan(ids.indexOf('videoCodecs'));
    expect(row.querySelectorAll('.codec-card-handle')).toHaveLength(ids.length);
    expect(row.querySelectorAll('.donut-svg')).toHaveLength(ids.length);
  });

  test('watched card merges per-user counts with Never watched', async () => {
    codecsHost();
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();

    const watched = document.querySelector('.chart-box[data-chart-id="watched"]')!;
    const codecs = [...watched.querySelectorAll('.codec-row-name')].map((n) => n.textContent);
    expect(codecs).toEqual(expect.arrayContaining(['alice', 'bob', 'Never watched']));
  });

  test('empty payload renders the no-data placeholder', async () => {
    codecsHost();
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData({ Movies: [], TvShows: [], Other: [], Music: [], Books: [], Libraries: [] });
    await flush();
    expect(document.getElementById('codecsContent')!.textContent).toContain('No data');
  });

  test('missing codecsContent container is tolerated', () => {
    document.body.innerHTML = '';
    expect(() => ui.fillCodecsData(richData())).not.toThrow();
  });
});

describe('watched tooltip data (buildWatchedTooltipLibraries)', () => {
  test('watched donut tooltip names the user and library', async () => {
    codecsHost();
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();

    const container = document
      .querySelector('.chart-box[data-chart-id="watched"] .donut-container') as HTMLElement;
    const aliceSeg = [...container.querySelectorAll('.donut-segment')].find(
      (s) => (s as HTMLElement).dataset.codec === 'alice',
    ) as HTMLElement;
    ui.showDonutTooltip(container, { clientX: 5, clientY: 5 }, aliceSeg);
    const tip = container.querySelector('.donut-tooltip')!;
    expect(tip.classList.contains('visible')).toBe(true);
    expect(tip.textContent).toContain('alice');
    expect(tip.textContent).toContain('Movies');
  });
});

describe('collectDrilldownMeta resolves per chart from live scan data', () => {
  test('resolution/audio/subtitle metas resolve against the last scan', async () => {
    codecsHost();
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();

    expect(ui.collectDrilldownMeta('resolutions')).toEqual({
      '/m/a.mkv': '1920x1080',
      '/m/d.mkv': '3840x2160',
    });
    expect(ui.collectDrilldownMeta('audioLanguages')).toEqual({
      '/m/a.mkv': 'English, Director',
      '/m/b.mkv': 'Deutsch',
    });
    expect(ui.collectDrilldownMeta('subtitleLanguages')).toEqual({ '/m/a.mkv': 'English SDH' });
    expect(ui.collectDrilldownMeta('containers')).toBeNull();
  });
});

describe('donut segment keyboard activation opens the drilldown', () => {
  test('Enter on a segment triggers the matching codec row', async () => {
    codecsHost();
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    ui.fillCodecsData(richData());
    await flush();

    const vc = document.querySelector('.chart-box[data-chart-id="videoCodecs"]')!;
    const seg = [...vc.querySelectorAll('.donut-segment')].find(
      (s) => (s as HTMLElement).dataset.codec === 'h264',
    ) as HTMLElement;
    keydown(seg, 'Enter');
    const row = [...vc.querySelectorAll('.codec-clickable')].find(
      (r) => (r as HTMLElement).dataset.codec === 'h264',
    )!;
    expect(row.classList.contains('codec-row-active')).toBe(true);
    expect(document.getElementById('codecDetail_videoCodecs')!.textContent).toContain('a.mkv');
  });
});

describe('keyboard card reorder lifecycle (drag engine via fillCodecsData)', () => {
  // Fresh scope so persisted-order state starts clean for grab/move/drop assertions.
  const fresh = freshConfigPage();

  async function render() {
    codecsHost(fresh);
    const c = ajaxStub({
      'JellyfinHelper/Configuration/CodecCardOrder': (opts: any) =>
        Promise.resolve({ codecCardOrder: JSON.parse(opts.data).codecCardOrder }),
      'JellyfinHelper/Configuration': {},
    });
    fresh.fillCodecsData(richData());
    await flush();
    return c;
  }

  test('grab + arrow-move + drop persists the new order', async () => {
    const calls = await render();
    const row = document.querySelector('#codecsContent .charts-row')!;
    const ids = () => [...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    const before = ids();
    const first = row.querySelector('.chart-box .codec-card-handle')!;

    keydown(first, 'Enter'); // grab
    expect(first.getAttribute('aria-grabbed')).toBe('true');
    keydown(first, 'ArrowDown'); // move one slot toward the end
    const moved = ids();
    expect(moved[0]).toBe(before[1]);
    expect(moved[1]).toBe(before[0]);
    keydown(first, 'Enter'); // drop + save
    await flush();
    expect(first.getAttribute('aria-grabbed')).toBe('false');

    const put = calls.find((c) => String(c.url).includes('CodecCardOrder'));
    expect(put).toBeTruthy();
    const body = JSON.parse(put.data).codecCardOrder;
    // Persisted order rebases onto canonical: the moved pair swaps the two leading slots.
    expect(body[0]).toBe(before[1]);
    expect(body[1]).toBe(before[0]);
  });

  test('grab then immediate drop with no move sends no PUT', async () => {
    const calls = await render();
    const handle = document.querySelector('#codecsContent .codec-card-handle')!;
    keydown(handle, 'Enter'); // grab
    keydown(handle, 'Enter'); // drop, unmoved
    await flush();
    expect(calls.some((c) => String(c.url).includes('CodecCardOrder'))).toBe(false);
  });

  test('Escape restores the grabbed card to its origin slot', async () => {
    await render();
    const row = document.querySelector('#codecsContent .charts-row')!;
    const ids = () => [...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    const origin = ids();
    const handle = row.querySelector('.chart-box .codec-card-handle')!;
    keydown(handle, 'Enter');
    keydown(handle, 'ArrowDown');
    expect(ids()[0]).toBe(origin[1]);
    keydown(handle, 'Escape');
    expect(ids()).toEqual(origin);
    expect(handle.getAttribute('aria-grabbed')).toBe('false');
  });

  test('arrow key without a grab does nothing', async () => {
    await render();
    const row = document.querySelector('#codecsContent .charts-row')!;
    const before = [...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    const handle = row.querySelector('.chart-box .codec-card-handle')!;
    keydown(handle, 'ArrowDown');
    const after = [...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    expect(after).toEqual(before);
  });
});

function pointerEvt(type: string, props: Record<string, unknown> = {}) {
  const e = new window.Event(type, { bubbles: true, cancelable: true });
  Object.assign(e, { pointerId: 1, pointerType: 'mouse', button: 0, clientX: 0, clientY: 0 }, props);
  return e;
}

// Lays a deterministic 2-up grid onto the rendered cards so the drag engine's rect reads
// (home band, siblings, rows) resolve to real numbers under jsdom's zeroed layout.
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
  // Reduced motion takes the synchronous FLIP path (plain mutate, no rAF glide) so reorder
  // assertions settle immediately under fake timers. The global afterEach unstubs it again.
  vi.stubGlobal('matchMedia', () => ({ matches: true }));
  return cards;
}

describe('pointer drag reorder (via fillCodecsData handles)', () => {
  const fresh = freshConfigPage();

  async function render(routes: Record<string, unknown> = {}) {
    codecsHost(fresh);
    const calls = ajaxStub({
      'JellyfinHelper/Configuration/CodecCardOrder': (opts: any) =>
        Promise.resolve({ codecCardOrder: JSON.parse(opts.data).codecCardOrder }),
      'JellyfinHelper/Configuration': {},
      ...routes,
    });
    fresh.fillCodecsData(richData());
    await flush();
    return calls;
  }

  test('mouse pointerdown below lift threshold then up never arms a drag', async () => {
    await render();
    const handle = document.querySelector('#codecsContent .codec-card-handle')!;
    handle.dispatchEvent(pointerEvt('pointerdown', { clientX: 10, clientY: 10 }));
    // A one-pixel nudge stays under CODEC_MOUSE_LIFT_PX, so no clone is created.
    window.dispatchEvent(pointerEvt('pointermove', { clientX: 11, clientY: 10 }));
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
    window.dispatchEvent(pointerEvt('pointerup'));
    expect(handle.getAttribute('aria-grabbed')).toBe('false');
  });

  test('non-primary mouse button is ignored', async () => {
    await render();
    const handle = document.querySelector('#codecsContent .codec-card-handle')!;
    handle.dispatchEvent(pointerEvt('pointerdown', { button: 2, clientX: 5, clientY: 5 }));
    window.dispatchEvent(pointerEvt('pointermove', { clientX: 60, clientY: 60 }));
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
  });

  test('mouse lift creates a clone, drag to another slot reorders and the drop persists', async () => {
    const calls = await render();
    const cards = stubGrid();
    const row = document.querySelector('#codecsContent .charts-row')!;
    const ids = () => [...row.querySelectorAll('.chart-box')].map((b) => (b as HTMLElement).dataset.chartId);
    const before = ids();
    const handle = cards[0].querySelector('.codec-card-handle')!;

    handle.dispatchEvent(pointerEvt('pointerdown', { clientX: 10, clientY: 10 }));
    // Past the lift threshold: a clone is parented to the body and the source is hidden.
    window.dispatchEvent(pointerEvt('pointermove', { clientX: 40, clientY: 40 }));
    expect(document.querySelector('.codec-drag-clone')).not.toBeNull();
    expect(cards[0].classList.contains('codec-dragging-source')).toBe(true);
    expect(handle.getAttribute('aria-grabbed')).toBe('true');

    // Drag the pointer well below the grid: findSlotIndex appends, so the source leaves slot 0.
    window.dispatchEvent(pointerEvt('pointermove', { clientX: 200, clientY: 500 }));
    expect(ids()[0]).not.toBe(before[0]);
    expect(ids().at(-1)).toBe(before[0]);

    window.dispatchEvent(pointerEvt('pointerup', { clientX: 200, clientY: 500 }));
    await flush();
    // Clone cleaned up, aria reset, order persisted.
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
    expect(handle.getAttribute('aria-grabbed')).toBe('false');
    const put = calls.find((c) => String(c.url).includes('CodecCardOrder'));
    expect(put).toBeTruthy();
    expect(put.type).toBe('PUT');
  });

  test('pointerdown while a drag is already live is ignored', async () => {
    await render();
    const cards = stubGrid();
    const h0 = cards[0].querySelector('.codec-card-handle')!;
    const h1 = cards[1].querySelector('.codec-card-handle')!;
    h0.dispatchEvent(pointerEvt('pointerdown', { clientX: 10, clientY: 10 }));
    window.dispatchEvent(pointerEvt('pointermove', { clientX: 40, clientY: 40 }));
    // A second grip cannot start a competing drag while the first is active.
    h1.dispatchEvent(pointerEvt('pointerdown', { pointerId: 2, clientX: 210, clientY: 10 }));
    expect(h1.getAttribute('aria-grabbed')).toBe('false');
    window.dispatchEvent(pointerEvt('pointerup', { clientX: 40, clientY: 40 }));
  });
});

describe('touch drag arming', () => {
  const fresh = freshConfigPage();

  async function render() {
    codecsHost(fresh);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    fresh.fillCodecsData(richData());
    await flush();
  }

  test('long-press arms a touch drag; movement before the timer cancels it', async () => {
    await render();
    stubGrid();
    const handle = document.querySelector('#codecsContent .codec-card-handle')!;
    handle.dispatchEvent(pointerEvt('pointerdown', { pointerType: 'touch', button: -1, clientX: 10, clientY: 10 }));
    // Before the long-press timer fires, a big move is a scroll and must cancel arming.
    window.dispatchEvent(pointerEvt('pointermove', { pointerType: 'touch', clientX: 10, clientY: 60 }));
    await vi.advanceTimersByTimeAsync(500);
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
  });

  test('long-press without movement arms the drag and lifts a clone', async () => {
    await render();
    stubGrid();
    const handle = document.querySelector('#codecsContent .codec-card-handle')!;
    handle.dispatchEvent(pointerEvt('pointerdown', { pointerType: 'touch', button: -1, clientX: 10, clientY: 10 }));
    await vi.advanceTimersByTimeAsync(500);
    expect(document.querySelector('.codec-drag-clone')).not.toBeNull();
    expect(handle.getAttribute('aria-grabbed')).toBe('true');
    // A drop with no travel does not persist.
    window.dispatchEvent(pointerEvt('pointerup', { clientX: 10, clientY: 10 }));
    await flush();
    expect(document.querySelector('.codec-drag-clone')).toBeNull();
  });
});

describe('donut mouse and touch interaction (attachDonutHoverTooltips)', () => {
  const fresh = freshConfigPage();

  async function render() {
    codecsHost(fresh);
    ajaxStub({ 'JellyfinHelper/Configuration': {} });
    fresh.fillCodecsData(richData());
    await flush();
  }

  function firstSegPath() {
    const vc = document.querySelector('.chart-box[data-chart-id="videoCodecs"]')!;
    const seg = [...vc.querySelectorAll('.donut-segment')].find(
      (s) => (s as HTMLElement).dataset.codec === 'h264',
    ) as HTMLElement;
    return { container: vc.querySelector('.donut-container') as HTMLElement, seg, path: seg.querySelector('path')! };
  }

  test('mouseenter shows the tooltip, mousemove keeps it, mouseleave hides it', async () => {
    await render();
    const { container, seg, path } = firstSegPath();
    path.dispatchEvent(Object.assign(new window.Event('mouseenter'), { clientX: 10, clientY: 10 }));
    const tip = container.querySelector('.donut-tooltip')!;
    expect(tip.classList.contains('visible')).toBe(true);
    expect(seg.classList.contains('donut-segment-hover')).toBe(true);
    path.dispatchEvent(Object.assign(new window.Event('mousemove'), { clientX: 12, clientY: 12 }));
    expect(tip.classList.contains('visible')).toBe(true);
    path.dispatchEvent(Object.assign(new window.Event('mouseleave'), {}));
    expect(tip.classList.contains('visible')).toBe(false);
    expect(seg.classList.contains('donut-segment-hover')).toBe(false);
  });

  test('desktop click on a segment opens the drill-down', async () => {
    await render();
    const { seg, path } = firstSegPath();
    path.dispatchEvent(new window.Event('click', { bubbles: true }));
    expect(fresh.isSegmentPanelOpen(seg)).toBe(true);
    expect(document.getElementById('codecDetail_videoCodecs')!.textContent).toContain('a.mkv');
  });

  test('touchend taps open the tooltip + panel, a second tap toggles both off', async () => {
    await render();
    const { container, seg, path } = firstSegPath();
    const touchEvt = () =>
      Object.assign(new window.Event('touchend', { bubbles: true, cancelable: true }), {
        changedTouches: [{ clientX: 20, clientY: 20 }],
      });
    path.dispatchEvent(touchEvt());
    expect(container.querySelector('.donut-tooltip')!.classList.contains('visible')).toBe(true);
    expect(fresh.isSegmentPanelOpen(seg)).toBe(true);
    // Tap again on the open segment: dismisses tooltip and closes the panel.
    path.dispatchEvent(touchEvt());
    expect(container.querySelector('.donut-tooltip')!.classList.contains('visible')).toBe(false);
    expect(fresh.isSegmentPanelOpen(seg)).toBe(false);
  });

  test('touch-originated synthetic mouse events are suppressed after a tap', async () => {
    await render();
    const { container, path } = firstSegPath();
    path.dispatchEvent(
      Object.assign(new window.Event('touchend', { bubbles: true, cancelable: true }), {
        changedTouches: [{ clientX: 20, clientY: 20 }],
      }),
    );
    // The trailing compat mouseenter within the guard window must not re-show after the tap toggled.
    const before = container.querySelector('.donut-tooltip')!.classList.contains('visible');
    path.dispatchEvent(Object.assign(new window.Event('mouseleave'), {}));
    // mouseleave is guarded (<800ms since touch), so it is a no-op, not a crash.
    expect(container.querySelector('.donut-tooltip')!.classList.contains('visible')).toBe(before);
  });

  test('tapping outside any segment clears a live tooltip', async () => {
    await render();
    const { container, path } = firstSegPath();
    path.dispatchEvent(
      Object.assign(new window.Event('touchend', { bubbles: true, cancelable: true }), {
        changedTouches: [{ clientX: 20, clientY: 20 }],
      }),
    );
    expect(container.querySelector('.donut-tooltip')!.classList.contains('visible')).toBe(true);
    // An outside touchstart dismisses the stranded tooltip.
    document.body.dispatchEvent(new window.Event('touchstart', { bubbles: true }));
    expect(container.querySelector('.donut-tooltip')!.classList.contains('visible')).toBe(false);
  });
});

describe('card order persistence paths', () => {
  test('server CodecCardOrder (PascalCase) wins and re-renders', async () => {
    const fresh = freshConfigPage();
    codecsHost(fresh);
    ajaxStub({ 'JellyfinHelper/Configuration': { CodecCardOrder: ['watched', 'containers'] } });
    fresh.fillCodecsData(richData());
    await flush();
    const ids = [...document.querySelectorAll('#codecsContent .chart-box')].map(
      (b) => (b as HTMLElement).dataset.chartId,
    );
    // Persisted order floats watched and containers to the front.
    expect(ids.indexOf('watched')).toBeLessThan(ids.indexOf('resolutions'));
    expect(ids[0]).toBe('watched');
  });

  test('lowercase codecCardOrder key is also honored', async () => {
    const fresh = freshConfigPage();
    codecsHost(fresh);
    ajaxStub({ 'JellyfinHelper/Configuration': { codecCardOrder: ['videoCodecs'] } });
    fresh.fillCodecsData(richData());
    await flush();
    const ids = [...document.querySelectorAll('#codecsContent .chart-box')].map(
      (b) => (b as HTMLElement).dataset.chartId,
    );
    expect(ids[0]).toBe('videoCodecs');
  });

  test('a failed save rolls back the order and re-renders from the server copy', async () => {
    const fresh = freshConfigPage();
    codecsHost(fresh);
    ajaxStub({
      'JellyfinHelper/Configuration/CodecCardOrder': () => Promise.reject({ status: 500 }),
      'JellyfinHelper/Configuration': {},
    });
    fresh.fillCodecsData(richData());
    await flush();

    const row = document.querySelector('#codecsContent .charts-row')!;
    const originalFirst = (row.querySelector('.chart-box') as HTMLElement).dataset.chartId;
    const handle = row.querySelector('.chart-box .codec-card-handle')!;
    const kd = (k: string) => handle.dispatchEvent(new window.KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true }));
    kd('Enter');
    kd('ArrowDown');
    kd('Enter'); // drop -> save -> rejected -> rollback + re-render
    await flush();

    // After rollback the DOM returns to the server-held order (original first card leads again).
    const afterFirst = (document.querySelector('#codecsContent .chart-box') as HTMLElement).dataset.chartId;
    expect(afterFirst).toBe(originalFirst);
  });

  test('save feedback icon appears on the reordered card and fades out', async () => {
    const fresh = freshConfigPage();
    codecsHost(fresh);
    ajaxStub({
      'JellyfinHelper/Configuration/CodecCardOrder': (opts: any) =>
        Promise.resolve({ codecCardOrder: JSON.parse(opts.data).codecCardOrder }),
      'JellyfinHelper/Configuration': {},
    });
    fresh.fillCodecsData(richData());
    await flush();

    const row = document.querySelector('#codecsContent .charts-row')!;
    const handle = row.querySelector('.chart-box .codec-card-handle')!;
    const card = handle.closest('.chart-box')!;
    const kd = (k: string) => handle.dispatchEvent(new window.KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true }));
    kd('Enter');
    kd('ArrowDown');
    kd('Enter');
    await flush();

    // The moved card (now at a new slot) shows a success checkmark that fades after its timer.
    const movedCard = [...document.querySelectorAll('#codecsContent .chart-box')].find(
      (b) => b.querySelector('.codec-save-icon'),
    )!;
    const icon = movedCard.querySelector('.codec-save-icon')!;
    expect(icon.classList.contains('codec-save-icon--visible')).toBe(true);
    expect(icon.classList.contains('codec-save-icon--error')).toBe(false);
    await vi.advanceTimersByTimeAsync(2100);
    expect(icon.classList.contains('codec-save-icon--visible')).toBe(false);
  });
});
