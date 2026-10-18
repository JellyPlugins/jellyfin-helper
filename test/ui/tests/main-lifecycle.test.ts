import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => {
  ui = freshConfigPage();
});

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown> = {}) {
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

describe('main extras', () => {
  test('initTabs settings dirty discard proceeds', () => {
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
    const discard = [...document.querySelectorAll('button')].find((b) => String(b.textContent).includes('Discard')) as HTMLElement;
    discard.click();
    expect(document.getElementById('tab-overview')!.classList.contains('active')).toBe(true);
  });

  test('doTabSwitch cleanup and recommendations/logs', () => {
    ajaxRouter({});
    document.body.innerHTML = '<div class="tab-content active" id="tab-logs"></div><button id="b"></button>';
    (globalThis as any).window.JellyfinHelper = (globalThis as any).window.JellyfinHelper || {};
    (globalThis as any).destroyLogsTab = () => undefined;
    ui.doTabSwitch(document.getElementById('b') as any, 'recommendations');
    expect(document.getElementById('b')!.classList.contains('active')).toBe(true);
  });

  test('loadLatestStatistics 204 path', async () => {
    ajaxRouter({});
    (globalThis as any).fetch = () => Promise.resolve({ status: 204, ok: true });
    document.body.innerHTML = '<div id="overviewContent"></div>';
    (globalThis as any).window.JellyfinHelper._latestStatsAuthRetries = 0;
    ui.loadLatestStatistics();
    await flush();
    await vi.advanceTimersByTimeAsync(10);
    await flush();
    expect((globalThis as any).window.JellyfinHelper._latestStatsAuthRetries).toBe(0);
  });

  test('loadLatestStatistics transient exhausted', async () => {
    ajaxRouter({});
    (globalThis as any).fetch = () => Promise.reject({ status: 401 });
    document.body.innerHTML = '';
    (globalThis as any).window.JellyfinHelper._latestStatsAuthRetries = 8;
    ui.loadLatestStatistics();
    await flush();
    expect((globalThis as any).window.JellyfinHelper._latestStatsAuthRetries).toBe(0);
  });

  test('initPage missing button retries', async () => {
    ajaxRouter({});
    (globalThis as any).window.JellyfinHelper._pageLifecycleBound = false;
    document.body.innerHTML = '<div id="JellyfinHelperConfigPage"></div>';
    ui.bindPageLifecycle();
    const pageEl = document.getElementById('JellyfinHelperConfigPage')!;
    (globalThis as any).window.JellyfinHelper._pageInitialized = false;
    (globalThis as any).window.JellyfinHelper._initRetries = 0;
    (globalThis as any).window.JellyfinHelper._handlersBound = false;
    pageEl.dispatchEvent(new window.Event('pageshow'));
    await vi.advanceTimersByTimeAsync(0);
    await flush();
    expect((globalThis as any).window.JellyfinHelper._initRetries).toBe(1);
  });

  test('initPage exhausted retries', async () => {
    ajaxRouter({});
    (globalThis as any).window.JellyfinHelper._pageLifecycleBound = false;
    document.body.innerHTML = '<div id="JellyfinHelperConfigPage"></div>';
    ui.bindPageLifecycle();
    const pageEl = document.getElementById('JellyfinHelperConfigPage')!;
    (globalThis as any).window.JellyfinHelper._pageInitialized = false;
    (globalThis as any).window.JellyfinHelper._handlersBound = false;
    pageEl.dispatchEvent(new window.Event('pageshow'));
    (globalThis as any).window.JellyfinHelper._initRetries = 20;
    await vi.advanceTimersByTimeAsync(0);
    await flush();
    expect((globalThis as any).window.JellyfinHelper._initRetries).toBe(21);
  });

  test('initPage with button renders', async () => {
    ajaxRouter({});
    (globalThis as any).fetch = () => Promise.resolve({ status: 204, ok: true });
    (globalThis as any).window.JellyfinHelper._pageLifecycleBound = false;
    document.body.innerHTML = '<div id="JellyfinHelperConfigPage"></div><button id="btnScanLibraries"></button><div id="statsPlaceholder"></div><div id="statsResult"></div><div id="loadingIndicator"></div>';
    ui.bindPageLifecycle();
    const pageEl = document.getElementById('JellyfinHelperConfigPage')!;
    (globalThis as any).window.JellyfinHelper._pageInitialized = false;
    (globalThis as any).window.JellyfinHelper._initRetries = 0;
    (globalThis as any).window.JellyfinHelper._handlersBound = false;
    (globalThis as any).window.JellyfinHelper._statisticsInFlight = false;
    pageEl.dispatchEvent(new window.Event('pageshow'));
    await vi.advanceTimersByTimeAsync(0);
    await flush();
    await vi.advanceTimersByTimeAsync(300);
    await flush();
    expect((globalThis as any).window.JellyfinHelper._pageInitialized).toBe(true);
  });

  test('bindPageLifecycle viewshow and hide events', () => {
    (globalThis as any).window.JellyfinHelper = (globalThis as any).window.JellyfinHelper || {};
    (globalThis as any).window.JellyfinHelper._pageLifecycleBound = false;
    document.body.innerHTML = '<div id="JellyfinHelperConfigPage"></div>';
    ui.bindPageLifecycle();
    const pageEl = document.getElementById('JellyfinHelperConfigPage')!;
    pageEl.dispatchEvent(new window.Event('viewshow'));
    pageEl.dispatchEvent(new window.Event('pagehide'));
    pageEl.dispatchEvent(new window.Event('viewhide'));
    expect((globalThis as any).window.JellyfinHelper._pageLifecycleBound).toBe(true);
  });
});
