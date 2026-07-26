/**
 * Recommendations tab: the per-user selector drives per-user data loads, and
 * the collapsible sections open. The tab button is hidden when
 * RecommendationsTaskMode == Deactivate, so we set a non-Deactivate mode here
 * via the API (DryRun — no side effects) rather than relying on leftover state
 * from a prior api spec (which left it Deactivate, making this test skip).
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';
import { apiContext, loadAuth, p } from '../setup/api-client.ts';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { RecommendationsTaskMode: 'DryRun' },
  });
});
test.afterAll(async () => {
  await ctx.dispose();
});

test('Recommendations tab: user selector loads per-user data; sections toggle', async ({ page }) => {
  await openDashboard(page);

  // The tab must be visible now (beforeAll set a non-Deactivate mode).
  const recsBtn = page.locator('.tab-btn[data-tab="recommendations"]');
  await expect(recsBtn).toBeVisible({ timeout: 15_000 });

  // Opening the tab auto-loads the initial user's data: initRecommendationsTab
  // calls onUserChanged(initialIdx) → GET Recommendations/WatchProfile/{userId}
  // (Recommendations.js). Arm the wait BEFORE switching so we catch that request
  // — the <select> has no placeholder option, so re-selecting index 0 emits no
  // 'change' event and would fire nothing.
  const [profileResp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/Recommendations/WatchProfile/'),
      { timeout: 20_000 },
    ),
    switchTab(page, 'recommendations'),
  ]);
  // A user with no watch history legitimately yields 404/503; 200 returns the
  // profile. Anything else (esp. 5xx) is a real failure.
  expect([200, 400, 404, 503], `WatchProfile status ${profileResp.status()}`).toContain(
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
    expect([200, 400, 404, 503]).toContain(changeResp.status());
  }

  // Toggle the recommendations grid section open.
  const gridToggle = page.locator('#recsGridToggle');
  if (await gridToggle.count()) {
    await gridToggle.click();
    await expect(page.locator('#recsGridBody')).toHaveClass(/open/, { timeout: 5000 });
  }
});
