import { afterAll, describe, expect, test } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);

describe('collectFlatPaths and collectDictPaths', () => {
  const libs = [
    { P: ['/a', '/b'], D: { h264: ['/a'] } },
    { P: null, D: {} },
    { Nope: ['/x'] },
  ];

  test('flattens across libraries and skips missing props', () => {
    expect(ui.collectFlatPaths(libs, 'P')).toEqual(['/a', '/b']);
    expect(ui.collectFlatPaths(null, 'P')).toEqual([]);
    expect(ui.collectFlatPaths(libs, 'Missing')).toEqual([]);
  });

  test('picks one dictionary key across libraries', () => {
    expect(ui.collectDictPaths(libs, 'D', 'h264')).toEqual(['/a']);
    expect(ui.collectDictPaths(libs, 'D', 'hevc')).toEqual([]);
    expect(ui.collectDictPaths(null, 'D', 'h264')).toEqual([]);
  });
});

describe('aggregateDict', () => {
  test('sums numeric values per key', () => {
    expect(ui.aggregateDict([{ C: { a: 1, b: 2 } }, { C: { a: 3 } }], 'C')).toEqual({ a: 4, b: 2 });
  });

  test('ignores missing dictionaries', () => {
    expect(ui.aggregateDict([{}, { C: null }], 'C')).toEqual({});
  });
});

describe('buildPathTree and countTreeItems', () => {
  test('nests by segments and counts recursively', () => {
    const tree = ui.buildPathTree(['/media/Movies/a.mkv', '/media/Movies/b.mkv', '/media/Music/s.mp3'], ['/media']);
    expect(Object.keys(tree.children).sort()).toEqual(['Movies', 'Music']);
    expect(ui.countTreeItems(tree)).toBe(3);
    expect(ui.countTreeItems(tree.children.Movies)).toBe(2);
  });

  test('leaf without segments keeps the full path as name', () => {
    const tree = ui.buildPathTree(['lonely'], []);
    expect(tree.items).toEqual([{ name: 'lonely', fullPath: 'lonely', meta: null }]);
  });

  test('meta attaches per-file labels', () => {
    const tree = ui.buildPathTree(['/m/a.mkv'], ['/m'], { '/m/a.mkv': '1920x800' });
    expect(tree.items[0].meta).toBe('1920x800');
  });

  test('empty input yields an empty root', () => {
    const tree = ui.buildPathTree([], []);
    expect(ui.countTreeItems(tree)).toBe(0);
  });
});

describe('encodeTreeKey and resolveTreeNode', () => {
  test('round-trips segments with separators and unicode', () => {
    const tree = ui.buildPathTree(['/m/A B/c.mkv', '/m/A%s/d.mkv'], ['/m']);
    for (const key of [ui.encodeTreeKey(['A B']), ui.encodeTreeKey(['A%s'])]) {
      const node = ui.resolveTreeNode(tree, key);
      expect(node && node.items.length).toBe(1);
    }
  });

  test('empty key resolves the root, garbage resolves null', () => {
    const tree = ui.buildPathTree(['/m/a.mkv'], ['/m']);
    expect(ui.resolveTreeNode(tree, '')).toBe(tree);
    expect(ui.resolveTreeNode(tree, 'nope')).toBeNull();
    expect(ui.resolveTreeNode(tree, '%zz')).toBeNull();
  });
});

describe('renderFileTree', () => {
  const result = {
    movies: ['/m/a.mkv'],
    tvShows: ['/t/s01e01.mkv'],
    music: [],
    books: [],
    other: [],
    rootPaths: { movies: ['/m'], tvShows: ['/t'], music: [], books: [], other: [] },
  };

  test('empty result renders the empty state', () => {
    const html = ui.renderFileTree({ movies: [], tvShows: [], other: [], rootPaths: {} }, 'T');
    expect(html).toContain('file-tree-empty');
  });

  test('sections render with counts and total', () => {
    const html = ui.renderFileTree(result, 'Title <b>');
    expect(html).toContain('data-tree-total="2"');
    expect(html).toContain('(1)');
    expect(html).toContain('Title &lt;b&gt;');
    expect(html).toContain('file-tree-multi');
  });

  test('single section omits the multi class', () => {
    const html = ui.renderFileTree({ movies: ['/m/a.mkv'], rootPaths: { movies: ['/m'] } }, 'T');
    expect(html).not.toContain('file-tree-multi');
  });

  test('directory leaves keep the folder icon', () => {
    const dir = ui.renderTreeLeaf({ name: 'd', fullPath: '/m/d/', meta: null }, 'SECTIONICON');
    const file = ui.renderTreeLeaf({ name: 'f', fullPath: '/m/f', meta: null }, 'SECTIONICON');
    expect(dir).not.toContain('SECTIONICON');
    expect(file).toContain('SECTIONICON');
  });

  test('file leaves show meta and escaped names', () => {
    const html = ui.renderTreeLeaf({ name: 'a<b', fullPath: '/m/a', meta: '5.1' }, 'ICON');
    expect(html).toContain('a&lt;b');
    expect(html).toContain('5.1');
    expect(html).toContain('ICON');
  });
});

describe('bindFileTreeHandlers is idempotent', () => {
  test('second bind is a no-op', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    ui.bindFileTreeHandlers(c);
    ui.bindFileTreeHandlers(c);
    expect(c.dataset.treeBound).toBe('1');
  });

  test('null container does not throw', () => {
    expect(() => ui.bindFileTreeHandlers(null)).not.toThrow();
  });
});

describe('lazy tree expansion', () => {
  function treeDom() {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    c.innerHTML = ui.renderFileTree(
      { movies: ['/m/d/f1.mkv', '/m/d/f2.mkv'], rootPaths: { movies: ['/m'] } },
      'T',
    );
    ui.bindFileTreeHandlers(c);
    return c;
  }

  test('folders start collapsed and materialize on toggle', () => {
    const c = treeDom();
    expect(c.querySelector('.tree-leaf')).toBeNull();
    (c.querySelector('[data-tree-toggle]') as HTMLElement).click();
    const leaves = c.querySelectorAll('.tree-leaf');
    expect(leaves).toHaveLength(2);
    expect(c.textContent).toContain('f1.mkv');
  });

  test('expand-all fills everything and collapse closes', () => {
    const c = treeDom();
    ui.runTreeAction(c, 'expand', c);
    expect(c.querySelectorAll('.tree-leaf')).toHaveLength(2);
    ui.runTreeAction(c, 'collapse', c);
    expect(c.querySelector('.tree-node.tree-expanded')).toBeNull();
  });

  test('keyboard enter toggles folders', () => {
    const c = treeDom();
    const toggle = c.querySelector('[data-tree-toggle]') as HTMLElement;
    toggle.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(c.querySelector('.tree-leaf')).not.toBeNull();
  });

  test('toggle helpers tolerate garbage', () => {
    expect(() => ui.toggleTreeNode(null)).not.toThrow();
    expect(ui.ensureTreeChildren(null)).toBe(0);
    document.body.innerHTML = '<div id="c"></div>';
    expect(() => ui.collapseAllTreeNodes(document.getElementById('c')!)).not.toThrow();
  });
});

describe('normalizeStatisticsLibraries', () => {
  test('passes through array payloads untouched', () => {
    const data = { Libraries: [{ LibraryName: 'M' }] };
    expect(ui.normalizeStatisticsLibraries(data)).toBe(data);
  });

  test('rebuilds the union honoring LibraryOrder', () => {
    const data = {
      Movies: [{ LibraryName: 'B' }, { LibraryName: 'A' }],
      TvShows: [{ LibraryName: 'C' }],
      LibraryOrder: ['A', 'B'],
    } as any;
    const out = ui.normalizeStatisticsLibraries(data);
    expect(out.Libraries.map((l: any) => l.LibraryName)).toEqual(['A', 'B', 'C']);
  });

  test('dedups the same library across groups', () => {
    const lib = { LibraryName: 'A', CollectionType: 'movies' };
    const out = ui.normalizeStatisticsLibraries({ Movies: [lib], Other: [lib] }) as any;
    expect(out.Libraries).toEqual([lib]);
  });

  test('null input stays null', () => {
    expect(ui.normalizeStatisticsLibraries(null)).toBeNull();
  });
});

describe('pluralize and formatTimeAgo', () => {
  test('singular only for exactly one', () => {
    expect(ui.pluralize(1, 'sk', 'pk', 's', 'p')).toBe('s');
    expect(ui.pluralize(0, 'sk', 'pk', 's', 'p')).toBe('p');
    expect(ui.pluralize(2, 'sk', 'pk', 's', 'p')).toBe('p');
  });

  test('time buckets', () => {
    expect(ui.formatTimeAgo('')).toBe('');
    expect(ui.formatTimeAgo(new Date(Date.now() + 60000).toISOString())).toBe('');
    expect(ui.formatTimeAgo(new Date().toISOString())).toBe('just now');
    expect(ui.formatTimeAgo(new Date(Date.now() - 5 * 60000).toISOString())).toBe('5 min ago');
    expect(ui.formatTimeAgo(new Date(Date.now() - 3 * 3600000).toISOString())).toBe('3 hours ago');
    expect(ui.formatTimeAgo(new Date(Date.now() - 2 * 86400000).toISOString())).toBe('2 days ago');
  });
});

describe('describeApiError classifies without leaking bodies', () => {
  test.each([
    [null, 'network'],
    [{}, 'network'],
    [{ status: 401 }, 'unauthorized'],
    [{ status: 403 }, 'unauthorized'],
    [{ status: 404 }, 'notfound'],
    [{ status: 500 }, 'server'],
    [{ status: 200 }, 'unknown'],
  ])('status maps correctly', (err, kind) => {
    expect(ui.describeApiError(err).kind).toBe(kind);
  });

  test('long bodies are truncated', () => {
    const out = ui.describeApiError({ status: 500, responseText: 'x'.repeat(500) });
    expect(out.snippet.length).toBeLessThanOrEqual(201);
  });

  test('plain errors stringify', () => {
    expect(ui.describeApiError(new Error('boom')).snippet).toContain('boom');
  });
});

describe('dialog helpers', () => {
  test('overlay carries title and buttons', () => {
    const d = ui.createDialogOverlay('dlg1', 'Title', '#fff', 'Body');
    document.body.appendChild(d.overlay);
    expect(document.getElementById('dlg1')).not.toBeNull();
    expect(d.overlay.textContent).toContain('Title');
    const btn = ui.createDialogBtn('Ok', 'success', () => undefined);
    d.btnRow.appendChild(btn);
    expect(btn.textContent).toBe('Ok');
    ui.removeDialogById('dlg1');
    expect(document.getElementById('dlg1')).toBeNull();
  });

  test('removing a missing dialog is a no-op', () => {
    expect(() => ui.removeDialogById('nope')).not.toThrow();
  });

  test('cancel buttons stay transparent', () => {
    const cancel = ui.createDialogBtn('C', 'cancel', () => undefined);
    const danger = ui.createDialogBtn('D', 'danger', () => undefined);
    // jsdom silently drops `background: transparent` (an unsupported shorthand
    // value) from cssText, so a cancel button ends up with no background at all,
    // while a colored button keeps its concrete rgb value. The 1px border is the
    // other cancel-only marker that survives.
    expect(cancel.style.cssText).toContain('border: 1px solid');
    expect(cancel.style.cssText).not.toContain('background:');
    expect(danger.style.cssText).toContain('background: rgb(');
  });
});
