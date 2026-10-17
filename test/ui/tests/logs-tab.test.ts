import { afterAll, describe, expect, test } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown>, failures: string[] = []) {
  const calls: string[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
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

async function flush() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

describe('formatLogTimestamp', () => {
  test.each([[null], [undefined], ['']])('empty %s stays empty', (v) => {
    expect(ui.formatLogTimestamp(v)).toBe('');
  });

  test('formats local time deterministically', () => {
    const out = ui.formatLogTimestamp('2026-03-05T04:07:09');
    expect(out).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/);
    expect(out).toContain('2026-03-05');
  });

  test.each([['not-a-date'], ['2026-13-99']])('invalid %s is flagged', (v) => {
    expect(ui.formatLogTimestamp(v)).toBe('[invalid date]');
  });
});

describe('renderLogsTab shell', () => {
  test('toolbar controls and download button exist', () => {
    document.body.innerHTML = ui.renderLogsTab();
    expect(document.getElementById('logsLevelFilter')).not.toBeNull();
    expect(document.getElementById('logsSourceFilter')).not.toBeNull();
    expect(document.getElementById('btnLogsDownload')).not.toBeNull();
    expect(document.getElementById('btnLogsClear')).not.toBeNull();
  });
});

describe('loadLogs', () => {
  test('renders rows with safe levels and counts', async () => {
    ajaxRouter({
      'Logs?': {
        Entries: [
          { Timestamp: '2026-03-05T04:07:09', Level: 'INFO', Source: 'A', Message: 'hi' },
          { Timestamp: '', Level: 'EVIL<script>', Source: 'B', Message: 'x', Exception: 'boom' },
        ],
        TotalBuffered: 10,
        Returned: 2,
      },
    });
    document.body.innerHTML = ui.renderLogsTab();
    ui.loadLogs();
    await flush();
    const rows = document.querySelectorAll('.logs-table tbody tr');
    expect(rows).toHaveLength(2);
    expect(document.getElementById('logsCount')!.textContent).toContain('2');
    expect(document.body.innerHTML).toContain('log-level-info');
    expect(document.body.innerHTML).not.toContain('<script>');
  });

  test('unknown levels fall back to info styling', async () => {
    ajaxRouter({ 'Logs?': { Entries: [{ Level: 'TRACE', Message: 'm' }], TotalBuffered: 1, Returned: 1 } });
    document.body.innerHTML = ui.renderLogsTab();
    ui.loadLogs();
    await flush();
    expect(document.body.innerHTML).toContain('log-level-info');
  });

  test('empty entries render the empty state', async () => {
    ajaxRouter({ 'Logs?': { Entries: [], TotalBuffered: 0, Returned: 0 } });
    document.body.innerHTML = ui.renderLogsTab();
    ui.loadLogs();
    await flush();
    expect(document.body.innerHTML).toContain('No log entries.');
  });

  test('filters travel in the query string', async () => {
    const calls = ajaxRouter({ 'Logs?': { Entries: [] } });
    document.body.innerHTML = ui.renderLogsTab();
    (document.getElementById('logsLevelFilter') as HTMLSelectElement).value = 'ERROR';
    (document.getElementById('logsSourceFilter') as HTMLInputElement).value = 'a b';
    ui.loadLogs();
    await flush();
    expect(calls.some((u) => u.includes('minLevel=ERROR') && u.includes('source=a%20b'))).toBe(true);
  });

  test('failure renders the error state', async () => {
    ajaxRouter({}, ['Logs?']);
    document.body.innerHTML = ui.renderLogsTab();
    ui.loadLogs();
    await flush();
    expect(document.body.innerHTML).toContain('Failed to load logs.');
  });

  test('missing wrapper does not throw', () => {
    ajaxRouter({});
    document.body.innerHTML = '';
    expect(() => ui.loadLogs()).not.toThrow();
  });
});

describe('downloadLogs', () => {
  test('creates an object url and re-enables the button', async () => {
    ajaxRouter({});
    (globalThis as any).ApiClient.accessToken = () => 't';
    (globalThis as any).fetch = () =>
      Promise.resolve({ ok: true, blob: () => Promise.resolve(new Blob(['x'])) });
    const seen: string[] = [];
    (globalThis as any).URL.createObjectURL = () => {
      seen.push('created');
      return 'blob:mock';
    };
    (globalThis as any).URL.revokeObjectURL = () => undefined;
    document.body.innerHTML = ui.renderLogsTab();
    ui.downloadLogs();
    await flush();
    expect(seen).toEqual(['created']);
    expect((document.getElementById('btnLogsDownload') as HTMLButtonElement).disabled).toBe(false);
  });

  test('failure shows button feedback and re-enables', async () => {
    ajaxRouter({});
    (globalThis as any).ApiClient.accessToken = () => 't';
    (globalThis as any).fetch = () => Promise.reject(new Error('down'));
    document.body.innerHTML = ui.renderLogsTab();
    ui.downloadLogs();
    await flush();
    const btn = document.getElementById('btnLogsDownload') as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
    expect(btn.textContent).toMatch(/failed/i);
  });
});

describe('clearLogs dialog', () => {
  test('cancel closes without calling delete', async () => {
    const calls = ajaxRouter({});
    document.body.innerHTML = ui.renderLogsTab();
    ui.clearLogs();
    const overlay = document.getElementById('logsClearDialogOverlay')!;
    expect(overlay).not.toBeNull();
    const cancel = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Cancel')!;
    cancel.click();
    await flush();
    expect(document.getElementById('logsClearDialogOverlay')).toBeNull();
    expect(calls.some((u) => u.includes('JellyfinHelper/Logs') && !u.includes('Download'))).toBe(false);
  });
});

describe('lifecycle guards', () => {
  test('destroy and reset never throw', () => {
    expect(() => ui.destroyLogsTab()).not.toThrow();
    expect(() => ui.resetLogsTabState()).not.toThrow();
  });

  test('log level falls back to INFO when config fails', async () => {
    ajaxRouter({}, ['Configuration']);
    document.body.innerHTML = ui.renderLogsTab();
    ui.loadLogLevelFromConfig(() => undefined);
    await flush();
    expect((document.getElementById('logsLevelFilter') as HTMLSelectElement).value).toBe('INFO');
  });
});
