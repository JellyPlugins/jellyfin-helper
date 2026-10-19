import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { ajaxRouter, flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => {
  ui = freshConfigPage();
});

afterAll(saveCoverage);


// Minimal form that satisfies buildSettingsPayload's getElementById chain.
function payloadForm() {
  document.body.innerHTML =
    '<form id="settingsForm">' +
    '<select id="cfgRecommendationsMode"><option value="DryRun">x</option><option value="Activate">a</option><option value="Deactivate">d</option></select>' +
    '<input id="cfgSeerrUrl" value=""><input id="cfgSeerrApiKey" value="">' +
    '<input type="checkbox" id="cfgDiscoveryUserAccess"><div id="discoveryAccessWrapper"></div>' +
    '<input id="cfgOrphanAge" value="0">' +
    '<select id="cfgTrickplayMode"><option value="DryRun">x</option></select>' +
    '<select id="cfgEmptyFolderMode"><option value="DryRun">x</option></select>' +
    '<select id="cfgSubtitleMode"><option value="DryRun">x</option></select><select id="cfgLinkMode"><option value="DryRun">x</option></select>' +
    '<input type="checkbox" id="cfgTrash"><input id="cfgTrashPath" value=""><input id="cfgTrashDays" value="0">' +
    '<select id="cfgSeerrMode"><option value="Deactivate">x</option></select><input id="cfgSeerrAgeDays" value="0">' +
    '<input type="checkbox" id="cfgSyncPlaylist"><input type="checkbox" id="cfgSeerrSkipCert">' +
    '<select id="cfgLang"><option value="en">en</option></select>' +
    '<div id="cfgExcludedWrapper"></div><div id="arrResult"></div>' +
    '<button id="btnSaveSettings"></button><div id="settingsMsg"></div><div id="settingsSaveBand"></div>' +
    '</form>';
  (document.getElementById('cfgRecommendationsMode') as HTMLSelectElement).value = 'DryRun';
}

describe('validateTrashPath matrix', () => {
  test('disabled trash ignores the path entirely', () => {
    expect(ui.validateTrashPath('a*b', false)).toBeNull();
    expect(ui.validateTrashPath('', false)).toBeNull();
  });

  test('empty path with trash enabled is required', () => {
    expect(ui.validateTrashPath('', true)).toMatch(/required/i);
    expect(ui.validateTrashPath('   ', true)).toMatch(/required/i);
  });

  test('invalid characters are rejected', () => {
    expect(ui.validateTrashPath('a?b', true)).toMatch(/invalid characters/i);
    expect(ui.validateTrashPath('a|b', true)).toMatch(/invalid characters/i);
  });

  test('trailing slash and backslash rejected', () => {
    expect(ui.validateTrashPath('/media/trash/', true)).toMatch(/slash or backslash/i);
    expect(ui.validateTrashPath('C:\\trash\\', true)).toMatch(/slash or backslash/i);
  });

  test('consecutive separators rejected', () => {
    expect(ui.validateTrashPath('/media//trash', true)).toMatch(/consecutive/i);
  });

  test('dot and dot-dot segments rejected', () => {
    expect(ui.validateTrashPath('/media/../trash', true)).toMatch(/directory references/i);
    expect(ui.validateTrashPath('/media/./trash', true)).toMatch(/directory references/i);
  });

  test('sensitive system path rejected', () => {
    expect(ui.validateTrashPath('/etc/evil', true)).toMatch(/protected system/i);
    expect(ui.validateTrashPath('C:\\Windows\\x', true)).toMatch(/protected system/i);
  });

  // A bare drive root ends with a separator, so the trailing-slash check (step 2)
  // rejects it before the "at least one real segment" guard ever runs.
  test('bare Windows drive root is rejected by the trailing-separator check', () => {
    expect(ui.validateTrashPath('C:\\', true)).toMatch(/slash or backslash/i);
    expect(ui.validateTrashPath('C:/', true)).toMatch(/slash or backslash/i);
  });

  // The UNC strip regex needs \\host<sep>share; a bare \\server has no second
  // segment so the strip misses. Only a single leading backslash is stripped,
  // leaving "\server" which then trips the consecutive-separator / empty-segment
  // logic and returns the generic invalid error today (not null).
  test('UNC prefix without a share returns the generic invalid error', () => {
    expect(ui.validateTrashPath('\\\\server', true)).toMatch(/invalid/i);
  });

  test('a normal relative path is valid', () => {
    expect(ui.validateTrashPath('.jellyfin-trash', true)).toBeNull();
    expect(ui.validateTrashPath('/media/trash', true)).toBeNull();
    expect(ui.validateTrashPath('\\\\server\\share\\trash', true)).toBeNull();
  });
});

describe('isSensitiveSystemPath', () => {
  test('empty and relative paths are never sensitive', () => {
    expect(ui.isSensitiveSystemPath('')).toBe(false);
    expect(ui.isSensitiveSystemPath('relative/path')).toBe(false);
  });

  test('exact roots and subpaths are sensitive', () => {
    expect(ui.isSensitiveSystemPath('/config')).toBe(true);
    expect(ui.isSensitiveSystemPath('/config/sub')).toBe(true);
    expect(ui.isSensitiveSystemPath('C:\\ProgramData\\x')).toBe(true);
  });

  test('a prefix match that is not a path boundary is safe', () => {
    // /configuration is not under /config even though the string starts with it.
    expect(ui.isSensitiveSystemPath('/configuration')).toBe(false);
  });
});

describe('buildSettingsPayload zero and string round-trips', () => {
  test('zero numeric fields are preserved not coerced away', () => {
    payloadForm();
    (document.getElementById('cfgOrphanAge') as HTMLInputElement).value = '0';
    (document.getElementById('cfgTrashDays') as HTMLInputElement).value = '0';
    const p = ui.buildSettingsPayload();
    expect(p.OrphanMinAgeDays).toBe(0);
    // TrashRetentionDays coerces 0 through its clamp, which keeps 0.
    expect(p.TrashRetentionDays).toBe(0);
  });

  test('string fields round-trip verbatim', () => {
    payloadForm();
    (document.getElementById('cfgSeerrUrl') as HTMLInputElement).value = 'http://seerr:5055';
    (document.getElementById('cfgSeerrApiKey') as HTMLInputElement).value = 'secret-key';
    (document.getElementById('cfgTrash') as HTMLInputElement).checked = true;
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = '/media/trash';
    const p = ui.buildSettingsPayload();
    expect(p.SeerrUrl).toBe('http://seerr:5055');
    expect(p.SeerrApiKey).toBe('secret-key');
    expect(p.TrashFolderPath).toBe('/media/trash');
  });

  test('seerr cleanup mode forced to Deactivate without configured seerr', () => {
    payloadForm();
    (document.getElementById('cfgSeerrMode') as HTMLSelectElement).innerHTML =
      '<option value="Activate">a</option>';
    (document.getElementById('cfgSeerrMode') as HTMLSelectElement).value = 'Activate';
    // URL+Key empty, so the forcer overrides the Activate selection.
    expect(ui.buildSettingsPayload().SeerrCleanupTaskMode).toBe('Deactivate');
  });

  test('invalid seerr age falls back to 365', () => {
    payloadForm();
    (document.getElementById('cfgSeerrAgeDays') as HTMLInputElement).value = '0';
    expect(ui.buildSettingsPayload().SeerrCleanupAgeDays).toBe(365);
  });
});

describe('library multiselect summary branches', () => {
  const libs = [
    { Name: 'A', CollectionType: 'movies' },
    { Name: 'B', CollectionType: 'movies' },
    { Name: 'C', CollectionType: 'tvshows' },
    { Name: 'D', CollectionType: 'tvshows' },
  ];

  test('more than three selected collapses to a +N summary', () => {
    document.body.innerHTML = '<div id="w"></div>';
    ui.renderLibraryMultiSelect('w', libs, { a: 'A', b: 'B', c: 'C', d: 'D' }, 'excluded');
    expect(document.querySelector('#w .library-multiselect-summary')!.textContent).toBe('A, B +2');
  });

  test('arr empty widget shows the automatic label', () => {
    document.body.innerHTML = '<div id="w"></div>';
    ui.renderLibraryMultiSelect('w', libs, {}, 'arr');
    expect(document.querySelector('#w .library-multiselect-summary')!.textContent).toContain('Automatic');
  });

  test('empty library list renders the no-data placeholder', () => {
    document.body.innerHTML = '<div id="w"></div>';
    ui.renderLibraryMultiSelect('w', [], {}, 'excluded');
    expect(document.querySelector('#w .library-multiselect-panel')!.textContent).toMatch(/no data/i);
  });

  test('selected-but-missing libraries are stashed and preserved in the value', () => {
    document.body.innerHTML = '<div id="w"></div>';
    // "Ghost" is selected but not in the returned list (renamed/deleted library).
    ui.renderLibraryMultiSelect('w', libs, { a: 'A', ghost: 'Ghost' }, 'excluded');
    const wrap = document.getElementById('w')!;
    expect(wrap.dataset.missingValues).toBe('Ghost');
    // Only A is a real checkbox and it is checked; the missing one is appended.
    expect(ui.getLibraryMultiSelectValue('w')).toBe('A, Ghost');
  });

  test('updateLibraryMultiSelectSummary recomputes after manual checkbox edits', () => {
    document.body.innerHTML = '<div id="w"></div>';
    ui.renderLibraryMultiSelect('w', libs, {}, 'excluded');
    (document.querySelector('#w input[value="A"]') as HTMLInputElement).checked = true;
    (document.querySelector('#w input[value="C"]') as HTMLInputElement).checked = true;
    ui.updateLibraryMultiSelectSummary('w');
    expect(document.querySelector('#w .library-multiselect-summary')!.textContent).toBe('A, C');
    // Deselect all -> back to the empty label.
    (document.querySelector('#w input[value="A"]') as HTMLInputElement).checked = false;
    (document.querySelector('#w input[value="C"]') as HTMLInputElement).checked = false;
    ui.updateLibraryMultiSelectSummary('w');
    expect(document.querySelector('#w .library-multiselect-summary')!.textContent).toContain('None excluded');
  });

  test('getLibraryMultiSelectValue reads a fallback text input', () => {
    document.body.innerHTML = '<div id="w"><input type="text" value="Legacy"></div>';
    expect(ui.getLibraryMultiSelectValue('w')).toBe('Legacy');
  });

  test('getLibraryMultiSelectValue returns initial value before the widget renders', () => {
    document.body.innerHTML = '<div id="w"></div>';
    (document.getElementById('w') as HTMLElement).dataset.initialValue = 'Pending';
    expect(ui.getLibraryMultiSelectValue('w')).toBe('Pending');
  });

  test('toggleLibraryDropdown is a no-op without a panel sibling', () => {
    document.body.innerHTML = '<button id="b"></button>';
    expect(() => ui.toggleLibraryDropdown(document.getElementById('b'))).not.toThrow();
  });
});

describe('showTrashDisableDialog folder-list branches', () => {
  function disableForm() {
    payloadForm();
    (document.getElementById('cfgTrash') as HTMLInputElement).checked = false;
  }

  test('keep-folders saves and leaves the folders alone', async () => {
    const calls = ajaxRouter({ 'Trash/Folders': { Paths: ['/t/a', '/t/b'] } });
    disableForm();
    ui.takeSettingsSnapshot();
    ui.showTrashDisableDialog(ui.buildSettingsPayload());
    await flush();
    const overlay = document.getElementById('trashDialogOverlay')!;
    // The listed folders appear in the dialog body.
    expect(overlay.textContent).toContain('/t/a');
    const keep = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Keep Folders')!;
    keep.click();
    await flush();
    expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Configuration'))).toBe(true);
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
  });

  test('delete-folders routes to the delete confirmation', async () => {
    ajaxRouter({ 'Trash/Folders': { Paths: ['/t/a'] } });
    disableForm();
    ui.takeSettingsSnapshot();
    ui.showTrashDisableDialog(ui.buildSettingsPayload());
    await flush();
    let overlay = document.getElementById('trashDialogOverlay')!;
    const del = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Delete Folders')!;
    del.click();
    await flush();
    // A fresh confirmation dialog replaces the first one.
    overlay = document.getElementById('trashDialogOverlay')!;
    expect(overlay.textContent).toMatch(/cannot be undone/i);
    expect([...overlay.querySelectorAll('button')].some((b) => b.textContent === 'Yes, Delete All')).toBe(true);
  });

  test('folders lookup failure falls through to a direct save', async () => {
    const calls = ajaxRouter({}, ['Trash/Folders']);
    disableForm();
    ui.takeSettingsSnapshot();
    ui.showTrashDisableDialog(ui.buildSettingsPayload());
    await flush();
    expect(document.getElementById('trashDialogOverlay')).toBeNull();
    expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Configuration'))).toBe(true);
  });
});

describe('showTrashDeleteConfirmation cancel path', () => {
  test('cancel re-checks trash and saves nothing', async () => {
    const calls = ajaxRouter({});
    payloadForm();
    (document.getElementById('cfgTrash') as HTMLInputElement).checked = false;
    ui.showTrashDeleteConfirmation(ui.buildSettingsPayload(), ['/t/a']);
    const overlay = document.getElementById('trashDialogOverlay')!;
    const cancel = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Cancel')!;
    cancel.click();
    await flush();
    expect((document.getElementById('cfgTrash') as HTMLInputElement).checked).toBe(true);
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Configuration'))).toBe(false);
  });
});

describe('probeBackendReachability success path', () => {
  test('a reachable ping resolves and logs an info line', async () => {
    const calls = ajaxRouter({ 'JellyfinHelper/Ping': {} });
    const info = vi.spyOn(console, 'info').mockImplementation(() => undefined);
    ui.probeBackendReachability({ status: 500, kind: 'server' });
    await flush();
    expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Ping'))).toBe(true);
    expect(info).toHaveBeenCalled();
    info.mockRestore();
  });
});

describe('buildSaveErrorLabel branches', () => {
  test('proxy and network and unauthorized map to their hints', () => {
    expect(ui.buildSaveErrorLabel({ kind: 'proxy', status: 502 }, '')).toMatch(/proxy/i);
    expect(ui.buildSaveErrorLabel({ kind: 'network', status: 0 }, '')).toMatch(/unreachable/i);
    expect(ui.buildSaveErrorLabel({ kind: 'unauthorized', status: 401 }, '')).toMatch(/authorized/i);
  });

  test('a positive status with no hint falls back to the HTTP label', () => {
    expect(ui.buildSaveErrorLabel({ kind: 'other', status: 418 }, '')).toContain('HTTP 418');
  });

  test('status 0 with no hint uses the plain fallback', () => {
    expect(ui.buildSaveErrorLabel({ kind: 'other', status: 0 }, '')).toMatch(/failed to save/i);
  });
});

describe('attachBackupHandlers import flow', () => {
  function backupForm() {
    document.body.innerHTML =
      '<button id="btnBackupExport"></button>' +
      '<button type="button" id="btnBackupImport"></button>' +
      '<input type="file" id="btnBackupImportFile">' +
      '<div id="backupMsg"></div>';
  }

  test('import button forwards a chosen file to the confirmation dialog', async () => {
    ajaxRouter({});
    backupForm();
    ui.attachBackupHandlers();
    const fileInput = document.getElementById('btnBackupImportFile') as HTMLInputElement;
    // Simulate a user-selected small file and the change event the handler listens for.
    Object.defineProperty(fileInput, 'files', {
      value: [{ name: 'restore.json', size: 42 }],
      configurable: true,
    });
    fileInput.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    const overlay = document.getElementById('backupDialogOverlay')!;
    expect(overlay).not.toBeNull();
    expect(overlay.textContent).toContain('restore.json');
  });

  test('confirming the import dialog triggers the raw post', async () => {
    const calls = ajaxRouter({ 'Backup/Import': { Summary: { ConfigurationRestored: true } } });
    backupForm();
    const realReader = (globalThis as any).FileReader;
    (globalThis as any).FileReader = class {
      onload: any = null;
      readAsText(f: any) {
        this.onload?.({ target: { result: f.__content } });
      }
    };
    try {
      ui.showBackupImportConfirmation({ name: 'b.json', size: 5, __content: '{"a":1}' } as any);
      const overlay = document.getElementById('backupDialogOverlay')!;
      const ok = [...overlay.querySelectorAll('button')].find((b) => b.textContent === 'Yes, Import')!;
      ok.click();
      await flush();
      expect(calls.some((c) => String(c.url).includes('Backup/Import'))).toBe(true);
    } finally {
      (globalThis as any).FileReader = realReader;
    }
  });

  test('export failure without a server message shows the generic error', async () => {
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: () => Promise.reject({}),
    };
    document.body.innerHTML = '<button id="btnBackupExport"></button><div id="backupMsg"></div>';
    ui.triggerBackupExport();
    await flush();
    expect(document.getElementById('backupMsg')!.textContent).toMatch(/failed to export/i);
  });

  test('import FileReader error surfaces the import error', async () => {
    ajaxRouter({});
    document.body.innerHTML = '<div id="backupMsg"></div>';
    const realReader = (globalThis as any).FileReader;
    (globalThis as any).FileReader = class {
      onerror: any = null;
      readAsText() {
        this.onerror?.();
      }
    };
    try {
      ui.doBackupImport({ name: 'b.json', size: 5 } as any);
      await flush();
      expect(document.getElementById('backupMsg')!.textContent).toMatch(/failed to import/i);
    } finally {
      (globalThis as any).FileReader = realReader;
    }
  });

  test('import server rejection shows the server message', async () => {
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: () => Promise.reject({ responseText: '{"message":"corrupt backup"}' }),
    };
    document.body.innerHTML = '<div id="backupMsg"></div>';
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
      expect(document.getElementById('backupMsg')!.textContent).toContain('corrupt backup');
    } finally {
      (globalThis as any).FileReader = realReader;
    }
  });
});

describe('refreshDiscoveryAccessState hint and button wiring', () => {
  function discoveryDom(mode: string, url: string, key: string) {
    document.body.innerHTML =
      '<select id="cfgRecommendationsMode"><option value="Activate">a</option><option value="DryRun">d</option><option value="Deactivate">x</option></select>' +
      `<input id="cfgSeerrUrl" value="${url}"><input id="cfgSeerrApiKey" value="${key}">` +
      '<input type="checkbox" id="cfgDiscoveryUserAccess">' +
      '<div id="discoveryAccessWrapper"></div>' +
      '<div class="discovery-access-disabled-hint" style="display:none;"></div>' +
      '<button id="btnToggleDiscoveryHint"></button>' +
      '<div class="discovery-setup-hint"></div>' +
      '<div id="traktSourcingRow" style="display:none;"></div><div id="traktSourcingHint" style="display:none;"></div>';
    (document.getElementById('cfgRecommendationsMode') as HTMLSelectElement).value = mode;
  }

  test('enabled hides the disabled hint and clears wrapper opacity', () => {
    discoveryDom('Activate', 'http://s:5055', 'k');
    expect(ui.refreshDiscoveryAccessState()).toBe(true);
    expect((document.querySelector('.discovery-access-disabled-hint') as HTMLElement).style.display).toBe('none');
    expect((document.getElementById('discoveryAccessWrapper') as HTMLElement).style.opacity).toBe('');
  });

  test('disabled greys the wrapper and shows the hint and forces button pointer events', () => {
    discoveryDom('Deactivate', 'http://s:5055', 'k');
    expect(ui.refreshDiscoveryAccessState()).toBe(false);
    expect((document.getElementById('discoveryAccessWrapper') as HTMLElement).style.opacity).toBe('0.5');
    expect((document.querySelector('.discovery-access-disabled-hint') as HTMLElement).style.display).toBe('');
    expect((document.getElementById('btnToggleDiscoveryHint') as HTMLElement).style.pointerEvents).toBe('auto');
    expect((document.querySelector('.discovery-setup-hint') as HTMLElement).style.pointerEvents).toBe('auto');
  });

  test('missing optional nodes do not throw', () => {
    document.body.innerHTML = '';
    expect(() => ui.refreshDiscoveryAccessState()).not.toThrow();
  });
});

describe('refreshTraktRowState combinations', () => {
  function traktDom(checked: boolean, disabled: boolean) {
    document.body.innerHTML =
      '<div id="traktSourcingRow" style="display:none;"></div>' +
      '<div id="traktSourcingHint" style="display:none;"></div>' +
      `<input type="checkbox" id="cfgDiscoveryUserAccess"${checked ? ' checked' : ''}${disabled ? ' disabled' : ''}>`;
  }

  test('absent row is a no-op', () => {
    document.body.innerHTML = '';
    expect(() => ui.refreshTraktRowState()).not.toThrow();
  });

  // Without the plugin probe the module flag defaults false, so the row stays
  // hidden even with discovery checked and enabled.
  test('hidden while the plugin flag is unset', () => {
    traktDom(true, false);
    ui.refreshTraktRowState();
    expect(document.getElementById('traktSourcingRow')!.style.display).toBe('none');
    expect(document.getElementById('traktSourcingHint')!.style.display).toBe('none');
  });
});

describe('updateSeerrUIState count badge', () => {
  test('configured sets the arr count badge, unconfigured clears it', () => {
    document.body.innerHTML =
      '<div class="seerr-task-mode-wrapper"><select id="cfgSeerrMode"></select></div>' +
      '<div class="seerr-age-wrapper"><input id="cfgSeerrAgeDays"></div>' +
      '<span id="arrCountSeerr"></span>' +
      '<div class="seerr-not-configured-hint"></div>';
    ui.updateSeerrUIState(true);
    expect(document.getElementById('arrCountSeerr')!.innerHTML).not.toBe('');
    ui.updateSeerrUIState(false);
    expect(document.getElementById('arrCountSeerr')!.innerHTML).toBe('');
  });
});

describe('saveSettings guards', () => {
  test('invalid trash path blocks the save and re-enables the button', async () => {
    const calls = ajaxRouter({});
    payloadForm();
    (document.getElementById('cfgTrash') as HTMLInputElement).checked = true;
    (document.getElementById('cfgTrashPath') as HTMLInputElement).value = 'bad*path';
    ui.takeSettingsSnapshot();
    ui.saveSettings();
    await flush();
    expect(document.getElementById('trashPathErrorMsg')).not.toBeNull();
    expect((document.getElementById('btnSaveSettings') as HTMLButtonElement).disabled).toBe(false);
    expect(calls.some((c) => c.method === 'POST' || c.method === 'PUT')).toBe(false);
  });
});

// A full loadSettings render wires every handler (auto-save, task-desc, discovery
// copy, input clamps, library multiselect init), so driving it then interacting
// with the rendered DOM covers large swaths of otherwise-dead code. The config GET
// must echo trash-enabled so a later save's rebuildUI re-render keeps trash checked.
const RENDER_CFG = {
  Language: 'en',
  PluginLogLevel: 'INFO',
  UseTrash: true,
  TrashFolderPath: '/t/old',
  TrashRetentionDays: 30,
  OrphanMinAgeDays: 7,
  TrickplayTaskMode: 'DryRun',
  EmptyMediaFolderTaskMode: 'DryRun',
  OrphanedSubtitleTaskMode: 'DryRun',
  LinkRepairTaskMode: 'DryRun',
  RecommendationsTaskMode: 'Activate',
  SyncRecommendationsToPlaylist: false,
  SeerrUrl: 'http://seerr:5055',
  SeerrApiKey: 'k',
  SeerrCleanupTaskMode: 'Activate',
  SeerrCleanupAgeDays: 60,
  DiscoveryUserAccessEnabled: true,
  TraktSourcingEnabled: false,
  ExcludedLibraries: 'Movies',
  RadarrInstances: [],
  SonarrInstances: [],
};

function renderRoutes(extra: Record<string, unknown> = {}) {
  return {
    'JellyfinHelper/Configuration/Libraries': {
      Libraries: [
        { Name: 'Movies', CollectionType: 'movies' },
        { Name: 'Shows', CollectionType: 'tvshows' },
      ],
    },
    'Trakt/OfficialPluginStatus': { Present: true },
    'JellyfinHelper/Configuration': RENDER_CFG,
    ...extra,
  };
}

async function renderSettings(extra: Record<string, unknown> = {}, failures: string[] = []) {
  const calls = ajaxRouter(renderRoutes(extra), failures);
  document.body.innerHTML = '<form id="settingsForm"></form>';
  ui.loadSettings();
  await flush();
  return calls;
}

describe('loadSettings wiring and interactions', () => {
  test('discovery copy toggle opens the hint panel', async () => {
    await renderSettings();
    const toggle = document.getElementById('btnToggleDiscoveryHint') as HTMLButtonElement;
    const panel = document.getElementById('discoveryHintPanel') as HTMLElement;
    expect(panel.style.display).toBe('none');
    toggle.click();
    expect(panel.style.display).toBe('block');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    toggle.click();
    expect(panel.style.display).toBe('none');
  });

  test('copy button uses the clipboard API and reports copied', async () => {
    await renderSettings();
    const writeText = vi.fn().mockResolvedValue(undefined);
    (globalThis as any).navigator.clipboard = { writeText };
    const btn = document.getElementById('btnCopyDiscoveryHtml') as HTMLButtonElement;
    btn.click();
    await flush();
    expect(writeText).toHaveBeenCalledWith('<div class="jellyfinhelper discovery"></div>');
    expect(btn.querySelector('span:last-child')!.textContent).toMatch(/copied/i);
  });

  test('orphan age input clamps above the max on input', async () => {
    await renderSettings();
    const input = document.getElementById('cfgOrphanAge') as HTMLInputElement;
    input.value = '99999';
    input.dispatchEvent(new window.Event('input', { bubbles: true }));
    expect(input.value).toBe('3650');
    input.value = '-4';
    input.dispatchEvent(new window.Event('input', { bubbles: true }));
    expect(input.value).toBe('0');
  });

  test('orphan age keydown blocks scientific-notation chars', async () => {
    await renderSettings();
    const input = document.getElementById('cfgOrphanAge') as HTMLInputElement;
    const ev = new window.KeyboardEvent('keydown', { key: 'e', bubbles: true, cancelable: true });
    input.dispatchEvent(ev);
    expect(ev.defaultPrevented).toBe(true);
  });

  test('trash days input clamps above the max', async () => {
    await renderSettings();
    const input = document.getElementById('cfgTrashDays') as HTMLInputElement;
    input.value = '5000';
    input.dispatchEvent(new window.Event('input', { bubbles: true }));
    expect(input.value).toBe('3650');
  });

  test('editing the trash path clears a prior error', async () => {
    await renderSettings();
    ui.showTrashPathError('bad');
    expect(document.getElementById('trashPathErrorMsg')).not.toBeNull();
    const input = document.getElementById('cfgTrashPath') as HTMLInputElement;
    input.dispatchEvent(new window.Event('input', { bubbles: true }));
    expect(document.getElementById('trashPathErrorMsg')).toBeNull();
  });

  test('toggling the trash checkbox off greys the fieldset', async () => {
    await renderSettings();
    const chk = document.getElementById('cfgTrash') as HTMLInputElement;
    chk.checked = false;
    chk.dispatchEvent(new window.Event('change', { bubbles: true }));
    const fs = document.getElementById('trashSettingsWrapper') as HTMLFieldSetElement;
    expect(fs.disabled).toBe(true);
    expect(fs.style.opacity).toBe('0.5');
  });

  test('library multiselect renders the excluded selection from config', async () => {
    await renderSettings();
    const summary = document.querySelector('#cfgExcludedWrapper .library-multiselect-summary');
    expect(summary!.textContent).toBe('Movies');
  });

  test('library fallback renders a text input when the library API fails', async () => {
    await renderSettings({}, ['Configuration/Libraries']);
    const fallback = document.querySelector('#cfgExcludedWrapper input[type="text"]') as HTMLInputElement;
    expect(fallback).not.toBeNull();
    expect(fallback.value).toBe('Movies');
  });

  test('language change saves then rebuilds the UI', async () => {
    const calls = await renderSettings();
    const puts = () => calls.filter((c) => String(c.url) === 'JellyfinHelper/Configuration' && c.body !== undefined).length;
    const before = puts();
    const lang = document.getElementById('cfgLang') as HTMLSelectElement;
    lang.value = 'de';
    lang.dispatchEvent(new window.Event('change', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(300);
    expect(puts()).toBeGreaterThan(before);
  });

  test('discovery toggle schedules a coalesced quiet save', async () => {
    const calls = await renderSettings();
    const puts = () => calls.filter((c) => String(c.url) === 'JellyfinHelper/Configuration' && c.body !== undefined).length;
    const before = puts();
    const disc = document.getElementById('cfgDiscoveryUserAccess') as HTMLInputElement;
    disc.checked = false;
    disc.dispatchEvent(new window.Event('change', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(400);
    expect(puts()).toBeGreaterThan(before);
  });

  test('trakt sourcing toggle schedules a quiet save', async () => {
    const calls = await renderSettings();
    const puts = () => calls.filter((c) => String(c.url) === 'JellyfinHelper/Configuration' && c.body !== undefined).length;
    const before = puts();
    const trakt = document.getElementById('cfgTraktSourcingEnabled') as HTMLInputElement;
    trakt.checked = true;
    trakt.dispatchEvent(new window.Event('change', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(400);
    expect(puts()).toBeGreaterThan(before);
  });

  test('playlist sync toggle quiet-saves and shows an inline indicator', async () => {
    const calls = await renderSettings();
    const sync = document.getElementById('cfgSyncPlaylist') as HTMLInputElement;
    sync.checked = true;
    sync.dispatchEvent(new window.Event('change', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(300);
    expect(calls.some((c) => String(c.url) === 'JellyfinHelper/Configuration' && c.body !== undefined)).toBe(true);
    const label = sync.nextElementSibling as HTMLElement;
    expect(label.querySelector('.inline-save-indicator')).not.toBeNull();
  });

  test('recommendations mode change to Deactivate hides the tab and clears playlist', async () => {
    const calls = await renderSettings();
    document.body.insertAdjacentHTML(
      'beforeend',
      '<button class="tab-btn active" data-tab="recommendations"></button>' +
        '<button class="tab-btn" data-tab="overview"></button>' +
        '<div id="tab-recommendations"></div>',
    );
    const recs = document.getElementById('cfgRecommendationsMode') as HTMLSelectElement;
    recs.value = 'Deactivate';
    recs.dispatchEvent(new window.Event('change', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(300);
    expect(calls.some((c) => String(c.url) === 'JellyfinHelper/Configuration' && c.body !== undefined)).toBe(true);
    expect((document.getElementById('cfgSyncPlaylist') as HTMLInputElement).checked).toBe(false);
  });

  test('checking an excluded library autosaves with the new summary', async () => {
    const calls = await renderSettings();
    const putsBefore = calls.filter((c) => String(c.url) === 'JellyfinHelper/Configuration' && c.body !== undefined).length;
    const box = document.querySelector('#cfgExcludedWrapper input[value="Shows"]') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(document.querySelector('#cfgExcludedWrapper .library-multiselect-summary')!.textContent).toContain('Shows');
    const putsAfter = calls.filter((c) => String(c.url) === 'JellyfinHelper/Configuration' && c.body !== undefined).length;
    expect(putsAfter).toBeGreaterThan(putsBefore);
  });
});

describe('fallbackCopy path when clipboard API is unavailable', () => {
  test('copy falls back to execCommand and reports copied', async () => {
    await renderSettings();
    (globalThis as any).navigator.clipboard = undefined;
    (document as any).execCommand = vi.fn().mockReturnValue(true);
    const btn = document.getElementById('btnCopyDiscoveryHtml') as HTMLButtonElement;
    btn.click();
    await flush();
    expect((document as any).execCommand).toHaveBeenCalledWith('copy');
    expect(btn.querySelector('span:last-child')!.textContent).toMatch(/copied/i);
  });

  test('a failed execCommand shows the failure glyph', async () => {
    await renderSettings();
    (globalThis as any).navigator.clipboard = undefined;
    (document as any).execCommand = vi.fn().mockReturnValue(false);
    const btn = document.getElementById('btnCopyDiscoveryHtml') as HTMLButtonElement;
    btn.click();
    await flush();
    expect(btn.querySelector('span:last-child')!.textContent).toBe('✗');
  });
});

describe('loud save drives the band to saved then hides it', () => {
  test('a loud save after an edit shows saved then fades to hidden', async () => {
    const calls = await renderSettings();
    // Edit and fire input so the dirty tracker marks the form interacted; the
    // post-save band only shows "saved" (vs silently hiding) after a real edit.
    const age = document.getElementById('cfgOrphanAge') as HTMLInputElement;
    age.value = '9';
    age.dispatchEvent(new window.Event('input', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(200);
    const band = document.getElementById('settingsSaveBand')!;
    ui.saveSettings();
    expect(band.classList.contains('is-saving')).toBe(true);
    await flush();
    // Trash stays enabled with the same path, so no rebuild clobbers the band.
    expect(band.classList.contains('is-saved')).toBe(true);
    await vi.advanceTimersByTimeAsync(3000);
    expect(band.classList.contains('is-visible')).toBe(false);
    expect(calls.some((c) => String(c.url) === 'JellyfinHelper/Configuration' && c.body !== undefined)).toBe(true);
  });
});
