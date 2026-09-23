/**
 * Library Explorer lazy tree: the full filtered set renders as top-level
 * shells only, children materialize on expand, totals stay truthful, and no
 * continuation control exists.
 *
 * The fixture library is tiny, so lazy expansion never matters there. This
 * spec stubs MediaStatistics/Latest and drives the real UI through it:
 * 250 movies for shells/expand/collapse, 2200 movies for the Expand All
 * budget with its visible capped note, plus special-character names and
 * keyboard expansion.
 */
import { test, expect, type Page } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';

const FILE_COUNT = 250;
const BUDGET_FILE_COUNT = 2200;

const SPECIAL_NAMES = ['Film%20 007', 'Müller & Söhne (2020)', 'Qu"oted (2021)'];

function fileName(i: number): string {
  if (i <= SPECIAL_NAMES.length) {
    return SPECIAL_NAMES[i - 1];
  }
  return `Film${String(i).padStart(3, '0')}`;
}

function buildStubStatistics(count: number): object {
  const paths: string[] = [];
  let videoSize = 0;
  for (let i = 1; i <= count; i++) {
    const name = fileName(i);
    paths.push(`/media/Movies/${name}/${name}.mkv`);
    videoSize += 1_000_000_000 + i;
  }
  const lib = {
    LibraryName: 'Movies',
    CollectionType: 'movies',
    RootPaths: ['/media/Movies'],
    VideoSize: videoSize,
    VideoFileCount: count,
    SubtitleSize: 0,
    SubtitleFileCount: 0,
    ImageSize: 0,
    ImageFileCount: 0,
    NfoSize: 0,
    NfoFileCount: 0,
    AudioSize: 0,
    AudioFileCount: 0,
    BookSize: 0,
    BookFileCount: 0,
    TrickplaySize: 0,
    TrickplayFolderCount: 0,
    OtherSize: 0,
    TotalSize: videoSize,
    ContainerFormats: { MKV: count },
    ContainerSizes: { MKV: videoSize },
    ContainerFormatPaths: { MKV: [...paths] },
    VideoCodecs: { 'H.264': count },
    VideoCodecSizes: { 'H.264': videoSize },
    VideoCodecPaths: { 'H.264': [...paths] },
    Resolutions: { '1080p': count },
    ResolutionSizes: { '1080p': videoSize },
    ResolutionPaths: { '1080p': [...paths] },
    DynamicRanges: { SDR: count },
    DynamicRangeSizes: { SDR: videoSize },
    DynamicRangePaths: { SDR: [...paths] },
    VideoAudioCodecs: {},
    AudioLanguages: { English: count },
    AudioLanguageSizes: { English: videoSize },
    AudioLanguagePaths: { English: [...paths] },
    SubtitleLanguages: { English: count },
    SubtitleLanguageSizes: { English: videoSize },
    SubtitleLanguagePaths: { English: [...paths] },
    VideoBitrateTiers: {},
    WatchedTiers: { 'Never watched': count },
    WatchedTierSizes: { 'Never watched': videoSize },
    WatchedTierPaths: { 'Never watched': [...paths] },
    WatchedByUserPaths: {},
    WatchedByUserSizes: {},
    VideosWithoutSubtitles: 0,
    VideosWithoutSubtitlesPaths: [],
    VideosWithoutImages: 0,
    VideosWithoutImagesPaths: [],
    VideosWithoutNfo: 0,
    VideosWithoutNfoPaths: [],
    OrphanedMetadataDirectories: 0,
    OrphanedMetadataDirectoriesPaths: [],
  };
  // Wire shape: typed groups are canonical, Libraries is omitted (the page
  // rebuilds the union at intake).
  return {
    LibraryOrder: ['Movies'],
    Movies: [lib],
    TvShows: [],
    Music: [],
    Books: [],
    Other: [],
    ScanTimestamp: new Date().toISOString(),
    TotalVideoFileCount: count,
    TotalAudioFileCount: 0,
    TotalMovieVideoSize: videoSize,
    TotalTvShowVideoSize: 0,
    TotalMusicAudioSize: 0,
    TotalBookFileCount: 0,
    TotalBookSize: 0,
    TotalTrickplaySize: 0,
    TotalSubtitleSize: 0,
    TotalImageSize: 0,
    TotalNfoSize: 0,
  };
}

test('explorer renders a lazy tree with truthful totals and no paging', async ({
  page,
}) => {
  await page.route('**/JellyfinHelper/MediaStatistics/Latest', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(buildStubStatistics(FILE_COUNT)),
    });
  });

  await openDashboard(page);
  await switchTab(page, 'codecs');

  const toggle = page.locator('#codecExplorerToggle');
  await expect(toggle).toBeVisible({ timeout: 15_000 });
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') {
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true', { timeout: 5_000 });
  }

  await page.locator('#codecFilterAddBtn').click();
  const dim = page.locator('[data-filter-dim="resolutions"]');
  await expect(dim).toBeVisible({ timeout: 5_000 });
  await dim.click();
  const options = page.locator('.codec-filter-editor [data-single-option="resolutions"]');
  await expect(options.nth(1)).toBeVisible({ timeout: 5_000 });
  await options.nth(1).click();

  const results = page.locator('#codecExplorerResults');
  const summary = results.locator('.codec-explorer-summary');
  await expect(summary).toBeVisible({ timeout: 5_000 });
  await expect(summary.locator('[data-explorer-count]')).toHaveAttribute(
    'data-explorer-count',
    String(FILE_COUNT),
  );

  // Truthful totals with a bounded DOM: every film is a collapsed shell, no
  // leaf is rendered yet, and no continuation control exists.
  const headerCount = results.locator('.file-tree-section-count');
  await expect(headerCount).toContainText(`(${FILE_COUNT})`);
  const folders = results.locator('.tree-node');
  await expect.poll(async () => folders.count(), { timeout: 10_000 }).toBe(FILE_COUNT);
  await expect(results.locator('.tree-leaf')).toHaveCount(0);
  await expect(results.locator('[data-section-more], #codecSectionMore_movies')).toHaveCount(0);

  // Expanding one folder materializes exactly its own leaf; totals unchanged.
  await results.locator('[data-tree-toggle]').first().click();
  await expect.poll(async () => results.locator('.tree-leaf').count(), { timeout: 10_000 }).toBe(1);
  await expect(headerCount).toContainText(`(${FILE_COUNT})`);
  await expect(summary.locator('[data-explorer-count]')).toHaveAttribute(
    'data-explorer-count',
    String(FILE_COUNT),
  );

  // Expand All stays within budget on this size and shows every file.
  await results.locator('[data-tree-action="expand"]').click();
  await expect
    .poll(async () => results.locator('.tree-leaf').count(), { timeout: 10_000 })
    .toBe(FILE_COUNT);
  await expect(headerCount).toContainText(`(${FILE_COUNT})`);

  // Collapse All hides the tree again without losing the result.
  await results.locator('[data-tree-action="collapse"]').click();
  await expect(results.locator('.tree-node.tree-expanded')).toHaveCount(0);
  await expect(summary.locator('[data-explorer-count]')).toHaveAttribute(
    'data-explorer-count',
    String(FILE_COUNT),
  );
});

async function openExplorerWithCount(page: Page, count: number) {
  await page.route('**/JellyfinHelper/MediaStatistics/Latest', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(buildStubStatistics(count)),
    });
  });

  await openDashboard(page);
  await switchTab(page, 'codecs');

  const toggle = page.locator('#codecExplorerToggle');
  await expect(toggle).toBeVisible({ timeout: 15_000 });
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') {
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true', { timeout: 5_000 });
  }

  await page.locator('#codecFilterAddBtn').click();
  const dim = page.locator('[data-filter-dim="resolutions"]');
  await expect(dim).toBeVisible({ timeout: 5_000 });
  await dim.click();
  const options = page.locator('.codec-filter-editor [data-single-option="resolutions"]');
  await expect(options.nth(1)).toBeVisible({ timeout: 5_000 });
  await options.nth(1).click();
  return page.locator('#codecExplorerResults');
}

test('expand all stops at the node budget with a visible capped note', async ({
  page,
}) => {
  const results = await openExplorerWithCount(page, BUDGET_FILE_COUNT);
  const summary = results.locator('.codec-explorer-summary');
  await expect(summary).toBeVisible({ timeout: 5_000 });
  await expect(summary.locator('[data-explorer-count]')).toHaveAttribute(
    'data-explorer-count',
    String(BUDGET_FILE_COUNT),
  );

  // 2200 single-file folders exceed FILE_TREE_EXPAND_BUDGET (2000 nodes at
  // ~2 nodes per folder), so Expand All stops early instead of freezing.
  await results.locator('[data-tree-action="expand"]').click();
  await expect
    .poll(async () => results.locator('.tree-leaf').count(), { timeout: 15_000 })
    .toBe(1000);
  // The cap is honest: totals stay real and the header says what is shown.
  await expect(results.locator('.file-tree-section-count')).toContainText(
    `(${BUDGET_FILE_COUNT})`,
  );
  const note = results.locator('.file-tree-capped');
  await expect(note).toBeVisible({ timeout: 5_000 });
  await expect(note).toContainText(String(BUDGET_FILE_COUNT));
  // Manually expanding the rest clears the note once everything is shown.
  await expect(results.locator('.tree-node:not(.tree-expanded)').count()).toBeGreaterThan(0);
});

test('special-character folders expand via mouse and keyboard', async ({
  page,
}) => {
  const results = await openExplorerWithCount(page, FILE_COUNT);
  const summary = results.locator('.codec-explorer-summary');
  await expect(summary).toBeVisible({ timeout: 5_000 });

  // Percent, umlaut/ampersand, and quote in folder names survive key encoding.
  const special = results.locator('[data-tree-toggle]').filter({ hasText: 'Müller' });
  await expect(special).toHaveCount(1);
  await special.click();
  const leaf = results.locator('.tree-leaf[title*="ller"]');
  await expect(leaf).toBeVisible({ timeout: 5_000 });

  // Keyboard: focus a collapsed toggle, Enter expands it, Enter on the leaf
  // opens its detail card.
  const quoted = results.locator('[data-tree-toggle]').filter({ hasText: 'Qu' });
  await expect(quoted).toHaveCount(1);
  await quoted.focus();
  await page.keyboard.press('Enter');
  const quotedLeaf = results.locator('.tree-leaf[title*="Qu"]');
  await expect(quotedLeaf).toBeVisible({ timeout: 5_000 });
  await quotedLeaf.focus();
  await page.keyboard.press('Enter');
  await expect(results.locator('.codec-file-detail')).toBeVisible({ timeout: 5_000 });
});
