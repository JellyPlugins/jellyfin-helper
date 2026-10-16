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

const vitest = spawnSync('npx', ['vitest', 'run'], { stdio: 'inherit', shell: true });
const gate = spawnSync(
  'node',
  ['scripts/check-coverage.mjs', '--lines=85', '--branches=80'],
  { stdio: 'inherit', shell: true },
);

if (vitest.status !== 0) process.exit(vitest.status);
process.exit(gate.status ?? 1);
