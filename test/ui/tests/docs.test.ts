import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, test } from 'vitest';

const DIR = dirname(fileURLToPath(import.meta.url));
const README = join(DIR, '..', 'README.md');
const SELF = 'docs.test.ts';

describe('every test file is documented in README.md', () => {
  test('no undocumented test files', () => {
    const files = readdirSync(DIR)
      .filter((f) => f.endsWith('.test.ts') && f !== SELF)
      .sort();
    expect(files.length).toBeGreaterThan(0);
    const readme = readFileSync(README, 'utf-8');
    expect(files.filter((f) => !readme.includes(f))).toEqual([]);
  });

  test('no phantom references', () => {
    const real = new Set(readdirSync(DIR).filter((f) => f.endsWith('.test.ts')));
    const readme = readFileSync(README, 'utf-8');
    const phantom = [...readme.matchAll(/`((?:tests\/)?[^`]*\.test\.ts)`/g)]
      .map((m) => m[1].replace(/^tests\//, ''))
      .filter((n) => !n.includes('*') && !real.has(n));
    expect(phantom).toEqual([]);
  });
});
