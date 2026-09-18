import { defineConfig, devices } from '@playwright/test';

/**
 * E2E config for the Jellyfin Helper plugin. * * The stack (Jellyfin 12 + mock Arr/Seerr) is brought up by scripts/run.sh * BEFORE Playwright starts.
 */

const JELLYFIN_URL = process.env.JELLYFIN_URL ?? 'http://localhost:8096';

export default defineConfig({
  testDir: './tests',
  // Tasks + scans are slow; give each test room but keep the suite bounded.
  timeout: 90_000,
  expect: { timeout: 15_000 },
  // Server state is shared/mutable (config, library), so run serially by default.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  globalSetup: './setup/global-setup.ts',

  reporter: [
    ['list'],
    ['html', { outputFolder: 'playwright-report', open: 'never' }],
    ['junit', { outputFile: 'test-results/junit.xml' }],
  ],

  use: {
    baseURL: JELLYFIN_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    // Jellyfin's self-signed/dev setup: be lenient.
    ignoreHTTPSErrors: true,
  },

  projects: [
    {
      name: 'api',
      testMatch: /\.api\.spec\.ts/,
    },
    {
      name: 'ui',
      testMatch: /\.ui\.spec\.ts/,
      use: { ...devices['Desktop Chrome'] },
      // No dependencies entry on purpose: with workers:1 the api project still runs
      // first (declaration order), but a single api failure no longer marks the whole
      // ui project "did not run" - cascading skips hid the real signal. UI specs only
      // rely on global-setup state (libraries, seeded timeline), not on api specs.
    },
  ],
});
