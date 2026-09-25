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
  const resOptions = page.locator('.codec-filter-editor [data-single-option="resolutions"]');
  // gen-media.sh writes multiple resolutions and codecs; a lone Any row
  // means the scan or the stats pipeline broke - fail instead of skipping.
  expect(await resOptions.count(), 'resolution filter must offer fixture data').toBeGreaterThan(1);
  const firstValue = (await resOptions.nth(1).getAttribute('data-single-value')) ?? '';
  expect(firstValue.length, 'resolution option must carry a value').toBeGreaterThan(0);
  await resOptions.nth(1).click();
  // A single pick collapses the popover back to the bar with a removable pill.
  await expect(page.locator('[data-filter-pop]')).toHaveCount(0);
  await expect(page.locator('.codec-pill')).toContainText(firstValue, { timeout: 5_000 });
  const summary = page.locator('.codec-explorer-summary');
  await expect(summary).toBeVisible({ timeout: 5_000 });
  await expect(summary).toContainText(firstValue);
  const firstCount = await countFromSummary(summary);

  await openDimEditor(page, 'videoCodecs');
  const codecOptions = page.locator('.codec-filter-editor [data-single-option="videoCodecs"]');
  expect(await codecOptions.count(), 'video codec filter must offer fixture data').toBeGreaterThan(1);
  // Find a codec that keeps a non-empty result and does not expand it.
  // The fixture's most-frequent pair is correlated (e.g. 1080p is always H.264),
  // so strict < would fail even though the filter is correctly applied.
  let combinedCount = 0;
  let secondValue = '';
  const codecCount = await codecOptions.count();
  for (let i = 1; i < codecCount; i++) {
    const opt = page.locator('.codec-filter-editor [data-single-option="videoCodecs"]').nth(i);
    const val = (await opt.getAttribute('data-single-value')) ?? '';
    if (!val) continue;
    await opt.click();
    await expect(page.locator('[data-filter-pop]')).toHaveCount(0);
    await expect(summary).toContainText(val, { timeout: 5_000 });
    const c = await countFromSummary(summary);
    if (c > 0 && c <= firstCount) {
      combinedCount = c;
      secondValue = val;
      break;
    }
    // Try next codec - clear the current pill and reopen the editor.
    const clear = page.locator('[data-pill-clear="videoCodecs"]');
    if (await clear.count()) await clear.click();
    await openDimEditor(page, 'videoCodecs');
  }
  expect(secondValue.length, 'no codec kept a non-empty result – fixture lacks intersecting pair').toBeGreaterThan(0);
  // Combining a second filter must not expand the result and must stay non-empty.
  expect(combinedCount).toBeGreaterThan(0);
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
  const checkedValues = await page.locator('[data-library-option]:checked')
    .evaluateAll((els) => els.map((el) => (el as HTMLInputElement).value).sort());
  expect(checkedValues).toEqual(['Movies']);
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
  // Result rows expose their expansion state for assistive technology.
  // Dismiss the still open editor first: any outside click would rebuild
  // the results and collapse the tree right after expanding it.
  await page.keyboard.press('Escape');
  await page.locator('#codecExplorerResults .file-tree-section').first().locator('[data-tree-action="expand"]').click();
  const leaf = page.locator('#codecExplorerResults .tree-leaf[title]').first();
  await expect(leaf).toBeVisible({ timeout: 5_000 });
  await leaf.click();
  await expect(page.locator('#codecExplorerResults .codec-file-detail')).toBeVisible({ timeout: 5_000 });
  await expect(leaf).toHaveAttribute('aria-expanded', 'true');
});

test('excluded filter shows negated pill and result header', async ({ page }) => {
  await openExplorer(page);
  await openDimEditor(page, 'audioLanguages');
  const excludeBtn = page.locator('[data-exclude-toggle][data-exclude-value="1"]');
  await expect(excludeBtn).toBeVisible({ timeout: 5_000 });
  await excludeBtn.click();
  const panel = page.locator('.codec-filter-editor [data-multi-panel="audioLanguages"]');
  await expect(panel).toBeVisible({ timeout: 5_000 });
  const boxes = panel.locator('input[type="checkbox"]');
  expect(await boxes.count(), 'fixture must provide at least one audio language').toBeGreaterThanOrEqual(1);
  await boxes.nth(0).check();
  const first = await boxes.nth(0).inputValue();
  await expect(page.locator('.codec-pill')).toContainText('≠', { timeout: 5_000 });
  await expect(page.locator('.codec-pill')).toContainText(first);
  await expect(page.locator('.codec-explorer-summary')).toContainText('≠', { timeout: 5_000 });
});

test('subtitle multi-dropdown selects a value and lists it in the summary', async ({ page }) => {
  await openExplorer(page);
  await openDimEditor(page, 'subtitleLanguages');
  const toggle = page.locator('.codec-filter-editor [data-multi-toggle="subtitleLanguages"]');
  // Polyglot Clip carries embedded English + German subtitles; external sidecars
  // are excluded by design, so only embedded tracks may appear here.
  expect(await toggle.count(), 'subtitle filter must exist').toBeGreaterThan(0);
  expect(await toggle.isDisabled(), 'subtitle filter must be enabled on the fixture library').toBe(false);
  const panel = page.locator('.codec-filter-editor [data-multi-panel="subtitleLanguages"]');
  await expect(panel).toBeVisible({ timeout: 5_000 });
  const boxes = panel.locator('input[type="checkbox"]');
  expect(await boxes.count(), 'fixture must provide embedded subtitle languages').toBeGreaterThanOrEqual(2);
  await boxes.nth(0).check();
  const first = await boxes.nth(0).inputValue();
  const summary = page.locator('.codec-filter-editor [data-multi-toggle="subtitleLanguages"] .codec-multi-summary');
  await expect(summary).toContainText(first, { timeout: 5_000 });
  const resultSummary = page.locator('.codec-explorer-summary');
  await expect(resultSummary).toContainText(first);
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
  const resetOptions = page.locator('.codec-filter-editor [data-single-option="resolutions"]');
  expect(await resetOptions.count(), 'resolution filter must offer fixture data').toBeGreaterThan(1);
  await resetOptions.nth(1).click();
  await expect(page.locator('.codec-explorer-summary')).toBeVisible({ timeout: 5_000 });
  await page.locator('#codecExplorerReset').click();
  await expect(page.locator('#codecExplorerResults')).toContainText(/at least one filter/i, { timeout: 5_000 });
  await expect(page.locator('[data-library-option]:checked')).toHaveCount(0);
});

test('add-filter popover opens leftwards and stays inside the viewport on desktop', async ({ page }) => {
  const errors = trackConsoleErrors(page);
  await openExplorer(page);
  await page.locator('#codecFilterAddBtn').click();
  const pop = page.locator('[data-filter-pop]');
  await expect(pop).toBeVisible({ timeout: 5_000 });
  const box = await pop.boundingBox();
  expect(box, 'filter popover must be measurable').not.toBeNull();
  const viewport = page.viewportSize();
  expect(viewport, 'viewport must be set').not.toBeNull();
  expect(box!.x, 'popover must not start left of the viewport').toBeGreaterThanOrEqual(-1);
  expect(box!.x + box!.width, 'popover must not overflow the viewport to the right')
    .toBeLessThanOrEqual(viewport!.width + 1);
  // Desktop keeps the compact card, not the phone sheet.
  expect(box!.width, 'popover stays a compact card on desktop').toBeGreaterThan(200);
  expect(box!.width, 'popover stays a compact card on desktop').toBeLessThan(500);

  const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
  expect(scriptErrors, `uncaught JS errors: ${scriptErrors.join('\n')}`).toHaveLength(0);
});

test.describe('mobile filter popover', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  // The add button sits at the right edge of the bar on phones; a left-anchored
  // popover used to spill past the viewport and clip the option list.
  test('add-filter popover stays inside the viewport', async ({ page }) => {
    await openExplorer(page);
    await page.locator('#codecFilterAddBtn').click();
    const pop = page.locator('[data-filter-pop]');
    await expect(pop).toBeVisible({ timeout: 5_000 });
    const box = await pop.boundingBox();
    expect(box, 'filter popover must be measurable').not.toBeNull();
    const viewport = page.viewportSize();
    expect(viewport, 'viewport must be set').not.toBeNull();
    expect(box!.x, 'popover must not start left of the viewport').toBeGreaterThanOrEqual(-1);
    expect(box!.x + box!.width, 'popover must not overflow the viewport to the right')
      .toBeLessThanOrEqual(viewport!.width + 1);
    // Roomy sheet, not a content-width sliver: nearly full viewport width so
    // dim rows, toggle pairs and options are not squeezed.
    expect(box!.width, 'popover uses nearly the full viewport width').toBeGreaterThan(250);
  });

  test('filter editor stays inside the viewport', async ({ page }) => {
    await openExplorer(page);
    await openDimEditor(page, 'videoCodecs');
    const pop = page.locator('[data-filter-pop]');
    await expect(pop).toBeVisible({ timeout: 5_000 });
    const box = await pop.boundingBox();
    expect(box, 'filter editor must be measurable').not.toBeNull();
    const viewport = page.viewportSize();
    expect(viewport, 'viewport must be set').not.toBeNull();
    expect(box!.x, 'editor must not start left of the viewport').toBeGreaterThanOrEqual(-1);
    expect(box!.x + box!.width, 'editor must not overflow the viewport to the right')
      .toBeLessThanOrEqual(viewport!.width + 1);
    expect(box!.width, 'editor uses nearly the full viewport width').toBeGreaterThan(250);
  });
});
