import { afterAll, describe, expect, test } from 'vitest';
import { ajaxRouter, flush } from '../src/apiStub.js';
import { installDocListenerCleanup, richData } from '../src/explorerFixtures.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

afterAll(saveCoverage);
installDocListenerCleanup();

// Helper-level edge cases the behavioral explorer suite does not drive directly:
// the dimension index, the filter intersection builder and the base path set.
// The full UI flows live in codecs-explorer-coverage.test.ts and must not be
// duplicated here, otherwise the same assertions run under different listener
// conditions and drift apart.

function stubApi() {
  ajaxRouter({});
  if (!(globalThis as any).window.scrollTo) (globalThis as any).window.scrollTo = () => undefined;
  (globalThis as any).window.HTMLElement.prototype.scrollIntoView = function () {};
  delete (globalThis as any).requestAnimationFrame;
}

async function openExplorer(ui: UiBridge): Promise<HTMLElement> {
  stubApi();
  document.body.innerHTML = '<div id="codecsContent"></div>';
  ui.fillCodecsData(richData());
  await flush();
  return document.getElementById('codecsContent') as HTMLElement;
}

describe('explorer state, libraries and dimension index', () => {
  test('buildDimPathIndex through getDimPathIndex returns empty maps for an unknown dimension', () => {
    const ui = freshConfigPage();
    const index = ui.getDimPathIndex('bogus');
    expect(Object.keys(index.single)).toHaveLength(0);
    expect(Object.keys(index.multi)).toHaveLength(0);
  });
});

describe('filter intersection over path maps', () => {
  test('intersectExplorerFilters returns null without scan data', () => {
    const ui = freshConfigPage();
    const active = [{ dim: ui.getCodecsExplorerDimension('videoCodecs'), values: ['h264'], exclude: false }];
    expect(ui.intersectExplorerFilters(active)).toBeNull();
  });

  test('intersectExplorerFilters ANDs the unions of two dimensions', async () => {
    const ui = freshConfigPage();
    await openExplorer(ui);
    const active = [
      { dim: ui.getCodecsExplorerDimension('videoCodecs'), values: ['h264'], exclude: false },
      { dim: ui.getCodecsExplorerDimension('containers'), values: ['mkv'], exclude: false },
    ];
    const out = ui.intersectExplorerFilters(active);
    expect(Object.keys(out).sort()).toEqual(['/m/a.mkv', '/m/b.mkv', '/m/sub/d.mkv', '/t/e.mkv']);
  });
});
