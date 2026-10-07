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
      // Re-applies the Discovery config the api project overwrites, so the ui phase sees
      // DiscoveryUserAccessEnabled=true + a configured Seerr. Depends on 'api' so Playwright
      // GUARANTEES this runs after every api config write - declaration order alone does not,
      // and with workers > 1 an api PUT could otherwise land after this setup and restore the
      // 403 this phase exists to prevent.
      name: 'ui-setup',
      testMatch: /_ui-setup\.ts/,
      use: { ...devices['Desktop Chrome'] },
      dependencies: ['api'],
    },
    {
      name: 'ui',
      testMatch: /\.ui\.spec\.ts/,
      use: { ...devices['Desktop Chrome'] },
      // Ordered after api via the ui-setup dependency chain (api -> ui-setup -> ui), so the
      // Discovery reset always lands after api's config writes regardless of worker count.
      dependencies: ['ui-setup'],
    },
  ],
});
