import { afterAll, describe, expect, test } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);


describe('formatBytes', () => {
  test.each([
    [0, '0 B'],
    [-1, '0 B'],
    [NaN, '0 B'],
    [Infinity, '0 B'],
    [1, '1.00 B'],
    [1023, '1023.00 B'],
    [1024, '1.00 KB'],
    [1536, '1.50 KB'],
    [1048576, '1.00 MB'],
    [1073741824, '1.00 GB'],
  ])('formats %s as %s', (input, expected) => {
    expect(ui.formatBytes(input)).toBe(expected);
  });

  test('clamps beyond TB instead of inventing units', () => {
    expect(ui.formatBytes(Number.MAX_SAFE_INTEGER)).toContain('TB');
  });
});

describe('escHtml and escAttr', () => {
  test.each([
    ['<script>alert(1)</script>', '&lt;script&gt;alert(1)&lt;/script&gt;'],
    ['a"b\'c&d', 'a&quot;b&#39;c&amp;d'],
    ['plain', 'plain'],
    ['', ''],
  ])('escHtml escapes %s', (input, expected) => {
    expect(ui.escHtml(input)).toBe(expected);
  });

  test('escAttr escapes quotes for attribute sinks', () => {
    expect(ui.escAttr('a"b<c')).toBe('a&quot;b&lt;c');
  });

  test('escHtml stringifies nullish input', () => {
    expect(ui.escHtml(null)).toBe('');
    expect(ui.escHtml(undefined)).toBe('');
  });
});

describe('allowSafeHtml keeps only strong and br', () => {
  test('restores the trusted tags after escaping', () => {
    expect(ui.allowSafeHtml('Click <strong>x</strong> now<br>next')).toBe(
      'Click <strong>x</strong> now<br>next',
    );
  });

  test.each([
    ['<script>alert(1)</script>', '<img src=x onerror=y>', '<a href="javascript:z">t</a>', '<div>hi</div>'],
  ])('never restores %s', (input) => {
    expect(ui.allowSafeHtml(input)).not.toContain('<script');
    expect(ui.allowSafeHtml(input)).toContain('&lt;');
  });

  test('nested markup inside strong stays escaped', () => {
    const out = ui.allowSafeHtml('<strong><em>x</em></strong>');
    expect(out).toBe('<strong>&lt;em&gt;x&lt;/em&gt;</strong>');
  });
});

describe('getScoreThresholdClass boundaries', () => {
  test.each([
    [80, 'high'],
    [79.9, 'mid'],
    [50, 'mid'],
    [49.9, 'low'],
    [0, 'low'],
  ])('score %s maps correctly', (score, cls) => {
    expect(ui.getScoreThresholdClass(score, 'high', 'mid', 'low')).toBe(cls);
  });
});

describe('getPathSegments', () => {
  test('strips the longest matching root', () => {
    expect(ui.getPathSegments('/media/Movies/f.mkv', ['/media', '/media/Movies'])).toEqual(['f.mkv']);
  });

  test('handles windows separators and empty input', () => {
    expect(ui.getPathSegments('C:\\media\\f.mkv', ['C:\\media'])).toEqual(['f.mkv']);
    expect(ui.getPathSegments('', ['/media'])).toEqual([]);
  });

  test('keeps the full path when no root matches', () => {
    expect(ui.getPathSegments('/other/f.mkv', ['/media'])).toEqual(['other', 'f.mkv']);
  });
});
