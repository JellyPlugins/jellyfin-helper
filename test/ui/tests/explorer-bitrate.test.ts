import { afterAll, beforeAll, describe, expect, test, vi } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

beforeAll(() => {
  vi.useFakeTimers();
});

afterAll(() => {
  vi.useRealTimers();
  saveCoverage();
});

function scanData(): any {
  return {
    Movies: [
      {
        LibraryName: 'Movies',
        VideoCodecs: { h264: 2 },
        VideoBitrates: { '/m/a.mkv': 8, '/m/b.mkv': 20 },
        AudioTrackLabels: { '/m/a.mkv': ['en'] },
        ResolutionDimensions: { '/m/a.mkv': '1920x800' },
        FileSizes: { '/m/a.mkv': 100 },
        WatchedByUserPaths: { alice: ['/m/a.mkv'] },
        WatchedTiers: { 'Never watched': 1 },
        ContainerFormats: { mkv: 2 },
      },
    ],
    TvShows: [],
    Other: [],
    Music: [],
    Books: [],
    Libraries: [{ LibraryName: 'Movies', ContainerFormats: { mkv: 2 } }],
    ContainerFormats: { mkv: 2 },
  };
}

function seed() {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: () => Promise.resolve({}),
  };
  document.body.innerHTML = '';
  ui.fillCodecsData(scanData());
}

// Seeds exactly one integer bitrate so getBitrateBounds hits its single-value
// branch, where floor == ceil and the span must widen by one.
function seedOne(bitrate: number) {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: () => Promise.resolve({}),
  };
  document.body.innerHTML = '';
  const data = scanData();
  data.Movies[0].VideoBitrates = { '/m/a.mkv': bitrate };
  ui.fillCodecsData(data);
}

describe('explorer seeding through fillCodecsData', () => {
  test('renders one card per populated descriptor', () => {
    if (!(globalThis as any).window.scrollTo) (globalThis as any).window.scrollTo = () => undefined;
    document.body.innerHTML = '<div id="codecsContent"></div>';
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: () => Promise.resolve({}),
    };
    ui.fillCodecsData(scanData());
    const cards = document.querySelectorAll('#codecsContent .chart-box[data-chart-id]');
    expect(cards.length).toBeGreaterThan(2);
    expect(document.querySelector('[data-chart-id="videoCodecs"]')).not.toBeNull();
  });

  test('libraries resolve from the seeded scan', () => {
    seed();
    expect(ui.getCodecsExplorerLibraries().map((l: any) => l.LibraryName)).toEqual(['Movies']);
    expect(ui.getCodecsExplorerDimension('videoCodecs')!.id).toBe('videoCodecs');
    expect(ui.getCodecsExplorerDimension('nope')).toBeNull();
  });

  test('full explorer render runs the search pipeline', () => {
    seed();
    document.body.innerHTML = '<div id="explorerHost"></div>';
    const host = document.getElementById('explorerHost')!;
    expect(() => ui.renderCodecsExplorer(host)).not.toThrow();
    expect(host.querySelector('.codec-explorer')).not.toBeNull();
  });
});

describe('bitrate maps and bounds', () => {
  test('bounds floor and ceil with a unit span guard', () => {
    seed();
    expect(ui.getBitrateBounds()).toMatchObject({ min: 8, max: 20 });
  });

  test('single value widens to a unit span', () => {
    seedOne(15);
    expect(ui.getBitrateBounds()).toMatchObject({ min: 15, max: 16, count: 1 });
  });

  test('range keeps only measured files inside', () => {
    seed();
    const set = { '/m/a.mkv': true, '/m/b.mkv': true, '/m/ghost.mkv': true };
    expect(ui.applyBitrateRange(set, { min: 0, max: 10 })).toEqual({ '/m/a.mkv': true });
  });

  test('histogram bins cover every file exactly once', () => {
    seed();
    const { bins } = ui.getBitrateHistogram();
    expect(bins.reduce((a: number, b: number) => a + b, 0)).toBe(2);
  });

  test('per-file bitrate is null without measurement', () => {
    seed();
    expect(ui.getExplorerFileBitrate('/m/a.mkv')).toBe(8);
    expect(ui.getExplorerFileBitrate('/missing.mkv')).toBeNull();
  });

  test('track labels resolve per kind', () => {
    seed();
    expect(ui.getTrackLabelMap('audio')).toEqual({ '/m/a.mkv': ['en'] });
    expect(ui.getExplorerFileTrackLabels('audio', '/m/a.mkv')).toEqual(['en']);
    expect(ui.getExplorerFileTrackLabels('subs', '/m/a.mkv')).toBeNull();
  });

  test('no active range by default', () => {
    seed();
    expect(ui.hasActiveBitrateRange()).toBe(false);
  });
});

describe('formatBitrateRange pills', () => {
  const bounds = { min: 8, max: 20, count: 2 };

  test.each([
    [{ min: 8, max: 20 }, 'Any'],
    [{ min: 8, max: 12 }, '≤ 12 Mbps'],
    [{ min: 15, max: 20 }, '≥ 15 Mbps'],
    [{ min: 9, max: 19 }, '9–19 Mbps'],
  ])('range %s', (range, text) => {
    expect(ui.formatBitrateRange(range, bounds)).toBe(text);
  });

  test('null bounds always render the span', () => {
    expect(ui.formatBitrateRange({ min: 8, max: 20 }, null)).toBe('8–20 Mbps');
  });

  test('normalize collapses full-span ranges to null', () => {
    expect(ui.normalizeBitrateRange({ min: 8, max: 20 }, bounds)).toBeNull();
    expect(ui.normalizeBitrateRange({ min: 9, max: 19 }, bounds)).toMatchObject({ min: 9, max: 19 });
  });
});

describe('explorer path helpers', () => {
  test.each([
    ['C:\\Movies\\a.mkv', true],
    ['C:/Movies/a.mkv', true],
    ['\\\\srv\\share\\a.mkv', true],
    ['/media/a.mkv', false],
    ['\\\\', true],
  ])('windows detection for %s', (p, expected) => {
    expect(ui.codecExplorerIsWindowsPath(p)).toBe(expected);
  });

  test.each([['/m/', '/m'], ['C:\\v\\\\', 'C:\\v'], ['', '']])('trim %s', (input, expected) => {
    expect(ui.codecExplorerTrimRoot(input)).toBe(expected);
  });

  test('empty roots match everything', () => {
    expect(ui.codecExplorerPathInRoots('/any', [])).toBe(true);
    expect(ui.codecExplorerPathInRoots('/any', null)).toBe(true);
  });

  test('directory boundaries prevent prefix leaks', () => {
    expect(ui.codecExplorerPathInRoots('/movies/a', ['/movies'])).toBe(true);
    expect(ui.codecExplorerPathInRoots('/movies2/a', ['/movies'])).toBe(false);
    expect(ui.codecExplorerPathInRoots('C:\\V\\a', ['c:\\v'])).toBe(true);
    expect(ui.codecExplorerPathInRoots('/MEDIA/a', ['/media'])).toBe(false);
  });

  test('universe aggregates scoped counts and watchers', () => {
    seed();
    const dim = ui.getCodecsExplorerDimension('videoCodecs')!;
    const scoped = [{ VideoCodecs: { h264: 2 } }];
    expect(ui.getExplorerUniverse(dim, scoped)).toEqual({ h264: 2 });
    expect(
      ui.getExplorerUniverse(ui.getCodecsExplorerDimension('watched')!, [
        { WatchedTiers: { 'Never watched': 2 }, WatchedByUserPaths: { bob: ['/x'] } },
      ]),
    ).toEqual({ 'Never watched': 2, bob: 1 });
  });

  test('watched universe skips empty watchers', () => {
    expect(ui.countWatchedUniverse([{ WatchedTiers: {}, WatchedByUserPaths: { zed: [] } }])).toEqual({});
  });

  test('selection reads the filter state', () => {
    seed();
    expect(ui.getCodecsExplorerSelection('videoCodecs')).toEqual([]);
    expect(ui.isCodecsExplorerExcluded('videoCodecs')).toBe(false);
  });
});
