import { afterAll, beforeEach, describe, expect, test } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);


function failingApi() {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    ajax: () => Promise.reject(new Error('unreachable')),
  };
}

function openOnTrashPath(path: string) {
  document.body.innerHTML = `<input id="cfgTrashPath" value="${path}">`;
  ui.openFolderBrowserDialog();
  return document.getElementById('folderBrowserOverlay')!;
}

describe('folder browser dialog', () => {
  beforeEach(failingApi);

  test('unreachable backend shows an error instead of hanging', async () => {
    openOnTrashPath('/media');
    for (let i = 0; i < 10; i++) await Promise.resolve();
    const listing = document.getElementById('folderBrowserListing')!;
    expect(listing.textContent).toMatch(/cannot access|error/i);
    // A failed browse must not leave a stale path behind for Select to persist.
    document.getElementById('folderBrowserSelect')!.dispatchEvent(new window.Event('click', { bubbles: true }));
    expect(document.getElementById('folderBrowserOverlay')).toBeNull();
    expect((document.getElementById('cfgTrashPath') as HTMLInputElement).value).toBe('/media');
  });

  test('escape closes the dialog', () => {
    const overlay = openOnTrashPath('/media');
    overlay.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(document.getElementById('folderBrowserOverlay')).toBeNull();
  });

  test('clicking the backdrop closes the dialog', () => {
    const overlay = openOnTrashPath('/media');
    overlay.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
    expect(document.getElementById('folderBrowserOverlay')).toBeNull();
  });

  test('an invalid new name shows an inline error and keeps the dialog open', () => {
    openOnTrashPath('/media');
    (document.getElementById('folderBrowserNewName') as HTMLInputElement).value = 'a*b';
    document.getElementById('folderBrowserSelect')!.dispatchEvent(new window.Event('click', { bubbles: true }));
    expect(document.getElementById('folderBrowserOverlay')).not.toBeNull();
    expect(document.getElementById('folderBrowserListing')!.textContent).toMatch(/invalid/i);
  });

  test('empty selection with no name just closes', () => {
    openOnTrashPath('');
    (document.getElementById('folderBrowserNewName') as HTMLInputElement).value = '';
    document.getElementById('folderBrowserSelect')!.dispatchEvent(new window.Event('click', { bubbles: true }));
    expect(document.getElementById('folderBrowserOverlay')).toBeNull();
  });
});

describe('folder browser navigation', () => {
  function browseRoutes() {
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: (opts: any) => {
        const url = String(opts.url);
        if (url.includes('LibraryPaths')) {
          return Promise.resolve({ libraryPaths: [{ path: '/media', name: 'Media' }] });
        }
        if (url.includes('BrowseFolders')) {
          const path = new URL(url, 'http://x').searchParams.get('path');
          if (!path) {
            return Promise.resolve({
              CurrentPath: null, ParentPath: null, CanGoUp: false,
              Directories: [{ Name: 'media', Path: '/media', HasChildren: true }],
            });
          }
          return Promise.resolve({
            CurrentPath: path, ParentPath: '/', CanGoUp: true,
            Directories: [{ Name: 'trash', Path: path + '/trash', HasChildren: false }],
          });
        }
        return Promise.resolve({});
      },
    };
  }

  async function flush() {
    for (let i = 0; i < 10; i++) await Promise.resolve();
  }

  test('roots list, quick jump and drill-down', async () => {
    browseRoutes();
    document.body.innerHTML = '<input id="cfgTrashPath" value="">';
    ui.openFolderBrowserDialog();
    await flush();
    expect(document.querySelectorAll('.folder-browser-item').length).toBe(1);
    expect(document.getElementById('folderBrowserQuickJump')!.textContent).toContain('Media');
    (document.querySelector('.folder-browser-item') as HTMLElement).click();
    await flush();
    expect(document.getElementById('folderBrowserBreadcrumb')!.textContent).toContain('/media');
    expect(document.querySelector('.folder-browser-up')).not.toBeNull();
  });

  test('select persists the browsed path and saves', async () => {
    browseRoutes();
    document.body.innerHTML =
      '<input id="cfgTrashPath" value=""><input id="cfgOrphanAge" value="30">' +
      '<select id="cfgTrickplayMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgEmptyFolderMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgSubtitleMode"><option value="DryRun">x</option></select><select id="cfgLinkMode"><option value="DryRun">x</option></select>' +
      '<input id="cfgTrashDays" value="30">';
    ui.openFolderBrowserDialog();
    await flush();
    (document.querySelector('.folder-browser-item') as HTMLElement).click();
    await flush();
    document.getElementById('folderBrowserSelect')!.dispatchEvent(new window.Event('click', { bubbles: true }));
    await flush();
    expect((document.getElementById('cfgTrashPath') as HTMLInputElement).value).toBe('/media');
    expect(document.getElementById('folderBrowserOverlay')).toBeNull();
  });

  test('new folder names join onto the current path', async () => {
    browseRoutes();
    document.body.innerHTML =
      '<input id="cfgTrashPath" value=""><input id="cfgOrphanAge" value="30">' +
      '<select id="cfgTrickplayMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgEmptyFolderMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgSubtitleMode"><option value="DryRun">x</option></select><select id="cfgLinkMode"><option value="DryRun">x</option></select>' +
      '<input id="cfgTrashDays" value="30">';
    ui.openFolderBrowserDialog();
    await flush();
    (document.querySelector('.folder-browser-item') as HTMLElement).click();
    await flush();
    (document.getElementById('folderBrowserNewName') as HTMLInputElement).value = 'trash';
    document.getElementById('folderBrowserSelect')!.dispatchEvent(new window.Event('click', { bubbles: true }));
    await flush();
    expect((document.getElementById('cfgTrashPath') as HTMLInputElement).value).toBe('/media/trash');
  });

  test('keyboard enter navigates like a click', async () => {
    browseRoutes();
    document.body.innerHTML = '<input id="cfgTrashPath" value="">';
    ui.openFolderBrowserDialog();
    await flush();
    const item = document.querySelector('.folder-browser-item') as HTMLElement;
    item.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await flush();
    expect(document.getElementById('folderBrowserBreadcrumb')!.textContent).toContain('/media');
  });

  test('partial errors keep the listing with a warning', async () => {
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: (opts: any) => {
        if (String(opts.url).includes('BrowseFolders')) {
          return Promise.resolve({
            CurrentPath: '/media', ParentPath: '/', CanGoUp: true,
            Directories: [{ Name: 'a', Path: '/media/a', HasChildren: false }],
            Error: 'slow disk',
          });
        }
        return Promise.resolve({});
      },
    };
    document.body.innerHTML = '<input id="cfgTrashPath" value="/media">';
    ui.openFolderBrowserDialog();
    await flush();
    const listing = document.getElementById('folderBrowserListing')!;
    expect(listing.textContent).toContain('slow disk');
    expect(listing.querySelectorAll('.folder-browser-item').length).toBeGreaterThan(1);
  });
});
