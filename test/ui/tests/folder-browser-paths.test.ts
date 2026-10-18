import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => { ui = freshConfigPage(); });

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown> = {}) {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
}

describe('folder browser extras', () => {
  test('browseTo error with no dirs clears path', async () => {
    ajaxRouter({ 'BrowseFolders': { Error: 'bad', Directories: [], CurrentPath: '/x' } });
    document.body.innerHTML = '<div id="listing"></div><div id="bc"></div>';
    const state: any = {};
    ui.browseTo('/x', document.getElementById('listing')!, document.getElementById('bc')!, state);
    await flush();
    expect(state.currentPath).toBeNull();
  });

  test('browseTo at top shows go to roots', async () => {
    ajaxRouter({ 'BrowseFolders': { CurrentPath: '/root', CanGoUp: false, Directories: [] } });
    document.body.innerHTML = '<div id="listing"></div><div id="bc"></div>';
    const state: any = {};
    ui.browseTo('/root', document.getElementById('listing')!, document.getElementById('bc')!, state);
    await flush();
    expect(document.getElementById('listing')!.innerHTML).toContain('Go up');
  });

  test('browseTo item click go to roots', async () => {
    ajaxRouter({ 'BrowseFolders': { CurrentPath: '/root', CanGoUp: false, Directories: [] } });
    document.body.innerHTML = '<div id="listing"></div><div id="bc"></div>';
    const state: any = {};
    ui.browseTo('/root', document.getElementById('listing')!, document.getElementById('bc')!, state);
    await flush();
    const item = document.querySelector('.folder-browser-item') as HTMLElement;
    expect(item).not.toBeNull();
    item.click();
    await flush();
  });

  test('open dialog windows path select with new folder', async () => {
    ajaxRouter({
      'Configuration/LibraryPaths': { libraryPaths: [] },
      'Configuration/BrowseFolders': { CurrentPath: 'C:\\trash', CanGoUp: true, ParentPath: 'C:\\', Directories: [] },
      'Settings': {},
    });
    document.body.innerHTML =
      '<input id="cfgTrashPath" value="C:\\trash">' +
      '<input id="cfgOrphanAge" value="30"><input id="cfgTrashDays" value="30">' +
      '<select id="cfgTrickplayMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgEmptyFolderMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgSubtitleMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgLinkMode"><option value="DryRun">x</option></select>' +
      '<div id="x"></div>';
    ui.openFolderBrowserDialog();
    await flush();
    (document.getElementById('folderBrowserNewName') as HTMLInputElement).value = 'new';
    (document.getElementById('folderBrowserSelect') as HTMLElement).click();
    await flush();
    await vi.advanceTimersByTimeAsync(2100);
    await flush();
    expect(document.getElementById('cfgTrashPath')!.value).toBe('C:\\trash\\new');
  });

  test('open dialog quick jump click', async () => {
    ajaxRouter({
      'Configuration/LibraryPaths': { libraryPaths: [{ path: '/lib', name: 'Lib' }] },
      'Configuration/BrowseFolders': { CurrentPath: null, CanGoUp: false, Directories: [] },
    });
    document.body.innerHTML = '<input id="cfgTrashPath" value=""><div id="x"></div>';
    ui.openFolderBrowserDialog();
    await flush();
    await vi.advanceTimersByTimeAsync(0);
    await flush();
    const qbtn = document.querySelector('.folder-browser-quick-btn') as HTMLElement;
    expect(qbtn).not.toBeNull();
    if (qbtn) qbtn.click();
    await flush();
  });
});
