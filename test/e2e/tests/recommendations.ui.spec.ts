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
      ),
      userSelect.selectOption({ index: 0 }),
    ]);
    // The request must actually fire and return a documented status. A user with
    // no watch history legitimately yields 404/503; a 200 returns the profile.
    // Anything else (esp. 5xx) is a real failure.
    expect([200, 400, 404, 503], `WatchProfile status ${profileResp.status()}`).toContain(
      profileResp.status(),
    );
  }

  // Toggle the recommendations grid section open.
  const gridToggle = page.locator('#recsGridToggle');
  if (await gridToggle.count()) {
    await gridToggle.click();
    await expect(page.locator('#recsGridBody')).toHaveClass(/open/, { timeout: 5000 });
  }
});
