import { afterAll, describe, expect, test } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);

describe('save indicators', () => {
  test('null element is a no-op', () => {
    expect(() => ui.showAutoSaveIndicatorOverlay(null, true)).not.toThrow();
  });

  test('text inputs gain a background check', () => {
    document.body.innerHTML = '<div><input type="text" id="t"></div>';
    const el = document.getElementById('t')!;
    ui.showAutoSaveIndicatorOverlay(el, true);
    expect(el.style.backgroundImage).toContain('data:image/svg+xml');
  });

  test('multiselect chevrons swap inline', () => {
    document.body.innerHTML = '<div><button><span class="library-multiselect-chevron">v</span></button></div>';
    const btn = document.querySelector('button')!;
    ui.showAutoSaveIndicatorOverlay(btn, false);
    expect(btn.querySelector('.library-multiselect-chevron')!.innerHTML).toContain('color:');
  });

  test('other elements get an overlay indicator', () => {
    document.body.innerHTML = '<div><button id="b">x</button></div>';
    const btn = document.getElementById('b')!;
    ui.showAutoSaveIndicatorOverlay(btn, true);
    expect(btn.previousElementSibling).not.toBeNull();
  });

  test('createSaveIndicator colors by outcome', () => {
    document.body.innerHTML = '';
    expect(ui.createSaveIndicator(null, true).style.color).toContain('46, 204, 113');
    expect(ui.createSaveIndicator(null, false).style.color).toContain('231, 76, 60');
  });

  test('fade delays differ by outcome', () => {
    expect(ui.calculateFadeDelay(true)).toBe(2000);
    expect(ui.calculateFadeDelay(false)).toBe(3000);
  });
});

describe('getCssVar', () => {
  test('falls back when unset', () => {
    expect(ui.getCssVar('--does-not-exist', '#fff')).toBe('#fff');
    expect(ui.getCssVar('--does-not-exist', '')).toBe('');
  });
});

describe('resolveArrInstances', () => {
  test.each([[null], [undefined], [{}]])('empty config yields empty', (cfg: any) => {
    expect(ui.resolveArrInstances(cfg, 'Radarr')).toEqual([]);
  });

  test('returns the typed instances', () => {
    const cfg = { RadarrInstances: [{ Name: 'R' }], SonarrInstances: [] };
    expect(ui.resolveArrInstances(cfg, 'Radarr')).toEqual([{ Name: 'R' }]);
    expect(ui.resolveArrInstances(cfg, 'Sonarr')).toEqual([]);
  });
});
