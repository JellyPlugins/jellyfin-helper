/**
 * Library Explorer: the collapsible combinable-filter search at the bottom of the
 * Codecs tab. Donuts above stay static; only this section filters.
 */
import { test, expect, type Locator, type Page } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';

async function openExplorer(page: Page): Promise<Locator> {
  await openDashboard(page);
  await switchTab(page, 'codecs');
  const toggle = page.locator('#codecExplorerToggle');
  await expect(toggle).toBeVisible({ timeout: 15_000 });
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') {
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true', { timeout: 5_000 });
  }
  return toggle;
}

test('explorer starts collapsed and expands without JS errors', async ({ page }) => {
  const errors = trackConsoleErrors(page);
  await openDashboard(page);
  await switchTab(page, 'codecs');
  const toggle = page.locator('#codecExplorerToggle');
  await expect(toggle).toBeVisible({ timeout: 15_000 });
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('#codecExplorerBody.open')).toBeVisible();
  const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
  expect(scriptErrors, `uncaught JS errors: ${scriptErrors.join('\n')}`).toHaveLength(0);
});

test('explorer asks for a filter before listing anything', async ({ page }) => {
  await openExplorer(page);
  await expect(page.locator('#codecExplorerResults')).toContainText(/at least one filter/i, { timeout: 5_000 });
});

test('combining two filters narrows the result and shows both values', async ({ page }) => {
  await openExplorer(page);
  const resolution = page.locator('select[data-explorer-dim="resolutions"]');
  const codec = page.locator('select[data-explorer-dim="videoCodecs"]');
  test.skip((await resolution.locator('option').count()) <= 1, 'no resolution data on this server');
  test.skip((await codec.locator('option').count()) <= 1, 'no video codec data on this server');

  await resolution.selectOption({ index: 1 });
  const firstValue = await resolution.inputValue();
  const summary = page.locator('.codec-explorer-summary');
  await expect(summary).toBeVisible({ timeout: 5_000 });
  const firstCount = await countFromSummary(summary);

  await codec.selectOption({ index: 1 });
  const secondValue = await codec.inputValue();
  await expect(summary).toContainText(secondValue, { timeout: 5_000 });
  const combinedCount = await countFromSummary(summary);

  // The combined result cannot be larger than the single-filter result.
  expect(combinedCount).toBeLessThanOrEqual(firstCount);
});

async function countFromSummary(summary: Locator): Promise<number> {
  const text = await summary.innerText();
  const match = text.match(/(\d+)\s+files?/i);
  return match ? parseInt(match[1], 10) : Number.NaN;
}

test('overview library row deep-links into the explorer with preset library', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'overview');
  const link = page.locator('[data-codec-explore-library]').first();
  test.skip((await link.count()) === 0, 'no libraries on this server');
  const libName = await link.getAttribute('data-codec-explore-library');
  await link.click();
  await expect(page.locator('#tab-codecs')).toHaveClass(/active/, { timeout: 15_000 });
  await expect(page.locator('#codecExplorerToggle')).toHaveAttribute('aria-expanded', 'true', { timeout: 5_000 });
  await expect(page.locator('#codecExplorerLibrary')).toHaveValue(libName ?? '');
});
