// Runs vitest and always merges coverage afterwards, so a failing suite
// still produces lcov.info for Codecov. Exits non-zero if either step fails.
import { spawnSync } from 'node:child_process';

const vitest = spawnSync('npx', ['vitest', 'run'], { stdio: 'inherit', shell: true });
const gate = spawnSync(
  'node',
  ['scripts/check-coverage.mjs', '--lines=85', '--branches=80'],
  { stdio: 'inherit', shell: true },
);

if (vitest.status !== 0) process.exit(vitest.status);
process.exit(gate.status ?? 1);
