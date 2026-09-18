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

async function openDimEditor(page: Page, dimId: string): Promise<void> {
  await page.locator('#codecFilterAddBtn').click();
  const dim = page.locator(`[data-filter-dim="${dimId}"]`);
  await expect(dim).toBeVisible({ timeout: 5_000 });
  await dim.click();
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

  await openDimEditor(page, 'resolutions');
  const resolution = page.locator('.codec-filter-editor select[data-explorer-dim="resolutions"]');
  // gen-media.sh writes multiple resolutions and codecs; a single placeholder option
  // means the scan or the stats pipeline broke - fail instead of skipping.
  expect(await resolution.locator('option').count(), 'resolution filter must offer fixture data').toBeGreaterThan(1);
  const firstValue = (await resolution.locator('option').nth(1).getAttribute('value')) ?? '';
  expect(firstValue.length, 'resolution option must carry a value').toBeGreaterThan(0);
  await resolution.selectOption({ index: 1 });
  // A single pick collapses the popover back to the bar with a removable pill.
  await expect(page.locator('[data-filter-pop]')).toHaveCount(0);
  await expect(page.locator('.codec-pill')).toContainText(firstValue, { timeout: 5_000 });
  const summary = page.locator('.codec-explorer-summary');
  await expect(summary).toBeVisible({ timeout: 5_000 });
  await expect(summary).toContainText(firstValue);
  const firstCount = await countFromSummary(summary);

  await openDimEditor(page, 'videoCodecs');
  const codec = page.locator('.codec-filter-editor select[data-explorer-dim="videoCodecs"]');
  expect(await codec.locator('option').count(), 'video codec filter must offer fixture data').toBeGreaterThan(1);
  const secondValue = (await codec.locator('option').nth(1).getAttribute('value')) ?? '';
  expect(secondValue.length, 'codec option must carry a value').toBeGreaterThan(0);
  await codec.selectOption({ index: 1 });
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
  // global-setup always creates Movies/Shows/Books; zero rows means the libraries or
  // the overview table broke - fail instead of skipping.
  expect(await link.count(), 'overview must list fixture libraries').toBeGreaterThan(0);
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
  expect(await card.count(), 'overview must link the fixture Movies library').toBeGreaterThan(0);
  await card.first().click();
  await expect(page.locator('#tab-codecs')).toHaveClass(/active/, { timeout: 15_000 });
  await page.locator('[data-library-toggle]').click();
  await expect(page.locator('[data-library-option]:checked').first()).toBeChecked({ timeout: 5_000 });
});

test('language multi-dropdown selects several values and lists all in the summary', async ({ page }) => {
  await openExplorer(page);
  await openDimEditor(page, 'audioLanguages');
  const toggle = page.locator('.codec-filter-editor [data-multi-toggle="audioLanguages"]');
  // The fixture videos carry multiple audio languages; missing or disabled controls
  // mean the scan or the stats pipeline broke - fail instead of skipping.
  expect(await toggle.count(), 'audio language filter must exist').toBeGreaterThan(0);
  expect(await toggle.isDisabled(), 'audio language filter must be enabled on the fixture library').toBe(false);
  const panel = page.locator('.codec-filter-editor [data-multi-panel="audioLanguages"]');
  await expect(panel).toBeVisible({ timeout: 5_000 });
  const boxes = panel.locator('input[type="checkbox"]');
  expect(await boxes.count(), 'fixture must provide at least two audio languages').toBeGreaterThanOrEqual(2);
  await boxes.nth(0).check();
  await boxes.nth(1).check();
  const first = await boxes.nth(0).inputValue();
  const second = await boxes.nth(1).inputValue();
  const summary = page.locator('.codec-filter-editor [data-multi-toggle="audioLanguages"] .codec-multi-summary');
  await expect(summary).toContainText(first, { timeout: 5_000 });
  await expect(summary).toContainText(second);
  const resultSummary = page.locator('.codec-explorer-summary');
  await expect(resultSummary).toContainText(first);
  await expect(resultSummary).toContainText(second);
});

test('dimension picker hides facets without options in the picked scope', async ({ page }) => {
  await openExplorer(page);
  // Scope to Books: video facets have no options there and must vanish from
  // the picker instead of offering empty rows.
  await page.locator('[data-library-toggle]').click();
  const book = page.locator('[data-library-option][value="Books"]');
  await expect(book).toHaveCount(1);
  await book.check();
  await page.locator('#codecFilterAddBtn').click();
  const pop = page.locator('[data-filter-pop]');
  await expect(pop).toBeVisible({ timeout: 5_000 });
  await expect(pop.locator('[data-filter-dim="bookFormats"]')).toHaveCount(1);
  await expect(pop.locator('[data-filter-dim="dynamicRanges"]')).toHaveCount(0);
  await expect(pop.locator('[data-filter-dim="resolutions"]')).toHaveCount(0);
  await expect(pop.locator('[data-filter-dim="videoCodecs"]')).toHaveCount(0);
  await expect(pop.locator('[data-filter-dim="bitrate"]')).toHaveCount(0);
});

test('bitrate editor offers an absolute range with a distribution preview', async ({ page }) => {
  await openExplorer(page);
  await openDimEditor(page, 'bitrate');
  const editor = page.locator('[data-bitrate-editor]');
  // The fixture clips are real ffmpeg files with measurable streams; a missing
  // editor means the per file bitrate pipeline broke - fail instead of skipping.
  await expect(editor).toBeVisible({ timeout: 5_000 });
  const bars = editor.locator('.codec-bitrate-bar');
  expect(await bars.count(), 'bitrate histogram must render bins').toBeGreaterThan(0);
  const min = page.locator('#codecBitrateMin');
  const max = page.locator('#codecBitrateMax');
  const lo = parseFloat(await min.inputValue());
  const hi = parseFloat(await max.inputValue());
  expect(hi, 'fixture bitrates must span a real range').toBeGreaterThan(lo);
  // Narrowing the top end keeps a removable pill with an absolute label.
  // Tab leaves the field so the change commits even when fill alone fires none.
  await max.fill(String(lo));
  await max.press('Tab');
  await expect(page.locator('.codec-pill')).toContainText('Mbps', { timeout: 5_000 });
  await expect(page.locator('.codec-explorer-summary')).toContainText('Mbps');
});

test('reset clears filters and scope, showing the idle hint again', async ({ page }) => {
  await openExplorer(page);
  await openDimEditor(page, 'resolutions');
  const resolution = page.locator('.codec-filter-editor select[data-explorer-dim="resolutions"]');
  expect(await resolution.locator('option').count(), 'resolution filter must offer fixture data').toBeGreaterThan(1);
  await resolution.selectOption({ index: 1 });
  await expect(page.locator('.codec-explorer-summary')).toBeVisible({ timeout: 5_000 });
  await page.locator('#codecExplorerReset').click();
  await expect(page.locator('#codecExplorerResults')).toContainText(/at least one filter/i, { timeout: 5_000 });
  await expect(page.locator('[data-library-option]:checked')).toHaveCount(0);
});
