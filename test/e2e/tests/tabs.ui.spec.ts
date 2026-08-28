/** * Dashboard tab navigation: the 8 tabs render and switch, with no uncaught JS * errors. */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';

// data-tab values (NOT the same as labels): arr = "ArrIntegration".
const ALWAYS_TABS = ['overview', 'codecs', 'health', 'trends', 'settings', 'arr', 'logs'];

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

test('overview renders stat cards after scan', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'overview');
  // After the global-setup scan, the overview should have content (stat cards
  // or a library table). Wait for either to appear.
  await expect(
    page.locator('#overviewContent .stat-card, #overviewContent .library-table').first(),
  ).toBeVisible({ timeout: 20_000 });
});
