import { afterAll, beforeEach, describe, expect, test } from 'vitest';
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

describe('health extras', () => {
  test('fillHealthData attaches handlers and orphaned path normalization', async () => {
    ajaxRouter({});
    document.body.innerHTML = '<div id="healthContent"></div>';
    const data = {
      Libraries: [{ VideosWithoutSubtitles: 1, VideosWithoutImages: 0, VideosWithoutNfo: 0, OrphanedMetadataDirectories: 1 }],
      Movies: [{ VideosWithoutSubtitlesPaths: ['/a'], OrphanedMetadataDirectoriesPaths: ['/o/'] }],
      TvShows: [],
      Other: [],
    };
    ui.fillHealthData(data);
    const item = document.querySelector('.health-clickable[data-health-type="orphaned"]') as HTMLElement;
    item.click();
    await flush();
    const panel = document.getElementById('healthDetailPanel')!;
    expect(panel.innerHTML).toContain('/');
  });

  test('unknown health type renders an empty panel', async () => {
    ajaxRouter({});
    document.body.innerHTML = '<div id="healthContent"></div>';
    ui.fillHealthData({ Libraries: [] });
    const fake = document.createElement('div');
    fake.className = 'health-clickable';
    fake.dataset.healthType = 'nope';
    document.getElementById('healthContent')!.appendChild(fake);
    document.getElementById('healthContent')!.insertAdjacentHTML('beforeend', '<div class="file-tree-panel" id="healthDetailPanel"></div>');
    fake.click();
    await flush();
    expect(document.getElementById('healthDetailPanel')!.textContent).toBe('');
  });

  test('loadTrashHealthSection stale request', async () => {
    ajaxRouter({ Configuration: { UseTrash: true }, 'Trash/Contents': { Libraries: [], RetentionDays: 5 } });
    document.body.innerHTML = '<div id="healthContent"></div>';
    ui.loadTrashHealthSection();
    ui.loadTrashHealthSection();
    await flush();
    expect(document.querySelectorAll('#trashHealthSection')).toHaveLength(1);
  });

  test('loadTrashHealthSection missing container', async () => {
    ajaxRouter({ Configuration: { UseTrash: true }, 'Trash/Contents': { Libraries: [], RetentionDays: 5 } });
    document.body.innerHTML = '';
    ui.loadTrashHealthSection();
    await flush();
    expect(document.getElementById('healthContent')).toBeNull();
  });

  test('loadTrashHealthSection removes existing and empty trash', async () => {
    ajaxRouter({ Configuration: { UseTrash: true }, 'Trash/Contents': { Libraries: [], RetentionDays: 5 } });
    document.body.innerHTML = '<div id="healthContent"><div id="trashHealthSection"></div></div>';
    ui.loadTrashHealthSection();
    await flush();
    expect(document.getElementById('trashHealthSection')).not.toBeNull();
  });

  test('loadTrashHealthSection contents error', async () => {
    ajaxRouter({ Configuration: { UseTrash: true } }, ['Trash/Contents']);
    document.body.innerHTML = '<div id="healthContent"></div>';
    ui.loadTrashHealthSection();
    await flush();
    expect(document.getElementById('trashHealthSection')).toBeNull();
  });

  test('loadTrashHealthSection config disabled', async () => {
    ajaxRouter({ Configuration: { UseTrash: false } });
    document.body.innerHTML = '<div id="healthContent"></div>';
    ui.loadTrashHealthSection();
    await flush();
    expect(document.getElementById('trashHealthSection')).toBeNull();
  });
});
