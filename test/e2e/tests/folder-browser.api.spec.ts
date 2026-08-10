/**
 * FolderBrowser coverage - behavioral + adversarial. This endpoint had ZERO e2e
 * coverage, and it is filesystem-facing (the admin folder-picker in config), so it
 * is prime hardening territory.
 *
 * Behavioral: browsing roots and a known media dir lists real children; going up
 * works. LibraryPaths lists the configured libraries.
 *
 * Adversarial / hardening (canary-guarded): the endpoint must never let a caller
 * DELETE, MOVE, or otherwise mutate anything - it is read-only - and must reject
 * `..` traversal, non-absolute paths, NUL bytes, and sensitive system directories
 * (Jellyfin's own /config, /data and OS roots like /etc, /var - refused via the
 * shared PathValidator). Validation failures are surfaced as HTTP 200 with a
 * non-null `Error` field (not an error status), so we assert on the body, not the
 * status code. Whatever the input, the library-external canaries must stay intact.
 *
 * Requires the container FS; skips loudly when Docker is unreachable.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  containerFileExists,
  readContainerFile,
  verifyCanaries,
} from '../setup/fs-assert.ts';

interface FolderEntry { Name: string; Path: string; HasChildren: boolean }
interface FolderBrowseResult {
  CurrentPath: string | null;
  ParentPath: string | null;
  CanGoUp: boolean;
  Directories: FolderEntry[];
  Error: string | null;
}
interface LibraryPathEntry { Name: string; Path: string }
interface FolderBrowserResponse { LibraryPaths: LibraryPathEntry[] }

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

test.beforeEach(() => {
  ensureCanariesPlanted(); // skips loudly w/o docker; guarantees canaries exist
});
test.afterEach(async () => {
  // The picker is read-only; nothing outside /media may ever change.
  expect(verifyCanaries(), 'FolderBrowser must never touch anything outside /media').toEqual([]);
  await assertPluginActive(ctx);
});

async function browse(path?: string): Promise<FolderBrowseResult> {
  const suffix = path === undefined ? 'Configuration/BrowseFolders' : `Configuration/BrowseFolders?path=${encodeURIComponent(path)}`;
  const res = await ctx.get(p(suffix));
  expect(res.ok(), `BrowseFolders status ${res.status()}`).toBeTruthy();
  return (await res.json()) as FolderBrowseResult;
}

test.describe('FolderBrowser - behavioral', () => {
  test('no path lists filesystem roots and cannot go up', async () => {
    const result = await browse();
    expect(result.Directories.length, 'roots should be listed').toBeGreaterThan(0);
    expect(result.CanGoUp, 'roots have no parent').toBe(false);
  });

  test('browsing /media lists the real Movies/Shows library dirs and can go up', async () => {
    const result = await browse('/media');
    const names = result.Directories.map((d) => d.Name);
    expect(names, 'Movies library dir must be listed under /media').toContain('Movies');
    expect(result.CanGoUp, '/media has a parent').toBe(true);
    // Entry paths are absolute and rooted at the browsed dir.
    for (const d of result.Directories) {
      expect(d.Path.startsWith('/media'), `child path ${d.Path} should be under /media`).toBe(true);
    }
  });

  test('LibraryPaths returns the configured libraries', async () => {
    const res = await ctx.get(p('Configuration/LibraryPaths'));
    expect(res.ok(), `LibraryPaths status ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as FolderBrowserResponse;
    expect(Array.isArray(body.LibraryPaths)).toBe(true);
    // global-setup created at least a Movies library pointing under /media.
    expect(body.LibraryPaths.some((e) => e.Path.includes('/media'))).toBe(true);
  });
});

test.describe('FolderBrowser - adversarial / hardening (canary-guarded)', () => {
  test('relative-traversal path is rejected with an Error, not an HTTP error or a listing', async () => {
    const result = await browse('../../../etc');
    expect(result.Error, 'a ".." path must be refused').toBeTruthy();
    expect(result.Error).toMatch(/\.\./);
    expect(result.Directories, 'a rejected path lists nothing').toEqual([]);
  });

  test('a path containing a NUL byte is refused (never lists a directory)', async () => {
    // A real embedded NUL (%00) - the injection the server guards against
    // (FolderBrowserService.ValidatePath: "Path contains invalid characters.").
    // Sent as %00 rather than a literal NUL in source so this file stays valid
    // UTF-8. Two acceptable outcomes, both non-listing: the ASP.NET pipeline may
    // reject the control character with a 400 before the action runs, OR the
    // action runs and returns its own {Error} with no Directories. A 200 that
    // LISTS anything would mean the NUL slipped through - the regression we catch.
    const res = await ctx.get(p('Configuration/BrowseFolders?path=/media%00/etc'));
    if (res.ok()) {
      const body = (await res.json()) as FolderBrowseResult;
      expect(body.Error, 'a NUL-byte path must be refused with an Error').toBeTruthy();
      expect(body.Directories, 'a rejected NUL path lists nothing').toEqual([]);
    } else {
      expect(res.status(), 'framework may reject the NUL before the action').toBe(400);
    }
  });

  test('a relative (non-absolute) path is rejected', async () => {
    const result = await browse('media/Movies');
    expect(result.Error, 'a non-absolute path must be refused').toBeTruthy();
    expect(result.Directories).toEqual([]);
  });

  test('a non-existent absolute path returns an Error, never a crash', async () => {
    const result = await browse('/no/such/dir/jfh-does-not-exist');
    expect(result.Error, 'a missing dir must produce an Error, not 500').toBeTruthy();
    expect(result.Directories).toEqual([]);
  });

  test('sensitive system directories are refused (not listed) and canaries stay intact', async () => {
    // Hardening: the picker must refuse Jellyfin's own /config, /data and OS roots like
    // /etc, /var - with the protected-folder Error and NO listing - so the admin can
    // neither browse into nor select them. (Consistent with the link-repair / trash
    // sensitive-path guard via the shared PathValidator.) The canary inside /config must
    // also remain byte-for-byte intact - the endpoint is strictly read-only.
    // The /config canary is guaranteed planted by ensureCanariesPlanted() in
    // beforeEach, so capture its content unconditionally as the baseline.
    const canaryInConfig = '/config/jfh-canary/marker.txt';
    expect(containerFileExists(canaryInConfig), 'config canary must be planted').toBe(true);
    const before = readContainerFile(canaryInConfig);

    for (const target of ['/config', '/config/data', '/etc', '/var']) {
      const result = await browse(target);
      expect(result.Error, `${target} must be refused as a protected folder`).toBeTruthy();
      expect(result.Error).toMatch(/protected system folder/i);
      expect(result.Directories, `${target} must list nothing`).toEqual([]);
    }

    // The canary inside /config is unchanged (refusal never touched it).
    expect(containerFileExists(canaryInConfig), 'config canary must survive').toBe(true);
    expect(readContainerFile(canaryInConfig)).toBe(before);
    // afterEach also runs full verifyCanaries() + assertPluginActive().
  });
});
