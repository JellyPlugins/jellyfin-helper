import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => { ui = freshConfigPage(); });

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown> = {}, failures: string[] = []) {
  const calls: { url: string }[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      calls.push({ url: String(opts.url) });
      if (failures.some((f) => String(opts.url).includes(f))) return Promise.reject(new Error('down'));
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
  return calls;
}

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
    ajaxRouter({ 'LogLevel': {} });
    document.body.innerHTML = ui.renderLogsTab();
    ui.saveLogLevelToConfig('DEBUG');
    await flush();
    ajaxRouter({}, ['LogLevel']);
    ui.saveLogLevelToConfig('INFO');
    await flush();
    expect(document.getElementById('logsLevelFilter')).not.toBeNull();
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
    ajaxRouter({ 'JellyfinHelper/Logs': {} });
    document.body.innerHTML = ui.renderLogsTab();
    ui.clearLogs();
    const confirmBtn = [...document.querySelectorAll('button')].find((b) => String(b.textContent).trim() === 'Clear') as HTMLElement;
    confirmBtn.click();
    await flush();

    ajaxRouter({}, ['JellyfinHelper/Logs']);
    document.body.innerHTML = ui.renderLogsTab();
    ui.clearLogs();
    const confirmBtn2 = [...document.querySelectorAll('button')].find((b) => String(b.textContent).trim() === 'Clear') as HTMLElement;
    confirmBtn2.click();
    await flush();
    expect(document.getElementById('logsTableWrapper')).not.toBeNull();
  });

  test('formatLogTimestamp invalid', () => {
    expect(ui.formatLogTimestamp('not-a-date')).toBe('[invalid date]');
    expect(ui.formatLogTimestamp('')).toBe('');
  });
});
