/**
 * Per-Library Breakdown table: the Other column (NFO + unrecognized sidecars)
 * and the conditional Books column keep every row gapless against its Total.
 * Base columns: Library, Type, Video, Audio, Subtitles, Images, Trickplay,
 * Other, Total - plus Books right before Total when a book library exists.
 */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';

/** 0-based index of a header cell matched case-insensitively, -1 when absent. */
async function headerIndex(
  page: import('@playwright/test').Page,
  table: import('@playwright/test').Locator,
  name: RegExp,
): Promise<number> {
  const headers = table.locator('thead th');
  const count = await headers.count();
  for (let i = 0; i < count; i++) {
    if ((await headers.nth(i).textContent())?.match(name)) {
      return i;
    }
  }
  return -1;
}

test('breakdown table is gapless with conditional Books column', async ({ page }) => {
  const errors = trackConsoleErrors(page);
  await openDashboard(page);
  await switchTab(page, 'overview');

  const table = page.locator('#overviewContent .library-table');
  await expect(table).toBeVisible({ timeout: 20_000 });

  // Header and every row agree on the column count, with or without Books.
  const headers = table.locator('thead th');
  const headerCount = await headers.count();
  expect(headerCount, 'base columns plus optional Books').toBeGreaterThanOrEqual(9);
  const rows = table.locator('tbody tr');
  expect(await rows.count(), 'fixture libraries listed').toBeGreaterThan(0);
  for (let i = 0; i < (await rows.count()); i++) {
    await expect(rows.nth(i).locator('td')).toHaveCount(headerCount);
  }

  const otherIdx = await headerIndex(page, table, /^other$/i);
  expect(otherIdx, 'Other header present').toBeGreaterThanOrEqual(0);

  // The fixture seeds .strm/.mxf/.txt sidecars into Movies, so its Other cell is
  // non-zero: proves the column is wired to real data, not a static zero.
  const moviesRow = table.locator('tbody tr', { hasText: 'Movies' }).first();
  await expect(moviesRow).toBeVisible();
  await expect(moviesRow.locator('td').nth(otherIdx)).not.toHaveText('0 B');

  // The fixture always has a Books library: its row carries the Books column,
  // and with only EPUB/PDF fixtures the Books cell equals the Total cell.
  const booksIdx = await headerIndex(page, table, /^books$/i);
  expect(booksIdx, 'Books header present on book fixture').toBeGreaterThanOrEqual(0);
  const booksRow = table.locator('tbody tr:has(.badge-books)').first();
  await expect(booksRow).toBeVisible();
  const booksCell = await booksRow.locator('td').nth(booksIdx).textContent();
  const totalCell = await booksRow.locator('td').last().textContent();
  expect(booksCell, 'Books cell holds the whole Books total').toBe(totalCell);

  const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
  expect(scriptErrors, `uncaught JS errors: ${scriptErrors.join('\n')}`).toHaveLength(0);
});
