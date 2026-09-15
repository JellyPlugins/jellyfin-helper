/**
 * Codecs + Health collapsible "trees": clicking a breakdown row opens a file
 * tree; folder toggles expand/collapse; Expand All / Collapse All work. All
 * client-side (no API call on expand), driven by stable classes/data-attrs.
 */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab, expandAllLibrariesExplorer } from './_ui-helpers.ts';

/** Expands the given dimension's donut section inside the "All Libraries" explorer, if collapsed. */
async function expandDonutSection(page: import('@playwright/test').Page, dimension: string) {
  const section = page.locator(`.stat-explorer[data-scope="all"] .stat-donut-section[data-dimension="${dimension}"]`);
  const header = section.locator('.stat-donut-header');
  await expect(header).toBeVisible({ timeout: 20_000 });
  if ((await header.getAttribute('aria-expanded')) !== 'true') {
    await header.click();
  }
  return section;
}

test('Statistics tab: clicking a breakdown row opens a file tree that expands/collapses', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'statistics');
  await expandAllLibrariesExplorer(page);

  // Requires scan data; wait for at least one clickable breakdown row.
  const section = page.locator('.stat-explorer[data-scope="all"] .stat-donut-section').first();
  const dimension = await section.getAttribute('data-dimension');
  const header = section.locator('.stat-donut-header');
  await expect(header).toBeVisible({ timeout: 20_000 });
  await header.click();

  const row = section.locator('.stat-breakdown-row').first();
  await expect(row).toBeVisible({ timeout: 20_000 });
  await row.click();

  // The results panel switches to the tree view for the clicked value.
  const panel = page.locator('#statResultsPanel_all');
  const treeView = panel.locator('.stat-tree-view');
  await expect(treeView).toBeVisible();
  await expect(treeView.locator('.tree-view, .file-tree-section').first()).toBeVisible();
  await expect(section.locator('.stat-breakdown-row').first()).toHaveClass(/stat-breakdown-tree-open/);

  // Expand a folder node if present.
  const toggle = treeView.locator('[data-tree-toggle]').first();
  if (await toggle.count()) {
    const node = toggle.locator('xpath=ancestor::*[contains(@class,"tree-node")][1]');
    await toggle.click();
    await expect(node).toHaveClass(/tree-expanded/);

    // Expand All / Collapse All buttons.
    const expandAll = treeView.locator('[data-tree-action="expand"]');
    const collapseAll = treeView.locator('[data-tree-action="collapse"]');
    if (await expandAll.count()) {
      await expandAll.click();
      await expect(treeView.locator('.tree-node.tree-expanded').first()).toBeVisible();
      await collapseAll.click();
    }
  }

  // Clicking the row again deselects the filter and drops back to the curated default view.
  await row.click();
  await expect(panel.locator('.stat-curated')).toBeVisible();
  expect(dimension).toBeTruthy();
});

test('Health tab: clicking a health item opens its detail tree', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'health');

  const item = page.locator('.health-item.health-clickable').first();
  await expect(item).toBeVisible({ timeout: 20_000 });
  await item.click();

  const panel = page.locator('#healthDetailPanel');
  await expect(panel).toHaveClass(/file-tree-panel-visible/);
});

test('Statistics tab: clicking a book format shows the book file tree, not an empty state', async ({ page }) => {
  // Regression guard for the book-format drill-down: under Jellyfin 12 the file
  // tree renderer had no "books" section and excluded books from its file total,
  // so clicking a book format (CBZ/EPUB/PDF) rendered "No files found." even
  // though the chart above listed those formats. This clicks the bookFormats row
  // and asserts a real books section with at least one file node appears.
  await openDashboard(page);
  await switchTab(page, 'statistics');
  await expandAllLibrariesExplorer(page);

  // The e2e fixture always provisions a Books library (EPUB+PDF), so the
  // bookFormats breakdown must render. Do not skip on absence, or this regression
  // guard would pass without ever exercising the book file-tree path.
  const section = await expandDonutSection(page, 'bookFormats');
  const bookRow = section.locator('.stat-breakdown-row').first();
  await expect(bookRow, 'bookFormats breakdown row must render from the Books fixture').toBeVisible({ timeout: 20_000 });
  await bookRow.click();

  const panel = page.locator('#statResultsPanel_all .stat-tree-view');
  await expect(panel).toBeVisible();
  // The books section must render (badge-books) and must NOT be the empty state.
  await expect(panel.locator('.file-tree-section .badge-books')).toBeVisible();
  await expect(panel.locator('.stat-no-results')).toHaveCount(0);
  // Book file paths are present in the tree (this is what BookFormatPaths feeds).
  // They live inside collapsed folder nodes, so assert at least one exists, then
  // expand the tree to prove a real leaf becomes visible - mirroring the codec test.
  const leaves = panel.locator('.tree-leaf, .tree-leaf-file-name');
  expect(await leaves.count(), 'book file leaves must be rendered').toBeGreaterThan(0);
  const expandAll = panel.locator('[data-tree-action="expand"]');
  if (await expandAll.count()) {
    await expandAll.click();
    await expect(leaves.first()).toBeVisible();
  }
});

test('Settings tab: the Excluded Libraries multi-select lists libraries', async ({ page }) => {
  // Regression guard for the empty "Excluded Libraries" dropdown: the frontend
  // read data.libraries (camelCase) but Jellyfin 12 serializes the response as
  // data.Libraries (PascalCase), so the list came back empty and the panel showed
  // "No data". This asserts the multi-select is populated with real entries.
  await openDashboard(page);
  await switchTab(page, 'settings');

  const wrapper = page.locator('#cfgExcludedWrapper');
  await expect(wrapper.locator('.library-multiselect-toggle')).toBeVisible({ timeout: 20_000 });

  // Open the dropdown panel and assert it contains selectable library entries
  // rather than the "No data" fallback.
  await wrapper.locator('.library-multiselect-toggle').click();
  await expect(wrapper.locator('.library-multiselect-item').first()).toBeVisible();
  expect(await wrapper.locator('.library-multiselect-item').count()).toBeGreaterThan(0);
});
