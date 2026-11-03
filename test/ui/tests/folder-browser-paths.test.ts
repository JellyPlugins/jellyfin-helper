import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => { ui = freshConfigPage(); });

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown> = {}) {
  const calls: { url: string }[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      calls.push({ url: String(opts.url) });
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
  return calls;
}

describe('folder browser extras', () => {
  test.each([
    ['/media', ' new ', '/media/new'],
    ['/media/', 'new', '/media/new'],
    ['C:\\media', 'new', 'C:\\media\\new'],
    ['C:\\media\\', 'new', 'C:\\media\\new'],
    ['C:\\media/mixed', 'new', 'C:\\media/mixed/new'],
    ['', 'new', 'new'],
    ['/media', '   ', '/media'],
  ])('joins %s with %s without changing separator semantics', (path, name, expected) => {
    expect(ui.appendFolderBrowserName(path, name)).toBe(expected);
  });

  test('missing trash input and missing new-name input remain safe', async () => {
    ajaxRouter({ 'Configuration/BrowseFolders': { Directories: [] } });
    document.body.innerHTML = '';
    ui.openFolderBrowserDialog();
    await flush();
    document.getElementById('folderBrowserNewName')!.remove();
    (document.getElementById('folderBrowserSelect') as HTMLElement).click();
    expect(document.getElementById('folderBrowserOverlay')).toBeNull();
  });

  test('listing escapes both DTO casings and preserves falsy fallback precedence', () => {
    document.body.innerHTML = ui.renderFolderBrowserListing([
      { Name: '<img src=x onerror=bad()>', Path: '/a" data-evil="yes', HasChildren: true },
      { Name: '', name: 'lower', Path: '', path: '/lower', HasChildren: false, hasChildren: true },
    ], null, false, null, '<script>bad()</script>');
    const items = document.querySelectorAll<HTMLElement>('.folder-browser-item');
    expect(items).toHaveLength(2);
    expect(items[0].dataset.path).toBe('/a" data-evil="yes');
    expect(items[0].textContent).toContain('<img src=x onerror=bad()>');
    expect(items[1].dataset.path).toBe('/lower');
    expect(items[1].textContent).toContain('lower');
    expect(items[1].querySelectorAll(':scope > span')).toHaveLength(3);
    expect(document.querySelector('[data-evil], img, script')).toBeNull();
    expect(document.body.textContent).toContain('<script>bad()</script>');
  });

  test.each([false, true])('stale browse response is ignored (failure=%s)', async (failOld) => {
    const requests: Array<{ resolve: (v: unknown) => void; reject: (e: unknown) => void }> = [];
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      ajax: () => new Promise((resolve, reject) => requests.push({ resolve, reject })),
    };
    document.body.innerHTML = '<div id="listing"></div><div id="bc"></div>';
    const listing = document.getElementById('listing')!;
    const breadcrumb = document.getElementById('bc')!;
    const state: any = {};
    ui.browseTo('/old', listing, breadcrumb, state);
    ui.browseTo('/new', listing, breadcrumb, state);
    requests[1].resolve({ CurrentPath: '/new', Directories: [{ Name: 'new', Path: '/new/child' }] });
    await flush();
    const newHtml = listing.innerHTML;
    if (failOld) requests[0].reject(new Error('old failure'));
    else requests[0].resolve({ CurrentPath: '/old', Directories: [] });
    await flush();
    expect(state.currentPath).toBe('/new');
    expect(breadcrumb.textContent).toContain('/new');
    expect(listing.innerHTML).toBe(newHtml);
  });

  test('multiple quick-jump and directory handlers read their own row', async () => {
    const calls = ajaxRouter({
      'Configuration/LibraryPaths': { libraryPaths: [
        { path: '/first', name: 'First' }, { Path: '/second', Name: 'Second' },
      ] },
      'Configuration/BrowseFolders': { Directories: [
        { Name: 'first', Path: '/first' }, { name: 'second', path: '/second' },
      ] },
    });
    document.body.innerHTML = '<input id="cfgTrashPath" value="">';
    ui.openFolderBrowserDialog();
    await flush();
    (document.querySelectorAll<HTMLElement>('.folder-browser-quick-btn')[1]).click();
    await flush();
    const item = document.querySelectorAll<HTMLElement>('.folder-browser-item')[1];
    expect(item.getAttribute('tabindex')).toBe('0');
    expect(item.getAttribute('role')).toBe('button');
    item.dispatchEvent(new window.KeyboardEvent('keydown', { key: ' ', bubbles: true }));
    await flush();
    expect(calls.filter((c) => c.url.includes('path=' + encodeURIComponent('/second')))).toHaveLength(2);
    expect(calls.some((c) => c.url.includes('path=' + encodeURIComponent('/first')))).toBe(false);
  });

  test('select closes before the input event and sends the selected path in the settings DTO', async () => {
    const requests: any[] = [];
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      ajax: (opts: any) => {
        requests.push(opts);
        return Promise.resolve(String(opts.url).includes('BrowseFolders')
          ? { CurrentPath: '/media', Directories: [] } : {});
      },
    };
    document.body.innerHTML =
      '<input id="cfgTrashPath" value="/media"><input id="cfgOrphanAge" value="30">' +
      '<input id="cfgTrashDays" value="30">' +
      '<select id="cfgTrickplayMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgEmptyFolderMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgSubtitleMode"><option value="DryRun">x</option></select>' +
      '<select id="cfgLinkMode"><option value="DryRun">x</option></select>';
    const input = document.getElementById('cfgTrashPath') as HTMLInputElement;
    const onInput = vi.fn(() => {
      expect(document.getElementById('folderBrowserOverlay')).toBeNull();
      expect(input.value).toBe('/media/new');
    });
    input.addEventListener('input', onInput);
    ui.openFolderBrowserDialog();
    await flush();
    (document.getElementById('folderBrowserNewName') as HTMLInputElement).value = ' new ';
    (document.getElementById('folderBrowserSelect') as HTMLElement).click();
    await flush();
    expect(onInput).toHaveBeenCalledOnce();
    const save = requests.find((r) => r.type === 'PUT');
    expect(save).toBeDefined();
    expect(JSON.parse(save.data).TrashFolderPath).toBe('/media/new');
  });

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
    const calls = ajaxRouter({ 'BrowseFolders': { CurrentPath: '/root', CanGoUp: false, Directories: [] } });
    document.body.innerHTML = '<div id="listing"></div><div id="bc"></div>';
    const state: any = {};
    ui.browseTo('/root', document.getElementById('listing')!, document.getElementById('bc')!, state);
    await flush();
    const item = document.querySelector('.folder-browser-item') as HTMLElement;
    expect(item).not.toBeNull();
    item.click();
    await flush();
    expect(calls.some((c) => c.url === 'JellyfinHelper/Configuration/BrowseFolders')).toBe(true);
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
    expect((document.getElementById('cfgTrashPath') as HTMLInputElement).value).toBe('C:\\trash\\new');
  });

  test('open dialog quick jump click', async () => {
    const calls = ajaxRouter({
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
    qbtn.click();
    await flush();
    expect(calls.some((c) => c.url.includes('BrowseFolders?path=' + encodeURIComponent('/lib')))).toBe(true);
  });
});
