import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);


function discoveryDom(opts: { mode: string; url: string; key: string; checked: boolean; disabled?: boolean }) {
  document.body.innerHTML =
    '<select id="cfgRecommendationsMode">' +
    `<option value="Activate">a</option><option value="DryRun">d</option><option value="Deactivate">x</option></select>` +
    `<input id="cfgSeerrUrl" value="${opts.url}">` +
    `<input id="cfgSeerrApiKey" value="${opts.key}">` +
    '<div id="discoveryAccessWrapper"></div>' +
    `<input type="checkbox" id="cfgDiscoveryUserAccess"${opts.checked ? ' checked' : ''}${opts.disabled ? ' disabled' : ''}>` +
    '<div class="discovery-access-disabled-hint" style="display:none;"></div>' +
    '<button id="btnToggleDiscoveryHint"></button>' +
    '<div class="discovery-setup-hint"></div>';
  (document.getElementById('cfgRecommendationsMode') as HTMLSelectElement).value = opts.mode;
}

function traktDom(checked: boolean, disabled: boolean) {
  document.body.innerHTML =
    '<div id="traktSourcingRow" style="display:none;"></div>' +
    '<div id="traktSourcingHint" style="display:none;"></div>' +
    `<input type="checkbox" id="cfgDiscoveryUserAccess"${checked ? ' checked' : ''}${disabled ? ' disabled' : ''}>`;
}

function ajaxReturning(payload: unknown) {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    ajax: () => Promise.resolve(payload),
  };
}

async function flush() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

describe('isSeerrConfigured', () => {
  test.each([
    ['http://x', 'k', true],
    ['  http://x  ', 'k', true],
    ['', 'k', false],
    ['http://x', '', false],
    ['   ', '   ', false],
  ])('url=%s key=%s', (url, key, expected) => {
    expect(ui.isSeerrConfigured(url, key)).toBe(expected);
  });
});

describe('refreshDiscoveryAccessState', () => {
  beforeEach(() => {
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      ajax: () => Promise.resolve({}),
    };
  });

  test('enabled only with active mode and configured seerr', () => {
    discoveryDom({ mode: 'DryRun', url: 'http://seerr:5055', key: 'k', checked: true });
    ui.refreshDiscoveryAccessState();
    const chk = document.getElementById('cfgDiscoveryUserAccess') as HTMLInputElement;
    expect(chk.disabled).toBe(false);
    expect(document.getElementById('discoveryAccessWrapper')!.getAttribute('aria-disabled')).toBe('false');
  });

  test('Deactivate mode disables without unchecking', () => {
    discoveryDom({ mode: 'Deactivate', url: 'http://seerr:5055', key: 'k', checked: true });
    ui.refreshDiscoveryAccessState();
    const chk = document.getElementById('cfgDiscoveryUserAccess') as HTMLInputElement;
    expect(chk.disabled).toBe(true);
    expect(chk.checked).toBe(true);
  });

  // Only Deactivate disables: an unselected mode still counts as "not Deactivate",
  // so discovery stays enabled when Seerr is configured.
  test('empty mode leaves discovery enabled', () => {
    discoveryDom({ mode: '', url: 'http://seerr:5055', key: 'k', checked: true });
    ui.refreshDiscoveryAccessState();
    expect((document.getElementById('cfgDiscoveryUserAccess') as HTMLInputElement).disabled).toBe(false);
  });

  test('blank key disables even in activate mode', () => {
    discoveryDom({ mode: 'Activate', url: 'http://seerr:5055', key: '  ', checked: false });
    ui.refreshDiscoveryAccessState();
    expect((document.getElementById('cfgDiscoveryUserAccess') as HTMLInputElement).disabled).toBe(true);
  });
});

describe('trakt row follows probe and discovery toggle', () => {
  test('visible only when the plugin is present and discovery is on', async () => {
    ajaxReturning({ Present: true });
    traktDom(true, false);
    ui.loadTraktOfficialStatus();
    await flush();
    expect(document.getElementById('traktSourcingRow')!.style.display).toBe('');
  });

  test('hidden when discovery is off but the stored value survives', async () => {
    ajaxReturning({ Present: true });
    traktDom(false, false);
    ui.loadTraktOfficialStatus();
    await flush();
    const row = document.getElementById('traktSourcingRow')!;
    expect(row.style.display).toBe('none');
  });

  test('hidden when the official plugin is absent', async () => {
    ajaxReturning({ Present: false });
    traktDom(true, false);
    ui.loadTraktOfficialStatus();
    await flush();
    expect(document.getElementById('traktSourcingRow')!.style.display).toBe('none');
  });

  test('a failing probe hides instead of throwing', async () => {
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      ajax: () => Promise.reject(new Error('down')),
    };
    traktDom(true, false);
    ui.loadTraktOfficialStatus();
    await flush();
    expect(document.getElementById('traktSourcingRow')!.style.display).toBe('none');
  });
});

describe('updateRecsTabVisibility', () => {
  beforeEach(() => {
    document.body.innerHTML =
      '<button class="tab-btn active" data-tab="recommendations"></button>' +
      '<button class="tab-btn" data-tab="overview"></button>' +
      '<div id="tab-recommendations" class="active"></div>';
  });

  test('deactivate hides and reroutes to overview', () => {
    const overview = document.querySelector('.tab-btn[data-tab="overview"]') as HTMLElement;
    const spy = vi.spyOn(overview, 'click');
    ui.updateRecsTabVisibility('Deactivate');
    expect(document.querySelector('.tab-btn[data-tab="recommendations"]')!.getAttribute('style')).toContain('none');
    expect(spy).toHaveBeenCalled();
  });

  test.each([['Activate'], ['DryRun'], ['']])('mode %s keeps the tab', (mode) => {
    ui.updateRecsTabVisibility(mode);
    expect(document.querySelector('.tab-btn[data-tab="recommendations"]')!.style.display).toBe('');
  });
});

describe('updateSeerrUIState is inert when greyed out', () => {
  beforeEach(() => {
    document.body.innerHTML =
      '<div class="seerr-task-mode-wrapper"><select id="cfgSeerrMode"></select></div>' +
      '<div class="seerr-age-wrapper"><input id="cfgSeerrAgeDays"></div>' +
      '<div class="seerr-not-configured-hint"></div>';
  });

  test('unconfigured disables inputs and marks aria', () => {
    ui.updateSeerrUIState(false);
    expect((document.getElementById('cfgSeerrMode') as HTMLSelectElement).disabled).toBe(true);
    expect(document.querySelector('.seerr-task-mode-wrapper')!.getAttribute('aria-disabled')).toBe('true');
    expect((document.querySelector('.seerr-not-configured-hint') as HTMLElement).style.display).toBe('');
  });

  test('configured restores inputs', () => {
    ui.updateSeerrUIState(true);
    expect((document.getElementById('cfgSeerrMode') as HTMLSelectElement).disabled).toBe(false);
    expect((document.querySelector('.seerr-not-configured-hint') as HTMLElement).style.display).toBe('none');
  });
});
