/**
 * Per-Library Breakdown table: the Other column (NFO + unrecognized sidecars)
 * and the conditional Books column keep every row gapless against its Total.
 * Column order: Library, Type, Video, Audio, Images, [Books,] Subtitles,
 * Trickplay, Other, Total.
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

  // The fixture seeds .mxf/.txt sidecars into Movies, so its Other cell is
  // non-zero: proves the column is wired to real data, not a static zero.
  const moviesRow = table.locator('tbody tr', { hasText: 'Movies' }).first();
  await expect(moviesRow).toBeVisible();
  await expect(moviesRow.locator('td').nth(otherIdx).locator('.library-table-bytes')).not.toHaveText('0 B');

  // Every category cell carries its file-count sub-line (folders for trickplay).
  const moviesCells = await moviesRow.locator('td').count();
  for (let c = 2; c < moviesCells - 1; c++) {
    await expect(moviesRow.locator('td').nth(c).locator('.library-table-sub')).toHaveText(/^\d+ (files?|folders?)$/);
  }

  // The fixture always has a Books library: its row carries the Books column,
  // and with only EPUB/PDF fixtures the Books cell equals the Total cell.
  const booksIdx = await headerIndex(page, table, /^books$/i);
  expect(booksIdx, 'Books header present on book fixture').toBeGreaterThanOrEqual(0);
  const booksRow = table.locator('tbody tr:has(.badge-books)').first();
  await expect(booksRow).toBeVisible();
  const booksCell = await booksRow.locator('td').nth(booksIdx).locator('.library-table-bytes').textContent();
  const totalCell = await booksRow.locator('td').last().locator('.library-table-bytes').textContent();
  expect(booksCell, 'Books cell holds the whole Books total').toBe(totalCell);

  // Every row's displayed category cells sum to its displayed Total: catches a
  // renderer mapping the wrong field into a cell (cell count alone would not).
  // formatBytes rounds to 2 decimals, so each parsed value carries up to half a
  // unit-step of error - the bound below is the worst-case sum of those steps,
  // tight enough to catch any dropped non-trivial category.
  const byteUnits: Record<string, number> = { B: 1, KB: 1024, MB: 1024 ** 2, GB: 1024 ** 3, TB: 1024 ** 4 };
  const parseCell = async (row: number, cell: number): Promise<{ bytes: number; bound: number }> => {
    const text = ((await rows.nth(row).locator('td').nth(cell).locator('.library-table-bytes').textContent()) ?? '').trim();
    const m = text.match(/^([\d.]+)\s+([KMGT]?B)$/);
    expect(m, `row ${row} cell ${cell} parses as bytes (got ${JSON.stringify(text)})`).not.toBeNull();
    const bytes = parseFloat(m![1]) * byteUnits[m![2]];
    return { bytes, bound: 0.005 * byteUnits[m![2]] };
  };
  const rowCount = await rows.count();
  for (let r = 0; r < rowCount; r++) {
    let sum = 0;
    let bound = 0;
    // Category cells run from Video (index 2) to the cell before Total.
    for (let c = 2; c < headerCount - 1; c++) {
      const part = await parseCell(r, c);
      sum += part.bytes;
      bound += part.bound;
    }
    const total = await parseCell(r, headerCount - 1);
    bound += total.bound;
    expect(
      Math.abs(sum - total.bytes),
      `row ${r} categories sum to Total (sum ${sum}, total ${total.bytes})`,
    ).toBeLessThanOrEqual(bound);
  }

  const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
  expect(scriptErrors, `uncaught JS errors: ${scriptErrors.join('\n')}`).toHaveLength(0);
});

test.describe('boxset rows never link into the explorer', () => {
  // Boxset libraries are deliberately excluded from the explorer scope picker;
  // linking one would open an empty "no library" state. The row renders plain
  // text instead. Stubbed payload (no fixture boxsets exist), same shape as
  // explorer-lazy-tree.ui.spec.ts stubs.
  const boxsetPayload = {
    Movies: [
      {
        LibraryName: 'Movies',
        CollectionType: 'movies',
        RootPaths: [],
        VideoSize: 1000,
        VideoFileCount: 1,
        AudioSize: 0,
        AudioFileCount: 0,
        SubtitleSize: 0,
        SubtitleFileCount: 0,
        ImageSize: 0,
        ImageFileCount: 0,
        NfoSize: 0,
        NfoFileCount: 0,
        TrickplaySize: 0,
        TrickplayFolderCount: 0,
        OtherSize: 0,
        OtherFileCount: 0,
        BookSize: 0,
        BookFileCount: 0,
        TotalSize: 1000,
      },
    ],
    TvShows: [],
    Music: [],
    Books: [],
    Other: [
      {
        LibraryName: 'Sammlungen',
        CollectionType: 'boxsets',
        RootPaths: [],
        VideoSize: 0,
        VideoFileCount: 0,
        AudioSize: 0,
        AudioFileCount: 0,
        SubtitleSize: 0,
        SubtitleFileCount: 0,
        ImageSize: 0,
        ImageFileCount: 0,
        NfoSize: 0,
        NfoFileCount: 0,
        TrickplaySize: 0,
        TrickplayFolderCount: 0,
        OtherSize: 5000,
        OtherFileCount: 2,
        BookSize: 0,
        BookFileCount: 0,
        TotalSize: 5000,
      },
    ],
    LibraryOrder: ['Movies', 'Sammlungen'],
    TotalBookFileCount: 0,
  };

  test('boxset row renders plain text, movies row keeps its deep-link', async ({ page }) => {
    const stubErrors = trackConsoleErrors(page);
    await page.route('**/JellyfinHelper/MediaStatistics/Latest', async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(boxsetPayload) });
    });
    await openDashboard(page);
    await switchTab(page, 'overview');

    const table = page.locator('#overviewContent .library-table');
    await expect(table).toBeVisible({ timeout: 20_000 });

    const boxsetRow = table.locator('tbody tr', { hasText: 'Sammlungen' }).first();
    await expect(boxsetRow).toBeVisible();
    await expect(boxsetRow.locator('[data-codec-explore-library]')).toHaveCount(0);
    await expect(boxsetRow.locator('td').first()).toHaveText('Sammlungen');

    const moviesRow = table.locator('tbody tr', { hasText: 'Movies' }).first();
    await expect(moviesRow.locator('[data-codec-explore-library]').first()).toBeVisible();

    const scriptErrors = stubErrors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
    expect(scriptErrors, `uncaught JS errors: ${scriptErrors.join('\n')}`).toHaveLength(0);
  });
});
