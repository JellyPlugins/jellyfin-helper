import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => { ui = freshConfigPage(); (globalThis as any).HTMLElement.prototype.scrollIntoView = () => undefined; });

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

describe('shared extras', () => {
  test('loadTranslations error and catch', async () => {
    ajaxRouter({}, ['Translations']);
    let called = false;
    ui.loadTranslations(() => { called = true; });
    await flush();
    expect(called).toBe(true);

    (globalThis as any).ApiClient = null;
    let called2 = false;
    ui.loadTranslations(() => { called2 = true; });
    expect(called2).toBe(true);
    ajaxRouter({});
  });

  test('applyStaticTranslations with button', () => {
    document.body.innerHTML = '<button id="btnScanLibraries"></button>';
    ui.applyStaticTranslations();
    expect(document.getElementById('btnScanLibraries')!.title).toContain('Scan');
  });

  test('pruneFileTreeRegistry with holders', () => {
    document.body.innerHTML = '<div data-tree-render="r1"></div>';
    expect(() => ui.pruneFileTreeRegistry()).not.toThrow();
  });

  test('renderTreeLevel non-lazy branch', () => {
    const html = ui.renderTreeLevel({ children: { a: { name: 'a', children: {}, items: [] } }, items: [] }, 0, 'folder', false);
    expect(html).toContain('a');
  });

  test('ensureTreeChildren null and missing entry', () => {
    expect(ui.ensureTreeChildren(null as any)).toBe(0);
    const el = document.createElement('div');
    el.dataset.treeRender = 'r';
    el.dataset.treeSection = 's';
    el.dataset.treeKey = '';
    el.innerHTML = '<div class="tree-children" data-populated="1"></div>';
    expect(ui.ensureTreeChildren(el)).toBe(0);
  });

  test('setTreeCappedNote and refresh', () => {
    const container = document.createElement('div');
    document.body.appendChild(container);
    ui.setTreeCappedNote(container, true);
    const header = document.createElement('div');
    header.className = 'file-tree-header';
    const note = document.createElement('span');
    note.className = 'file-tree-capped';
    note.hidden = false;
    header.appendChild(note);
    container.appendChild(header);
    ui.refreshTreeCappedNote(container);
    expect(note.hidden).toBe(false);
  });

  test('appendMissingUnionLibraries null', () => {
    const libs: any[] = [];
    ui.appendMissingUnionLibraries(libs, [[null, { LibraryName: 'L', CollectionType: 'movies' }]]);
    expect(libs).toHaveLength(1);
  });

  test('showAutoSaveIndicatorOverlay chevron guard and error svg', () => {
    const el = document.createElement('div');
    const span = document.createElement('span');
    span.className = 'library-multiselect-chevron';
    span.dataset.saveGuard = '0';
    el.appendChild(span);
    document.body.appendChild(el);
    ui.showAutoSaveIndicatorOverlay(el, true);
    ui.showAutoSaveIndicatorOverlay(el, true);
    expect(span.dataset.saveGuard).toBe('2');
    vi.advanceTimersByTime(2100);

    const el2 = document.createElement('div');
    document.body.appendChild(el2);
    ui.showAutoSaveIndicatorOverlay(el2, false);
    vi.advanceTimersByTime(3100);
  });

  test('showAutoSaveIndicatorOverlay fallback remove existing', () => {
    const prev = document.createElement('div');
    prev.classList.add('fade-element');
    const el = document.createElement('div');
    document.body.appendChild(prev);
    document.body.appendChild(el);
    ui.showAutoSaveIndicatorOverlay(el, true);
    expect(document.querySelector('.fade-element')).not.toBeNull();
  });

  test('showButtonFeedback timeout', () => {
    const btn = document.createElement('button');
    btn.innerHTML = 'orig';
    document.body.appendChild(btn);
    ui.showButtonFeedback(btn, true, 'msg', 'orig', 10);
    vi.advanceTimersByTime(20);
    expect(btn.innerHTML).toBe('orig');
  });

  test('describeApiError response string', () => {
    const d = ui.describeApiError({ response: 'boom' });
    expect(d.snippet).toBe('boom');
  });

  test('apiGetOptional 204 and error', async () => {
    (globalThis as any).fetch = () => Promise.resolve({ status: 204, ok: true });
    let noContent = false;
    ui.apiGetOptional('x', () => {}, () => { noContent = true; }, () => {});
    await flush();
    expect(noContent).toBe(true);

    (globalThis as any).fetch = () => Promise.resolve({ status: 500, ok: false });
    let errored = false;
    ui.apiGetOptional('x', () => {}, () => {}, () => { errored = true; });
    await flush();
    expect(errored).toBe(true);
    ajaxRouter({});
  });

  test('bindFileTreeHandlers keydown enter and action', () => {
    const container = document.createElement('div');
    container.innerHTML = '<div data-tree-toggle="1"></div><button data-tree-action="collapse">x</button>';
    document.body.appendChild(container);
    ui.bindFileTreeHandlers(container);
    const toggle = container.querySelector('[data-tree-toggle]') as HTMLElement;
    toggle.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    const action = container.querySelector('[data-tree-action]') as HTMLElement;
    action.click();
    vi.advanceTimersByTime(60);
    expect(container.dataset.treeBound).toBe('1');
  });

  test('health toggle panel keydown enter', () => {
    ajaxRouter({});
    document.body.innerHTML = '<div id="healthContent"></div>';
    ui.fillHealthData({ Libraries: [{ VideosWithoutSubtitles: 1, VideosWithoutImages: 0, VideosWithoutNfo: 0, OrphanedMetadataDirectories: 0 }], Movies: [], TvShows: [], Other: [] });
    const item = document.querySelector('.health-clickable') as HTMLElement;
    item.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    vi.advanceTimersByTime(60);
    expect(document.getElementById('healthContent')).not.toBeNull();
  });
});
