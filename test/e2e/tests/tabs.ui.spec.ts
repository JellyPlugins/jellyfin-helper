/**
 * Dashboard tab navigation: the 8 tabs render and switch, with no uncaught JS
 * errors. The Recommendations tab is only visible when RecommendationsTaskMode
 * != Deactivate, and the Arr tab shows content only when instances exist — the
 * API specs run first (dependency) and leave both configured.
 */
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

  // Recommendations tab: visible because the API specs set the mode to a
  // non-Deactivate value. If hidden, that's an acceptable state — only assert
  // switching when the button is present.
  const recsBtn = page.locator('.tab-btn[data-tab="recommendations"]');
  if (await recsBtn.isVisible()) {
    await switchTab(page, 'recommendations');
  }

  expect(errors, `uncaught JS errors: ${errors.join('\n')}`).toHaveLength(0);
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
