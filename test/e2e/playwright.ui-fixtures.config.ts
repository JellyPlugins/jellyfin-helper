import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  testMatch: /ui-primitives\.fixture\.spec\.ts/,
  timeout: 30_000,
  workers: 1,
  reporter: 'list',
  use: { screenshot: 'only-on-failure', trace: 'retain-on-failure' },
  projects: [
    { name: 'documentation', testMatch: /coverage-doc\.api\.spec\.ts/ },
    { name: 'chromium', use: { browserName: 'chromium' }, dependencies: ['documentation'] },
    { name: 'webkit', use: { browserName: 'webkit' }, dependencies: ['documentation'] },
  ],
});
