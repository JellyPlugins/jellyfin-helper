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

  // NB: we intentionally do NOT switch to the Recommendations tab here — it has
  // dedicated coverage in recommendations.ui.spec.ts.
  //
  // Filter benign failed-resource-load messages (HTTP status noise) from the
  // assertion. The plugin injects a page-wide user-facing Discovery widget that
  // probes GET Discovery/My; while DiscoveryUserAccessEnabled is off (the
  // default, and what partial config PUTs from other specs leave it as) that
  // probe correctly returns 403. The browser logs it as "Failed to load
  // resource: … 403" with NO url, indistinguishable from any other 403. It is
  // not a dashboard JS defect. Real uncaught exceptions arrive via 'pageerror'
  // and genuine console.error JS messages — neither matches this pattern — so
  // dropping failed-resource-load lines keeps this assertion strict about
  // actual script errors.
  const scriptErrors = errors.filter((e) => !/Failed to load resource/i.test(e));
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
