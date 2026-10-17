import { afterAll, describe, expect, test } from 'vitest';
import { flush } from '../src/apiStub.js';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown>, failures: string[] = []) {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    ajax: (opts: any) => {
      if (failures.some((f) => String(opts.url).includes(f))) return Promise.reject(new Error('down'));
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
}

const scanFixture = () => ({
  Movies: [{ VideosWithoutSubtitlesPaths: ['/m/a.mkv'] }],
  TvShows: [{ VideosWithoutSubtitlesPaths: ['/t/s.mkv'] }],
  Other: [],
  MovieRootPaths: ['/m'],
  TvShowRootPaths: ['/t'],
  OtherRootPaths: [],
  Libraries: [
    { VideosWithoutSubtitles: 2, VideosWithoutImages: 0, VideosWithoutNfo: 0, OrphanedMetadataDirectories: 0 },
    { VideosWithoutSubtitles: 0, VideosWithoutImages: 3, VideosWithoutNfo: 0, OrphanedMetadataDirectories: 1 },
  ],
});

describe('collectHealthPaths', () => {
  test('merges per-section paths and roots', () => {
    const out = ui.collectHealthPaths(scanFixture(), 'VideosWithoutSubtitlesPaths');
    expect(out.movies).toEqual(['/m/a.mkv']);
    expect(out.tvShows).toEqual(['/t/s.mkv']);
    expect(out.music).toEqual([]);
    expect(out.rootPaths.movies).toEqual(['/m']);
  });
});

describe('renderHealthChecks', () => {
  test('warn classes only where counts are positive', () => {
    const html = ui.renderHealthChecks(scanFixture());
    expect(html).toContain('health-warn">2');
    expect(html).toContain('health-warn">3');
    expect(html).toContain('health-ok">0');
    expect(html).toContain('health-bad">1');
  });

  test('zero library renders all ok', () => {
    const html = ui.renderHealthChecks({ Libraries: [] });
    expect(html).not.toContain('health-warn');
    expect(html).not.toContain('health-bad');
  });
});

describe('fillHealthData', () => {
  test('renders cards and skips trash when disabled', async () => {
    ajaxRouter({ Configuration: { UseTrash: false } });
    document.body.innerHTML = '<div id="healthContent"></div>';
    ui.fillHealthData(scanFixture());
    await flush();
    const c = document.getElementById('healthContent')!;
    expect(c.querySelectorAll('.health-item')).toHaveLength(4);
    expect(document.getElementById('trashHealthSection')).toBeNull();
  });

  test('trash enabled renders totals from contents', async () => {
    ajaxRouter({
      Configuration: { UseTrash: true },
      'Trash/Contents': {
        RetentionDays: 30,
        Libraries: [
          { LibraryName: 'Movies', Items: [{ OriginalName: 'old.mkv', Size: 100, PurgeDate: '2026-01-01T00:00:00' }] },
        ],
      },
    });
    document.body.innerHTML = '<div id="healthContent"></div>';
    ui.fillHealthData(scanFixture());
    await flush();
    const section = document.getElementById('trashHealthSection')!;
    expect(section.textContent).toContain('old.mkv');
    expect(section.textContent).toContain('30d');
  });

  test('clicking a health item fills the detail panel', async () => {
    ajaxRouter({ Configuration: { UseTrash: false } });
    document.body.innerHTML = '<div id="healthContent"></div>';
    ui.fillHealthData(scanFixture());
    await flush();
    const item = document.querySelector('.health-clickable[data-health-type="noSubs"]') as HTMLElement;
    item.click();
    const panel = document.getElementById('healthDetailPanel')!;
    expect(panel.textContent).toContain('a.mkv');
    item.click();
    expect(panel.innerHTML).toBe('');
  });
});

describe('overview badges and bars', () => {
  test.each([
    ['tvshows', 'badge-tvshows'],
    ['movies', 'badge-movies'],
    ['', 'badge-movies'],
    ['music', 'badge-music'],
    ['books', 'badge-books'],
    ['weird', 'badge-other'],
  ])('collection %s', (type, cls) => {
    expect(ui.getCollectionBadge(type)).toContain(cls);
  });

  test('type names are escaped', () => {
    expect(ui.getCollectionBadge('<x>')).toContain('&lt;x&gt;');
  });

  test('empty data yields no bar', () => {
    expect(
      ui.buildBarSegments({
        TotalMovieVideoSize: 0, TotalTvShowVideoSize: 0, TotalSubtitleSize: 0,
        TotalImageSize: 0, TotalTrickplaySize: 0, TotalNfoSize: 0,
        TotalMusicAudioSize: 0, TotalBookSize: 0, TotalBookFileCount: 0, Libraries: [],
      }),
    ).toBe('');
  });

  test('books segment appears only with book files', () => {
    const base = {
      TotalMovieVideoSize: 100, TotalTvShowVideoSize: 0, TotalSubtitleSize: 0,
      TotalImageSize: 0, TotalTrickplaySize: 0, TotalNfoSize: 0,
      TotalMusicAudioSize: 0, TotalBookSize: 50, Libraries: [],
    };
    expect(ui.buildBarSegments({ ...base, TotalBookFileCount: 0 })).not.toContain('bar-books');
    expect(ui.buildBarSegments({ ...base, TotalBookFileCount: 2 })).toContain('bar-books');
  });
});

describe('loadCleanupStats', () => {
  test('renders freed bytes and item counts', async () => {
    ajaxRouter({ CleanupStatistics: { TotalBytesFreed: 2048, TotalItemsDeleted: 3, LastCleanupTimestamp: '2026-05-01T10:00:00' } });
    document.body.innerHTML = '<div id="cleanup-stats-container"></div>';
    ui.loadCleanupStats();
    await flush();
    const c = document.getElementById('cleanup-stats-container')!;
    expect(c.textContent).toContain('2.00 KB');
    expect(c.textContent).toContain('3');
  });

  test('failure renders the error hint', async () => {
    ajaxRouter({}, ['CleanupStatistics']);
    document.body.innerHTML = '<div id="cleanup-stats-container"></div>';
    ui.loadCleanupStats();
    await flush();
    expect(document.getElementById('cleanup-stats-container')!.textContent).toMatch(/could not load/i);
  });

  test('missing container does not throw', async () => {
    ajaxRouter({ CleanupStatistics: {} });
    document.body.innerHTML = '';
    expect(() => ui.loadCleanupStats()).not.toThrow();
    await flush();
    // With no container the loader is a no-op: nothing is injected into the body.
    expect(document.body.children).toHaveLength(0);
  });
});

describe('fillOverviewData', () => {
  const data = () => ({
    Movies: [{ VideoFileCount: 2 }],
    TvShows: [{ VideoFileCount: 1 }],
    Libraries: [
      {
        LibraryName: 'Movies', CollectionType: 'movies', TotalSize: 300,
        VideoSize: 100, VideoFileCount: 2, AudioSize: 0, AudioFileCount: 0,
        ImageSize: 10, ImageFileCount: 1, SubtitleSize: 5, SubtitleFileCount: 1,
        TrickplaySize: 0, TrickplayFolderCount: 0, OtherSize: 0, OtherFileCount: 0,
        NfoSize: 0, NfoFileCount: 0,
      },
      { LibraryName: 'Box', CollectionType: 'boxsets', TotalSize: 0 },
    ],
    TotalVideoFileCount: 3,
    TotalAudioFileCount: 0,
    TotalMovieVideoSize: 100,
    TotalTvShowVideoSize: 50,
    TotalMusicAudioSize: 0,
    TotalBookFileCount: 0,
    TotalTrickplaySize: 0,
    InternalTrickplaySize: 7,
    TotalSubtitleSize: 5,
  });

  test('renders cards, internal trickplay fallback and plain boxset rows', () => {
    document.body.innerHTML = '<div id="overviewContent"></div>';
    ui.fillOverviewData(data());
    const c = document.getElementById('overviewContent')!;
    expect(c.textContent).toContain('Video Data - Movies');
    expect(c.innerHTML).toContain('(internal)');
    expect(c.querySelector('.codec-explore-name')).not.toBeNull();
    expect(c.querySelector('button[data-codec-explore-library="Movies"]')).not.toBeNull();
  });

  test('books card and column appear only with book files', () => {
    document.body.innerHTML = '<div id="overviewContent"></div>';
    ui.fillOverviewData({ ...data(), TotalBookFileCount: 1, TotalBookSize: 9 });
    expect(document.getElementById('overviewContent')!.innerHTML).toContain('Books');
  });

  test('missing container does not throw', () => {
    document.body.innerHTML = '';
    expect(() => ui.fillOverviewData(data())).not.toThrow();
  });
});
