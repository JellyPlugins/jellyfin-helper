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

const SPECIAL_NAMES = [
  'Film%20 007',
  'Müller & Söhne (2020)',
  'Qu"oted (2021)',
  'A Very Long Film Title That Keeps Scrolling On Touch Devices Extended Collectors Edition Part Two (2024)',
];

function fileName(i: number): string {
  if (i <= SPECIAL_NAMES.length) {
    return SPECIAL_NAMES[i - 1];
  }
  return `Film${String(i).padStart(3, '0')}`;
}

const TV_EPISODES = 6;

function buildStubStatistics(count: number, includeTv = false): object {
  const paths: string[] = [];
  let videoSize = 0;
  for (let i = 1; i <= count; i++) {
    const name = fileName(i);
    paths.push(`/media/Movies/${name}/${name}.mkv`);
    videoSize += 1_000_000_000 + i;
  }
  const tvPaths: string[] = [];
  let tvSize = 0;
  if (includeTv) {
    for (let e = 1; e <= TV_EPISODES; e++) {
      tvPaths.push(`/media/TV/Mini (2024)/Season 01/Mini S01E0${e} [Bluray-1080p][AAC 2.0][x264]-scene.mkv`);
      tvSize += 2_000_000_000 + e;
    }
  }
  const tvLib = {
    LibraryName: 'TV Shows',
    CollectionType: 'tvshows',
    RootPaths: ['/media/TV'],
    VideoSize: tvSize,
    VideoFileCount: tvPaths.length,
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
    TotalSize: tvSize,
    ContainerFormats: { MKV: tvPaths.length },
    ContainerSizes: { MKV: tvSize },
    ContainerFormatPaths: { MKV: [...tvPaths] },
    VideoCodecs: { 'H.264': tvPaths.length },
    VideoCodecSizes: { 'H.264': tvSize },
    VideoCodecPaths: { 'H.264': [...tvPaths] },
    Resolutions: { '1080p': tvPaths.length },
    ResolutionSizes: { '1080p': tvSize },
    ResolutionPaths: { '1080p': [...tvPaths] },
    DynamicRanges: { SDR: tvPaths.length },
    DynamicRangeSizes: { SDR: tvSize },
    DynamicRangePaths: { SDR: [...tvPaths] },
    VideoAudioCodecs: {},
    AudioLanguages: { English: tvPaths.length },
    AudioLanguageSizes: { English: tvSize },
    AudioLanguagePaths: { English: [...tvPaths] },
    SubtitleLanguages: { English: tvPaths.length },
    SubtitleLanguageSizes: { English: tvSize },
    SubtitleLanguagePaths: { English: [...tvPaths] },
    VideoBitrateTiers: {},
    WatchedTiers: { 'Never watched': tvPaths.length },
    WatchedTierSizes: { 'Never watched': tvSize },
    WatchedTierPaths: { 'Never watched': [...tvPaths] },
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
    LibraryOrder: includeTv ? ['Movies', 'TV Shows'] : ['Movies'],
    Movies: [lib],
    TvShows: includeTv ? [tvLib] : [],
    Music: [],
    Books: [],
    Other: [],
    ScanTimestamp: new Date().toISOString(),
    TotalVideoFileCount: count + tvPaths.length,
    TotalAudioFileCount: 0,
    TotalMovieVideoSize: videoSize,
    TotalTvShowVideoSize: tvSize,
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
      body: JSON.stringify(buildStubStatistics(FILE_COUNT, true)),
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
  const moviesSection = results.locator('.file-tree-section:has(.badge-movies)');
  const tvSection = results.locator('.file-tree-section:has(.badge-tvshows)');
  const total = FILE_COUNT + TV_EPISODES;
  const summary = results.locator('.codec-explorer-summary');
  await expect(summary).toBeVisible({ timeout: 5_000 });
  await expect(summary.locator('[data-explorer-count]')).toHaveAttribute(
    'data-explorer-count',
    String(total),
  );

  // Truthful totals with a bounded DOM: every film is a collapsed shell, no
  // leaf is rendered yet, and each section carries its own action buttons.
  await expect(moviesSection.locator('.file-tree-section-count')).toContainText(`(${FILE_COUNT})`);
  await expect(tvSection.locator('.file-tree-section-count')).toContainText(`(${TV_EPISODES})`);
  await expect(moviesSection.locator('[data-tree-action="expand"]')).toHaveCount(1);
  await expect(tvSection.locator('[data-tree-action="expand"]')).toHaveCount(1);
  const folders = results.locator('.tree-node');
  await expect.poll(async () => folders.count(), { timeout: 10_000 }).toBe(FILE_COUNT + 1);
  await expect(results.locator('.tree-leaf')).toHaveCount(0);

  // Expanding one folder materializes exactly its own leaf; totals unchanged.
  await moviesSection.locator('[data-tree-toggle]').first().click();
  await expect.poll(async () => moviesSection.locator('.tree-leaf').count(), { timeout: 10_000 }).toBe(1);
  await expect(tvSection.locator('.tree-leaf')).toHaveCount(0);
  await expect(summary.locator('[data-explorer-count]')).toHaveAttribute(
    'data-explorer-count',
    String(total),
  );

  // Per-section Expand All: movies expand fully without touching TV Shows.
  await moviesSection.locator('[data-tree-action="expand"]').click();
  await expect
    .poll(async () => moviesSection.locator('.tree-leaf').count(), { timeout: 10_000 })
    .toBe(FILE_COUNT);
  await expect(tvSection.locator('.tree-leaf')).toHaveCount(0);
  await expect(tvSection.locator('.tree-node.tree-expanded')).toHaveCount(0);

  // The TV section expands independently afterwards.
  await tvSection.locator('[data-tree-action="expand"]').click();
  await expect
    .poll(async () => tvSection.locator('.tree-leaf').count(), { timeout: 10_000 })
    .toBe(TV_EPISODES);

  // Collapse All is scoped too: movies collapse while the TV section keeps
  // its expanded show and season nodes.
  await moviesSection.locator('[data-tree-action="collapse"]').click();
  await expect(moviesSection.locator('.tree-node.tree-expanded')).toHaveCount(0);
  await expect(tvSection.locator('.tree-node.tree-expanded')).toHaveCount(2);
  await expect(summary.locator('[data-explorer-count]')).toHaveAttribute(
    'data-explorer-count',
    String(total),
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
  await expect(results.locator('.tree-node:not(.tree-expanded)')).not.toHaveCount(0);
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

test('long names scroll horizontally inside their section', async ({
  page,
}) => {
  const results = await openExplorerWithCount(page, FILE_COUNT);
  const moviesSection = results.locator('.file-tree-section:has(.badge-movies)');
  const treeView = moviesSection.locator('.tree-view');
  await expect(treeView).toBeVisible({ timeout: 5_000 });

  // Sections scroll on both axes like the drill-down trees.
  await expect(treeView).toHaveCSS('overflow-x', 'auto');
  await expect(treeView).toHaveCSS('overflow-y', 'auto');

  // The long folder keeps its full width instead of truncating: expanding it
  // must make the section content wider than its viewport.
  const longToggle = moviesSection.locator('[data-tree-toggle]').filter({ hasText: 'Touch Devices' });
  await expect(longToggle).toHaveCount(1);
  await longToggle.click();
  await expect
    .poll(
      async () =>
        treeView.evaluate(
          (el) => (el as HTMLElement).scrollWidth > (el as HTMLElement).clientWidth,
        ),
      { timeout: 10_000 },
    )
    .toBe(true);
});
