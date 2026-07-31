/**
 * Documentation drift guard - every e2e spec must be documented in COVERAGE.md.
 *
 * COVERAGE.md is the human-facing map of what the harness verifies. It silently
 * rots the moment someone adds a `*.spec.ts` without a matching entry: the file
 * runs in CI but nobody reading the coverage doc knows it exists. This test makes
 * that failure loud - add a spec, you must mention its filename in COVERAGE.md.
 *
 * Pure filesystem: needs no Jellyfin stack, no auth, no network. It reads the
 * sibling `tests/` directory and the repo `COVERAGE.md`.
 *
 * To satisfy it when you add a spec: reference the exact filename (e.g.
 * `my-feature.api.spec.ts`) somewhere in COVERAGE.md - typically a `→ file.spec.ts`
 * section heading plus a bullet or two describing what it asserts.
 */
import { test, expect } from '@playwright/test';
import { readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const TESTS_DIR = __dirname;
const COVERAGE_PATH = join(__dirname, '..', 'COVERAGE.md');

// This drift guard itself is harness infrastructure, not feature coverage - it
// must not require documenting itself in COVERAGE.md.
const SELF = 'coverage-doc.api.spec.ts';

test('every *.spec.ts is documented in COVERAGE.md', () => {
  const specFiles = readdirSync(TESTS_DIR)
    .filter((f) => f.endsWith('.spec.ts'))
    .filter((f) => f !== SELF)
    .sort();

  // Sanity: the guard is worthless if it can't see the specs it is meant to guard.
  expect(specFiles.length, 'no *.spec.ts files discovered - path resolution broke').toBeGreaterThan(0);

  const coverage = readFileSync(COVERAGE_PATH, 'utf-8');

  const undocumented = specFiles.filter((f) => !coverage.includes(f));

  expect(
    undocumented,
    `these spec files are not referenced in COVERAGE.md - add a section/bullet for each:\n` +
      undocumented.map((f) => `  - ${f}`).join('\n'),
  ).toEqual([]);
});
