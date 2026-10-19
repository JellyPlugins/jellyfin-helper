import { afterAll, afterEach, beforeAll, describe, expect, test } from 'vitest';
import { ajaxRouter, flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

afterAll(saveCoverage);

// Helper-level edge cases the behavioral explorer suite does not drive directly:
// the dimension index, the filter intersection builder and the base path set.
// The full UI flows live in codecs-explorer-coverage.test.ts and must not be
// duplicated here, otherwise the same assertions run under different listener
// conditions and drift apart.

// Every freshConfigPage bundle re-binds document-level listeners (deep-link
// handlers, touch-outside tooltip) that nothing ever removes, so they stack
// across tests in this file and a single click would replay dozens of renders.
// Record what the bundles add and strip it all after each test.
const docListeners: [string, EventListener][] = [];
const origAdd = document.addEventListener.bind(document);
beforeAll(() => {
  document.addEventListener = function (type: string, fn: EventListener, ...rest: unknown[]) {
    docListeners.push([type, fn]);
    return origAdd(type, fn as EventListener, ...(rest as []));
  } as typeof document.addEventListener;
});
afterEach(() => {
  while (docListeners.length) {
    const [type, fn] = docListeners.pop()!;
    document.removeEventListener(type, fn as EventListener);
  }
});

function stubApi() {
  ajaxRouter({});
  if (!(globalThis as any).window.scrollTo) (globalThis as any).window.scrollTo = () => undefined;
  (globalThis as any).window.HTMLElement.prototype.scrollIntoView = function () {};
  delete (globalThis as any).requestAnimationFrame;
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

describe('explorer state, libraries and dimension index', () => {
  test('buildDimPathIndex through getDimPathIndex returns empty maps for an unknown dimension', () => {
    const ui = freshConfigPage();
    const index = ui.getDimPathIndex('bogus');
    expect(Object.keys(index.single)).toHaveLength(0);
    expect(Object.keys(index.multi)).toHaveLength(0);
  });
});

describe('filter intersection over path maps', () => {
  test('intersectExplorerFilters returns null without scan data', () => {
    const ui = freshConfigPage();
    const active = [{ dim: ui.getCodecsExplorerDimension('videoCodecs'), values: ['h264'], exclude: false }];
    expect(ui.intersectExplorerFilters(active)).toBeNull();
  });

  test('intersectExplorerFilters ANDs the unions of two dimensions', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    const active = [
      { dim: ui.getCodecsExplorerDimension('videoCodecs'), values: ['h264'], exclude: false },
      { dim: ui.getCodecsExplorerDimension('containers'), values: ['mkv'], exclude: false },
    ];
    const out = ui.intersectExplorerFilters(active);
    expect(Object.keys(out).sort()).toEqual(['/m/a.mkv', '/m/b.mkv', '/m/sub/d.mkv', '/t/e.mkv']);
  });
});
