/**
 * ArrIntegration tab: the reachability indicator reacts to the dropdown, and
 * the Compare button renders a comparison result. Requires configured Radarr
 * instances - we set them up here via the API rather than relying on whatever
 * state a prior api spec happened to leave (hardening.api.spec.ts clears
 * RadarrInstances, so relying on leftover state made these tests skip).
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';
import { apiContext, loadAuth, p } from '../setup/api-client.ts';

const ARR_URL = process.env.MOCK_ARR_URL ?? 'http://mock-arr:9000';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
  // Guarantee a reachable Mock Radarr instance exists for the UI to drive.
  const seed = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      RadarrInstances: [{ Name: 'Mock Radarr', Url: ARR_URL, ApiKey: 'radarr-key' }],
      SonarrInstances: [{ Name: 'Mock Sonarr', Url: ARR_URL, ApiKey: 'sonarr-key' }],
    },
  });
  expect(seed.ok(), `Arr seed failed: ${seed.status()}`).toBeTruthy();
});
test.afterAll(async () => {
  await ctx.dispose();
});

test('Arr tab: selecting an instance updates the reachability indicator', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'arr');

  const select = page.locator('#arrSelectRadarr');
  // The beforeAll guarantees a Radarr instance, so the selector must exist -
  // its absence is now a real failure, not a skip.
  await expect(select).toBeVisible({ timeout: 15_000 });

  const status = page.locator('#arrStatusRadarr');
  // Changing the selection triggers a TestConnection; against the mock the status
  // must reach is-ok. A single assertion both proves the indicator left the pending
  // state and fails fast (within one 15s window) if it ends up is-error instead.
  await select.selectOption({ index: 0 });
  await expect(status).toHaveClass(/is-ok/, { timeout: 15_000 });
});

test('Arr tab: Compare button renders a comparison card', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'arr');

  const compareBtn = page.locator('#btnCompareRadarr');
  // Guaranteed by beforeAll - absence is a real failure now.
  await expect(compareBtn).toBeVisible({ timeout: 15_000 });

  const [resp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/ArrIntegration/Compare/Radarr'),
      { timeout: 20_000 },
    ),
    compareBtn.click(),
  ]);
  // Against the mock this must succeed, not merely avoid a 500.
  expect(resp.ok(), `Compare failed: ${resp.status()}`).toBeTruthy();

  // The result area shows a comparison card with sections.
  await expect(page.locator('#arrResult .arr-card, #arrResult .arr-section').first()).toBeVisible({
    timeout: 15_000,
  });
});
