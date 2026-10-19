import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { ajaxRouter, flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => { ui = freshConfigPage(); });

afterAll(saveCoverage);

describe('logs extras', () => {
  test('initLogsTab handlers and level/source changes', async () => {
    const calls = ajaxRouter({ Configuration: {} });
    document.body.innerHTML = ui.renderLogsTab();
    ui.initLogsTab();
    await flush();
    const level = document.getElementById('logsLevelFilter') as HTMLSelectElement;
    level.value = 'ERROR';
    level.dispatchEvent(new Event('change'));
    const source = document.getElementById('logsSourceFilter') as HTMLInputElement;
    source.value = 'x';
    source.dispatchEvent(new Event('input'));
    await vi.advanceTimersByTimeAsync(450);
    await flush();
    expect(calls.some((c) => c.url.includes('minLevel=ERROR') && c.url.includes('source=x'))).toBe(true);
  });

  test('loadLogLevelFromConfig loaded path', async () => {
    ajaxRouter({ Configuration: { PluginLogLevel: 'WARN' } });
    document.body.innerHTML = '<div id="settingsForm"></div>';
    ui.loadSettings();
    await flush();
    document.body.innerHTML = ui.renderLogsTab();
    ui.loadLogLevelFromConfig(() => undefined);
    expect((document.getElementById('logsLevelFilter') as HTMLSelectElement).value).toBe('WARN');
  });

  test('saveLogLevelToConfig success and failure', async () => {
    const okCalls = ajaxRouter({ 'LogLevel': {} });
    document.body.innerHTML = ui.renderLogsTab();
    ui.saveLogLevelToConfig('DEBUG');
    await flush();
    expect(okCalls.filter((c) => c.url.includes('LogLevel'))).toHaveLength(1);
    const failCalls = ajaxRouter({}, ['LogLevel']);
    ui.saveLogLevelToConfig('INFO');
    await flush();
    expect(failCalls.filter((c) => c.url.includes('LogLevel'))).toHaveLength(1);
  });

  test('loadLogs stale request', async () => {
    let releaseFirst!: (v: unknown) => void;
    let n = 0;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: (opts: any) => {
        n++;
        if (n === 1) return new Promise((r) => { releaseFirst = r; });
        return Promise.resolve({ Entries: [{ Level: 'INFO', Message: 'second' }], TotalBuffered: 1, Returned: 1 });
      },
    };
    document.body.innerHTML = ui.renderLogsTab();
    ui.loadLogs();
    ui.loadLogs();
    await flush();
    releaseFirst({ Entries: [{ Level: 'INFO', Message: 'first' }], TotalBuffered: 1, Returned: 1 });
    await flush();
    const wrapper = document.getElementById('logsTableWrapper')!;
    expect(wrapper.textContent).toContain('second');
    expect(wrapper.textContent).not.toContain('first');
  });

  test('downloadLogs with source', async () => {
    (globalThis as any).URL.createObjectURL = () => 'blob:x';
    (globalThis as any).URL.revokeObjectURL = () => undefined;
    ajaxRouter({});
    const seen: string[] = [];
    (globalThis as any).fetch = (url: string) => {
      seen.push(url);
      return Promise.resolve({ ok: true, blob: () => Promise.resolve(new Blob(['x'])) });
    };
    document.body.innerHTML = ui.renderLogsTab();
    (document.getElementById('logsLevelFilter') as HTMLSelectElement).value = 'WARN';
    (document.getElementById('logsSourceFilter') as HTMLInputElement).value = 'src';
    ui.downloadLogs();
    await flush();
    await vi.advanceTimersByTimeAsync(150);
    expect(seen).toHaveLength(1);
    expect(seen[0]).toContain('minLevel=WARN');
    expect(seen[0]).toContain('source=src');
  });

  test('clearLogs success and failure', async () => {
    const okCalls = ajaxRouter({ 'JellyfinHelper/Logs': {} });
    document.body.innerHTML = ui.renderLogsTab();
    ui.clearLogs();
    const okOverlay = document.getElementById('logsClearDialogOverlay')!;
    const confirmBtn = [...okOverlay.querySelectorAll('button')].find((b) => String(b.textContent).trim() === 'Clear') as HTMLElement;
    confirmBtn.click();
    await flush();
    expect(okCalls.filter((c) => c.method === 'DELETE' && String(c.url) === 'JellyfinHelper/Logs')).toHaveLength(1);
    expect(document.getElementById('btnLogsClear')!.classList.contains('success')).toBe(true);

    const failCalls = ajaxRouter({}, ['JellyfinHelper/Logs']);
    document.body.innerHTML = ui.renderLogsTab();
    ui.clearLogs();
    const failOverlay = document.getElementById('logsClearDialogOverlay')!;
    const confirmBtn2 = [...failOverlay.querySelectorAll('button')].find((b) => String(b.textContent).trim() === 'Clear') as HTMLElement;
    confirmBtn2.click();
    await flush();
    expect(failCalls.filter((c) => c.method === 'DELETE' && String(c.url) === 'JellyfinHelper/Logs')).toHaveLength(1);
    expect(document.getElementById('btnLogsClear')!.classList.contains('error')).toBe(true);
  });

  test('formatLogTimestamp invalid', () => {
    expect(ui.formatLogTimestamp('not-a-date')).toBe('[invalid date]');
    expect(ui.formatLogTimestamp('')).toBe('');
  });
});
