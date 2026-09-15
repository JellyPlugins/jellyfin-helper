/** * Dashboard tab navigation: the 8 tabs render and switch, with no uncaught JS * errors. */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';

// data-tab values (NOT the same as labels): arr = "ArrIntegration".
const ALWAYS_TABS = ['statistics', 'health', 'trends', 'settings', 'arr', 'logs'];

test('all core tabs switch and activate without JS errors', async ({ page }) => {
  const errors = trackConsoleErrors(page);
  await openDashboard(page);

  for (const tab of ALWAYS_TABS) {
    await switchTab(page, tab);
    // The active button + panel share the tab id.
    await expect(page.locator(`.tab-btn[data-tab="${tab}"]`)).toHaveClass(/active/);
  }

  // NB: we intentionally do NOT switch to the Recommendations tab here - it has dedicated coverage in recommendations.ui.spec.ts.
  const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
  expect(scriptErrors, `uncaught JS errors: ${scriptErrors.join('\n')}`).toHaveLength(0);
});

test('statistics renders KPI strip after scan', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'statistics');
  // After the global-setup scan, the statistics tab should have content (KPI strip
  // or a library table). Wait for either to appear.
  await expect(
    page.locator('#statisticsContent .stat-kpi-card, #statisticsContent .library-table').first(),
  ).toBeVisible({ timeout: 20_000 });
});

test('a transient auth failure on refresh is retried, not shown as a stuck admin error', async ({ page }) => {
  // Regression guard for the refresh race: on reload the stats read can fire before
  // Jellyfin's ApiClient token is ready and come back 401/403. The plugin must retry
  // the idempotent Latest read instead of leaving the admin an error banner. We force
  // the failure deterministically: the first two MediaStatistics/Latest requests are
  // answered 403, the rest pass through. Without the retry this leaves the banner
  // stuck; with it, the banner clears and the overview populates.
  let latestHits = 0;
  await page.route('**/JellyfinHelper/MediaStatistics/Latest', async (route) => {
    latestHits += 1;
    if (latestHits <= 2) {
      await route.fulfill({ status: 403, contentType: 'application/json', body: '{}' });
      return;
    }
    await route.continue();
  });

  await openDashboard(page);

  const errBanner = page.locator('#statisticsContent .error-msg', { hasText: /administrator/i });

  // The forced 403s must have been consumed (proves the retry actually re-requested).
  await expect.poll(() => latestHits, { timeout: 20_000 }).toBeGreaterThan(2);
  // After the retries resolve, no stuck admin-error banner.
  await expect(errBanner).toBeHidden({ timeout: 20_000 });
  // And the statistics tab ultimately shows real content, proving stats loaded post-retry.
  await expect(
    page.locator('#statisticsContent .stat-kpi-card, #statisticsContent .library-table').first(),
  ).toBeVisible({ timeout: 20_000 });
});
