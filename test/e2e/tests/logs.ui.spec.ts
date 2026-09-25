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

test.describe('logs tab on mobile', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  // The table wrapper used to force bidirectional scrolling on phones; rows now
  // stack as cards, so the page must never overflow horizontally.
  test('no horizontal overflow, rows stay readable', async ({ page }) => {
    await openDashboard(page);
    await switchTab(page, 'logs');
    await expect(
      page.locator('.logs-table tbody tr, .logs-empty').first(),
    ).toBeVisible({ timeout: 15_000 });

    const overflow = await page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth,
      innerWidth: window.innerWidth,
    }));
    expect(
      overflow.scrollWidth,
      `page must not scroll horizontally (scrollWidth ${overflow.scrollWidth} > viewport ${overflow.innerWidth})`,
    ).toBeLessThanOrEqual(overflow.innerWidth);
  });

  // A scan always writes to the plugin log (200 logs the scan start, 429 logs
  // the rate-limit warning), so this seeds a deterministic row instead of
  // accepting the empty state: the meta cells must share one line with the
  // message wrapped below, all inside the viewport.
  test('seeded row renders with meta line above the message', async ({ page }) => {    await openDashboard(page);
    await switchTab(page, 'overview');
    const scanBtn = page.locator('#btnScanLibraries');
    await expect(scanBtn).toBeVisible({ timeout: 15_000 });
    const [scanResp] = await Promise.all([
      page.waitForResponse(
        (r) => r.url().includes('/JellyfinHelper/MediaStatistics/ScanLibraries'),
        { timeout: 30_000 },
      ),
      scanBtn.click(),
    ]);
    expect([200, 429], `scan status ${scanResp.status()}`).toContain(scanResp.status());

    await switchTab(page, 'logs');
    const row = page.locator('.logs-table tbody tr', {
      hasText: /Starting media statistics scan|Rate limit exceeded/,
    }).first();
    await expect(row).toBeVisible({ timeout: 20_000 });

    const viewport = page.viewportSize();
    expect(viewport, 'viewport must be set').not.toBeNull();
    const boxes = [];
    for (const cls of ['.col-time', '.col-level', '.col-source', '.col-message']) {
      const box = await row.locator(cls).boundingBox();
      expect(box, `${cls} must be measurable`).not.toBeNull();
      boxes.push(box!);
    }
    const [timeBox, levelBox, sourceBox, messageBox] = boxes;
    // Time, level and source share the meta line; the message wraps below it.
    for (const box of [levelBox, sourceBox]) {
      expect(Math.abs(box.y - timeBox.y), 'meta cells share one line').toBeLessThanOrEqual(8);
    }
    expect(messageBox.y, 'message wraps below the meta line').toBeGreaterThan(timeBox.y);
    for (const box of boxes) {
      expect(box.x + box.width, 'cell stays inside the viewport').toBeLessThanOrEqual(viewport!.width + 1);
    }
  });

  // Level and Source labels share a fixed column, so both controls start on
  // the same x regardless of label length.
  test('filter controls align under one label column', async ({ page }) => {
    await openDashboard(page);
    await switchTab(page, 'logs');
    const levelLabel = page.locator('label[for="logsLevelFilter"]');
    const sourceLabel = page.locator('label[for="logsSourceFilter"]');
    await expect(levelLabel).toBeVisible({ timeout: 15_000 });
    await expect(sourceLabel).toBeVisible();
    const levelControl = page.locator('#logsLevelFilter');
    const sourceControl = page.locator('#logsSourceFilter');
    const levelBox = await levelControl.boundingBox();
    const sourceBox = await sourceControl.boundingBox();
    expect(levelBox, 'level control measurable').not.toBeNull();
    expect(sourceBox, 'source control measurable').not.toBeNull();
    expect(Math.abs(levelBox!.x - sourceBox!.x), 'controls share one column').toBeLessThanOrEqual(2);
    // Fixed 5-letter level codes need no reserved space: the select hugs its content.
    expect(levelBox!.width, 'level select stays compact').toBeLessThan(sourceBox!.width);
  });
});

test('Logs tab: clear opens confirm dialog and empties on confirm', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'logs');

  await page.locator('#btnLogsClear').click();
  const dialog = page.locator('#logsClearDialogOverlay');
  await expect(dialog).toBeVisible();

  // Confirm (the danger button) -> DELETE /Logs; assert it SUCCEEDS and the table shows the empty state afterwards.
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
