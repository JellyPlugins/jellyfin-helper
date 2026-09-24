/**
 * Per-Library Breakdown table: the Other column (NFO + other + book bytes) keeps
 * every row gapless against its Total. Columns: Library, Type, Video, Audio,
 * Subtitles, Images, Trickplay, Other, Total.
 */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';

// 0-based cell positions in the breakdown table.
const OTHER_CELL = 7;
const EXPECTED_COLUMNS = 9;

test('breakdown table has an Other column and gapless rows', async ({ page }) => {
  const errors = trackConsoleErrors(page);
  await openDashboard(page);
  await switchTab(page, 'overview');

  const table = page.locator('#overviewContent .library-table');
  await expect(table).toBeVisible({ timeout: 20_000 });

  // Header gains exactly one column for Other; rows must match it cell for cell.
  await expect(table.locator('thead th')).toHaveCount(EXPECTED_COLUMNS);
  const rows = table.locator('tbody tr');
  expect(await rows.count(), 'fixture libraries listed').toBeGreaterThan(0);
  for (let i = 0; i < (await rows.count()); i++) {
    await expect(rows.nth(i).locator('td')).toHaveCount(EXPECTED_COLUMNS);
  }

  // The fixture seeds .strm/.mxf sidecars into Movies, so its Other cell is
  // non-zero: proves the column is wired to real data, not a static zero.
  const moviesRow = table.locator('tbody tr', { hasText: 'Movies' }).first();
  await expect(moviesRow).toBeVisible();
  await expect(moviesRow.locator('td').nth(OTHER_CELL)).not.toHaveText('0 B');

  const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
  expect(scriptErrors, `uncaught JS errors: ${scriptErrors.join('\n')}`).toHaveLength(0);
});
