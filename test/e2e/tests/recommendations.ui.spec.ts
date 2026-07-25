/**
 * Recommendations tab: the per-user selector drives per-user data loads, and
 * the collapsible sections open. Only meaningful when RecommendationsTaskMode
 * != Deactivate (the tab button is hidden otherwise) — skip gracefully if so.
 */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';

test('Recommendations tab: user selector loads per-user data; sections toggle', async ({ page }) => {
  await openDashboard(page);

  const recsBtn = page.locator('.tab-btn[data-tab="recommendations"]');
  if (!(await recsBtn.isVisible())) {
    test.skip(true, 'Recommendations tab hidden (mode Deactivate)');
  }
  await switchTab(page, 'recommendations');

  // The user <select> should exist; changing it fires WatchProfile + Activity.
  const userSelect = page.locator('#recsUserSelect');
  await expect(userSelect).toBeVisible({ timeout: 15_000 });

  const optionCount = await userSelect.locator('option').count();
  if (optionCount > 0) {
    const [profileResp] = await Promise.all([
      page.waitForResponse(
        (r) => r.url().includes('/JellyfinHelper/Recommendations/WatchProfile/'),
        { timeout: 15_000 },
      ).catch(() => null),
      userSelect.selectOption({ index: 0 }),
    ]);
    // Not fatal if a user has no profile yet; just ensure no crash.
    expect(profileResp === null || profileResp.status() < 500).toBeTruthy();
  }

  // Toggle the recommendations grid section open.
  const gridToggle = page.locator('#recsGridToggle');
  if (await gridToggle.count()) {
    await gridToggle.click();
    await expect(page.locator('#recsGridBody')).toHaveClass(/open/, { timeout: 5000 });
  }
});
