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
  await expect(summary).toContainText(firstValue);
  const firstCount = await countFromSummary(summary);

  await codec.selectOption({ index: 1 });
  const secondValue = await codec.inputValue();
  await expect(summary).toContainText(secondValue, { timeout: 5_000 });
  const combinedCount = await countFromSummary(summary);

  // The combined result cannot be larger than the single-filter result.
  expect(combinedCount).toBeLessThanOrEqual(firstCount);
});

async function countFromSummary(summary: Locator): Promise<number> {
  // Locale-proof: the machine-readable count rides along as a data attribute.
  const raw = await summary.locator('[data-explorer-count]').getAttribute('data-explorer-count');
  return raw === null ? Number.NaN : parseInt(raw, 10);
}

test('overview library row deep-links into the explorer with preset library', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'overview');
  const link = page.locator('#overviewContent .library-table [data-codec-explore-library]').first();
  test.skip((await link.count()) === 0, 'no libraries on this server');
  const libName = await link.getAttribute('data-codec-explore-library');
  await link.click();
  await expect(page.locator('#tab-codecs')).toHaveClass(/active/, { timeout: 15_000 });
  await expect(page.locator('#codecExplorerToggle')).toHaveAttribute('aria-expanded', 'true', { timeout: 5_000 });
  // The scope panel starts closed; the checked box underneath still proves the preset.
  await page.locator('[data-library-toggle]').click();
  // Match by input value, not by attribute selector: data-library-option is a
  // marker flag ("1") while the library name rides along in value.
  const options = page.locator('[data-library-option]');
  const values = await options.evaluateAll((els) => els.map((el) => (el as HTMLInputElement).value));
  expect(values).toContain(libName);
  await expect(options.nth(values.indexOf(libName ?? ''))).toBeChecked({ timeout: 5_000 });
});

test('overview movies card deep-links into the explorer with scoped libraries', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'overview');
  const card = page.locator('.stat-card-link[data-codec-explore-library="type:movies"]');
  test.skip((await card.count()) === 0, 'no movie libraries on this server');
  await card.first().click();
  await expect(page.locator('#tab-codecs')).toHaveClass(/active/, { timeout: 15_000 });
  await page.locator('[data-library-toggle]').click();
  await expect(page.locator('[data-library-option]:checked').first()).toBeChecked({ timeout: 5_000 });
});

test('language multi-dropdown selects several values and lists all in the summary', async ({ page }) => {
  await openExplorer(page);
  const toggle = page.locator('[data-multi-toggle="audioLanguages"]');
  test.skip((await toggle.count()) === 0, 'no audio language data on this server');
  test.skip(await toggle.isDisabled(), 'audio language options are all empty on this server');
  await toggle.click();
  const panel = page.locator('[data-multi-panel="audioLanguages"]');
  await expect(panel).toBeVisible({ timeout: 5_000 });
  const boxes = panel.locator('input[type="checkbox"]');
  test.skip((await boxes.count()) < 2, 'not enough audio languages on this server');
  await boxes.nth(0).check();
  await boxes.nth(1).check();
  const first = await boxes.nth(0).inputValue();
  const second = await boxes.nth(1).inputValue();
  const summary = page.locator('[data-multi-toggle="audioLanguages"] .codec-multi-summary');
  await expect(summary).toContainText(first, { timeout: 5_000 });
  await expect(summary).toContainText(second);
  const resultSummary = page.locator('.codec-explorer-summary');
  await expect(resultSummary).toContainText(first);
  await expect(resultSummary).toContainText(second);
});

test('reset clears filters and scope, showing the idle hint again', async ({ page }) => {
  await openExplorer(page);
  const resolution = page.locator('select[data-explorer-dim="resolutions"]');
  test.skip((await resolution.locator('option').count()) <= 1, 'no resolution data on this server');
  await resolution.selectOption({ index: 1 });
  await expect(page.locator('.codec-explorer-summary')).toBeVisible({ timeout: 5_000 });
  await page.locator('#codecExplorerReset').click();
  await expect(page.locator('#codecExplorerResults')).toContainText(/at least one filter/i, { timeout: 5_000 });
  await expect(page.locator('[data-library-option]:checked')).toHaveCount(0);
});
