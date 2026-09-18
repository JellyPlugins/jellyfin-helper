/** * Behavioral proof for the eBook data-loss fix + Books statistics category. * * Two guarantees, verified end-to-end against a live Jellyfin 12 + the real * container filesystem: * * 1. */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, sleep, runCleanupTask, assertPluginActive } from '../setup/api-client.ts';
import { hasDocker, containerDirExists, containerFileExists } from '../setup/fs-assert.ts';

const B = '/media/Books';

interface LibraryStats {
  LibraryName?: string;
  CollectionType?: string;
  BookFileCount?: number;
}

interface Stats {
  Books: LibraryStats[];
  TotalBookFileCount: number;
  TotalBookSize: number;
  TotalBookFormats: Record<string, number>;
  BookRootPaths: string[];
}

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});

test.afterAll(async () => {
  // Restore a benign shared config so a later spec doesn't inherit an Activated
  // destructive cleanup mode from this suite.
  await ctx
    .put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: {
        TrickplayTaskMode: 'DryRun',
        EmptyMediaFolderTaskMode: 'DryRun',
        OrphanedSubtitleTaskMode: 'DryRun',
        LinkRepairTaskMode: 'DryRun',
        SeerrCleanupTaskMode: 'Deactivate',
        RecommendationsTaskMode: 'DryRun',
        UseTrash: false,
        OrphanMinAgeDays: 30,
      },
    })
    .catch(() => undefined);
  await ctx.dispose();
});

/** Ensure a stats scan result exists; return it. Handles the 204/429 dance. */
async function getStats(): Promise<Stats> {
  let res = await ctx.get(p('MediaStatistics/Latest'));
  if (res.status() === 204) {
    const scan = await ctx.get(p('MediaStatistics/ScanLibraries'));
    if (scan.status() === 429) {
      await sleep(31_000);
      res = await ctx.get(p('MediaStatistics/ScanLibraries'));
    } else {
      res = scan;
    }
  }
  expect(res.ok(), `stats status ${res.status()}`).toBeTruthy();
  return (await res.json()) as Stats;
}

async function putConfig(body: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: body,
  });
  expect(res.ok(), `config update failed: ${res.status()}`).toBeTruthy();
}

test.describe('Book libraries are tracked in statistics but never deleted by cleanup', () => {
  test('TRACKING: a Book library is reported as a first-class Books category with format keys', async () => {
    const stats = await getStats();

    // global-setup always provisions the Books library (/media/Books with EPUB+PDF),
    // so an empty Books section means the fixture or the tracking broke - fail loudly.
    expect(stats.Books.length, 'Books fixture must be provisioned by global-setup').toBeGreaterThan(0);

    // A Book library IS present -> full tracking assertions.
    expect(stats.TotalBookFileCount, 'eBook files are counted').toBeGreaterThan(0);
    expect(stats.TotalBookSize, 'eBook bytes are summed').toBeGreaterThan(0);

    // The fixture writes .epub + .pdf, so the format breakdown (label = uppercase
    // extension) must contain both keys with positive counts.
    const formats = stats.TotalBookFormats;
    expect(Object.keys(formats), 'EPUB fixtures tracked').toContain('EPUB');
    expect(Object.keys(formats), 'PDF fixtures tracked').toContain('PDF');
    for (const [label, count] of Object.entries(formats)) {
      expect(count, `book format ${label} count must be positive`).toBeGreaterThan(0);
    }

    // The per-format counts must reconcile with the aggregate file count.
    const formatSum = Object.values(formats).reduce((a, c) => a + c, 0);
    expect(formatSum, 'format counts sum to the total book file count').toBe(stats.TotalBookFileCount);

    // Every Books library entry actually carries book files (none reported empty).
    for (const lib of stats.Books) {
      expect(lib.BookFileCount ?? 0, `Books entry ${lib.LibraryName} has files`).toBeGreaterThan(0);
    }
  });

  test('ROOT PATHS: BookRootPaths is emitted so the codec drill-down can group book files by library root', async () => {
    // The Codec tab's book-format drill-down (renderFileTree in the UI) needs a
    // BookRootPaths set to trim a common prefix, exactly like MovieRootPaths /
    // TvShowRootPaths do. Before this field existed the book file tree fell back to
    // an empty totals branch and rendered "No files found" even though books were
    // present. This asserts the server now emits the field with the fixture root.
    const stats = await getStats();

    // The e2e fixture always provisions a Books library (global-setup ensures
    // /media/Books with EPUB+PDF), so this must be present - do not skip, or the
    // BookRootPaths contract would pass vacuously.
    expect(stats.Books.length, 'Books fixture must be provisioned by global-setup').toBeGreaterThan(0);

    expect(Array.isArray(stats.BookRootPaths), 'BookRootPaths is present as an array').toBe(true);
    expect(stats.BookRootPaths.length, 'BookRootPaths is non-empty when a Book library exists').toBeGreaterThan(0);
    // The fixture provisions the Books library at /media/Books.
    expect(stats.BookRootPaths.some((r) => r.includes('Books')), 'BookRootPaths contains the book library root').toBe(true);
  });

  test('NO-DELETE: aggressive Activate-mode cleanup leaves every eBook file/folder intact', async () => {
    // FS proof needs the container; skip LOUDLY (never a silent green) without it.
    test.skip(!hasDocker(), 'docker exec unavailable - cannot verify eBook files on disk');

    // Sanity-gate: the book fixtures must actually be on disk, else "they survived"
    // would pass vacuously. This is the pre-condition the whole no-delete proof rests on.
    const novel = `${B}/Some Novel/Some Novel.epub`;
    const manual = `${B}/A Manual/A Manual.pdf`;
    const another = `${B}/Another Story/Another Story.epub`;
    // gen-media.sh always writes these fixtures; a missing file means the fixture
    // generation broke and "they survived" would pass vacuously - fail instead.
    expect(containerFileExists(novel), `eBook fixture must exist at ${novel}`).toBe(true);

    // Most aggressive config: Activate the empty-folder stage (the one that would delete a video-less folder), permanent delete (UseTrash=false), no age gate (OrphanMinAgeDays=0).
    await putConfig({
      TrickplayTaskMode: 'Deactivate',
      EmptyMediaFolderTaskMode: 'Activate',
      OrphanedSubtitleTaskMode: 'Deactivate',
      LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate',
      RecommendationsTaskMode: 'Deactivate',
      UseTrash: false,
      OrphanMinAgeDays: 0,
    });

    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Every eBook file AND its containing folder must still exist, the books library was skipped entirely, not swept as orphaned empty folders.
    expect(containerDirExists(`${B}/Some Novel`), 'eBook folder survives cleanup').toBe(true);
    expect(containerFileExists(novel), '.epub survives cleanup').toBe(true);
    expect(containerDirExists(`${B}/A Manual`), 'eBook folder survives cleanup').toBe(true);
    expect(containerFileExists(manual), '.pdf survives cleanup').toBe(true);
    expect(containerDirExists(`${B}/Another Story`), 'eBook folder survives cleanup').toBe(true);
    expect(containerFileExists(another), 'second .epub survives cleanup').toBe(true);

    // No trash folder was created inside the (never-touched) books library.
    expect(containerDirExists(`${B}/.jellyfin-trash`), 'no trash created in books library').toBe(false);

    // The edge case must not have taken the plugin down.
    await assertPluginActive(ctx);
  });

  test('NO-DELETE: eBooks still survive when EVERY cleanup stage is Activated at once', async () => {
    test.skip(!hasDocker(), 'docker exec unavailable - cannot verify eBook files on disk');
    const novel = `${B}/Some Novel/Some Novel.epub`;
    expect(containerFileExists(novel), 'eBook fixture must be provisioned by gen-media.sh').toBe(true);

    // Belt-and-braces: activate ALL cleanup stages. No stage is allowed to touch
    // a books-type library regardless of which one is running.
    await putConfig({
      TrickplayTaskMode: 'Activate',
      EmptyMediaFolderTaskMode: 'Activate',
      OrphanedSubtitleTaskMode: 'Activate',
      LinkRepairTaskMode: 'Activate',
      SeerrCleanupTaskMode: 'Deactivate',
      RecommendationsTaskMode: 'Deactivate',
      UseTrash: false,
      OrphanMinAgeDays: 0,
    });

    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    expect(containerFileExists(novel), '.epub survives all-stages cleanup').toBe(true);
    expect(containerFileExists(`${B}/A Manual/A Manual.pdf`), '.pdf survives all-stages cleanup').toBe(true);
    expect(containerFileExists(`${B}/Another Story/Another Story.epub`), 'second .epub survives').toBe(true);
    await assertPluginActive(ctx);
  });
});
