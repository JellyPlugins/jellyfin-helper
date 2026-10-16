import { afterAll, describe, expect, test } from 'vitest';
import { loadConfigPage, saveCoverage } from '../src/loader.js';

const ui = loadConfigPage();

afterAll(saveCoverage);


describe('validateTrashPath', () => {
  test('disabled trash accepts anything', () => {
    expect(ui.validateTrashPath('', false)).toBeNull();
    expect(ui.validateTrashPath('/etc', false)).toBeNull();
  });

  test.each([[''], ['   '], [null], [undefined]])('rejects empty path %s', (p) => {
    expect(typeof ui.validateTrashPath(p, true)).toBe('string');
  });

  test.each([
    ['/media/a*b', 'glob char'],
    ['/media/a?b', 'glob char'],
    ['/media/a<b', 'redirect char'],
    ['/media/a|b', 'pipe'],
    ['/media/a"b', 'quote'],
    ['/media/a\x01b', 'control char'],
  ])('rejects invalid chars in %s', (p) => {
    expect(typeof ui.validateTrashPath(p, true)).toBe('string');
  });

  test.each([
    ['/etc', true],
    ['/etc/', true],
    ['/ETC/passwd', true],
    ['/etc2', false],
    ['/etc2/passwd', false],
    ['C:\\Windows', true],
    ['c:\\windows\\system32', true],
    ['C:\\Windows2', false],
    ['relative/path', false],
    ['/media/trash', false],
  ])('sensitive check for %s', (p, sensitive) => {
    expect(ui.isSensitiveSystemPath(p)).toBe(sensitive);
    if (sensitive) expect(typeof ui.validateTrashPath(p, true)).toBe('string');
  });

  test.each([
    ['/media/trash/', 'trailing slash'],
    ['/media//trash', 'double slash'],
    ['/media/./trash', 'dot segment'],
    ['/media/../trash', 'dotdot segment'],
    ['/', 'bare root'],
    ['C:\\', 'bare drive'],
  ])('rejects %s', (p) => {
    expect(typeof ui.validateTrashPath(p, true)).toBe('string');
  });

  test.each([['/media/.trash'], ['/media/my trash'], ['C:\\media\\trash'], ['\\\\srv\\share\\trash']])(
    'accepts %s',
    (p) => {
      expect(ui.validateTrashPath(p, true)).toBeNull();
    },
  );
});
