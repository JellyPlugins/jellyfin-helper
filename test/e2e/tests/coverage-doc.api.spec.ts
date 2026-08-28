/** * Documentation drift guard - every e2e spec must be documented in COVERAGE.md. * * COVERAGE.md is the human-facing map of what the harness verifies. */
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
