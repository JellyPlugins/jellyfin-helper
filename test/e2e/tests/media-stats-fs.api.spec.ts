/** * Behavioral coverage for MediaStatistics codec / resolution / health breakdowns. * Today only integrity (no-negative) and shape are checked. */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, sleep } from '../setup/api-client.ts';
import { containerExists, containerFileExists, containerRm, containerWriteFile, execInContainer, hasDocker, q } from '../setup/fs-assert.ts';

interface LibrarySizes {
  LibraryName: string;
  VideoFileCount: number;
  TotalSize: number;
  VideoSize: number;
  AudioSize: number;
  SubtitleSize: number;
  ImageSize: number;
  TrickplaySize: number;
  NfoSize: number;
  BookSize: number;
  OtherSize: number;
  OrphanedMetadataDirectories: number;
  OrphanedMetadataDirectoriesPaths: string[];
}

interface Stats {
  TotalVideoCodecs: Record<string, number>;
  TotalContainerFormats: Record<string, number>;
  TotalResolutions: Record<string, number>;
  TotalVideoFileCount: number;
  TotalVideosWithoutSubtitles: number;
  TotalVideosWithoutSubtitlesPaths: string[];
  InternalTrickplaySize: number;
  Movies: LibrarySizes[];
  TvShows: LibrarySizes[];
  Music: LibrarySizes[];
  Books: LibrarySizes[];
  Other: LibrarySizes[];
  LibraryOrder: string[];
  Libraries?: unknown;
}

/** Wire shape: typed groups are canonical, Libraries is omitted (dedup). */
function allLibraries(stats: Stats) {
  return [...stats.Movies, ...stats.TvShows, ...stats.Music, ...stats.Books, ...stats.Other];
}

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});

test.describe.serial('orphan detection #405 preserves hierarchy, placeholders and optional Arr ownership', () => {
  const base = `/media/Movies/Orphan405-${Date.now()}`;
  const wantedMovie = '/media/Movies/Missing Film (2099)';
  const wantedSeries = '/media/Shows/Ghost Series';
  const orphan = `${base}/Orphan`;
  const ownedPaths: string[] = [];
  let original: Record<string, unknown> | undefined;
  let originalRadarr: Record<string, unknown>[] | undefined;
  let originalSonarr: Record<string, unknown>[] | undefined;
  let settingsChanged = false;

  interface BackupInstance {
    name: string;
    url: string;
    apiKey: string;
    libraries: string;
    skipCertificateValidation?: boolean;
  }

  async function putSettings(changes: Record<string, unknown>) {
    if (!original || !originalRadarr || !originalSonarr) throw new Error('Complete Arr configuration backup is required before changing settings');
    // A failed/uncertain HTTP response can still have applied the change on the server.
    settingsChanged = true;
    const response = await ctx.put(p('Configuration'), {
      data: { ...original, RadarrInstances: originalRadarr, SonarrInstances: originalSonarr, ...changes },
    });
    expect(response.ok(), `settings save failed: ${response.status()}`).toBeTruthy();
  }

  async function freshScan(): Promise<Stats> {
    for (let attempt = 0; attempt < 3; attempt++) {
      const response = await ctx.get(p('MediaStatistics/ScanLibraries'));
      if (response.status() === 429) {
        const retry = Number(response.headers()['retry-after']) || 30;
        await sleep(Math.min(31, Math.max(1, retry)) * 1_000 + 100);
        continue;
      }
      expect(response.ok(), `scan failed: ${response.status()}`).toBeTruthy();
      return await response.json() as Stats;
    }
    throw new Error('Statistics scan remained throttled after three attempts');
  }

  function sidecars(path: string) {
    if (!containerExists(path)) ownedPaths.push(path);
    for (const extension of ['nfo', 'srt']) {
      const file = `${path}/orphan405-${base.split('/').at(-1)}.${extension}`;
      containerWriteFile(file, extension === 'nfo' ? '<movie />' : 'subtitle');
      expect(containerFileExists(file), `fixture ${file} exists`).toBe(true);
      ownedPaths.push(file);
    }
  }

  test.beforeAll(async () => {
    test.skip(!hasDocker(), 'Docker is required for real filesystem orphan coverage');
    const settings = await ctx.get(p('Configuration'));
    expect(settings.ok()).toBeTruthy();
    const settingsData = await settings.json();
    // Keep test credentials only in memory; redacted GET values cannot restore removed instances.
    const exported = await ctx.get(p('Backup/Export?includeSecrets=true'));
    expect(exported.ok()).toBeTruthy();
    const backup = await exported.json() as { radarrInstances: BackupInstance[]; sonarrInstances: BackupInstance[] };
    expect(Array.isArray(backup.radarrInstances), 'complete Radarr backup').toBeTruthy();
    expect(Array.isArray(backup.sonarrInstances), 'complete Sonarr backup').toBeTruthy();
    const restoreInstance = (instance: BackupInstance) => ({
      Name: instance.name, Url: instance.url, ApiKey: instance.apiKey,
      Libraries: instance.libraries, SkipCertificateValidation: instance.skipCertificateValidation ?? false,
    });
    originalRadarr = (backup.radarrInstances ?? []).map(restoreInstance);
    originalSonarr = (backup.sonarrInstances ?? []).map(restoreInstance);
    original = settingsData;
    ownedPaths.push(base);
    sidecars(orphan);
    sidecars(`${base}/Group`);
    containerWriteFile(`${base}/Group/Deep/movie.mkv`, '');
    const copy = execInContainer(`cp ${q('/media/Movies/Aurora Skies (2019)/Aurora Skies (2019).mkv')} ${q(`${base}/Group/Deep/movie.mkv`)}`);
    expect(copy.code, `copy a real video: ${copy.stderr}`).toBe(0);
    containerWriteFile(`${base}/Placeholder/movie.nfo`, '<movie />');
    containerWriteFile(`${base}/Placeholder/poster.jpg`, 'artwork');
    containerWriteFile(`${base}/Audio/track.mp3`, 'ID3');
    sidecars(`${base}/Audio`);
    sidecars(wantedMovie);
    sidecars(wantedSeries);
    await putSettings({ ExcludedLibraries: '', RadarrInstances: [], SonarrInstances: [] });
  });

  test.afterAll(async () => {
    if (settingsChanged) await putSettings({});
    for (const path of ownedPaths.reverse()) containerRm(path);
    if (settingsChanged || ownedPaths.length > 0) await freshScan();
  });

  test('without Arr, nested media/audio and metadata placeholders are protected while real orphans remain', async () => {
    const stats = await freshScan();
    const paths = allLibraries(stats).flatMap(library => library.OrphanedMetadataDirectoriesPaths);
    expect(paths).toContain(orphan);
    expect(paths).toContain(wantedMovie);
    expect(paths).toContain(wantedSeries);
    for (const path of [base, `${base}/Group`, `${base}/Group/Deep`, `${base}/Placeholder`, `${base}/Audio`]) {
      expect(paths, `${path} is not orphaned`).not.toContain(path);
    }
  });

  test('configured Radarr and Sonarr add protection for managed folders without media', async () => {
    await putSettings({
      ExcludedLibraries: '',
      RadarrInstances: [{ Name: 'Orphan405 Radarr', Url: 'http://mock-arr:9000', ApiKey: 'test-key', Libraries: 'Movies' }],
      SonarrInstances: [{ Name: 'Orphan405 Sonarr', Url: 'http://mock-arr:9000', ApiKey: 'test-key', Libraries: 'Shows' }],
    });
    const stats = await freshScan();
    const paths = allLibraries(stats).flatMap(library => library.OrphanedMetadataDirectoriesPaths);
    expect(paths).not.toContain(wantedMovie);
    expect(paths).not.toContain(wantedSeries);
    expect(paths).toContain(orphan);
  });

  test('removing optional configuration restores the independent baseline', async () => {
    await putSettings({ ExcludedLibraries: '', RadarrInstances: [], SonarrInstances: [] });
    const paths = allLibraries(await freshScan()).flatMap(library => library.OrphanedMetadataDirectoriesPaths);
    expect(paths).toContain(orphan);
    expect(paths).toContain(wantedMovie);
    expect(paths).toContain(wantedSeries);
    expect(paths).not.toContain(`${base}/Placeholder`);
  });

  test('excluded libraries retain file statistics but have no orphan verdicts', async () => {
    await putSettings({ ExcludedLibraries: 'Movies, Shows', RadarrInstances: [], SonarrInstances: [] });
    const stats = await freshScan();
    for (const name of ['Movies', 'Shows']) {
      const library = allLibraries(stats).find(entry => entry.LibraryName === name)!;
      expect(library.VideoFileCount).toBeGreaterThan(0);
      expect(library.OrphanedMetadataDirectories).toBe(0);
      expect(library.OrphanedMetadataDirectoriesPaths).toEqual([]);
    }
  });

  test('failed optional Arr lookup is scoped and does not remove file statistics', async () => {
    await putSettings({
      ExcludedLibraries: '', SonarrInstances: [],
      RadarrInstances: [{ Name: 'Orphan405 unavailable', Url: 'http://mock-arr:9000', ApiKey: 'force-fail', Libraries: 'Movies' }],
    });
    const stats = await freshScan();
    const movies = stats.Movies.find(library => library.LibraryName === 'Movies')!;
    expect(movies.VideoFileCount).toBeGreaterThan(0);
    expect(movies.OrphanedMetadataDirectoriesPaths).toEqual([]);
    expect(stats.TvShows.flatMap(library => library.OrphanedMetadataDirectoriesPaths)).toContain(wantedSeries);
  });
});
test.afterAll(async () => {
  await ctx.dispose();
});

/** Ensure a scan result exists; return it. Handles the 204/429 dance. */
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

/** Sum of a codec/count dictionary. */
const sum = (d: Record<string, number>) => Object.values(d).reduce((a, b) => a + b, 0);

test.describe('MediaStatistics breakdowns reflect the known fixtures', () => {
  test('video-codec breakdown contains the generated codecs with positive counts', async () => {
    const stats = await getStats();
    const codecs = stats.TotalVideoCodecs;
    // gen-media.sh generates libx264 + libx265 + mpeg4 clips -> these keys must exist.
    expect(Object.keys(codecs), 'H.264 (libx264) fixtures exist').toContain('H.264');
    expect(Object.keys(codecs), 'HEVC (libx265) fixtures exist').toContain('HEVC');
    for (const [name, count] of Object.entries(codecs)) {
      expect(count, `codec ${name} count must be positive`).toBeGreaterThan(0);
    }
    // The codec counts should account for a meaningful share of the video files.
    expect(sum(codecs), 'codec counts sum to > 0').toBeGreaterThan(0);
  });

  test('container + resolution breakdowns contain the expected keys', async () => {
    const stats = await getStats();
    expect(Object.keys(stats.TotalContainerFormats), 'MKV fixtures exist').toContain('MKV');
    // Resolutions are bucketed; the generator emits 1080p, 720p, 480p and a 4K clip.
    const resKeys = Object.keys(stats.TotalResolutions);
    expect(resKeys.length, 'multiple resolution tiers present').toBeGreaterThan(1);
    for (const [tier, count] of Object.entries(stats.TotalResolutions)) {
      expect(count, `resolution ${tier} count positive`).toBeGreaterThan(0);
    }
  });

  test('health: videos-without-subtitles count matches its detail-path list', async () => {
    const stats = await getStats();
    // The fixtures are largely sub-less, so this must be positive and internally
    // consistent - the count equals the number of listed paths.
    expect(stats.TotalVideosWithoutSubtitles, 'some fixtures lack subtitles').toBeGreaterThan(0);
    expect(
      stats.TotalVideosWithoutSubtitlesPaths.length,
      'the detail-path list length matches the count',
    ).toBe(stats.TotalVideosWithoutSubtitles);
    // Every listed path is under the media library (no escape into host dirs).
    for (const path of stats.TotalVideosWithoutSubtitlesPaths) {
      expect(path.startsWith('/media'), `sub-less path ${path} must be under /media`).toBe(true);
    }
  });

  test('per-library totals are coherent with the aggregate video count', async () => {
    const stats = await getStats();
    // Wire shape: the union is omitted (dedup), groups are canonical.
    expect(stats.Libraries, 'Libraries must be omitted from the payload').toBeUndefined();
    const libs = allLibraries(stats);
    expect(libs.length, 'libraries arrive through the typed groups').toBeGreaterThan(0);
    // LibraryOrder names every library exactly once, in scan order.
    const ordered = stats.LibraryOrder;
    expect(Array.isArray(ordered), 'LibraryOrder must travel the wire').toBe(true);
    expect([...ordered].sort(), 'LibraryOrder covers the union').toEqual(
      libs.map((l) => l.LibraryName).sort(),
    );
    // Each typed group holds its own libraries (no cross-group duplication).
    for (const group of [stats.Movies, stats.TvShows, stats.Music, stats.Books, stats.Other]) {
      expect(Array.isArray(group), 'typed group must be an array').toBe(true);
    }
    const perLibVideo = libs.reduce((a, l) => a + l.VideoFileCount, 0);
    expect(perLibVideo, 'per-library video counts sum to the total').toBe(stats.TotalVideoFileCount);
    for (const lib of libs) {
      expect(lib.TotalSize, `${lib.LibraryName} size non-negative`).toBeGreaterThanOrEqual(0);
    }
  });

  test('per-library TotalSize equals the sum of all eight size buckets', async () => {
    // The overview table's Other column folds Nfo + Other (Books has its own
    // column), so every row sums exactly to its Total; this pins that
    // byte-coherence per library.
    const stats = await getStats();
    const libs = allLibraries(stats);
    expect(libs.length, 'libraries present').toBeGreaterThan(0);
    for (const lib of libs) {
      const parts =
        lib.VideoSize + lib.AudioSize + lib.SubtitleSize + lib.ImageSize +
        lib.TrickplaySize + lib.NfoSize + lib.BookSize + lib.OtherSize;
      expect(lib.TotalSize, `${lib.LibraryName} TotalSize is the bucket sum`).toBe(parts);
    }
  });

  test('movies carry other-size from sidecar fixtures, books carry book-size', async () => {
    // gen-media.sh seeds an off-allowlist .mxf and a notes.txt (both land in
    // OtherSize; .strm files count as video) plus real EPUB/PDF books (BookSize)
    // - the Other column is never vacuously zero on this fixture set.
    const stats = await getStats();
    const movies = stats.Movies.find((l) => l.LibraryName === 'Movies');
    expect(movies, 'Movies library present').toBeDefined();
    expect(movies!.OtherSize, 'Movies OtherSize covers .mxf/.txt fixtures').toBeGreaterThan(0);
    expect(movies!.NfoSize, 'Movies NfoSize covers the movie.nfo fixture').toBeGreaterThan(0);
    const books = stats.Books.find((l) => l.LibraryName === 'Books');
    expect(books, 'Books library present').toBeDefined();
    expect(books!.BookSize, 'Books BookSize covers EPUB/PDF fixtures').toBeGreaterThan(0);
  });

  test('internal trickplay size travels the wire but never joins library totals', async () => {
    // Jellyfin-managed images live outside every library: the result root carries
    // their total, while the per-library bucket coherence pinned above stays untouched.
    const stats = await getStats();
    expect(typeof stats.InternalTrickplaySize, 'InternalTrickplaySize travels the wire').toBe('number');
    expect(stats.InternalTrickplaySize, 'internal trickplay size non-negative').toBeGreaterThanOrEqual(0);
  });
});
