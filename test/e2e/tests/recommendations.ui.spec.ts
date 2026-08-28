/** * Recommendations tab: the per-user selector drives per-user data loads, and * the collapsible sections open. */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';
import { apiContext, loadAuth, p } from '../setup/api-client.ts';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
  const seed = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { RecommendationsTaskMode: 'DryRun' },
  });
  expect(seed.ok(), `RecommendationsTaskMode seed failed: ${seed.status()}`).toBeTruthy();
});
test.afterAll(async () => {
  await ctx.dispose();
});

test('Recommendations tab: user selector loads per-user data; sections toggle', async ({ page }) => {
  await openDashboard(page);

  // The tab must be visible now (beforeAll set a non-Deactivate mode).
  const recsBtn = page.locator('.tab-btn[data-tab="recommendations"]');
  await expect(recsBtn).toBeVisible({ timeout: 15_000 });

  // Opening the tab auto-loads the initial user's data: initRecommendationsTab calls onUserChanged(initialIdx) -> GET Recommendations/WatchProfile/{userId} (Recommendations.js).
  const [profileResp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/Recommendations/WatchProfile/'),
      { timeout: 20_000 },
    ),
    switchTab(page, 'recommendations'),
  ]);
  // WatchProfile is loaded for the initial user. With a non-Deactivate mode set in beforeAll, the request must not be gated (503) or malformed (400): it either returns the profile (200) or, for a user with genuinely no watch history, a documented 404.
  expect([200, 404], `WatchProfile status ${profileResp.status()}`).toContain(
    profileResp.status(),
  );

  // The user <select> is populated (at least the current user).
  const userSelect = page.locator('#recsUserSelect');
  await expect(userSelect).toBeVisible({ timeout: 15_000 });
  expect(await userSelect.locator('option').count()).toBeGreaterThan(0);

  // Selecting a DIFFERENT user (if more than one exists) must fire a fresh
  // WatchProfile load via the change handler.
  if ((await userSelect.locator('option').count()) > 1) {
    const [changeResp] = await Promise.all([
      page.waitForResponse(
        (r) => r.url().includes('/JellyfinHelper/Recommendations/WatchProfile/'),
        { timeout: 15_000 },
      ),
      userSelect.selectOption({ index: 1 }),
    ]);
    expect([200, 404], `WatchProfile status ${changeResp.status()}`).toContain(changeResp.status());
  }

  // Toggle the recommendations grid section open.
  const gridToggle = page.locator('#recsGridToggle');
  if (await gridToggle.count()) {
    await gridToggle.click();
    await expect(page.locator('#recsGridBody')).toHaveClass(/open/, { timeout: 5000 });
  }
});
