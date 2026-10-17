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

function fullForm() {
  document.body.innerHTML =
    '<form id="settingsForm">' +
    '<select id="cfgRecommendationsMode"><option value="DryRun">DryRun</option><option value="Activate">Activate</option><option value="Deactivate">Deactivate</option></select>' +
    '<input id="cfgSeerrUrl" value=""><input id="cfgSeerrApiKey" value="">' +
    '<input type="checkbox" id="cfgDiscoveryUserAccess">' +
    '<div id="discoveryAccessWrapper"></div><div class="discovery-access-disabled-hint" style="display:none"></div>' +
    '<button id="btnToggleDiscoveryHint"></button><div class="discovery-setup-hint"></div>' +
    '<div id="traktSourcingRow" style="display:none"></div><div id="traktSourcingHint" style="display:none"></div>' +
    '<input id="cfgOrphanAge" value="30">' +
    '<select id="cfgTrickplayMode"><option value="DryRun">x</option><option value="Activate">y</option></select>' +
    '<select id="cfgEmptyFolderMode"><option value="DryRun">x</option></select>' +
    '<select id="cfgSubtitleMode"><option value="DryRun">x</option></select><select id="cfgLinkMode"><option value="DryRun">x</option></select>' +
    '<input type="checkbox" id="cfgTrash"><input id="cfgTrashPath" value=""><input id="cfgTrashDays" value="30">' +
    '<select id="cfgLang"><option value="en">en</option><option value="de">de</option></select>' +
    '<input type="checkbox" id="cfgTraktSourcingEnabled">' +
    '<div id="cfgExcludedWrapper"></div><div id="arrResult"></div>' +
    '<button id="btnSaveSettings"></button><div id="settingsMsg"></div><div id="settingsSaveBand"></div>' +
    '<button id="btnBackupExport"></button><div id="backupMsg"></div>' +
    '<button id="btnBackupImport"></button><input type="file" id="btnBackupImportFile">' +
    '</form>';
  (document.getElementById('cfgRecommendationsMode') as HTMLSelectElement).value = 'DryRun';
}

describe('buildSettingsPayload', () => {
  test('modes pass through verbatim and trash stays off by default', () => {
    fullForm();
    (document.getElementById('cfgTrickplayMode') as HTMLSelectElement).value = 'Activate';
    const p = ui.buildSettingsPayload();
    expect(p.TrickplayTaskMode).toBe('Activate');
    expect(p.EmptyMediaFolderTaskMode).toBe('DryRun');
    expect(p.UseTrash).toBe(false);
    expect(p.Language).toBe('en');
    expect(p.RadarrInstances).toEqual([]);
  });

  test('numeric fields clamp to their ranges', () => {
    fullForm();
    (document.getElementById('cfgOrphanAge') as HTMLInputElement).value = '-5';
    (document.getElementById('cfgTrashDays') as HTMLInputElement).value = '99999';
    const p = ui.buildSettingsPayload();
    expect(p.OrphanMinAgeDays).toBe(0);
    expect(p.TrashRetentionDays).toBe(3650);
  });

  test('trakt sends null without the plugin, value with it', () => {
    fullForm();
    expect(ui.buildSettingsPayload().TraktSourcingEnabled).toBeNull();
  });

  test('discovery forcer drops stale true without prerequisites', () => {
    fullForm();
    const chk = document.getElementById('cfgDiscoveryUserAccess') as HTMLInputElement;
    chk.checked = true;
    (document.getElementById('cfgRecommendationsMode') as HTMLSelectElement).value = 'Deactivate';
    expect(ui.buildSettingsPayload().DiscoveryUserAccessEnabled).toBe(false);
  });

  test('discovery keeps true with mode and seerr set', () => {
    fullForm();
    (document.getElementById('cfgRecommendationsMode') as HTMLSelectElement).value = 'Activate';
    (document.getElementById('cfgSeerrUrl') as HTMLInputElement).value = 'http://s:5055';
    (document.getElementById('cfgSeerrApiKey') as HTMLInputElement).value = 'k';
    (document.getElementById('cfgDiscoveryUserAccess') as HTMLInputElement).checked = true;
    expect(ui.buildSettingsPayload().DiscoveryUserAccessEnabled).toBe(true);
  });
});

describe('snapshot and dirty tracking', () => {
  test('clean form reports no changes', () => {
    fullForm();
    ajaxRouter({});
    ui.takeSettingsSnapshot();
    expect(ui.hasUnsavedSettings()).toBe(false);
  });

  test('edited form reports changes', () => {
    fullForm();
    ajaxRouter({});
    ui.takeSettingsSnapshot();
    (document.getElementById('cfgOrphanAge') as HTMLInputElement).value = '31';
    expect(ui.hasUnsavedSettings()).toBe(true);
  });
});

describe('doSaveSettings', () => {
  test('quiet save puts the payload and calls back', async () => {
    const calls = ajaxRouter({});
    fullForm();
    ui.takeSettingsSnapshot();
    const ok = vi.fn();
    const payload = ui.buildSettingsPayload();
    ui.doSaveSettings(payload, { quiet: true, element: null, onSuccess: ok });
    await flush();
    const put = calls.find((c) => String(c.url).includes('JellyfinHelper/Configuration'));
    expect(put).toBeDefined();
    expect(put!.body).toMatchObject({ TrickplayTaskMode: 'DryRun', UseTrash: false });
    expect(ok).toHaveBeenCalled();
  });

  test('invalid trash path blocks the put and shows the error', async () => {
    fullForm();
    const calls = ajaxRouter({});
    (document.getElementById('cfgTrash') as HTMLInputElement).checked = true;
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = 'a*b';
    const err = vi.fn();
    ui.doSaveSettings(ui.buildSettingsPayload(), { quiet: true, element: null, onError: err });
    await flush();
    expect(err).toHaveBeenCalled();
    expect(calls).toHaveLength(0);
    expect(document.getElementById('trashPathErrorMsg')).not.toBeNull();
  });

  test('server failure surfaces the server message', async () => {
    fullForm();
    ajaxRouter({}, []);
    (globalThis as any).ApiClient.ajax = (opts: any) => {
      if (String(opts.url).includes('Configuration')) {
        return Promise.reject({ status: 400, responseText: '{"message":"bad mode"}' });
      }
      return Promise.resolve({});
    };
    const err = vi.fn();
    ui.doSaveSettings(ui.buildSettingsPayload(), { quiet: true, element: null, onError: err });
    await flush();
    expect(err).toHaveBeenCalledWith('bad mode');
  });

  test('loud save drives the band from saving to hidden without prior edits', async () => {
    ajaxRouter({});
    fullForm();
    ui.takeSettingsSnapshot();
    ui.doSaveSettings(ui.buildSettingsPayload(), {});
    const band = document.getElementById('settingsSaveBand')!;
    expect(band.classList.contains('is-saving')).toBe(true);
    await flush();
    expect(band.classList.contains('is-visible')).toBe(false);
  });

  test('formatPathList bullets each path', () => {
    expect(ui.formatPathList(['/a', '/b'])).toBe('\n  • /a\n  • /b');
    expect(ui.formatPathList([])).toBe('');
  });

  test('backup buttons wire to export and import entry points', async () => {
    ajaxRouter({ 'Backup/Export': '{}' });
    (globalThis as any).URL.createObjectURL = () => 'blob:mock';
    (globalThis as any).URL.revokeObjectURL = () => undefined;
    fullForm();
    ui.attachBackupHandlers();
    (document.getElementById('btnBackupExport') as HTMLButtonElement).click();
    await flush();
    expect(document.getElementById('backupMsg')!.textContent).toMatch(/success/i);
  });
});

describe('library multiselect', () => {
  const libs = [
    { Name: 'Movies', CollectionType: 'movies' },
    { Name: 'Shows', CollectionType: 'tvshows' },
  ];

  test('renders checked state and summaries', () => {
    fullForm();
    document.body.insertAdjacentHTML('beforeend', '<div id="cfgExcludedWrapper2"></div>');
    ui.renderLibraryMultiSelect('cfgExcludedWrapper2', libs, { movies: 'Movies' }, 'excluded');
    const wrap = document.getElementById('cfgExcludedWrapper2')!;
    expect(wrap.querySelectorAll('input[type="checkbox"]')).toHaveLength(2);
    expect(wrap.querySelector('.library-multiselect-summary')!.textContent).toBe('Movies');
  });

  test('toggle opens and closes the panel', () => {
    fullForm();
    document.body.insertAdjacentHTML('beforeend', '<div id="cfgExcludedWrapper2"></div>');
    ui.renderLibraryMultiSelect('cfgExcludedWrapper2', libs, {}, 'excluded');
    const btn = document.querySelector('#cfgExcludedWrapper2 .library-multiselect-toggle') as HTMLElement;
    btn.click();
    expect((document.querySelector('#cfgExcludedWrapper2 .library-multiselect-panel') as HTMLElement).style.display).toBe('block');
    btn.click();
    expect((document.querySelector('#cfgExcludedWrapper2 .library-multiselect-panel') as HTMLElement).style.display).toBe('none');
  });

  test('value joins checked names and preserves stale ones', () => {
    fullForm();
    document.body.insertAdjacentHTML('beforeend', '<div id="w3"></div>');
    ui.renderLibraryMultiSelect('w3', libs, {}, 'excluded');
    expect(ui.getLibraryMultiSelectValue('w3')).toBe('');
    (document.querySelector('#w3 input[value="Shows"]') as HTMLInputElement).checked = true;
    expect(ui.getLibraryMultiSelectValue('w3')).toBe('Shows');
    expect(ui.getLibraryMultiSelectValue('missing')).toBe('');
  });

  test('missing wrapper renders nothing and reads empty', () => {
    fullForm();
    expect(() => ui.renderLibraryMultiSelect('nope', libs, {}, 'excluded')).not.toThrow();
    expect(ui.getLibraryMultiSelectValue('nope')).toBe('');
  });

  test('checking updates the summary and autosaves', async () => {
    const calls = ajaxRouter({});
    fullForm();
    document.body.insertAdjacentHTML('beforeend', '<div id="cfgExcludedWrapper2"></div>');
    ui.renderLibraryMultiSelect('cfgExcludedWrapper2', libs, {}, 'excluded');
    ui.takeSettingsSnapshot();
    const box = document.querySelector('#cfgExcludedWrapper2 input[value="Movies"]') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(document.querySelector('#cfgExcludedWrapper2 .library-multiselect-summary')!.textContent).toBe('Movies');
    expect(calls.some((c) => String(c.url) === 'JellyfinHelper/Configuration')).toBe(true);
  });

  test('entry helpers accept both casings', () => {
    expect(ui.libraryEntryName({ name: 'x' })).toBe('x');
    expect(ui.libraryEntryName({})).toBe('');
    expect(ui.libraryEntryType({ collectionType: 'movies' })).toBe('movies');
    expect(ui.libraryMultiSelectEmptyLabel('arr')).toContain('Automatic');
    expect(ui.libraryMultiSelectEmptyLabel('excluded')).toContain('None excluded');
  });

  test('comma parser lowercases keys and drops empties', () => {
    expect(ui.parseCommaSeparatedSet('A, b ,A,, ')).toEqual({ a: 'A', b: 'b' });
    expect(ui.parseCommaSeparatedSet('')).toEqual({});
  });
});

describe('trash path error display', () => {
  test('sets and clears the inline error', () => {
    fullForm();
    ui.showTrashPathError('bad path');
    expect(document.getElementById('trashPathErrorMsg')).not.toBeNull();
    expect((document.getElementById('cfgTrashPath') as HTMLInputElement).style.borderColor).not.toBe('');
    ui.showTrashPathError(null);
    expect(document.getElementById('trashPathErrorMsg')).toBeNull();
  });
});

describe('buildSaveErrorLabel prefers server messages', () => {
  test.each([
    [{ kind: 'proxy', status: 502 }, '', 'proxy'],
    [{ kind: 'network', status: 0 }, '', 'unreachable'],
    [{ kind: 'unauthorized', status: 401 }, '', 'authorized'],
    [{ kind: 'server', status: 500 }, '', 'HTTP 500'],
    [{ kind: 'unknown', status: 0 }, '', 'Failed to save'],
  ])('label for %s', (diag, msg, fragment) => {
    expect(ui.buildSaveErrorLabel(diag, msg)).toMatch(new RegExp(fragment, 'i'));
  });

  test('server message wins over everything', () => {
    expect(ui.buildSaveErrorLabel({ kind: 'server', status: 500 }, 'custom says no')).toBe('custom says no');
  });
});

describe('backup export and import', () => {
  test('export downloads a dated file on success', async () => {
    ajaxRouter({ 'Backup/Export': '{"a":1}' });
    (globalThis as any).URL.createObjectURL = () => 'blob:mock';
    (globalThis as any).URL.revokeObjectURL = () => undefined;
    fullForm();
    ui.triggerBackupExport();
    await flush();
    const msg = document.getElementById('backupMsg')!;
    expect(msg.textContent).toMatch(/success/i);
    expect((document.getElementById('btnBackupExport') as HTMLButtonElement).disabled).toBe(false);
  });

  test('export failure shows the server message', async () => {
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: () => Promise.reject({ responseJSON: { message: 'disk full' } }),
    };
    fullForm();
    ui.triggerBackupExport();
    await flush();
    expect(document.getElementById('backupMsg')!.textContent).toContain('disk full');
  });

  test('oversize files are rejected before any dialog', () => {
    fullForm();
    ui.triggerBackupImport({ name: 'big.json', size: 11 * 1024 * 1024 } as File);
    expect(document.getElementById('backupMsg')!.textContent).toMatch(/too large/i);
    expect(document.getElementById('backupDialogOverlay')).toBeNull();
  });

  test('valid files open a confirmation naming the file', () => {
    fullForm();
    ui.triggerBackupImport({ name: 'b.json', size: 10 } as File);
    const overlay = document.getElementById('backupDialogOverlay')!;
    expect(overlay.textContent).toContain('b.json');
  });

  test('malformed json aborts the import with an error', async () => {
    ajaxRouter({ 'Backup/Import': {} });
    fullForm();
    const realReader = (globalThis as any).FileReader;
    (globalThis as any).FileReader = class {
      onload: any = null;
      readAsText(f: any) {
        this.onload?.({ target: { result: f.__content } });
      }
    };
    try {
      ui.doBackupImport({ name: 'b.json', size: 5, __content: 'not json' } as any);
      await flush();
      expect(document.getElementById('backupMsg')!.textContent).toMatch(/valid json/i);
    } finally {
      (globalThis as any).FileReader = realReader;
    }
  });

  test('import posts raw json and reports restored parts', async () => {
    const calls = ajaxRouter({ 'Backup/Import': { Summary: { ConfigurationRestored: true }, Warnings: ['w1'] } });
    fullForm();
    const realReader = (globalThis as any).FileReader;
    (globalThis as any).FileReader = class {
      onload: any = null;
      readAsText(f: any) {
        this.onload?.({ target: { result: f.__content } });
      }
    };
    try {
      ui.doBackupImport({ name: 'b.json', size: 5, __content: '{"a":1}' } as any);
      await flush();
      expect(calls.some((c) => String(c.url).includes('Backup/Import'))).toBe(true);
      expect(document.getElementById('backupMsg')!.textContent).toMatch(/success/i);
    } finally {
      (globalThis as any).FileReader = realReader;
    }
  });
});

describe('trash disable dialog', () => {
  test('no folders on disk saves straight through', async () => {
    const calls = ajaxRouter({ 'Trash/Folders': { Paths: [] } });
    fullForm();
    ui.takeSettingsSnapshot();
    ui.showTrashDisableDialog(ui.buildSettingsPayload());
    await flush();
    expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Configuration'))).toBe(true);
    expect(document.getElementById('trashDialogOverlay')).toBeNull();
  });

  test('cancel re-enables trash and saves nothing', async () => {
    const calls = ajaxRouter({ 'Trash/Folders': { Paths: ['/t/a'] } });
    fullForm();
    ui.takeSettingsSnapshot();
    (document.getElementById('cfgTrash') as HTMLInputElement).checked = false;
    ui.showTrashDisableDialog(ui.buildSettingsPayload());
    await flush();
    const overlay = document.getElementById('trashDialogOverlay')!;
    const cancel = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Cancel')!;
    cancel.click();
    expect((document.getElementById('cfgTrash') as HTMLInputElement).checked).toBe(true);
    expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Configuration'))).toBe(false);
  });
});

describe('loadSettings full render', () => {
  const cfg = () => ({
    Language: 'de',
    PluginLogLevel: 'DEBUG',
    UseTrash: true,
    TrashFolderPath: '/media/.trash',
    TrashRetentionDays: 14,
    OrphanMinAgeDays: 7,
    TrickplayTaskMode: 'Activate',
    EmptyMediaFolderTaskMode: 'Activate',
    OrphanedSubtitleTaskMode: 'Activate',
    LinkRepairTaskMode: 'Activate',
    RecommendationsTaskMode: 'Activate',
    SyncRecommendationsToPlaylist: true,
    SeerrUrl: 'http://seerr:5055',
    SeerrApiKey: 'seerr-secret',
    SeerrCleanupTaskMode: 'Activate',
    SeerrCleanupAgeDays: 60,
    DiscoveryUserAccessEnabled: true,
    TraktSourcingEnabled: true,
    ExcludedLibraries: '',
    RadarrInstances: [{ Name: 'R', Url: 'http://r:7878', ApiKey: 'rk', Libraries: '', SkipCertificateValidation: false }],
    SonarrInstances: [],
  });

  test('renders every card with server values and snapshots clean', async () => {
    ajaxRouter({
      'Configuration/Libraries': { Libraries: [{ Name: 'Movies', CollectionType: 'movies' }] },
      'Trakt/OfficialPluginStatus': { Present: true },
      'JellyfinHelper/Configuration': cfg(),
    });
    document.body.innerHTML = '<form id="settingsForm"></form>';
    const done = vi.fn();
    ui.loadSettings(done);
    await flush();
    expect((document.getElementById('cfgTrickplayMode') as HTMLSelectElement).value).toBe('Activate');
    expect((document.getElementById('cfgSeerrUrl') as HTMLInputElement).value).toBe('http://seerr:5055');
    expect((document.getElementById('cfgSeerrApiKey') as HTMLInputElement).value).toBe('seerr-secret');
    expect((document.getElementById('cfgTrash') as HTMLInputElement).checked).toBe(true);
    expect((document.getElementById('cfgTrashDays') as HTMLInputElement).value).toBe('14');
    expect((document.getElementById('cfgLang') as HTMLSelectElement).value).toBe('de');
    expect((document.getElementById('Radarr_0_name') as HTMLInputElement).value).toBe('R');
    expect((document.getElementById('Radarr_0_key') as HTMLInputElement).value).toBe('rk');
    expect((document.getElementById('cfgTraktSourcingEnabled') as HTMLInputElement).checked).toBe(true);
    expect(document.getElementById('traktSourcingRow')!.style.display).toBe('');
    expect(document.getElementById('btnSaveSettings')).not.toBeNull();
    expect(ui.hasUnsavedSettings()).toBe(false);
    expect(done).toHaveBeenCalled();
  });

  test('missing form does not throw', async () => {
    ajaxRouter({});
    document.body.innerHTML = '';
    expect(() => ui.loadSettings()).not.toThrow();
    await flush();
  });

  test('failed load renders the error state', async () => {
    ajaxRouter({}, ['JellyfinHelper/Configuration']);
    document.body.innerHTML = '<form id="settingsForm"></form>';
    ui.loadSettings();
    await flush();
    expect(document.getElementById('settingsForm')!.textContent).toMatch(/failed to load/i);
  });
});

describe('seerr connection test button', () => {
  test('empty fields never reach the network', async () => {
    const calls = ajaxRouter({});
    document.body.innerHTML = '<form id="settingsForm"></form>';
    ui.loadSettings();
    await flush();
    (document.getElementById('btnTestSeerr') as HTMLButtonElement).click();
    await flush();
    expect(calls.some((c) => String(c.url).includes('Seerr/Test'))).toBe(false);
    expect(document.getElementById('btnTestSeerr')!.textContent).toMatch(/fill in/i);
  });

  test('success autosaves and unlocks the seerr sections', async () => {
    const calls = ajaxRouter({
      'Configuration/Libraries': { Libraries: [] },
      'Seerr/Test': { Success: true, Message: 'ok' },
      'JellyfinHelper/Configuration': {
        Language: 'en', TrickplayTaskMode: 'DryRun', EmptyMediaFolderTaskMode: 'DryRun',
        OrphanedSubtitleTaskMode: 'DryRun', LinkRepairTaskMode: 'DryRun',
        RecommendationsTaskMode: 'Activate', RadarrInstances: [], SonarrInstances: [],
      },
    });
    document.body.innerHTML = '<form id="settingsForm"></form>';
    ui.loadSettings();
    await flush();
    expect((document.getElementById('cfgSeerrMode') as HTMLSelectElement).disabled).toBe(true);
    (document.getElementById('cfgSeerrUrl') as HTMLInputElement).value = 'http://s:5055';
    (document.getElementById('cfgSeerrApiKey') as HTMLInputElement).value = 'k';
    (document.getElementById('btnTestSeerr') as HTMLButtonElement).click();
    await flush();
    expect(calls.some((c) => String(c.url).includes('Seerr/Test'))).toBe(true);
    expect(calls.some((c) => String(c.url) === 'JellyfinHelper/Configuration')).toBe(true);
    expect((document.getElementById('cfgSeerrMode') as HTMLSelectElement).disabled).toBe(false);
  });
});

describe('autosave on change', () => {
  test('task mode change puts after the debounce', async () => {
    const calls = ajaxRouter({
      'Configuration/Libraries': { Libraries: [] },
      'Trakt/OfficialPluginStatus': { Present: false },
      'JellyfinHelper/Configuration': {
        Language: 'en', TrickplayTaskMode: 'DryRun', EmptyMediaFolderTaskMode: 'DryRun',
        OrphanedSubtitleTaskMode: 'DryRun', LinkRepairTaskMode: 'DryRun',
        RecommendationsTaskMode: 'DryRun', RadarrInstances: [], SonarrInstances: [],
      },
    });
    document.body.innerHTML = '<form id="settingsForm"></form>';
    ui.loadSettings();
    await flush();
    const puts = () => calls.filter((c) => String(c.url) === 'JellyfinHelper/Configuration' && c.body !== undefined).length;
    expect(puts()).toBe(0);
    const sel = document.getElementById('cfgTrickplayMode') as HTMLSelectElement;
    sel.value = 'DryRun';
    sel.dispatchEvent(new window.Event('change', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(300);
    expect(puts()).toBe(1);
  });

  test('task description buttons toggle their hints', async () => {
    ajaxRouter({
      'Configuration/Libraries': { Libraries: [] },
      'JellyfinHelper/Configuration': {
        Language: 'en', TrickplayTaskMode: 'DryRun', EmptyMediaFolderTaskMode: 'DryRun',
        OrphanedSubtitleTaskMode: 'DryRun', LinkRepairTaskMode: 'DryRun',
        RecommendationsTaskMode: 'DryRun', RadarrInstances: [], SonarrInstances: [],
      },
    });
    document.body.innerHTML = '<form id="settingsForm"></form>';
    ui.loadSettings();
    await flush();
    const btn = document.getElementById('taskDescBtn_cfgTrickplayMode') as HTMLButtonElement;
    const hint = document.getElementById('taskDescHint_cfgTrickplayMode') as HTMLElement;
    expect(hint.style.display).toBe('none');
    btn.click();
    expect(hint.style.display).toBe('block');
    expect(btn.getAttribute('aria-expanded')).toBe('true');
  });
});
