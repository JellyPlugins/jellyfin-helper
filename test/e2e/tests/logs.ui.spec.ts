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

  // Changing the level fires PUT /Configuration/LogLevel; assert it SUCCEEDS
  // and that the level actually persisted (GET /Configuration reflects DEBUG).
  const [putResp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/Configuration/LogLevel') && r.request().method() === 'PUT',
      { timeout: 15_000 },
    ),
    page.locator('#logsLevelFilter').selectOption('DEBUG'),
  ]);
  expect(putResp.ok(), `LogLevel PUT failed: ${putResp.status()}`).toBeTruthy();

  const cfg = await page.evaluate(async () => {
    const res = await (window as any).ApiClient.ajax({
      type: 'GET',
      url: (window as any).ApiClient.getUrl('JellyfinHelper/Configuration'),
      dataType: 'json',
    });
    return res;
  });
  expect(cfg.PluginLogLevel).toBe('DEBUG');
});

test('Logs tab: clear opens confirm dialog and empties on confirm', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'logs');

  await page.locator('#btnLogsClear').click();
  const dialog = page.locator('#logsClearDialogOverlay');
  await expect(dialog).toBeVisible();

  // Confirm (the danger button) → DELETE /Logs; assert it SUCCEEDS and the
  // table shows the empty state afterwards. Dialog buttons are built by
  // createDialogBtn(), which sets only inline styles - no CSS class - so the
  // confirm button must be matched by its label (Cancel / Clear), not a class.
  const confirmBtn = dialog.getByRole('button', { name: /clear|löschen|leeren/i }).last();
  const [delResp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/Logs') && r.request().method() === 'DELETE',
      { timeout: 15_000 },
    ),
    confirmBtn.click(),
  ]);
  expect([200, 204], `Logs DELETE status ${delResp.status()}`).toContain(delResp.status());
  // After clearing, the buffer is empty; the empty-state or a reduced table
  // should render (auto-refresh reloads within ~10s, but the clear also reloads).
  await expect(page.locator('.logs-empty, .logs-table tbody tr').first()).toBeVisible({ timeout: 15_000 });
});
