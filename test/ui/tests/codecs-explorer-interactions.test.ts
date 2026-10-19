import { afterAll, describe, expect, test, vi } from 'vitest';
import { ajaxRouter, flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

afterAll(saveCoverage);

function stubApi() {
  ajaxRouter({});
  if (!(globalThis as any).window.scrollTo) (globalThis as any).window.scrollTo = () => undefined;
  (globalThis as any).window.HTMLElement.prototype.scrollIntoView = function () {};
  delete (globalThis as any).requestAnimationFrame;
}

function fire(el: Element, type: string) {
  el.dispatchEvent(new (globalThis as any).window.Event(type, { bubbles: true }));
}

function click(el: Element) {
  el.dispatchEvent(new (globalThis as any).window.MouseEvent('click', { bubbles: true }));
}

function debounce() {
  return vi.advanceTimersByTimeAsync(200);
}

function openFilterPopover() {
  click(document.querySelector('[data-filter-add-btn]')!);
}

function openDim(dimId: string) {
  const pop = document.querySelector('[data-filter-pop]');
  if (!pop?.querySelector(`[data-filter-dim="${dimId}"]`)) {
    if (pop) openFilterPopover();
    openFilterPopover();
  }
  click(document.querySelector(`[data-filter-dim="${dimId}"]`)!);
}

function pickSingle(dimId: string, value: string) {
  openDim(dimId);
  click(document.querySelector(`[data-single-value="${value}"]`)!);
}

async function openExplorer(ui: UiBridge): Promise<HTMLElement> {
  stubApi();
  document.body.innerHTML = '<div id="codecsContent"></div>';
  ui.fillCodecsData(richData());
  await flush();
  return document.getElementById('codecsContent') as HTMLElement;
}

function richData(): any {
  const movies = {
    LibraryName: 'Movies',
    CollectionType: 'movies',
    RootPaths: ['/m'],
    VideoCodecs: { h264: 3, hevc: 1 },
    VideoCodecPaths: { h264: ['/m/a.mkv', '/m/b.mkv', '/m/sub/d.mkv'], hevc: ['/m/c.mkv'] },
    Resolutions: { '1080p': 3, '2160p': 1 },
    ResolutionPaths: { '1080p': ['/m/a.mkv', '/m/b.mkv', '/m/sub/d.mkv'], '2160p': ['/m/c.mkv'] },
    ResolutionDimensions: { '/m/a.mkv': '1920x1080', '/m/c.mkv': '3840x2160', '/m/sub/d.mkv': '1920x1080' },
    DynamicRanges: { SDR: 3, HDR10: 1 },
    DynamicRangePaths: { SDR: ['/m/a.mkv', '/m/b.mkv', '/m/sub/d.mkv'], HDR10: ['/m/c.mkv'] },
    VideoAudioCodecs: { aac: 3, eac3: 1 },
    VideoAudioCodecPaths: { aac: ['/m/a.mkv', '/m/b.mkv', '/m/sub/d.mkv'], eac3: ['/m/c.mkv'] },
    AudioLanguages: { eng: 3, deu: 2 },
    AudioLanguagePaths: { eng: ['/m/a.mkv', '/m/b.mkv', '/m/sub/d.mkv'], deu: ['/m/a.mkv', '/m/c.mkv'] },
    SubtitleLanguages: { eng: 2 },
    SubtitleLanguagePaths: { eng: ['/m/a.mkv', '/m/b.mkv'] },
    SubtitleTrackLabels: { '/m/a.mkv': ['English (SDH)'] },
    ContainerFormats: { mkv: 4 },
    ContainerFormatPaths: { mkv: ['/m/a.mkv', '/m/b.mkv', '/m/c.mkv', '/m/sub/d.mkv'] },
    VideoBitrates: { '/m/a.mkv': 8, '/m/b.mkv': 20, '/m/c.mkv': 40, '/m/sub/d.mkv': 12 },
    AudioTrackLabels: { '/m/a.mkv': ['English', 'German'] },
    FileSizes: { '/m/a.mkv': 100, '/m/b.mkv': 200, '/m/c.mkv': 300, '/m/sub/d.mkv': 150 },
    WatchedTiers: { 'Never watched': 1 },
    WatchedTierPaths: { 'Never watched': ['/m/c.mkv'] },
    WatchedByUserPaths: { alice: ['/m/a.mkv', '/m/sub/d.mkv'], bob: ['/m/a.mkv', '/m/b.mkv'] },
    WatchedDetails: {
      '/m/a.mkv': [
        { Username: 'alice', PlayCount: 1, LastPlayedDate: '2024-01-02T00:00:00Z' },
        { Username: 'bob', PlayCount: 3 },
      ],
      '/m/sub/d.mkv': [{ Username: 'alice', PlayCount: 2 }],
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
    VideoAudioCodecs: { aac: 1 },
    VideoAudioCodecPaths: { aac: ['/t/e.mkv'] },
    AudioLanguages: { eng: 1 },
    AudioLanguagePaths: { eng: ['/t/e.mkv'] },
    VideoBitrates: { '/t/e.mkv': 5 },
    FileSizes: { '/t/e.mkv': 50 },
    WatchedByUserPaths: { alice: ['/t/e.mkv'] },
    WatchedDetails: { '/t/e.mkv': [{ Username: 'alice', PlayCount: 1 }] },
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
  return {
    Movies: [movies],
    TvShows: [tv],
    Music: [music],
    Books: [books],
    Other: [],
    Libraries: [
      {
        LibraryName: 'Movies', CollectionType: 'movies', RootPaths: ['/m'], ContainerFormats: { mkv: 4 },
        FileSizes: { '/m/a.mkv': 100, '/m/b.mkv': 200, '/m/c.mkv': 300, '/m/sub/d.mkv': 150 },
        WatchedDetails: {
          '/m/a.mkv': [
            { Username: 'alice', PlayCount: 1, LastPlayedDate: '2024-01-02T00:00:00Z' },
            { Username: 'bob', PlayCount: 3 },
          ],
          '/m/sub/d.mkv': [{ Username: 'alice', PlayCount: 2 }],
        },
      },
      {
        LibraryName: 'TV', CollectionType: 'tvshows', RootPaths: ['/t'], ContainerFormats: { mkv: 1 },
        FileSizes: { '/t/e.mkv': 50 },
        WatchedDetails: { '/t/e.mkv': [{ Username: 'alice', PlayCount: 1 }] },
      },
      { LibraryName: 'Music', CollectionType: 'music', RootPaths: ['/mus'], ContainerFormats: { flac: 2 }, FileSizes: { '/mus/s1.flac': 10, '/mus/s2.flac': 12 } },
      { LibraryName: 'Books', CollectionType: 'books', RootPaths: ['/bk'], FileSizes: { '/bk/x.epub': 1 } },
      { LibraryName: 'Collections', CollectionType: 'boxsets', RootPaths: ['/box'] },
    ],
    ContainerFormats: { mkv: 5, flac: 2 },
  };
}

function orphanData(): any {
  return {
    Movies: [
      {
        LibraryName: 'Movies',
        CollectionType: 'movies',
        RootPaths: ['/m'],
        VideoCodecs: { h264: 2 },
        VideoCodecPaths: { h264: ['/m/a.mkv', '/orphan/x.mkv'] },
        ContainerFormats: { mkv: 2 },
        ContainerFormatPaths: { mkv: ['/m/a.mkv', '/orphan/x.mkv'] },
        VideoBitrates: { '/m/a.mkv': 8, '/orphan/x.mkv': 9 },
        FileSizes: { '/m/a.mkv': 1, '/orphan/x.mkv': 1 },
      },
    ],
    TvShows: [],
    Music: [],
    Books: [],
    Other: [],
    Libraries: [
      { LibraryName: 'Movies', CollectionType: 'movies', RootPaths: ['/m'], ContainerFormats: { mkv: 2 } },
    ],
    ContainerFormats: { mkv: 2 },
  };
}

function emptyScopeData(): any {
  return {
    Movies: [{ LibraryName: 'Movies', CollectionType: 'movies', RootPaths: ['/m'], FileSizes: {} }],
    TvShows: [],
    Music: [],
    Books: [],
    Other: [],
    Libraries: [{ LibraryName: 'Movies', CollectionType: 'movies', RootPaths: ['/m'], FileSizes: {} }],
    ContainerFormats: {},
  };
}

function histogramData(bitrates: Record<string, number | string>): any {
  return {
    Movies: [{ LibraryName: 'Movies', CollectionType: 'movies', RootPaths: ['/m'], VideoBitrates: bitrates }],
    TvShows: [],
    Music: [],
    Books: [],
    Other: [],
    Libraries: [{ LibraryName: 'Movies', CollectionType: 'movies', RootPaths: ['/m'] }],
    ContainerFormats: {},
  };
}

function resultCount(): number {
  const count = document.querySelector('[data-explorer-count]');
  return count ? Number(count.getAttribute('data-explorer-count')) : -1;
}

describe('explorer state, libraries and dimension index', () => {
  test('getCodecsExplorerState returns the shared state object', () => {
    const ui = freshConfigPage();
    const state = ui.getCodecsExplorerState();
    expect(state).toMatchObject({ libraries: null, filters: {}, expanded: false, scope: null, bitrateRange: null });
    expect(state).toBe(ui.getCodecsExplorerState());
  });

  test('getCodecsExplorerLibraries returns an empty array without scan data', () => {
    const ui = freshConfigPage();
    expect(ui.getCodecsExplorerLibraries()).toEqual([]);
  });

  test('buildDimPathIndex through getDimPathIndex returns empty maps for an unknown dimension', () => {
    const ui = freshConfigPage();
    const index = ui.getDimPathIndex('bogus');
    expect(Object.keys(index.single)).toHaveLength(0);
    expect(Object.keys(index.multi)).toHaveLength(0);
  });
});

describe('cached explorer maps respect the library scope', () => {
  test('mergeVideoMap, mergeLibraryMap and mergeWatcherMap skip unscoped libraries', () => {
    const ui = freshConfigPage();
    openExplorer(ui).then(() => {
      ui.getCodecsExplorerState().libraries = ['Movies'];
      const maps = ui.getCachedExplorerMaps();
      expect(maps.bitrates['/m/a.mkv']).toBe(8);
      expect(maps.bitrates['/t/e.mkv']).toBeUndefined();
      expect(maps.sizes['/m/b.mkv']).toBe(200);
      expect(maps.sizes['/t/e.mkv']).toBeUndefined();
      expect(maps.watchers['/m/a.mkv']).toHaveLength(2);
      expect(maps.watchers['/t/e.mkv']).toBeUndefined();
    });
  });
});

describe('bitrate bounds and histogram', () => {
  test('getBitrateBounds returns null when no file has a measured bitrate', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    const data = richData();
    delete data.Movies[0].VideoBitrates;
    delete data.TvShows[0].VideoBitrates;
    ui.fillCodecsData(data);
    expect(ui.getBitrateBounds()).toBeNull();
  });

  test('getBitrateHistogram counts only numeric bitrate values', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.fillCodecsData(histogramData({ '/m/a.mkv': 8, '/m/b.mkv': 'high' }));
    const { bins, binWidth, maxEdge } = ui.getBitrateHistogram();
    expect(bins.reduce((a: number, b: number) => a + b, 0)).toBe(1);
    expect(binWidth).toBe(4);
    expect(maxEdge).toBe(16);
  });

  test('getBitrateHistogram clamps a negative bin index to the first bin', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.fillCodecsData(histogramData({ '/m/a.mkv': -8, '/m/b.mkv': 4 }));
    const { bins, maxEdge } = ui.getBitrateHistogram();
    expect(bins).toEqual([1, 1, 0, 0]);
    expect(maxEdge).toBe(16);
  });

  test('getBitrateHistogram computes peak, bin count and max edge from the data', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.fillCodecsData(histogramData({ '/m/a.mkv': 2, '/m/b.mkv': 6, '/m/c.mkv': 20 }));
    const { bins, binWidth, maxEdge } = ui.getBitrateHistogram();
    expect(bins).toEqual([1, 1, 0, 0, 1]);
    expect(binWidth).toBe(4);
    expect(maxEdge).toBe(20);
  });

  test('getBitrateHistogram without scan data uses an empty subset', () => {
    const ui = freshConfigPage();
    stubApi();
    ui.getBitrateHistogram();
    const hist = ui.getBitrateHistogram();
    expect(hist.bins).toEqual([0, 0, 0, 0]);
    expect(hist.binWidth).toBe(4);
    expect(hist.maxEdge).toBe(16);
  });

  test('getBitrateHistogram reuses the cached subset across calls', () => {
    const ui = freshConfigPage();
    openExplorer(ui).then(() => {
      const first = ui.getBitrateHistogram();
      const second = ui.getBitrateHistogram();
      expect(first.bins).toEqual(second.bins);
      expect(second.bins.reduce((a: number, b: number) => a + b, 0)).toBe(5);
    });
  });
});

describe('filter intersection over path maps', () => {
  test('intersectPathMaps keeps only keys present in both maps', () => {
    const ui = freshConfigPage();
    const out = ui.intersectPathMaps({ '/m/a.mkv': true, '/m/b.mkv': true }, { '/m/b.mkv': true, '/m/c.mkv': true });
    expect(out).toEqual({ '/m/b.mkv': true });
  });

  test('intersectExplorerFilters returns null without scan data', () => {
    const ui = freshConfigPage();
    const active = [{ dim: ui.getCodecsExplorerDimension('videoCodecs'), values: ['h264'], exclude: false }];
    expect(ui.intersectExplorerFilters(active)).toBeNull();
  });

  test('intersectExplorerFilters ANDs the unions of two dimensions', () => {
    const ui = freshConfigPage();
    openExplorer(ui).then(() => {
      const active = [
        { dim: ui.getCodecsExplorerDimension('videoCodecs'), values: ['h264'], exclude: false },
        { dim: ui.getCodecsExplorerDimension('containers'), values: ['mkv'], exclude: false },
      ];
      const out = ui.intersectExplorerFilters(active);
      expect(Object.keys(out).sort()).toEqual(['/m/a.mkv', '/m/b.mkv', '/m/sub/d.mkv', '/t/e.mkv']);
    });
  });
});

describe('scoping stale library names', () => {
  test('a stale library name with an active filter yields no files', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.fillCodecsData(richData());
    ui.getCodecsExplorerState().scope = ['Ghost'];
    ui.getCodecsExplorerState().filters.videoCodecs = { values: ['h264'], exclude: false };
    ui.renderCodecsExplorer(document.getElementById('codecsContent')!);
    expect(resultCount()).toBe(0);
    expect(document.getElementById('codecExplorerResults')!.textContent).toContain('No files found.');
  });
});

describe('path subset computation with an active bitrate range', () => {
  async function commitRangeAndOpenVideoCodecs(ui: UiBridge) {
    openDim('bitrate');
    const maxInput = document.getElementById('codecBitrateMax') as HTMLInputElement;
    maxInput.value = '30';
    fire(maxInput, 'change');
    openDim('videoCodecs');
  }

  test('computePathsExcluding narrows option counts by the active range', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    await commitRangeAndOpenVideoCodecs(ui);
    expect(ui.hasActiveBitrateRange()).toBe(true);
    const h264 = document.querySelector('[data-single-value="h264"]')!;
    expect(h264.textContent).toContain('(4)');
    expect(document.querySelector('[data-single-value="hevc"]')).toBeNull();
    const hist = ui.getBitrateHistogram();
    expect(hist.bins.reduce((a: number, b: number) => a + b, 0)).toBe(5);
  });

  test('hasOtherActiveFilters counts the bitrate range as an active facet', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    await commitRangeAndOpenVideoCodecs(ui);
    expect(resultCount()).toBe(4);
    expect(document.querySelector('[data-single-value="hevc"]')).toBeNull();
  });
});

describe('result computation paths', () => {
  test('baseExplorerPaths yields no files when there is no scan data', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.renderCodecsExplorer(document.getElementById('codecsContent')!);
    ui.getCodecsExplorerState().filters.videoCodecs = { values: ['h264'], exclude: false };
    click(document.querySelector('[data-filter-add-btn]')!);
    expect(document.getElementById('codecExplorerResults')!.textContent).toBe('');
    expect(document.querySelector('.codec-filter-pop .codec-explorer-none')).not.toBeNull();
  });

  test('a scope without filters lists the scoped library files', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.fillCodecsData(richData());
    ui.getCodecsExplorerState().scope = ['Movies'];
    ui.renderCodecsExplorer(document.getElementById('codecsContent')!);
    expect(resultCount()).toBe(4);
  });

  test('no scope and no filters keeps the idle hint', () => {
    const ui = freshConfigPage();
    openExplorer(ui).then(() => {
      const results = document.getElementById('codecExplorerResults')!;
      expect(results.querySelector('.codec-explorer-empty')).not.toBeNull();
      expect(results.textContent).toContain('Pick at least one filter');
    });
  });
});

describe('option counting without matches', () => {
  test('single dimensions with zero options stay out of the picker', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.fillCodecsData(richData());
    ui.getCodecsExplorerState().scope = ['Music'];
    ui.renderCodecsExplorer(document.getElementById('codecsContent')!);
    openFilterPopover();
    expect(document.querySelector('[data-filter-dim="videoCodecs"]')).toBeNull();
    expect(document.querySelector('[data-filter-dim="audioLanguages"]')).toBeNull();
    expect(document.querySelector('[data-filter-dim="containers"]')).not.toBeNull();
    expect(document.querySelector('[data-filter-dim="musicAudioCodecs"]')).not.toBeNull();
  });

  test('multi dimensions with zero options stay out of the picker', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.fillCodecsData(richData());
    ui.getCodecsExplorerState().scope = ['Music'];
    ui.renderCodecsExplorer(document.getElementById('codecsContent')!);
    openFilterPopover();
    expect(document.querySelector('[data-filter-dim="watched"]')).toBeNull();
    expect(document.querySelector('[data-filter-dim="subtitleLanguages"]')).toBeNull();
  });
});

describe('control bar HTML states', () => {
  test('the dimension picker shows the no-matching-options hint for an empty scope', () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.fillCodecsData(emptyScopeData());
    ui.getCodecsExplorerState().scope = ['Movies'];
    ui.renderCodecsExplorer(document.getElementById('codecsContent')!);
    openFilterPopover();
    const hint = document.querySelector('.codec-filter-pop .codec-explorer-none');
    expect(hint).not.toBeNull();
    expect(hint!.textContent).toContain('No matching options');
    expect(document.querySelector('[data-filter-dim]')).toBeNull();
  });

  test('a valid dimension id opens its editor', () => {
    const ui = freshConfigPage();
    openExplorer(ui).then(() => {
      openDim('videoCodecs');
      expect(document.querySelector('.codec-filter-editor')).not.toBeNull();
      expect(document.querySelector('.codec-filter-editor-title')!.textContent).toContain('Video codec');
      expect(document.querySelector('.codec-single')).not.toBeNull();
    });
  });

  test('the bitrate editor shows the no-data hint after a rescan without bitrates', () => {
    const ui = freshConfigPage();
    openExplorer(ui).then(() => {
      openDim('bitrate');
      expect(document.querySelector('[data-bitrate-editor]')).not.toBeNull();
      const data = richData();
      delete data.Movies[0].VideoBitrates;
      delete data.TvShows[0].VideoBitrates;
      ui.fillCodecsData(data);
      const hint = document.querySelector('.codec-filter-editor .codec-explorer-none');
      expect(hint).not.toBeNull();
      expect(hint!.textContent).toContain('No bitrate data yet');
      expect(ui.hasActiveBitrateRange()).toBe(false);
    });
  });
});

describe('file detail cards', () => {
  test('buildExplorerFileDetail serves the cached HTML for a repeated open', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    pickSingle('videoCodecs', 'h264');
    await debounce();
    const results = document.getElementById('codecExplorerResults')!;
    const leaf = results.querySelector('.tree-leaf[title="/m/a.mkv"]') as HTMLElement;
    click(leaf);
    const firstHtml = (leaf.nextElementSibling as HTMLElement).outerHTML;
    click(leaf);
    expect(results.querySelectorAll('.codec-file-detail')).toHaveLength(0);
    click(leaf);
    const secondHtml = (leaf.nextElementSibling as HTMLElement).outerHTML;
    expect(secondHtml).toBe(firstHtml);
    expect(secondHtml).toContain('h264');
    expect(secondHtml).toContain('alice');
    expect(secondHtml).toContain('8.0 Mbps');
  });

  test('toggleExplorerFileDetail collapses an open detail card', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    pickSingle('videoCodecs', 'h264');
    await debounce();
    const results = document.getElementById('codecExplorerResults')!;
    const leaf = results.querySelector('.tree-leaf[title="/m/a.mkv"]') as HTMLElement;
    click(leaf);
    expect(leaf.classList.contains('codec-file-open')).toBe(true);
    expect(leaf.getAttribute('aria-expanded')).toBe('true');
    click(leaf);
    expect(results.querySelectorAll('.codec-file-detail')).toHaveLength(0);
    expect(leaf.classList.contains('codec-file-open')).toBe(false);
    expect(leaf.getAttribute('aria-expanded')).toBe('false');
  });

  test('toggleExplorerFileDetail returns early for a leaf without a title', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    pickSingle('videoCodecs', 'h264');
    await debounce();
    const results = document.getElementById('codecExplorerResults')!;
    const leaf = results.querySelector('.tree-leaf') as HTMLElement;
    leaf.removeAttribute('title');
    expect(() => click(leaf)).not.toThrow();
    expect(results.querySelectorAll('.codec-file-detail')).toHaveLength(0);
  });
});

describe('result tree binding and grouping', () => {
  test('bindExplorerFileDetails prepares leaves materialized by lazy expansion', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    pickSingle('videoCodecs', 'h264');
    await debounce();
    const results = document.getElementById('codecExplorerResults')!;
    const deepLeaf = results.querySelector('.tree-leaf[title="/m/sub/d.mkv"]');
    expect(deepLeaf).toBeNull();
    const toggle = results.querySelector('.tree-folder[data-tree-toggle]') as HTMLElement;
    click(toggle);
    const leaf = results.querySelector('.tree-leaf[title="/m/sub/d.mkv"]') as HTMLElement;
    expect(leaf).not.toBeNull();
    expect(leaf.getAttribute('tabindex')).toBe('0');
    expect(leaf.getAttribute('role')).toBe('button');
    expect(leaf.dataset.detailReady).toBe('1');
  });

  test('a second bindExplorerFileDetails call on the same host is idempotent', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    pickSingle('videoCodecs', 'h264');
    await debounce();
    const results = document.getElementById('codecExplorerResults')!;
    expect(results.dataset.detailBound).toBe('1');
    click(results.querySelector('.tree-folder[data-tree-toggle]')!);
    const leaf = results.querySelector('.tree-leaf[title="/m/sub/d.mkv"]') as HTMLElement;
    expect(leaf.getAttribute('tabindex')).toBe('0');
    expect(leaf.getAttribute('role')).toBe('button');
  });

  test('paths matching no library root group under Other', async () => {
    const ui = freshConfigPage();
    stubApi();
    document.body.innerHTML = '<div id="codecsContent"></div>';
    ui.fillCodecsData(orphanData());
    await flush();
    ui.renderCodecsExplorer(document.getElementById('codecsContent')!);
    pickSingle('videoCodecs', 'h264');
    const results = document.getElementById('codecExplorerResults')!;
    expect(results.querySelector('.badge-movies')).not.toBeNull();
    expect(results.querySelector('.badge-other')).not.toBeNull();
  });
});

describe('search host guards and focus restore', () => {
  test('runCodecsExplorerSearch returns early when the results host is gone', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    document.getElementById('codecExplorerResults')!.remove();
    expect(() => click(document.querySelector('[data-filter-add-btn]')!)).not.toThrow();
    expect(document.querySelector('[data-filter-pop]')).not.toBeNull();
    expect(document.getElementById('codecExplorerResults')).toBeNull();
  });

  test('describeActiveExplorerControl returns null for the body element', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    click(document.querySelector('[data-filter-add-btn]')!);
    expect(document.querySelector('[data-filter-pop]')).not.toBeNull();
  });

  test('restoreFilterFocus refocuses a dim button after a debounced rebuild', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    openFilterPopover();
    const dimBtn = document.querySelector('[data-filter-dim="videoCodecs"]') as HTMLElement;
    dimBtn.focus();
    const box = document.querySelector('[data-library-option][value="Movies"]') as HTMLInputElement;
    box.checked = true;
    fire(box, 'change');
    await debounce();
    expect(document.querySelector('[data-filter-pop]')).not.toBeNull();
    expect(document.activeElement).toBe(document.querySelector('[data-filter-dim="videoCodecs"]'));
  });

  test('restoreFilterFocus returns to the add button after a pill clear', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    pickSingle('videoCodecs', 'h264');
    const pill = document.querySelector('[data-pill-clear="videoCodecs"]') as HTMLElement;
    pill.focus();
    click(pill);
    expect(ui.getCodecsExplorerSelection('videoCodecs')).toEqual([]);
    expect(document.activeElement).toBe(document.getElementById('codecFilterAddBtn'));
  });

  test('findExplorerFocusTarget resolves descriptors by id, library value and dim value', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);

    openDim('bitrate');
    const minInput = document.getElementById('codecBitrateMin') as HTMLInputElement;
    minInput.focus();
    minInput.value = '10';
    fire(minInput, 'change');
    expect(document.activeElement).toBe(document.getElementById('codecBitrateMin'));

    const libBox = document.querySelector('[data-library-option][value="TV"]') as HTMLInputElement;
    libBox.focus();
    libBox.checked = true;
    fire(libBox, 'change');
    await debounce();
    expect(document.activeElement).toBe(document.querySelector('[data-library-option][value="TV"]'));

    openDim('audioLanguages');
    const engBox = document.querySelector('[data-multi-dim="audioLanguages"] input[type="checkbox"][value="eng"]') as HTMLInputElement;
    engBox.focus();
    engBox.checked = true;
    fire(engBox, 'change');
    await debounce();
    expect(document.activeElement).toBe(
      document.querySelector('[data-multi-dim="audioLanguages"] input[type="checkbox"][value="eng"]'),
    );

    const found = ui.findExplorerFocusBox('[data-library-widget]', '[data-library-option]', 'Movies');
    expect(found).not.toBeNull();
    expect((found as HTMLInputElement).value).toBe('Movies');
  });

  test('refreshCodecsExplorerControls returns early when the controls host is gone', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    click(document.querySelector('[data-filter-add-btn]')!);
    expect(document.querySelector('[data-filter-pop]')).not.toBeNull();
    document.getElementById('codecExplorerControls')!.remove();
    const evt = new (globalThis as any).window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true });
    expect(() => document.dispatchEvent(evt)).not.toThrow();
    expect(document.querySelector('[data-filter-pop]')).toBeNull();
  });
});

describe('control and filter event handlers', () => {
  test('rapid multi-select changes collapse into one debounced refresh', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    openDim('audioLanguages');
    const eng = document.querySelector('[data-multi-dim="audioLanguages"] input[type="checkbox"][value="eng"]') as HTMLInputElement;
    eng.checked = true;
    fire(eng, 'change');
    const deu = document.querySelector('[data-multi-dim="audioLanguages"] input[type="checkbox"][value="deu"]') as HTMLInputElement;
    deu.checked = true;
    fire(deu, 'change');
    await debounce();
    expect(ui.getCodecsExplorerSelection('audioLanguages')).toEqual(['eng', 'deu']);
    expect(resultCount()).toBe(5);
  });

  test('clicking the active single value again deletes the filter', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    pickSingle('videoCodecs', 'h264');
    expect(ui.getCodecsExplorerSelection('videoCodecs')).toEqual(['h264']);
    expect(document.querySelector('[data-pill-clear="videoCodecs"]')).not.toBeNull();
    openDim('videoCodecs');
    click(document.querySelector('[data-single-value="h264"]')!);
    expect(ui.getCodecsExplorerSelection('videoCodecs')).toEqual([]);
    expect(document.querySelector('[data-pill-clear]')).toBeNull();
  });

  test('unchecking every multi value deletes the filter', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    openDim('audioLanguages');
    const eng = document.querySelector('[data-multi-dim="audioLanguages"] input[type="checkbox"][value="eng"]') as HTMLInputElement;
    eng.checked = true;
    fire(eng, 'change');
    await debounce();
    expect(ui.getCodecsExplorerSelection('audioLanguages')).toEqual(['eng']);
    const fresh = document.querySelector('[data-multi-dim="audioLanguages"] input[type="checkbox"][value="eng"]') as HTMLInputElement;
    fresh.checked = false;
    fire(fresh, 'change');
    await debounce();
    expect(ui.getCodecsExplorerSelection('audioLanguages')).toEqual([]);
  });

  test('the Includes toggle with no selected values deletes the filter', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    openDim('audioLanguages');
    click(document.querySelector('[data-exclude-toggle="audioLanguages"][data-exclude-value="0"]')!);
    expect(ui.getCodecsExplorerSelection('audioLanguages')).toEqual([]);
    expect(document.querySelector('[data-pill-clear]')).toBeNull();
    expect(document.querySelector('[data-multi-dim="audioLanguages"]')).not.toBeNull();
  });

  test('setMultiPanelOpen opens and closes the option panel', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    openDim('audioLanguages');
    const toggle = document.querySelector('[data-multi-toggle="audioLanguages"]') as HTMLElement;
    const panel = document.querySelector('[data-multi-panel="audioLanguages"]') as HTMLElement;
    expect(panel.hasAttribute('hidden')).toBe(false);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    toggle.click();
    expect(panel.hasAttribute('hidden')).toBe(true);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
  });
});
