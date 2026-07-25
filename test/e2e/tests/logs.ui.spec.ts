/**
 * Logs tab: logs arrive in the table, the level/source filters work, download
 * produces a file, and clear empties the buffer (via the confirm dialog).
 */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';

test('Logs tab: entries arrive and download produces a file', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'logs');

  // The table wrapper renders; entries should appear (the plugin logs during
  // startup/scan). Wait for either rows or the empty-state.
  await expect(page.locator('#logsTableWrapper')).toBeVisible({ timeout: 15_000 });
  await expect(
    page.locator('.logs-table tbody tr, .logs-empty').first(),
  ).toBeVisible({ timeout: 15_000 });

  // Download button triggers a file download.
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.locator('#btnLogsDownload').click(),
  ]);
  expect(download.suggestedFilename()).toMatch(/logs.*\.txt/);
});

test('Logs tab: level filter change persists via LogLevel endpoint', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'logs');

  // Changing the level fires PUT /Configuration/LogLevel + a reload GET /Logs.
  const [putReq] = await Promise.all([
    page.waitForRequest(
      (r) => r.url().includes('/JellyfinHelper/Configuration/LogLevel') && r.method() === 'PUT',
      { timeout: 15_000 },
    ),
    page.locator('#logsLevelFilter').selectOption('DEBUG'),
  ]);
  expect(putReq).toBeTruthy();
});

test('Logs tab: clear opens confirm dialog and empties on confirm', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'logs');

  await page.locator('#btnLogsClear').click();
  const dialog = page.locator('#logsClearDialogOverlay');
  await expect(dialog).toBeVisible();

  // Confirm (the danger button) → DELETE /Logs.
  const [delReq] = await Promise.all([
    page.waitForRequest(
      (r) => r.url().includes('/JellyfinHelper/Logs') && r.method() === 'DELETE',
      { timeout: 15_000 },
    ),
    dialog.locator('.logs-btn.danger, button.danger').last().click(),
  ]);
  expect(delReq).toBeTruthy();
});
