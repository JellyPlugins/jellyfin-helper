import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

// Fresh bundle per test: the logs helpers carry module state (init sequence,
// auto-refresh timers) that would otherwise bleed across the shared singleton.
let ui: UiBridge;
beforeEach(() => {
  ui = freshConfigPage();
  tabDom('overview');
});

afterAll(saveCoverage);


function tabDom(active: string) {
  const tabs = ['overview', 'settings', 'logs'];
  document.body.innerHTML =
    tabs.map((t) => `<button class="tab-btn${t === active ? ' active' : ''}" data-tab="${t}"></button>`).join('') +
    tabs.map((t) => `<div class="tab-content${t === active ? ' active' : ''}" id="tab-${t}"></div>`).join('') +
    // Logs tab controls initLogsTab/loadLogs read; present so the real functions run.
    '<div id="logsTableWrapper"></div><span id="logsCount"></span>' +
    '<select id="logsLevelFilter"></select><input id="logsSourceFilter">' +
    '<button id="btnLogsDownload"></button><button id="btnLogsClear"></button>';
}

// The logs helpers are lexically scoped inside the shipped bundle, so they
// cannot be spied through globalThis. Assert their observable effects instead:
// entering logs fetches logs, leaving logs stops the auto-refresh.
function captureCalls() {
  const calls: string[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      calls.push(String(opts.url));
      return Promise.resolve({});
    },
  };
  return calls;
}

async function flush() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

describe('doTabSwitch', () => {
  test('activates the target and deactivates the rest', () => {
    const btn = document.querySelector('.tab-btn[data-tab="settings"]') as HTMLElement;
    ui.doTabSwitch(btn, 'settings');
    expect(btn.classList.contains('active')).toBe(true);
    expect(document.getElementById('tab-settings')!.classList.contains('active')).toBe(true);
    expect(document.querySelector('.tab-btn[data-tab="overview"]')!.classList.contains('active')).toBe(false);
  });

  test('leaving logs stops the auto-refresh', async () => {
    ui.resetLogsTabState();
    tabDom('logs');
    const calls = captureCalls();
    const logFetches = () => calls.filter((u) => u.includes('JellyfinHelper/Logs')).length;
    ui.initLogsTab();
    await flush();
    // Positive control: prove the 10s auto-refresh is actually running before we leave,
    // otherwise a silently disabled refresh would make the assertion below vacuous.
    const beforeTick = logFetches();
    await vi.advanceTimersByTimeAsync(11000);
    await flush();
    expect(logFetches()).toBeGreaterThan(beforeTick);
    const btn = document.querySelector('.tab-btn[data-tab="overview"]') as HTMLElement;
    ui.doTabSwitch(btn, 'overview');
    const afterLeave = logFetches();
    // Auto-refresh fires every 10s; after destroy it must not fetch again.
    await vi.advanceTimersByTimeAsync(30000);
    await flush();
    expect(logFetches()).toBe(afterLeave);
  });

  test('entering logs fetches the log output', async () => {
    ui.resetLogsTabState();
    const calls = captureCalls();
    const btn = document.querySelector('.tab-btn[data-tab="logs"]') as HTMLElement;
    ui.doTabSwitch(btn, 'logs');
    await flush();
    expect(calls.some((u) => u.includes('JellyfinHelper/Logs'))).toBe(true);
  });

  test('missing target panel does not throw', () => {
    document.body.innerHTML = '<button class="tab-btn active" data-tab="ghost"></button>';
    const btn = document.querySelector('.tab-btn') as HTMLElement;
    expect(() => ui.doTabSwitch(btn, 'ghost')).not.toThrow();
  });
});
