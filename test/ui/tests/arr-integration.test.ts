import { afterAll, beforeEach, describe, expect, test } from 'vitest';
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
    expect(ui.renderArrInstanceRow('Radarr', 0, { skipCertificateValidation: true }, false)).toContain('checked');
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
    expect(document.querySelectorAll('.arr-instance-row[data-type="Radarr"]').length).toBe(3);
    expect((document.getElementById('btnAddRadarr') as HTMLButtonElement).style.display).toBe('none');
    ui.addArrInstance('Radarr');
    expect(document.querySelectorAll('.arr-instance-row[data-type="Radarr"]').length).toBe(3);
  });

  test('remove deletes, reindexes and autosaves without the row', async () => {
    const calls = ajaxRouter({});
    arrShell([
      { Name: 'A', Url: 'http://a', ApiKey: 'k' },
      { Name: 'B', Url: 'http://b', ApiKey: 'k' },
    ]);
    ui.removeArrInstance('Radarr', 0);
    const rows = document.querySelectorAll('.arr-instance-row[data-type="Radarr"]');
    expect(rows.length).toBe(1);
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
    expect(calls.length).toBe(0);
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
