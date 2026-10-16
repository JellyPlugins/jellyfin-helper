import { defineConfig } from 'vitest/config';

// Coverage is measured by the istanbul-instrumenting loader (src/loader.ts)
// and gated by scripts/check-coverage.mjs, because the suite evaluates the
// shipped classic scripts as one bundle instead of importing ES modules.
export default defineConfig({
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.test.ts'],
    setupFiles: ['src/setup.ts'],
    pool: 'forks',
    poolOptions: { forks: { singleFork: true } },
    maxWorkers: 1,
    testTimeout: 15000,
  },
});
