// Runs vitest and always merges coverage afterwards, so a failing suite
// still produces lcov.info for Codecov. Exits non-zero if either step fails.
import { spawnSync } from 'node:child_process';
import { rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const TMP = join(HERE, '..', 'coverage', 'tmp');

// Clear stale per-file reports so a rerun in the same checkout measures only the
// current run. Merged counts are binary per statement for the gate, but stale
// reports could keep the number up after the current tests cover less.
rmSync(TMP, { recursive: true, force: true });

// Spawn the local binaries via this Node executable with absolute paths and no
// shell: nothing is resolved through PATH, so an attacker-writable PATH entry
// cannot substitute a different vitest/check script.
const NODE = process.execPath;
const VITEST_BIN = join(HERE, '..', 'node_modules', 'vitest', 'vitest.mjs');

const vitest = spawnSync(NODE, [VITEST_BIN, 'run'], { stdio: 'inherit' });
const gate = spawnSync(
  NODE,
  [join(HERE, 'check-coverage.mjs'), '--lines=85', '--branches=80'],
  { stdio: 'inherit' },
);

if (vitest.status !== 0) process.exit(vitest.status);
process.exit(gate.status ?? 1);
