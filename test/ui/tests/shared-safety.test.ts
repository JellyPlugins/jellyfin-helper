import { afterAll, beforeEach, describe, expect, test } from 'vitest';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

// Pins the exact behavior of code paths a review flagged as risky. Each case
// documents what the shipped code does today so a regression is caught, without
// changing production: the paths are safe by data-flow contract, not by luck.
let ui: UiBridge;
beforeEach(() => {
  ui = freshConfigPage();
});

afterAll(saveCoverage);

describe('file tree key decoding is crash-safe', () => {
  // resolveTreeNode decodes each segment under try/catch; a malformed percent
  // escape resolves to null instead of throwing, so the caller returns 0 nodes.
  test('an undecodable key resolves to null, never throws', () => {
    const tree = { name: 'root', children: {}, items: [] };
    expect(() => ui.resolveTreeNode(tree, '%zz')).not.toThrow();
    expect(ui.resolveTreeNode(tree, '%zz')).toBeNull();
  });

  test('a well-formed encoded key resolves through the children', () => {
    const leaf = { name: 'a b', children: {}, items: [] };
    const tree = { name: 'root', children: { 'a b': leaf }, items: [] };
    expect(ui.resolveTreeNode(tree, encodeURIComponent('a b'))).toBe(leaf);
  });

  // ensureTreeChildren only reaches its own key.split().map(decodeURIComponent)
  // after resolveTreeNode decoded the same key, so a bad key short-circuits to 0
  // before the second decode can run. Proven here through the real DOM path with
  // a registered tree, so the test exercises the registry lookup instead of
  // bailing out before it.
  test('ensureTreeChildren returns 0 for an undecodable node key without throwing', () => {
    document.body.innerHTML = ui.renderFileTree(
      {
        movies: ['/m/dir/a.mkv'],
        tvShows: [],
        music: [],
        books: [],
        other: [],
        rootPaths: { movies: ['/m'] },
      },
      'title',
    );
    const node = document.querySelector('.tree-node') as HTMLElement;
    expect(node).not.toBeNull();
    // Corrupt the key only after the tree was registered: the lookup now has to
    // decode it, which must fail closed instead of throwing.
    node.dataset.treeKey = '%zz';
    expect(() => ui.ensureTreeChildren(node)).not.toThrow();
    expect(ui.ensureTreeChildren(node)).toBe(0);
  });
});

describe('tree leaf icon marks directories', () => {
  // Health marks orphaned directories by appending a forward slash, so the leaf
  // renderer picks the folder icon from that suffix. Pin both branches.
  test('a path ending in / renders the folder icon', () => {
    const html = ui.renderTreeLeaf({ name: 'dir', fullPath: '/m/dir/', meta: null }, '<i>file</i>');
    expect(html).toContain('class="mi"');
    expect(html).not.toContain('<i>file</i>');
  });

  test('a plain file path keeps the section icon', () => {
    const html = ui.renderTreeLeaf({ name: 'f.mkv', fullPath: '/m/f.mkv', meta: null }, '<i>file</i>');
    expect(html).toContain('<i>file</i>');
  });
});

describe('getPathSegments tolerates its root argument', () => {
  test('an empty root list returns every segment', () => {
    expect(ui.getPathSegments('/a/b/c', [])).toEqual(['a', 'b', 'c']);
  });

  test('the longest matching root is stripped', () => {
    expect(ui.getPathSegments('/media/movies/x.mkv', ['/media', '/media/movies'])).toEqual(['x.mkv']);
  });

  test('an empty full path returns no segments', () => {
    expect(ui.getPathSegments('', ['/media'])).toEqual([]);
  });
});

describe('escapers coerce their input before escaping', () => {
  // The shipped escapers run (s || '') / String(s || ''), so a falsy numeric 0
  // becomes an empty string while the string "0" survives. Pin both so a future
  // change to the coercion is caught deliberately.
  test('escAttr blanks numeric 0 but keeps string "0"', () => {
    expect(ui.escAttr(0)).toBe('');
    expect(ui.escAttr('0')).toBe('0');
  });

  test('escAttr escapes the dangerous attribute characters', () => {
    expect(ui.escAttr('a"b<c\'')).toBe('a&quot;b&lt;c&#39;');
  });

  test('escHtml blanks numeric 0 but escapes markup in strings', () => {
    expect(ui.escHtml(0)).toBe('');
    expect(ui.escHtml('<b>&')).toBe('&lt;b&gt;&amp;');
  });

  test('allowSafeHtml blanks numeric 0 and keeps only the strong/br allowlist', () => {
    expect(ui.allowSafeHtml(0)).toBe('');
    expect(ui.allowSafeHtml('<strong>x</strong><script>y</script>'))
      .toBe('<strong>x</strong>&lt;script&gt;y&lt;/script&gt;');
  });
});

describe('library union dedup keys never hit Object.prototype', () => {
  // appendMissingUnionLibraries dedups by "name|type"; a library literally named
  // like a prototype member must still be appended exactly once.
  test('a library named "constructor" is appended once', () => {
    const libs: any[] = [];
    const groups = [
      [{ LibraryName: 'constructor', CollectionType: 'movies' }],
      [{ LibraryName: 'constructor', CollectionType: 'movies' }],
      null,
      undefined,
      [{ LibraryName: 'Shows', CollectionType: 'tvshows' }],
    ];
    ui.appendMissingUnionLibraries(libs, groups);
    expect(libs.filter((l) => l.LibraryName === 'constructor')).toHaveLength(1);
    expect(libs.filter((l) => l.LibraryName === 'Shows')).toHaveLength(1);
  });
});

describe('ensembleShareHtml treats the value as trusted markup', () => {
  // The name is escaped; the value is injected as-is because every caller passes
  // a numeric percentage from ensembleShareText or a pre-escaped token. Pin that
  // contract: the name is escaped, the value is placed verbatim inside <strong>.
  test('escapes the name and wraps the value in strong', () => {
    const html = ui.ensembleShareHtml('k', 'A & B', ui.ensembleShareText(0.5));
    expect(html).toContain('A &amp; B');
    expect(html).toContain('<strong class="recs-ensemble-val">50%</strong>');
  });

  test('ensembleShareText clamps to the 0..100 range', () => {
    expect(ui.ensembleShareText(-1)).toBe('0%');
    expect(ui.ensembleShareText(2)).toBe('100%');
    expect(ui.ensembleShareText(0.333)).toBe('33%');
  });
});

describe('setTreeCappedNote visibility guard', () => {
  function containerWith(total: string, leaves: number) {
    const leafHtml = '<div class="tree-leaf"></div>'.repeat(leaves);
    document.body.innerHTML =
      '<div class="file-tree-container">' +
      `<div class="file-tree-header" data-tree-total="${total}">` +
      '<span class="file-tree-capped"></span></div>' +
      leafHtml +
      '</div>';
    return document.querySelector('.file-tree-container') as HTMLElement;
  }

  // When everything is already shown (shown >= total) the note hides, so it
  // never claims files are still collapsed.
  test('hides once all files are shown', () => {
    const container = containerWith('3', 3);
    ui.setTreeCappedNote(container, true);
    expect((container.querySelector('.file-tree-capped') as HTMLElement).hidden).toBe(true);
  });

  // With files still collapsed the note appears with live counts.
  test('shows the live count while files remain collapsed', () => {
    const container = containerWith('10', 2);
    ui.setTreeCappedNote(container, true);
    const note = container.querySelector('.file-tree-capped') as HTMLElement;
    expect(note.hidden).toBe(false);
    expect(note.textContent).toContain('2');
    expect(note.textContent).toContain('10');
  });

  // show=false always hides regardless of totals.
  test('hides on an explicit collapse signal', () => {
    const container = containerWith('10', 2);
    ui.setTreeCappedNote(container, false);
    expect((container.querySelector('.file-tree-capped') as HTMLElement).hidden).toBe(true);
  });
});
