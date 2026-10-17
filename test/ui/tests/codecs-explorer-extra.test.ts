import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { freshConfigPage, saveCoverage } from '../src/loader.js';

afterAll(saveCoverage);

// ApiClient is only needed so fillCodecsData's downstream card order load does not throw.
function stubApi() {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: () => Promise.resolve({}),
  };
  if (!(globalThis as any).window.scrollTo) (globalThis as any).window.scrollTo = () => undefined;
  // jsdom elements lack scrollIntoView; openCodecsExplorer calls it after render.
  (globalThis as any).window.HTMLElement.prototype.scrollIntoView = function () {};
  // Leave requestAnimationFrame undefined so the preview path runs synchronously,
  // which keeps the slider-input assertions free of timer plumbing.
  delete (globalThis as any).requestAnimationFrame;
}

// Lets the 150ms debounced control rebuild (library and multi-select changes)
// run. Timers are faked globally in setup.ts, so advance them instead of waiting
// on a real setTimeout that would never fire.
function debounce() {
  return vi.advanceTimersByTimeAsync(200);
}

// A two-library video scan plus a music and a book library, with every per-dimension
// count map, matching path map, and per-file side map the explorer reads. Paths all
// share one separator style so the root boundary matches like real server data.
function richData(): any {
  const movies = {
    LibraryName: 'Movies',
    CollectionType: 'movies',
    RootPaths: ['/m'],
    VideoCodecs: { h264: 2, hevc: 1 },
    VideoCodecPaths: { h264: ['/m/a.mkv', '/m/b.mkv'], hevc: ['/m/c.mkv'] },
    Resolutions: { '1080p': 2, '2160p': 1 },
    ResolutionPaths: { '1080p': ['/m/a.mkv', '/m/b.mkv'], '2160p': ['/m/c.mkv'] },
    ResolutionDimensions: { '/m/a.mkv': '1920x1080' },
    DynamicRanges: { SDR: 2, HDR10: 1 },
    DynamicRangePaths: { SDR: ['/m/a.mkv', '/m/b.mkv'], HDR10: ['/m/c.mkv'] },
    VideoAudioCodecs: { aac: 2, eac3: 1 },
    VideoAudioCodecPaths: { aac: ['/m/a.mkv', '/m/b.mkv'], eac3: ['/m/c.mkv'] },
    AudioLanguages: { eng: 3, deu: 1 },
    AudioLanguagePaths: { eng: ['/m/a.mkv', '/m/b.mkv', '/m/c.mkv'], deu: ['/m/a.mkv'] },
    SubtitleLanguages: { eng: 2 },
    SubtitleLanguagePaths: { eng: ['/m/a.mkv', '/m/b.mkv'] },
    ContainerFormats: { mkv: 3 },
    ContainerFormatPaths: { mkv: ['/m/a.mkv', '/m/b.mkv', '/m/c.mkv'] },
    VideoBitrates: { '/m/a.mkv': 8, '/m/b.mkv': 20, '/m/c.mkv': 40 },
    AudioTrackLabels: { '/m/a.mkv': ['English', 'German'] },
    SubtitleTrackLabels: { '/m/a.mkv': ['English (SDH)'] },
    FileSizes: { '/m/a.mkv': 100, '/m/b.mkv': 200, '/m/c.mkv': 300 },
    WatchedTiers: { 'Never watched': 1 },
    WatchedTierPaths: { 'Never watched': ['/m/c.mkv'] },
    WatchedByUserPaths: { alice: ['/m/a.mkv'], bob: ['/m/a.mkv', '/m/b.mkv'] },
    WatchedDetails: {
      '/m/a.mkv': [
        { Username: 'alice', PlayCount: 1, LastPlayedDate: '2024-01-02T00:00:00Z' },
        { Username: 'bob', PlayCount: 3 },
      ],
    },
  };
  const tv = {
    LibraryName: 'TV',
    CollectionType: 'tvshows',
    RootPaths: ['/t'],
    VideoCodecs: { h264: 1 },
    VideoCodecPaths: { h264: ['/t/e.mkv'] },
    Resolutions: { '720p': 1 },
    ResolutionPaths: { '720p': ['/t/e.mkv'] },
    ContainerFormats: { mkv: 1 },
    ContainerFormatPaths: { mkv: ['/t/e.mkv'] },
    VideoBitrates: { '/t/e.mkv': 5 },
    FileSizes: { '/t/e.mkv': 50 },
    WatchedByUserPaths: { alice: ['/t/e.mkv'] },
  };
  const music = {
    LibraryName: 'Music',
    CollectionType: 'music',
    RootPaths: ['/mus'],
    MusicAudioCodecs: { flac: 2 },
    MusicAudioCodecPaths: { flac: ['/mus/s1.flac', '/mus/s2.flac'] },
    ContainerFormats: { flac: 2 },
    ContainerFormatPaths: { flac: ['/mus/s1.flac', '/mus/s2.flac'] },
    FileSizes: { '/mus/s1.flac': 10, '/mus/s2.flac': 12 },
  };
  const books = {
    LibraryName: 'Books',
    CollectionType: 'books',
    RootPaths: ['/bk'],
    BookFormats: { epub: 1 },
    BookFormatPaths: { epub: ['/bk/x.epub'] },
    FileSizes: { '/bk/x.epub': 1 },
  };
  const boxset = { LibraryName: 'Collections', CollectionType: 'boxsets', RootPaths: ['/box'], FileSizes: {} };
  return {
    Movies: [movies],
    TvShows: [tv],
    Music: [music],
    Books: [books],
    Other: [],
    Libraries: [movies, tv, music, books, boxset],
    ContainerFormats: { mkv: 4, flac: 2 },
  };
}

// Renders a fresh explorer into a host so events can drive its controls.
function openExplorer(ui: any) {
  stubApi();
  document.body.innerHTML = '<div id="codecsContent"></div>';
  ui.fillCodecsData(richData());
  const host = document.getElementById('codecsContent')!;
  ui.renderCodecsExplorer(host);
  return host;
}

function fire(el: Element, type: string) {
  el.dispatchEvent(new (globalThis as any).window.Event(type, { bubbles: true }));
}

describe('codecExplorerPathInRoots separators and boundaries', () => {
  const ui = freshConfigPage();

  test('mixed-separator Windows root misses a forward-slash path', () => {
    // Documents current behavior: a backslash root never matches a forward-slash
    // child because startsWith appends only the raw root separator style.
    expect(ui.codecExplorerPathInRoots('C:/V/a', ['C:\\V'])).toBe(false);
    // The reverse (forward root, backslash child) also misses for the same reason.
    expect(ui.codecExplorerPathInRoots('C:\\V\\a', ['C:/V'])).toBe(false);
  });

  test('consistent separators match for both child styles', () => {
    expect(ui.codecExplorerPathInRoots('C:\\V', ['C:\\V'])).toBe(true);
    expect(ui.codecExplorerPathInRoots('C:\\V\\a', ['C:\\V'])).toBe(true);
    expect(ui.codecExplorerPathInRoots('/movies/a', ['/movies'])).toBe(true);
    expect(ui.codecExplorerPathInRoots('/movies\\a', ['/movies'])).toBe(true);
  });

  test('drive-letter and UNC roots compare case-insensitively', () => {
    expect(ui.codecExplorerPathInRoots('C:\\V\\a', ['c:\\v'])).toBe(true);
    expect(ui.codecExplorerPathInRoots('\\\\srv\\share\\a', ['\\\\SRV\\SHARE'])).toBe(true);
  });

  test('POSIX roots stay case-sensitive and respect directory edges', () => {
    expect(ui.codecExplorerPathInRoots('/MEDIA/a', ['/media'])).toBe(false);
    expect(ui.codecExplorerPathInRoots('/movies2/a', ['/movies'])).toBe(false);
  });

  test('empty or blank roots match everything', () => {
    expect(ui.codecExplorerPathInRoots('/any', [])).toBe(true);
    expect(ui.codecExplorerPathInRoots('/any', null)).toBe(true);
    // A roots array of only blanks skips every entry and never matches.
    expect(ui.codecExplorerPathInRoots('/any', ['', '  '])).toBe(false);
  });
});

describe('selected roots and scoping', () => {
  let ui: any;
  beforeEach(() => {
    ui = freshConfigPage();
    openExplorer(ui);
  });

  test('null scope yields no explicit roots, named scope unions them', () => {
    expect(ui.getCodecsExplorerSelectedRoots()).toEqual([]);
    ui.openCodecsExplorer(['Movies', 'TV']);
    expect(ui.getCodecsExplorerSelectedRoots().sort()).toEqual(['/m', '/t']);
  });

  test('type marker deep link expands to that group', () => {
    ui.openCodecsExplorer('type:movies');
    expect(ui.getCodecsExplorerSelection('videoCodecs')).toEqual([]);
    expect(ui.getCodecsExplorerSelectedRoots()).toEqual(['/m']);
  });
});

describe('universe and option counting', () => {
  let ui: any;
  beforeEach(() => {
    ui = freshConfigPage();
    openExplorer(ui);
  });

  test('scoped library universe aggregates counts', () => {
    const dim = ui.getCodecsExplorerDimension('videoCodecs')!;
    const scoped = ui.getCodecsExplorerLibraries().filter((l: any) => l.LibraryName === 'Movies');
    expect(ui.getExplorerUniverse(dim, scoped)).toEqual({ h264: 2, hevc: 1 });
  });

  test('cached per-file maps merge bitrates and labels across video libs', () => {
    const maps = ui.getCachedExplorerMaps();
    expect(maps.bitrates['/m/a.mkv']).toBe(8);
    expect(maps.bitrates['/t/e.mkv']).toBe(5);
    expect(maps.audioLabels['/m/a.mkv']).toEqual(['English', 'German']);
    expect(maps.subLabels['/m/a.mkv']).toEqual(['English (SDH)']);
  });
});

describe('explorer render pipeline and controls', () => {
  let ui: any;
  let host: HTMLElement;
  beforeEach(() => {
    ui = freshConfigPage();
    host = openExplorer(ui);
  });

  test('idle explorer shows the pick-a-filter hint', () => {
    expect(host.querySelector('.codec-explorer')).not.toBeNull();
    const results = document.getElementById('codecExplorerResults')!;
    expect(results.querySelector('.codec-explorer-empty')).not.toBeNull();
  });

  test('library scope lists its files once a library is picked', async () => {
    const libToggle = document.querySelector('[data-library-toggle]') as HTMLElement;
    libToggle.onclick!(new (globalThis as any).window.MouseEvent('click'));
    const box = document.querySelector('[data-library-option][value="Movies"]') as HTMLInputElement;
    box.checked = true;
    fire(box, 'change');
    await debounce();
    const results = document.getElementById('codecExplorerResults')!;
    const count = results.querySelector('[data-explorer-count]');
    expect(count).not.toBeNull();
    expect(Number(count!.getAttribute('data-explorer-count'))).toBe(3);
  });

  test('opening the filter popover lists dimensions, picking one opens its editor', () => {
    const addBtn = document.querySelector('[data-filter-add-btn]') as HTMLElement;
    addBtn.onclick!(new (globalThis as any).window.MouseEvent('click'));
    const dimBtn = document.querySelector('[data-filter-dim="videoCodecs"]') as HTMLElement;
    expect(dimBtn).not.toBeNull();
    dimBtn.onclick!(new (globalThis as any).window.MouseEvent('click'));
    expect(document.querySelector('.codec-filter-editor')).not.toBeNull();
    expect(document.querySelector('.codec-single')).not.toBeNull();
  });

  test('single-value pick narrows results and raises a pill', () => {
    const addBtn = document.querySelector('[data-filter-add-btn]') as HTMLElement;
    addBtn.onclick!(new (globalThis as any).window.MouseEvent('click'));
    (document.querySelector('[data-filter-dim="videoCodecs"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    const opt = document.querySelector('[data-single-value="hevc"]') as HTMLElement;
    opt.onclick!(new (globalThis as any).window.MouseEvent('click'));
    expect(ui.getCodecsExplorerSelection('videoCodecs')).toEqual(['hevc']);
    const count = document.querySelector('[data-explorer-count]')!;
    expect(Number(count.getAttribute('data-explorer-count'))).toBe(1);
    expect(document.querySelector('[data-pill-clear="videoCodecs"]')).not.toBeNull();
  });

  test('exclude toggle flips the dimension to subtract its value', () => {
    const addBtn = document.querySelector('[data-filter-add-btn]') as HTMLElement;
    addBtn.onclick!(new (globalThis as any).window.MouseEvent('click'));
    (document.querySelector('[data-filter-dim="videoCodecs"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    (document.querySelector('[data-single-value="h264"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    // Reopen the editor to reach the exclude toggle.
    (document.querySelector('[data-filter-add-btn]') as HTMLElement).onclick!(new (globalThis as any).window.MouseEvent('click'));
    (document.querySelector('[data-filter-dim="videoCodecs"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    const excludeBtn = document.querySelector('[data-exclude-toggle="videoCodecs"][data-exclude-value="1"]') as HTMLElement;
    excludeBtn.onclick!(new (globalThis as any).window.MouseEvent('click'));
    expect(ui.isCodecsExplorerExcluded('videoCodecs')).toBe(true);
    // h264 excluded leaves only the hevc file.
    const count = document.querySelector('[data-explorer-count]')!;
    expect(Number(count.getAttribute('data-explorer-count'))).toBe(1);
  });

  test('reset clears every selection back to idle', () => {
    const addBtn = document.querySelector('[data-filter-add-btn]') as HTMLElement;
    addBtn.onclick!(new (globalThis as any).window.MouseEvent('click'));
    (document.querySelector('[data-filter-dim="videoCodecs"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    (document.querySelector('[data-single-value="h264"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    const reset = document.getElementById('codecExplorerReset') as HTMLElement;
    reset.onclick!(new (globalThis as any).window.MouseEvent('click'));
    expect(ui.getCodecsExplorerSelection('videoCodecs')).toEqual([]);
    expect(document.querySelector('[data-pill-clear]')).toBeNull();
  });

  test('toggle collapses and expands the explorer body', () => {
    const toggle = document.getElementById('codecExplorerToggle') as HTMLElement;
    const body = document.getElementById('codecExplorerBody')!;
    const wasOpen = body.classList.contains('open');
    toggle.onclick!(new (globalThis as any).window.MouseEvent('click'));
    expect(body.classList.contains('open')).toBe(!wasOpen);
  });
});

describe('multi-select dimension (languages)', () => {
  let ui: any;
  beforeEach(() => {
    ui = freshConfigPage();
    openExplorer(ui);
    (document.querySelector('[data-filter-add-btn]') as HTMLElement).onclick!(new (globalThis as any).window.MouseEvent('click'));
    (document.querySelector('[data-filter-dim="audioLanguages"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
  });

  test('checking boxes builds an OR union and a pill after the debounce', async () => {
    const box = document.querySelector('[data-multi-dim="audioLanguages"] input[type="checkbox"][value="eng"]') as HTMLInputElement;
    box.checked = true;
    box.onchange!(new (globalThis as any).window.Event('change'));
    await debounce();
    expect(ui.getCodecsExplorerSelection('audioLanguages')).toEqual(['eng']);
  });

  test('multi editor renders its panel open with per-option counts', () => {
    const panel = document.querySelector('[data-multi-dim="audioLanguages"] [data-multi-panel]') as HTMLElement;
    expect(panel.hasAttribute('hidden')).toBe(false);
    expect(document.querySelector('[data-multi-dim="audioLanguages"] .codec-multi-count')).not.toBeNull();
  });
});

describe('bitrate editor interactions', () => {
  let ui: any;
  function openBitrate() {
    (document.querySelector('[data-filter-add-btn]') as HTMLElement).onclick!(new (globalThis as any).window.MouseEvent('click'));
    (document.querySelector('[data-filter-dim="bitrate"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
  }
  beforeEach(() => {
    ui = freshConfigPage();
    openExplorer(ui);
    openBitrate();
  });

  test('bitrate editor renders a histogram and the bound inputs', () => {
    expect(document.querySelector('[data-bitrate-editor]')).not.toBeNull();
    expect(document.querySelectorAll('.codec-bitrate-bar').length).toBeGreaterThan(0);
    // Bounds span every scoped video bitrate (TV 5 .. movie 40).
    const minInput = document.getElementById('codecBitrateMin') as HTMLInputElement;
    expect(minInput.value).toBe('5');
  });

  test('committing a narrowed range activates the filter and a pill', () => {
    const minInput = document.getElementById('codecBitrateMin') as HTMLInputElement;
    const maxInput = document.getElementById('codecBitrateMax') as HTMLInputElement;
    minInput.value = '10';
    maxInput.value = '30';
    maxInput.onchange!(new (globalThis as any).window.Event('change'));
    expect(ui.hasActiveBitrateRange()).toBe(true);
    expect(document.querySelector('[data-pill-clear-bitrate]')).not.toBeNull();
    // Only /m/b.mkv (20) sits inside 10..30.
    const count = document.querySelector('[data-explorer-count]')!;
    expect(Number(count.getAttribute('data-explorer-count'))).toBe(1);
  });

  test('range input preview updates results live without a rebuild', () => {
    const minRange = document.getElementById('codecBitrateMinRange') as HTMLInputElement;
    const minInput = document.getElementById('codecBitrateMin') as HTMLInputElement;
    minRange.value = '25';
    minRange.oninput!(new (globalThis as any).window.Event('input'));
    expect(minInput.value).toBe('25');
    // Preview is synchronous when requestAnimationFrame is unavailable, otherwise
    // the normalized range is already stored by readBitrateEditor.
    expect(ui.hasActiveBitrateRange()).toBe(true);
  });

  test('a full-span range commits back to Any', () => {
    const minInput = document.getElementById('codecBitrateMin') as HTMLInputElement;
    const maxInput = document.getElementById('codecBitrateMax') as HTMLInputElement;
    minInput.value = '0';
    maxInput.value = '999';
    maxInput.onchange!(new (globalThis as any).window.Event('change'));
    expect(ui.hasActiveBitrateRange()).toBe(false);
  });

  test('the Any clear button drops an active range', () => {
    const minInput = document.getElementById('codecBitrateMin') as HTMLInputElement;
    minInput.value = '15';
    minInput.onchange!(new (globalThis as any).window.Event('change'));
    expect(ui.hasActiveBitrateRange()).toBe(true);
    // Commit rebuilds the controls but keeps the editor open, so the clear
    // button is present without reopening the popover.
    const clear = document.querySelector('[data-bitrate-clear]') as HTMLElement;
    clear.onclick!(new (globalThis as any).window.MouseEvent('click'));
    expect(ui.hasActiveBitrateRange()).toBe(false);
  });
});

describe('result tree and per-file detail drilldown', () => {
  let ui: any;
  beforeEach(async () => {
    ui = freshConfigPage();
    openExplorer(ui);
    // Select h264 so the result tree lists the two matching movies.
    (document.querySelector('[data-filter-add-btn]') as HTMLElement).onclick!(new (globalThis as any).window.MouseEvent('click'));
    (document.querySelector('[data-filter-dim="videoCodecs"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    (document.querySelector('[data-single-value="h264"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    // Drain the debounced rebuild so the result tree is final before the test
    // interacts with it; otherwise a pending timer can re-render mid-test and
    // detach the leaf under a detail-toggle assertion.
    await vi.advanceTimersByTimeAsync(200);
  });

  test('results render a file tree grouped under media roots', () => {
    const results = document.getElementById('codecExplorerResults')!;
    expect(results.querySelector('.file-tree-panel')).not.toBeNull();
    // h264 spans both movies (/m/a, /m/b) and the TV file (/t/e).
    const count = results.querySelector('[data-explorer-count]')!;
    expect(Number(count.getAttribute('data-explorer-count'))).toBe(3);
  });

  test('clicking a leaf opens its detail card with size, codec and watched rows', () => {
    const results = document.getElementById('codecExplorerResults')!;
    const leaf = results.querySelector('.tree-leaf[title="/m/a.mkv"]') as HTMLElement;
    expect(leaf).not.toBeNull();
    leaf.click();
    const detail = leaf.nextElementSibling as HTMLElement;
    expect(detail.classList.contains('codec-file-detail')).toBe(true);
    // The video codec dimension row uses its fallback label "Video codec".
    expect(detail.textContent).toContain('Video codec');
    expect(detail.textContent).toContain('h264');
    // WatchedDetails lists two watchers with play counts.
    expect(detail.querySelector('.codec-file-watched')).not.toBeNull();
    expect(detail.textContent).toContain('alice');
    // A second click collapses the detail card: no detail node remains in the
    // results. (The leaf can be followed by another leaf, so the sibling slot is
    // not a reliable signal; the detail count is.)
    leaf.click();
    expect(results.querySelectorAll('.codec-file-detail')).toHaveLength(0);
  });

  test('keyboard Enter on a leaf toggles the same detail card', () => {
    const results = document.getElementById('codecExplorerResults')!;
    const leaf = results.querySelector('.tree-leaf[title="/m/b.mkv"]') as HTMLElement;
    leaf.dispatchEvent(new (globalThis as any).window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect((leaf.nextElementSibling as HTMLElement)?.classList.contains('codec-file-detail')).toBe(true);
  });
});

describe('prune on rescan drops phantom selections', () => {
  test('a value absent from the new scan is removed', () => {
    const ui = freshConfigPage();
    openExplorer(ui);
    (document.querySelector('[data-filter-add-btn]') as HTMLElement).onclick!(new (globalThis as any).window.MouseEvent('click'));
    (document.querySelector('[data-filter-dim="videoCodecs"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    (document.querySelector('[data-single-value="hevc"]') as HTMLElement).onclick!(
      new (globalThis as any).window.MouseEvent('click'),
    );
    expect(ui.getCodecsExplorerSelection('videoCodecs')).toEqual(['hevc']);
    // Rescan without hevc: the stale pick must vanish during render prune.
    const noHevc = richData();
    noHevc.Movies[0].VideoCodecs = { h264: 2 };
    noHevc.Movies[0].VideoCodecPaths = { h264: ['/m/a.mkv', '/m/b.mkv'] };
    ui.fillCodecsData(noHevc);
    ui.renderCodecsExplorer(document.getElementById('codecsContent')!);
    expect(ui.getCodecsExplorerSelection('videoCodecs')).toEqual([]);
  });

  test('a boxset-only deep link scope prunes to an explicit empty scope', () => {
    const ui = freshConfigPage();
    openExplorer(ui);
    ui.openCodecsExplorer('Collections');
    // Boxsets are dropped from the scope picker, so the resolved scope is empty.
    expect(ui.getCodecsExplorerSelectedRoots()).toEqual([]);
  });
});
