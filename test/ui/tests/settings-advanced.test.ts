import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => {
  ui = freshConfigPage();
});

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown>, failures: string[] = []) {
  const calls: { url: string; body?: unknown; method?: string }[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      let body: unknown;
      try {
        body = typeof opts.data === 'string' ? JSON.parse(opts.data) : opts.data;
      } catch {
        body = opts.data;
      }
      calls.push({ url: String(opts.url), body, method: opts.type });
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

function trashForm() {
  document.body.innerHTML =
    '<form id="settingsForm">' +
    '<select id="cfgRecommendationsMode"><option value="DryRun">x</option></select>' +
    '<input id="cfgSeerrUrl" value=""><input id="cfgSeerrApiKey" value="">' +
    '<input type="checkbox" id="cfgDiscoveryUserAccess"><div id="discoveryAccessWrapper"></div>' +
    '<input id="cfgOrphanAge" value="30">' +
    '<select id="cfgTrickplayMode"><option value="DryRun">x</option></select>' +
    '<select id="cfgEmptyFolderMode"><option value="DryRun">x</option></select>' +
    '<select id="cfgSubtitleMode"><option value="DryRun">x</option></select><select id="cfgLinkMode"><option value="DryRun">x</option></select>' +
    '<input type="checkbox" id="cfgTrash" checked><input id="cfgTrashPath" value="/t/old"><input id="cfgTrashDays" value="30">' +
    '<select id="cfgLang"><option value="en">en</option></select>' +
    '<div id="cfgExcludedWrapper"></div><div id="arrResult"></div>' +
    '<button id="btnSaveSettings"></button><div id="settingsMsg"></div><div id="settingsSaveBand"></div>' +
    '</form>';
  (document.getElementById('cfgRecommendationsMode') as HTMLSelectElement).value = 'DryRun';
}

// A successful save flips the trash-enabled flag, so doSaveSettings runs
// rebuildUI, which refetches the configuration. The real server echoes the
// saved config, so the GET must return a trash-enabled payload or rebuildUI
// would re-render the trash checkbox unchecked and swallow the next change.
const SAVED_TRASH_CONFIG = { UseTrash: true, TrashFolderPath: '/t/old', TrashRetentionDays: 30 };

function trashRoutes(extra: Record<string, unknown> = {}) {
  return { 'JellyfinHelper/Configuration': SAVED_TRASH_CONFIG, ...extra };
}

async function enableTrash(calls: { url: string }[]) {
  ui.takeSettingsSnapshot();
  const ok = vi.fn();
  ui.doSaveSettings(ui.buildSettingsPayload(), { quiet: true, element: null, onSuccess: ok });
  await flush();
  expect(ok).toHaveBeenCalled();
  expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Configuration'))).toBe(true);
}

describe('trash path change dialog', () => {
  test('no old content saves straight through', async () => {
    const calls = ajaxRouter(trashRoutes({ 'FoldersForPath': { Paths: [] } }));
    trashForm();
    await enableTrash(calls);
    const puts = () => calls.filter((c) => String(c.url).includes('JellyfinHelper/Configuration')).length;
    const before = puts();
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = '/t/new';
    ui.doSaveSettings(ui.buildSettingsPayload(), { quiet: true, element: null });
    await flush();
    expect(puts()).toBeGreaterThan(before);
    expect(document.getElementById('trashDialogOverlay')).toBeNull();
  });

  test('cancel reverts the input and saves nothing more', async () => {
    const calls = ajaxRouter(trashRoutes({ 'FoldersForPath': { Paths: ['/t/old/x'] } }));
    trashForm();
    await enableTrash(calls);
    const puts = () => calls.filter((c) => String(c.url).includes('JellyfinHelper/Configuration')).length;
    const before = puts();
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = '/t/new';
    ui.doSaveSettings(ui.buildSettingsPayload(), { quiet: true, element: null });
    await flush();
    const overlay = document.getElementById('trashDialogOverlay')!;
    const cancel = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Cancel')!;
    cancel.click();
    expect((document.getElementById('cfgTrashPath') as HTMLInputElement).value).toBe('/t/old');
    expect(puts()).toBe(before);
  });

  test('move relocates after access check and reports success', async () => {
    const calls = ajaxRouter(trashRoutes({
      'FoldersForPath': { Paths: ['/t/old/x'] },
      'Trash/CheckAccess': { AllAccessible: true },
      'Trash/Relocate': { Moved: 2, Failed: 0 },
    }));
    trashForm();
    await enableTrash(calls);
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = '/t/new';
    ui.doSaveSettings(ui.buildSettingsPayload(), { quiet: true, element: null });
    await flush();
    const overlay = document.getElementById('trashDialogOverlay')!;
    const move = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Move Content')!;
    move.click();
    await flush();
    expect(calls.some((c) => String(c.url).includes('Trash/Relocate'))).toBe(true);
    expect(document.getElementById('settingsMsg')!.textContent).toMatch(/moved successfully/i);
  });

  test('denied access blocks the move with the server detail', async () => {
    const calls = ajaxRouter(trashRoutes({
      'FoldersForPath': { Paths: ['/t/old/x'] },
      'Trash/CheckAccess': { AllAccessible: false, Results: [{ HasFullAccess: false, ErrorMessage: 'ro fs' }] },
    }));
    trashForm();
    await enableTrash(calls);
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = '/t/new';
    ui.doSaveSettings(ui.buildSettingsPayload(), { quiet: true, element: null });
    await flush();
    const overlay = document.getElementById('trashDialogOverlay')!;
    const move = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Move Content')!;
    move.click();
    await flush();
    expect(document.getElementById('settingsMsg')!.textContent).toContain('ro fs');
  });

  test('delete-fresh removes old folders then saves the new path', async () => {
    const calls = ajaxRouter(trashRoutes({
      'FoldersForPath': { Paths: ['/t/old/x'] },
      'Trash/Folders': { Deleted: 1, Failed: 0 },
    }));
    trashForm();
    await enableTrash(calls);
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = '/t/new';
    ui.doSaveSettings(ui.buildSettingsPayload(), { quiet: true, element: null });
    await flush();
    const overlay = document.getElementById('trashDialogOverlay')!;
    const del = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Delete & Start Fresh')!;
    del.click();
    await flush();
    expect(calls.some((c) => String(c.url).includes('Trash/Folders') && c.method === 'DELETE')).toBe(true);
    expect((document.getElementById('cfgTrashPath') as HTMLInputElement).value).toBe('/t/new');
  });

  test('folders lookup failure still saves', async () => {
    const calls = ajaxRouter(trashRoutes(), ['FoldersForPath']);
    trashForm();
    await enableTrash(calls);
    const puts = () => calls.filter((c) => String(c.url).includes('JellyfinHelper/Configuration')).length;
    const before = puts();
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = '/t/new';
    ui.doSaveSettings(ui.buildSettingsPayload(), { quiet: true, element: null });
    await flush();
    expect(puts()).toBeGreaterThan(before);
    expect(document.getElementById('trashDialogOverlay')).toBeNull();
  });
});

describe('misc settings entries', () => {
  test('delete confirmation removes folders then saves', async () => {
    const calls = ajaxRouter(trashRoutes({ 'Trash/Folders': { Deleted: 2, Failed: 0 } }));
    trashForm();
    await enableTrash(calls);
    const configCallsBefore = calls.filter((c) => String(c.url).includes('JellyfinHelper/Configuration')).length;
    ui.showTrashDeleteConfirmation(ui.buildSettingsPayload(), ['/t/a']);
    await flush();
    const overlay = document.getElementById('trashDialogOverlay')!;
    const del = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Yes, Delete All')!;
    del.click();
    await flush();
    expect(calls.some((c) => String(c.url).includes('Trash/Folders') && c.method === 'DELETE')).toBe(true);
    expect(document.getElementById('settingsMsg')!.textContent).toMatch(/deleted/i);
    const configCallsAfter = calls.filter((c) => String(c.url).includes('JellyfinHelper/Configuration')).length;
    expect(configCallsAfter).toBeGreaterThan(configCallsBefore);
  });

  test('failed delete shows the error and saves nothing', async () => {
    const calls = ajaxRouter(trashRoutes(), ['Trash/Folders']);
    trashForm();
    await enableTrash(calls);
    const configCallsBefore = calls.filter((c) => String(c.url).includes('JellyfinHelper/Configuration')).length;
    ui.showTrashDeleteConfirmation(ui.buildSettingsPayload(), ['/t/a']);
    await flush();
    const overlay = document.getElementById('trashDialogOverlay')!;
    const del = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Yes, Delete All')!;
    del.click();
    await flush();
    expect(document.getElementById('settingsMsg')!.textContent).toMatch(/failed to delete/i);
    // A failed delete must not fall through to persisting the new config.
    const configCallsAfter = calls.filter((c) => String(c.url).includes('JellyfinHelper/Configuration')).length;
    expect(configCallsAfter).toBe(configCallsBefore);
  });
  test('saveSettings entry saves the built payload', async () => {
    const calls = ajaxRouter({});
    trashForm();
    (document.getElementById('cfgTrash') as HTMLInputElement).checked = false;
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = '';
    ui.takeSettingsSnapshot();
    ui.saveSettings();
    await flush();
    expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Configuration'))).toBe(true);
  });

  test('removeTrashDialog clears a stale overlay', () => {
    document.body.innerHTML = '<div id="trashDialogOverlay"></div>';
    ui.removeTrashDialog();
    expect(document.getElementById('trashDialogOverlay')).toBeNull();
    expect(() => ui.removeTrashDialog()).not.toThrow();
  });

  test('formatPathList bullets each path', () => {
    expect(ui.formatPathList(['/a', '/b'])).toBe('\n  • /a\n  • /b');
    expect(ui.formatPathList([])).toBe('');
  });

  test('failing ping still resolves the probe', async () => {
    ajaxRouter({}, ['JellyfinHelper/Ping']);
    await ui.probeBackendReachability({ status: 0, kind: 'network' });
    await flush();
  });
});
