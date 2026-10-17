import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => {
  ui = freshConfigPage();
});

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown>, failures: string[] = []) {
  const calls: string[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      calls.push(String(opts.url));
      if (failures.some((f) => String(opts.url).includes(f))) return Promise.reject(new Error('down'));
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
  return calls;
}

const scanFixture = () => ({
  Movies: [{ LibraryName: 'Movies', VideoFileCount: 1, VideoCodecs: { h264: 1 }, VideosWithoutSubtitlesPaths: [] }],
  TvShows: [],
  Other: [],
  Music: [],
  Books: [],
  Libraries: [
    {
      LibraryName: 'Movies', CollectionType: 'movies', TotalSize: 100,
      VideoSize: 100, VideoFileCount: 1, AudioSize: 0, AudioFileCount: 0,
      ImageSize: 0, ImageFileCount: 0, SubtitleSize: 0, SubtitleFileCount: 0,
      TrickplaySize: 0, TrickplayFolderCount: 0, OtherSize: 0, OtherFileCount: 0,
      NfoSize: 0, NfoFileCount: 0, VideosWithoutSubtitles: 0, VideosWithoutImages: 0,
      VideosWithoutNfo: 0, OrphanedMetadataDirectories: 0,
    },
  ],
  MovieRootPaths: ['/m'],
  TvShowRootPaths: [],
  OtherRootPaths: [],
  TotalVideoFileCount: 1,
  TotalAudioFileCount: 0,
  TotalMovieVideoSize: 100,
  TotalTvShowVideoSize: 0,
  TotalMusicAudioSize: 0,
  TotalBookFileCount: 0,
  TotalTrickplaySize: 0,
  TotalSubtitleSize: 0,
});

describe('renderShell tab skeleton', () => {
  test('all eight tabs exist with recommendations hidden first', () => {
    const html = ui.renderShell();
    for (const tab of ['overview', 'codecs', 'health', 'trends', 'settings', 'arr', 'recommendations', 'logs']) {
      expect(html).toContain(`data-tab="${tab}"`);
    }
    expect(html).toContain('data-tab="recommendations" style="display:none;"');
    expect(html).toContain('id="logsLevelFilter"');
  });
});

describe('updateLastScanBadge', () => {
  test('shows relative time and hides without input', () => {
    document.body.innerHTML = '<span id="lastScanBadge" style="display:none;"></span>';
    ui.updateLastScanBadge(new Date(Date.now() - 60000).toISOString());
    const badge = document.getElementById('lastScanBadge')!;
    expect(badge.style.display).toBe('flex');
    expect(badge.textContent).toContain('1 min ago');
    ui.updateLastScanBadge(null);
    expect(badge.style.display).toBe('none');
  });

  test('missing badge does not throw', () => {
    document.body.innerHTML = '';
    expect(() => ui.updateLastScanBadge('2026-01-01T00:00:00')).not.toThrow();
  });
});

describe('fillScanData end to end', () => {
  test('populates every tab from one scan payload', async () => {
    ajaxRouter({ Configuration: { UseTrash: false }, CleanupStatistics: {} });
    (globalThis as any).window.scrollTo = () => undefined;
    document.body.innerHTML = ui.renderShell();
    ui.fillScanData(scanFixture());
    await flush();
    expect(document.getElementById('overviewContent')!.textContent).toContain('Video Data - Movies');
    expect(document.querySelector('#codecsContent [data-chart-id="videoCodecs"]')).not.toBeNull();
    expect(document.querySelectorAll('#healthContent .health-item')).toHaveLength(4);
  });
});

describe('page lifecycle and folder entry points', () => {
  test('bindPageLifecycle attaches once and resets on show', () => {
    ajaxRouter({});
    document.body.innerHTML = '<div id="JellyfinHelperConfigPage"></div>';
    const pageEl = document.getElementById('JellyfinHelperConfigPage')!;
    const addSpy = vi.spyOn(pageEl, 'addEventListener');
    (globalThis as any).window.JellyfinHelper._pageLifecycleBound = false;
    ui.bindPageLifecycle();
    ui.bindPageLifecycle();
    expect((globalThis as any).window.JellyfinHelper._pageLifecycleBound).toBe(true);
    // The second call must be a no-op: a duplicate listener would run initPage twice per show.
    for (const type of ['pageshow', 'viewshow', 'pagehide', 'viewhide']) {
      expect(addSpy.mock.calls.filter((c) => c[0] === type)).toHaveLength(1);
    }
    // The registered pageshow listener re-arms a fresh init pass.
    (globalThis as any).window.JellyfinHelper._pageInitialized = true;
    (globalThis as any).window.JellyfinHelper._handlersBound = true;
    pageEl.dispatchEvent(new window.Event('pageshow'));
    expect((globalThis as any).window.JellyfinHelper._pageInitialized).toBe(false);
    expect((globalThis as any).window.JellyfinHelper._handlersBound).toBe(false);
    addSpy.mockRestore();
    // Drop the initPage timer the listener queued; the shared fake clock stays installed.
    vi.clearAllTimers();
  });

  test('bindPageLifecycle without the page element stays unbound', () => {
    ajaxRouter({});
    document.body.innerHTML = '';
    (globalThis as any).window.JellyfinHelper._pageLifecycleBound = false;
    ui.bindPageLifecycle();
    expect((globalThis as any).window.JellyfinHelper._pageLifecycleBound).toBe(false);
  });

  test('initFolderBrowser wires the browse button', () => {
    ajaxRouter({});
    document.body.innerHTML = '<input id="cfgTrashPath" value=""><button id="btnBrowseTrash"></button>';
    ui.initFolderBrowser();
    (document.getElementById('btnBrowseTrash') as HTMLButtonElement).click();
    expect(document.getElementById('folderBrowserOverlay')).not.toBeNull();
  });

  test('initFolderBrowser without a button does nothing', () => {
    document.body.innerHTML = '';
    expect(() => ui.initFolderBrowser()).not.toThrow();
  });

  test('failed scans surface the error banner', async () => {
    ajaxRouter({}, ['ScanLibraries']);
    document.body.innerHTML =
      '<button id="btnScanLibraries"></button><div id="loadingIndicator"></div>' +
      '<div id="statsPlaceholder"></div><div id="overviewContent"></div>';
    (globalThis as any).window.JellyfinHelper._statisticsInFlight = false;
    ui.loadStatistics();
    await flush();
    expect(document.getElementById('overviewContent')!.textContent).toMatch(/failed to load/i);
    expect((globalThis as any).window.JellyfinHelper._statisticsInFlight).toBe(false);
  });
});

describe('loadStatistics guards concurrent scans', () => {
  test('second call while in flight is ignored', async () => {
    const calls = ajaxRouter({ 'ScanLibraries': scanFixture() });
    document.body.innerHTML =
      '<button id="btnScanLibraries"></button><div id="loadingIndicator"></div><div id="statsPlaceholder"></div>';
    (globalThis as any).window.JellyfinHelper._statisticsInFlight = false;
    ui.loadStatistics();
    ui.loadStatistics();
    await flush();
    // The in-flight guard is what this test covers: exactly one scan leaves the client.
    expect(calls.filter((u) => u.includes('ScanLibraries'))).toHaveLength(1);
    expect((document.getElementById('btnScanLibraries') as HTMLButtonElement).disabled).toBe(false);
    expect((globalThis as any).window.JellyfinHelper._statisticsInFlight).toBe(false);
  });
});

describe('loadLatestStatistics retry and load', () => {
  function fetchStub(responder: (url: string) => unknown) {
    const calls: string[] = [];
    (globalThis as any).fetch = (url: string) => {
      calls.push(String(url));
      return responder(String(url));
    };
    return calls;
  }

  test('loads persisted stats without a rescan', async () => {
    const ajaxCalls = ajaxRouter({});
    const fetchCalls = fetchStub(() =>
      Promise.resolve({ status: 200, ok: true, json: () => Promise.resolve({ Libraries: [] }) }));
    document.body.innerHTML = '<div id="overviewContent"></div>';
    (globalThis as any).window.JellyfinHelper._latestStatsAuthRetries = 0;
    ui.loadLatestStatistics();
    await flush();
    // "Without a rescan": the persisted read must never trigger the expensive scan endpoint.
    expect(fetchCalls.filter((u) => u.includes('MediaStatistics/Latest'))).toHaveLength(1);
    expect(ajaxCalls.filter((u) => u.includes('ScanLibraries'))).toHaveLength(0);
    expect(fetchCalls.filter((u) => u.includes('ScanLibraries'))).toHaveLength(0);
    expect((globalThis as any).window.JellyfinHelper._latestStatsAuthRetries).toBe(0);
  });

  test('transient auth failures retry the idempotent read', async () => {
    ajaxRouter({});
    let n = 0;
    const calls = fetchStub(() => {
      n++;
      if (n < 3) return Promise.reject({ status: 403 });
      return Promise.resolve({ status: 200, ok: true, json: () => Promise.resolve({ Libraries: [] }) });
    });
    document.body.innerHTML = '';
    (globalThis as any).window.JellyfinHelper._latestStatsAuthRetries = 0;
    ui.loadLatestStatistics();
    await flush();
    await vi.advanceTimersByTimeAsync(400);
    await flush();
    await vi.advanceTimersByTimeAsync(400);
    await flush();
    expect(calls.filter((u) => u.includes('MediaStatistics/Latest'))).toHaveLength(3);
  });
});

describe('initTabs wiring', () => {
  test('clicking switches tabs through the guard', () => {
    document.body.innerHTML = ui.renderShell();
    ui.initTabs();
    const settings = document.querySelector('.tab-btn[data-tab="settings"]') as HTMLElement;
    settings.click();
    expect(document.getElementById('tab-settings')!.classList.contains('active')).toBe(true);
  });

  test('dirty settings raise the unsaved dialog instead of switching', () => {
    ajaxRouter({});
    document.body.innerHTML = ui.renderShell();
    document.getElementById('settingsForm')!.innerHTML =
      '<input id="cfgOrphanAge" value="30">' +
      '<select id="cfgTrickplayMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgEmptyFolderMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgSubtitleMode"><option value="DryRun">x</option></select><select id="cfgLinkMode"><option value="DryRun">x</option></select>' +
      '<input id="cfgTrashDays" value="30">';
    ui.initTabs();
    (document.querySelector('.tab-btn[data-tab="settings"]') as HTMLElement).click();
    ui.takeSettingsSnapshot();
    (document.getElementById('cfgOrphanAge') as HTMLInputElement).value = '31';
    (document.querySelector('.tab-btn[data-tab="overview"]') as HTMLElement).click();
    expect(document.getElementById('unsavedDialogOverlay')).not.toBeNull();
    expect(document.getElementById('tab-settings')!.classList.contains('active')).toBe(true);
  });
});
