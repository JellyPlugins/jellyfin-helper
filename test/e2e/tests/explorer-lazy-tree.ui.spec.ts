/**
 * Library Explorer lazy tree: the full filtered set renders as top-level
 * shells only, children materialize on expand, totals stay truthful, and no
 * continuation control exists.
 *
 * The fixture library is tiny, so lazy expansion never matters there. This
 * spec stubs MediaStatistics/Latest with 250 movies and drives the real UI
 * through it: pick a resolution filter, expect 250 folder shells with zero
 * leaves, expand one folder for a single leaf, Expand All for all 250.
 */
import { test, expect } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';

const FILE_COUNT = 250;

function buildStubStatistics(): object {
  const paths: string[] = [];
  let videoSize = 0;
  for (let i = 1; i <= FILE_COUNT; i++) {
    const name = `Film${String(i).padStart(3, '0')}`;
    paths.push(`/media/Movies/${name}/${name}.mkv`);
    videoSize += 1_000_000_000 + i;
  }
  const lib = {
    LibraryName: 'Movies',
    CollectionType: 'movies',
    RootPaths: ['/media/Movies'],
    VideoSize: videoSize,
    VideoFileCount: FILE_COUNT,
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
    ContainerFormats: { MKV: FILE_COUNT },
    ContainerSizes: { MKV: videoSize },
    ContainerFormatPaths: { MKV: [...paths] },
    VideoCodecs: { 'H.264': FILE_COUNT },
    VideoCodecSizes: { 'H.264': videoSize },
    VideoCodecPaths: { 'H.264': [...paths] },
    Resolutions: { '1080p': FILE_COUNT },
    ResolutionSizes: { '1080p': videoSize },
    ResolutionPaths: { '1080p': [...paths] },
    DynamicRanges: { SDR: FILE_COUNT },
    DynamicRangeSizes: { SDR: videoSize },
    DynamicRangePaths: { SDR: [...paths] },
    VideoAudioCodecs: {},
    AudioLanguages: { English: FILE_COUNT },
    AudioLanguageSizes: { English: videoSize },
    AudioLanguagePaths: { English: [...paths] },
    SubtitleLanguages: { English: FILE_COUNT },
    SubtitleLanguageSizes: { English: videoSize },
    SubtitleLanguagePaths: { English: [...paths] },
    VideoBitrateTiers: {},
    WatchedTiers: { 'Never watched': FILE_COUNT },
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
  return {
    Libraries: [lib],
    Movies: [lib],
    TvShows: [],
    Music: [],
    Books: [],
    Other: [],
    ScanTimestamp: new Date().toISOString(),
    TotalVideoFileCount: FILE_COUNT,
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
      body: JSON.stringify(buildStubStatistics()),
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
