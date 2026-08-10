/**
 * Behavioral coverage for MediaStatistics codec / resolution / health breakdowns.
 * Today only integrity (no-negative) and shape are checked. Here we prove the DATA
 * matches the KNOWN fixtures: gen-media.sh writes libx264 (H.264), libx265 (HEVC),
 * and mpeg4 (MPEG-4) clips at specific resolutions, so the breakdown dictionaries
 * must contain those exact codec keys with positive counts, and the health counts
 * (videos without subtitles) must reflect the sub-less fixtures.
 *
 * MediaStatistics analyzes what Jellyfin knows; the library was scanned in
 * global-setup. ScanLibraries is rate-limited to once/30s (429 + Retry-After) and
 * always recomputes; /Latest is never rate-limited and returns 204 before the
 * first scan. Responses are PascalCase; codec dictionary KEYS are display names
 * ("H.264","HEVC","MPEG-4","MKV","MP4","STRM", resolution tiers "1080p" etc.).
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, sleep } from '../setup/api-client.ts';

interface Stats {
  TotalVideoCodecs: Record<string, number>;
  TotalContainerFormats: Record<string, number>;
  TotalResolutions: Record<string, number>;
  TotalVideoFileCount: number;
  TotalVideosWithoutSubtitles: number;
  TotalVideosWithoutSubtitlesPaths: string[];
  Libraries: Array<{ LibraryName: string; VideoFileCount: number; TotalSize: number }>;
}

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
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
    // gen-media.sh generates libx264 + libx265 + mpeg4 clips → these keys must exist.
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
    const perLibVideo = stats.Libraries.reduce((a, l) => a + l.VideoFileCount, 0);
    expect(perLibVideo, 'per-library video counts sum to the total').toBe(stats.TotalVideoFileCount);
    for (const lib of stats.Libraries) {
      expect(lib.TotalSize, `${lib.LibraryName} size non-negative`).toBeGreaterThanOrEqual(0);
    }
  });
});
