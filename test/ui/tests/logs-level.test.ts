import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => { ui = freshConfigPage(); });

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown> = {}, failures: string[] = []) {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      if (failures.some((f) => String(opts.url).includes(f))) return Promise.reject(new Error('down'));
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
}

describe('logs extras', () => {
  test('initLogsTab handlers and level/source changes', async () => {
    ajaxRouter({ Configuration: {} });
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
    expect(level.value).toBe('ERROR');
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
    ajaxRouter({ 'Logs?': { Entries: [], TotalBuffered: 0, Returned: 0 } });
    document.body.innerHTML = ui.renderLogsTab();
    ui.loadLogs();
    (globalThis as any)._logsLoadSeq = 999;
    ui.loadLogs();
    await flush();
    expect(document.getElementById('logsTableWrapper')).not.toBeNull();
  });

  test('downloadLogs with source', async () => {
    (globalThis as any).URL.createObjectURL = () => 'blob:x';
    (globalThis as any).URL.revokeObjectURL = () => undefined;
    ajaxRouter({ 'Logs/Download': new Blob(['x']) });
    document.body.innerHTML = ui.renderLogsTab();
    (document.getElementById('logsLevelFilter') as HTMLSelectElement).value = 'WARN';
    (document.getElementById('logsSourceFilter') as HTMLInputElement).value = 'src';
    ui.downloadLogs();
    await flush();
    await vi.advanceTimersByTimeAsync(150);
    expect(document.getElementById('logsTableWrapper')).not.toBeNull();
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
