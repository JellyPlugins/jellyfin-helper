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
  // Filter ONLY the benign Discovery/My 403 probe noise — not every failed-resource
  // message. The plugin injects a page-wide user-facing Discovery widget that probes
  // GET Discovery/My; while DiscoveryUserAccessEnabled is off (the default, and what
  // partial config PUTs from other specs leave it as) that probe correctly returns
  // 403, logged as "Failed to load resource: … 403" with NO url. It is not a dashboard
  // JS defect. We match on the 403 status specifically so any OTHER broken asset (a
  // 404'd script/CSS/icon) still fails this assertion. Real uncaught exceptions arrive
  // via 'pageerror' / genuine console.error messages, which don't match this pattern.
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
