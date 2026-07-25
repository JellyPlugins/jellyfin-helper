/**
 * ArrIntegration tab: the reachability indicator reacts to the dropdown, and
 * the Compare button renders a comparison result. Requires configured Radarr
 * instances (the API specs leave a Mock Radarr configured).
 */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';

test('Arr tab: selecting an instance updates the reachability indicator', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'arr');

  const select = page.locator('#arrSelectRadarr');
  // If no Radarr configured, the tab shows a no-data container; skip gracefully.
  if (!(await select.count())) {
    test.skip(true, 'no Radarr instance configured in this run');
  }

  const status = page.locator('#arrStatusRadarr');
  // Changing the selection triggers a TestConnection; the status should end up
  // ok or error (never stuck) — against the mock it should be ok.
  await select.selectOption({ index: 0 });
  await expect(status).toHaveClass(/is-ok|is-error/, { timeout: 15_000 });
  await expect(status).toHaveClass(/is-ok/, { timeout: 15_000 });
});

test('Arr tab: Compare button renders a comparison card', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'arr');

  const compareBtn = page.locator('#btnCompareRadarr');
  if (!(await compareBtn.count())) {
    test.skip(true, 'no Radarr instance configured in this run');
  }

  const [resp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/ArrIntegration/Compare/Radarr'),
      { timeout: 20_000 },
    ),
    compareBtn.click(),
  ]);
  expect(resp.status()).toBeLessThan(500);

  // The result area shows a comparison card with sections.
  await expect(page.locator('#arrResult .arr-card, #arrResult .arr-section').first()).toBeVisible({
    timeout: 15_000,
  });
});
