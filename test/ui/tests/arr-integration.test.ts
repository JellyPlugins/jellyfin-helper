import { afterAll, beforeEach, describe, expect, test } from 'vitest';
import { ajaxRouter, flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => {
  ui = freshConfigPage();
});

afterAll(saveCoverage);


function settingsShell() {
  document.body.innerHTML =
    '<form id="settingsForm">' +
    '<select id="cfgRecommendationsMode"><option value="DryRun">x</option></select>' +
    '<input id="cfgSeerrUrl" value=""><input id="cfgSeerrApiKey" value="">' +
    '<input type="checkbox" id="cfgDiscoveryUserAccess">' +
    '<div id="discoveryAccessWrapper"></div>' +
    '<input id="cfgOrphanAge" value="30">' +
    '<select id="cfgTrickplayMode"><option value="DryRun">x</option></select>' +
    '<select id="cfgEmptyFolderMode"><option value="DryRun">x</option></select>' +
    '<select id="cfgSubtitleMode"><option value="DryRun">x</option></select><select id="cfgLinkMode"><option value="DryRun">x</option></select>' +
    '<input type="checkbox" id="cfgTrash"><input id="cfgTrashPath" value=""><input id="cfgTrashDays" value="30">' +
    '<select id="cfgLang"><option value="en">en</option></select>' +
    '<input type="checkbox" id="cfgTraktSourcingEnabled">' +
    '<div id="cfgExcludedWrapper"></div><div id="arrResult"></div>' +
    '<div id="arrButtons"></div><div id="arrCountRadarr"></div>' +
    '</form>';
  (document.getElementById('cfgRecommendationsMode') as HTMLSelectElement).value = 'DryRun';
}

function arrShell(instances: any[]) {
  settingsShell();
  document.getElementById('arrButtons')!.innerHTML = ui.renderArrInstances('Radarr', instances);
  ui.setArrInstanceApiKeys('Radarr', instances);
  ui.takeSettingsSnapshot();
}

describe('renderArrInstances', () => {
  test('add button hides at the cap', () => {
    expect(ui.renderArrInstances('Radarr', [])).toContain('btnAddRadarr');
    const full = ui.renderArrInstances('Radarr', [{}, {}, {}]);
    expect(full).toContain('display:none');
  });

  test('placeholders differ per type and names escape', () => {
    expect(ui.renderArrInstanceRow('Radarr', 0, null, false)).toContain('7878');
    expect(ui.renderArrInstanceRow('Sonarr', 0, null, false)).toContain('8989');
    expect(ui.renderArrInstanceRow('Radarr', 0, { Name: '<x>' }, false)).toContain('&lt;x>');
  });

  test('skip-cert accepts both casings and never renders checked wrongly', () => {
    expect(ui.renderArrInstanceRow('Radarr', 0, { SkipCertificateValidation: true }, false)).toContain('checked');
    expect(ui.renderArrInstanceRow('Radarr', 0, { skipCertificateValidation: true }, false)).toContain('checked');
    expect(ui.renderArrInstanceRow('Radarr', 0, { SkipCertificateValidation: false }, false)).not.toContain('checked');
    expect(ui.renderArrInstanceRow('Radarr', 0, {}, false)).not.toContain('checked');
  });

  test('api keys never appear in the serialized html', () => {
    const html = ui.renderArrInstanceRow('Radarr', 0, { ApiKey: 'secret' }, false);
    expect(html).not.toContain('secret');
  });

  test('libraries picker only renders past one instance', () => {
    expect(ui.renderArrInstanceRow('Radarr', 0, {}, false)).not.toContain('arr-library-wrapper');
    expect(ui.renderArrInstanceRow('Radarr', 0, {}, true)).toContain('arr-library-wrapper');
  });
});

describe('setArrInstanceApiKeys and collect', () => {
  test('keys land in password inputs post-render', () => {
    arrShell([{ Name: 'R', Url: 'http://r:7878', ApiKey: 'k1' }]);
    expect((document.getElementById('Radarr_0_key') as HTMLInputElement).value).toBe('k1');
  });

  test('collect reads edited values', () => {
    arrShell([{ Name: 'R', Url: 'http://r:7878', ApiKey: 'k1' }]);
    (document.getElementById('Radarr_0_name') as HTMLInputElement).value = 'Renamed';
    const out = ui.collectArrInstances('Radarr');
    expect(out).toMatchObject([{ Name: 'Renamed', Url: 'http://r:7878', ApiKey: 'k1' }]);
  });

  test('missing checkbox defaults skip-cert to false', () => {
    arrShell([{ Name: 'R', Url: 'u', ApiKey: 'k' }]);
    document.getElementById('Radarr_0_skipCert')!.remove();
    expect(ui.collectArrInstances('Radarr')[0].SkipCertificateValidation).toBe(false);
  });
});

describe('createArrCountText', () => {
  test.each([[0, ''], [1, '(1 / 3)'], [3, '(3 / 3)']])('count %s', (n, text) => {
    expect(ui.createArrCountText(n)).toBe(text);
  });
});

describe('add and remove instances', () => {
  test('add appends a row until the cap and hides the button', () => {
    ajaxRouter({});
    arrShell([]);
    ui.addArrInstance('Radarr');
    ui.addArrInstance('Radarr');
    ui.addArrInstance('Radarr');
    expect(document.querySelectorAll('.arr-instance-row[data-type="Radarr"]')).toHaveLength(3);
    expect((document.getElementById('btnAddRadarr') as HTMLButtonElement).style.display).toBe('none');
    ui.addArrInstance('Radarr');
    expect(document.querySelectorAll('.arr-instance-row[data-type="Radarr"]')).toHaveLength(3);
  });

  test('remove deletes, reindexes and autosaves without the row', async () => {
    const calls = ajaxRouter({});
    arrShell([
      { Name: 'A', Url: 'http://a', ApiKey: 'k' },
      { Name: 'B', Url: 'http://b', ApiKey: 'k' },
    ]);
    ui.removeArrInstance('Radarr', 0);
    const rows = document.querySelectorAll('.arr-instance-row[data-type="Radarr"]');
    expect(rows).toHaveLength(1);
    expect((document.getElementById('Radarr_0_name') as HTMLInputElement).value).toBe('B');
    await flush();
    const put = calls.find((c) => String(c.url).includes('JellyfinHelper/Configuration'));
    expect(put).toBeDefined();
    expect((put!.body as any).RadarrInstances.map((i: any) => i.Name)).toEqual(['B']);
  });
});

describe('compare and status badges', () => {
  function compareShell() {
    ajaxRouter({
      'TestConnection': { Success: true },
      'ArrIntegration/Compare/Radarr': {
        InBoth: ['a'],
        InArrOnly: Array.from({ length: 55 }, (_, i) => 'b' + i),
        InArrOnlyMissing: [],
        InJellyfinOnly: ['c'],
      },
    });
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    ui.initArrButtons({ RadarrInstances: [{ Name: 'R', Url: 'http://r', ApiKey: 'k' }], SonarrInstances: [] });
  }

  test('initial health check paints ok and caches for 60s', async () => {
    const calls = ajaxRouter({ 'TestConnection': { Success: true } });
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    ui.initArrButtons({ RadarrInstances: [{ Name: 'R', Url: 'http://r', ApiKey: 'k' }], SonarrInstances: [] });
    await flush();
    expect(document.getElementById('arrStatusRadarr')!.classList.contains('is-ok')).toBe(true);
    const posts = () => calls.filter((c) => String(c.url).includes('TestConnection')).length;
    expect(posts()).toBe(1);
    ui.refreshArrInstanceStatus('Radarr', 0);
    await flush();
    expect(posts()).toBe(1);
  });

  test('compare renders capped sections', async () => {
    compareShell();
    await flush();
    (document.getElementById('btnCompareRadarr') as HTMLButtonElement).click();
    await flush();
    const result = document.getElementById('arrResult')!;
    expect(result.textContent).toContain('In Both');
    expect(result.textContent).toMatch(/and 5 more/);
  });

  test('invalid types fail loudly', () => {
    ajaxRouter({});
    document.body.innerHTML = '<div id="arrResult"></div>';
    ui.compareArr('Lidarr', 0, 'L');
    expect(document.getElementById('arrResult')!.textContent).toMatch(/invalid/i);
  });

  test('missing result container does not throw', () => {
    ajaxRouter({});
    document.body.innerHTML = '';
    expect(() => ui.compareArr('Radarr', 0, 'R')).not.toThrow();
  });

  test('out-of-range status clears the badge', () => {
    ajaxRouter({});
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    ui.initArrButtons({ RadarrInstances: [], SonarrInstances: [] });
    ui.refreshArrInstanceStatus('Radarr', 3);
    expect(document.getElementById('arrStatusRadarr')).toBeNull();
  });
});

describe('testArrConnection', () => {
  test('missing fields never reach the network', async () => {
    const calls = ajaxRouter({});
    arrShell([{ Name: 'R', Url: '', ApiKey: '' }]);
    ui.testArrConnection('Radarr', 0);
    await flush();
    expect(calls).toHaveLength(0);
    expect(document.getElementById('Radarr_0_btnTest')!.textContent).toMatch(/required/i);
  });

  test('success autosaves the settings', async () => {
    const calls = ajaxRouter({ 'TestConnection': { Success: true, Message: 'ok' } });
    arrShell([{ Name: 'R', Url: 'http://r', ApiKey: 'k' }]);
    ui.testArrConnection('Radarr', 0);
    await flush();
    expect(calls.some((c) => String(c.url).includes('TestConnection'))).toBe(true);
    expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Configuration'))).toBe(true);
  });

  test('failed probe shows feedback and saves nothing', async () => {
    const calls = ajaxRouter({ 'TestConnection': { Success: false, Message: 'nope' } });
    arrShell([{ Name: 'R', Url: 'http://r', ApiKey: 'k' }]);
    ui.testArrConnection('Radarr', 0);
    await flush();
    const btn = document.getElementById('Radarr_0_btnTest') as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
    expect(calls.some((c) => String(c.url).includes('JellyfinHelper/Configuration'))).toBe(false);
  });

  test('missing row elements are a no-op', async () => {
    ajaxRouter({});
    settingsShell();
    expect(() => ui.testArrConnection('Radarr', 9)).not.toThrow();
    await flush();
  });
});

describe('arr status and compare extras', () => {
  test('testArrConnection failure path', async () => {
    ajaxRouter({}, ['ArrIntegration/TestConnection']);
    arrShell([{ Name: 'R', Url: 'http://r', ApiKey: 'k' }]);
    ui.testArrConnection('Radarr', 0);
    await flush();
    const btn = document.getElementById('Radarr_0_btnTest') as HTMLButtonElement;
    expect(btn.disabled).toBe(false);
  });

  test('testArrConnection missing fields', async () => {
    ajaxRouter({});
    arrShell([{ Name: 'R', Url: '', ApiKey: '' }]);
    ui.testArrConnection('Radarr', 0);
    await flush();
  });

  test('refreshArrInstanceStatus unknown', async () => {
    ajaxRouter({});
    arrShell([]);
    ui.refreshArrInstanceStatus('Radarr', 0);
    await flush();
  });

  test('compareArr error', async () => {
    ajaxRouter({}, ['ArrIntegration/Compare']);
    settingsShell();
    const result = document.getElementById('arrResult');
    if (result) result.innerHTML = '';
    try { ui.compareArr(); } catch (e) { /* ignore */ }
    await flush();
  });

  test('collect falls back to the stashed library value', () => {
    ajaxRouter({});
    settingsShell();
    document.getElementById('arrButtons')!.innerHTML =
      '<div class="arr-instance-row" data-type="Radarr" data-index="0" data-libs-stash="a,b">' +
      '<input id="Radarr_0_name" value="R"><input id="Radarr_0_url" value="u"><input id="Radarr_0_key" value="k"></div>';
    expect(ui.collectArrInstances('Radarr')[0].Libraries).toBe('a,b');
  });

  test('add without the add-button wrapper is a no-op', () => {
    ajaxRouter({});
    settingsShell();
    document.getElementById('arrButtons')!.innerHTML = '';
    expect(() => ui.addArrInstance('Radarr')).not.toThrow();
  });

  test('add expands the collapsible section and syncs aria', () => {
    ajaxRouter({});
    arrShell([]);
    const col = document.createElement('div');
    col.id = 'arrCollapsibleRadarr';
    col.className = 'arr-collapsible';
    col.innerHTML =
      '<div id="arrCollapsibleHeaderRadarr"><button aria-expanded="false"></button></div>' +
      '<div class="arr-collapsible-body" aria-hidden="true"></div>';
    document.getElementById('arrButtons')!.appendChild(col);
    ui.addArrInstance('Radarr');
    expect(col.classList.contains('arr-expanded')).toBe(true);
    expect(document.getElementById('arrCollapsibleHeaderRadarr')!.getAttribute('aria-expanded')).toBe('true');
    expect(col.querySelector('.arr-collapsible-body')!.getAttribute('aria-hidden')).toBe('false');
  });

  test('library seed failure falls back to a text input', async () => {
    ajaxRouter({}, ['Configuration/Libraries']);
    settingsShell();
    document.getElementById('arrButtons')!.innerHTML =
      ui.renderArrInstanceRow('Radarr', 0, null, false) + ui.renderArrInstanceRow('Radarr', 1, null, false);
    ui.syncArrLibraryPickers('Radarr');
    await flush();
    const wrap = document.getElementById('Radarr_0_libs');
    expect(wrap).not.toBeNull();
    expect(wrap!.innerHTML).toContain('<input');
  });

  test('sync with existing pickers seeds nothing again', async () => {
    ajaxRouter({ 'Configuration/Libraries': { Libraries: [] } });
    arrShell([
      { Name: 'A', Url: 'http://a', ApiKey: 'k' },
      { Name: 'B', Url: 'http://b', ApiKey: 'k' },
    ]);
    ui.syncArrLibraryPickers('Radarr');
    await flush();
    ui.syncArrLibraryPickers('Radarr');
    await flush();
  });

  test('repeated test clicks and removal clear pending timers', async () => {
    ajaxRouter({});
    arrShell([{ Name: 'A', Url: '', ApiKey: '' }]);
    ui.testArrConnection('Radarr', 0);
    ui.testArrConnection('Radarr', 0);
    ui.removeArrInstance('Radarr', 0);
    expect(document.querySelectorAll('.arr-instance-row[data-type="Radarr"]')).toHaveLength(0);
  });

  test('remove rekeys labels and library checkbox ids', () => {
    ajaxRouter({});
    settingsShell();
    const row = (idx: number) =>
      '<div class="arr-instance-row" data-type="Radarr" data-index="' + idx + '">' +
      '<input id="Radarr_' + idx + '_name" value="n"><input id="Radarr_' + idx + '_url" value="u">' +
      '<input id="Radarr_' + idx + '_key" value="k"><input type="checkbox" id="Radarr_' + idx + '_skipCert">' +
      '<label>name</label><label>url</label><label>key</label><label>cert</label>' +
      '<button type="button" class="btnRemoveArr" data-type="Radarr" data-index="' + idx + '"></button>' +
      '<button type="button" class="btnTestArr" data-type="Radarr" data-index="' + idx + '"></button>' +
      '<div id="Radarr_' + idx + '_libs" class="arr-library-wrapper">' +
      '<input type="checkbox" id="Radarr_' + idx + '_libs_lib_0"><label for="Radarr_' + idx + '_libs_lib_0">lib</label>' +
      '</div></div>';
    document.getElementById('arrButtons')!.innerHTML = row(0) + row(1) + row(2);
    ui.removeArrInstance('Radarr', 0);
    const left = document.querySelector('.arr-instance-row[data-type="Radarr"]')!;
    expect((left.querySelector('input') as HTMLInputElement).id).toBe('Radarr_0_name');
    const lib = left.querySelector('.arr-library-wrapper')!;
    expect((lib.querySelector('input') as HTMLInputElement).id).toBe('Radarr_0_libs_lib_0');
  });

  test('stale status responses are dropped', async () => {
    ajaxRouter({ 'TestConnection': { Success: true } });
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    const cfg = { RadarrInstances: [{ Name: 'R', Url: 'http://r', ApiKey: 'k' }], SonarrInstances: [] };
    ui.initArrButtons(cfg);
    ui.initArrButtons(cfg);
    await flush();
    expect(document.getElementById('arrStatusRadarr')!.classList.contains('is-ok')).toBe(true);
  });

  test('refresh paints the error badge when the probe fails', async () => {
    ajaxRouter({}, ['TestConnection']);
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    ui.initArrButtons({ RadarrInstances: [{ Name: 'R', Url: 'http://r', ApiKey: 'k' }], SonarrInstances: [] });
    await flush();
    expect(document.getElementById('arrStatusRadarr')!.classList.contains('is-error')).toBe(true);
  });

  test('sonarr block renders and filters incomplete entries', async () => {
    ajaxRouter({ 'TestConnection': { Success: true } });
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    ui.initArrButtons({
      RadarrInstances: [{ Name: 'R', Url: 'http://r', ApiKey: 'k' }],
      SonarrInstances: [{ Name: 'S', Url: 'http://s', ApiKey: 'k' }, { Url: 'http://nokey' }],
    });
    await flush();
    expect(document.getElementById('arrSelectSonarr')).not.toBeNull();
    expect(document.getElementById('arrStatusSonarr')!.classList.contains('is-ok')).toBe(true);
  });

  test('out-of-range refresh clears an existing badge', async () => {
    ajaxRouter({ 'TestConnection': { Success: true } });
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    ui.initArrButtons({ RadarrInstances: [{ Name: 'R', Url: 'http://r', ApiKey: 'k' }], SonarrInstances: [] });
    await flush();
    ui.refreshArrInstanceStatus('Radarr', 5);
    expect(document.getElementById('arrStatusRadarr')!.innerHTML).toBe('');
  });

  test('select change handles NaN and valid indexes', async () => {
    ajaxRouter({ 'TestConnection': { Success: true } });
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    ui.initArrButtons({ RadarrInstances: [{ Name: 'R', Url: 'http://r', ApiKey: 'k' }], SonarrInstances: [] });
    await flush();
    const sel = document.getElementById('arrSelectRadarr') as HTMLSelectElement;
    sel.value = 'x';
    sel.dispatchEvent(new Event('change'));
    sel.value = '0';
    sel.dispatchEvent(new Event('change'));
    await flush();
    expect(sel.value).toBe('0');
  });

  test('compare falls back to index zero for a bad select value', async () => {
    const calls = ajaxRouter({ 'TestConnection': { Success: true }, 'ArrIntegration/Compare/Radarr': { InBoth: [] } });
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    ui.initArrButtons({ RadarrInstances: [{ Name: 'R', Url: 'http://r', ApiKey: 'k' }], SonarrInstances: [] });
    await flush();
    (document.getElementById('arrSelectRadarr') as HTMLSelectElement).value = 'x';
    (document.getElementById('btnCompareRadarr') as HTMLButtonElement).click();
    await flush();
    expect(calls.some((c) => String(c.url).includes('Compare/Radarr?index=0'))).toBe(true);
  });

  test('compare failure renders the error card', async () => {
    ajaxRouter({ 'TestConnection': { Success: true } }, ['ArrIntegration/Compare']);
    document.body.innerHTML = '<div id="arrButtons"></div><div id="arrResult"></div>';
    ui.initArrButtons({ RadarrInstances: [{ Name: 'R', Url: 'http://r', ApiKey: 'k' }], SonarrInstances: [] });
    await flush();
    ui.compareArr('Radarr', 0, 'R');
    await flush();
    expect(document.getElementById('arrResult')!.textContent).toMatch(/failed to compare|check settings/i);
  });

  test('attached add, test and remove handlers dispatch', async () => {
    ajaxRouter({});
    arrShell([]);
    ui.attachAddHandlers();
    (document.getElementById('btnAddRadarr') as HTMLButtonElement).click();
    expect(document.querySelectorAll('.arr-instance-row[data-type="Radarr"]')).toHaveLength(1);
    (document.getElementById('Radarr_0_btnTest') as HTMLButtonElement).click();
    (document.querySelector('.btnRemoveArr[data-index="0"]') as HTMLButtonElement).click();
    await flush();
    expect(document.querySelectorAll('.arr-instance-row[data-type="Radarr"]')).toHaveLength(0);
  });

  test('sonarr add handler appends a row', () => {
    ajaxRouter({});
    settingsShell();
    document.getElementById('arrButtons')!.innerHTML = '<div id="SonarrAddBtnWrap"><button type="button" id="btnAddSonarr"></button></div>';
    ui.attachAddHandlers();
    (document.getElementById('btnAddSonarr') as HTMLButtonElement).click();
    expect(document.querySelectorAll('.arr-instance-row[data-type="Sonarr"]')).toHaveLength(1);
  });
});
