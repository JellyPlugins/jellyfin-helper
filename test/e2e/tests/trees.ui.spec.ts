/**
 * Codecs + Health collapsible "trees": clicking a breakdown row opens a file
 * tree; folder toggles expand/collapse; Expand All / Collapse All work. All
 * client-side (no API call on expand), driven by stable classes/data-attrs.
 */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';

test('Codecs tab: clicking a breakdown row opens a file tree that expands/collapses', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'codecs');

  // Requires scan data; wait for at least one clickable codec row.
  const row = page.locator('.codec-row.codec-clickable').first();
  await expect(row).toBeVisible({ timeout: 20_000 });

  const chart = await row.getAttribute('data-chart');
  await row.click();

  // The matching detail panel becomes visible with a rendered tree.
  const panel = page.locator(`#codecDetail_${chart}`);
  await expect(panel).toHaveClass(/file-tree-panel-visible/);
  await expect(panel.locator('.tree-view, .file-tree-section').first()).toBeVisible();

  // Expand a folder node if present.
  const toggle = panel.locator('[data-tree-toggle]').first();
  if (await toggle.count()) {
    const node = toggle.locator('xpath=ancestor::*[contains(@class,"tree-node")][1]');
    await toggle.click();
    await expect(node).toHaveClass(/tree-expanded/);

    // Expand All / Collapse All buttons.
    const expandAll = panel.locator('[data-tree-action="expand"]');
    const collapseAll = panel.locator('[data-tree-action="collapse"]');
    if (await expandAll.count()) {
      await expandAll.click();
      await expect(panel.locator('.tree-node.tree-expanded').first()).toBeVisible();
      await collapseAll.click();
    }
  }

  // Clicking the row again closes the panel (toggle).
  await row.click();
  await expect(panel).not.toHaveClass(/file-tree-panel-visible/);
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
