/**
 * Behavioural filesystem tests for POST /Trash/Relocate - the four
 * absolute/relative quadrants - plus the POST /Trash/CheckAccess success path.
 *
 * The existing trash specs cover only Relocate's REFUSAL/error branches and a
 * loose rel->rel "or degrades cleanly" call that seeds nothing and asserts no FS
 * state. These tests seed REAL trash content and prove the move happened on disk:
 * Moved > 0 / Failed == 0, the source is emptied (its now-empty folder removed),
 * and the destination holds exactly the moved entry with byte-identical content
 * (sha256). Relocate moves each child entry of the trash folder (dirs + files)
 * and removes the source folder once empty (TrashService.RelocateTrashContents).
 *
 * Path model: libraries are /media/Movies and /media/Shows. A path strictly
 * inside a library root is always a permitted trash target; a relative path is
 * resolved per-library. Absolute cases use one library (/media/Movies) so the
 * move is a single, unambiguous relocation.
 *
 * Requires the container FS (docker exec); skips loudly when unavailable.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  verifyCanaries,
  containerDirExists,
  containerFileExists,
  containerLs,
  containerMkdir,
  containerWriteFile,
  containerRm,
  sha256,
} from '../setup/fs-assert.ts';

const MOVIES = '/media/Movies';
const SHOWS = '/media/Shows';
const LIBS = [MOVIES, SHOWS];
const MARKER = 'RELOCATE-CONTENT';

let ctx: APIRequestContext;

interface RelocateResponse { Moved: number; Failed: number }

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
  // Trash must be enabled for relocation to be meaningful; keep the default path.
  const cfg = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 },
  });
  expect(cfg.ok(), `trash config failed: ${cfg.status()}`).toBeTruthy();
});

test.afterAll(async () => {
  await ctx
    .put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { UseTrash: false, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 },
    })
    .catch(() => undefined);
  await ctx.dispose();
});

/** Seed a trash "entry" folder holding one known file; returns the file path. */
function seedEntry(trashDir: string, entryName: string): string {
  const file = `${trashDir}/${entryName}/payload.mkv`;
  containerWriteFile(file, MARKER);
  return file;
}

function relocate(oldPath: string, newPath: string) {
  return ctx.post(p('Trash/Relocate'), {
    headers: { 'Content-Type': 'application/json' },
    data: { OldTrashPath: oldPath, NewTrashPath: newPath },
  });
}

test.describe.serial('Trash/Relocate moves real content across all four quadrants', () => {
  test.beforeEach(() => {
    ensureCanariesPlanted(); // skips loudly w/o docker; guarantees a canary exists
    // Clean any relocate scratch dirs from a previous test in both libraries.
    for (const lib of LIBS) {
      for (const d of ['.jellyfin-trash', '.jellyfin-trash-2', '.abs-old', '.abs-new', '.rel-abs-dest']) {
        containerRm(`${lib}/${d}`);
      }
    }
  });

  test.afterEach(async () => {
    expect(verifyCanaries(), 'relocation must never touch anything outside /media').toEqual([]);
    await assertPluginActive(ctx);
  });

  test('rel → rel: per-library trash contents move, source removed, content intact', async () => {
    // Seed a distinct entry in each library's default trash folder.
    for (const lib of LIBS) {
      seedEntry(`${lib}/.jellyfin-trash`, 'Old Movie (2001)');
    }
    const hashesBefore = LIBS.map((lib) => sha256(`${lib}/.jellyfin-trash/Old Movie (2001)/payload.mkv`));

    const res = await relocate('.jellyfin-trash', '.jellyfin-trash-2');
    expect(res.ok(), `relocate status ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as RelocateResponse;
    expect(body.Failed, 'no entry should fail to move').toBe(0);
    // Each seeded library contributes one moved entry. Assert at least our two
    // seeded entries moved (library enumeration is the plugin's, not ours).
    expect(body.Moved, 'both seeded libraries contribute a moved entry').toBeGreaterThanOrEqual(LIBS.length);

    for (let i = 0; i < LIBS.length; i++) {
      const lib = LIBS[i];
      // Destination holds the entry with byte-identical content...
      expect(containerFileExists(`${lib}/.jellyfin-trash-2/Old Movie (2001)/payload.mkv`), `${lib} dest file`).toBe(true);
      expect(sha256(`${lib}/.jellyfin-trash-2/Old Movie (2001)/payload.mkv`)).toBe(hashesBefore[i]);
      // ...and the source trash folder is gone (emptied then removed).
      expect(containerDirExists(`${lib}/.jellyfin-trash`), `${lib} source trash removed`).toBe(false);
    }
  });

  test('abs → abs: single absolute relocation moves the entry and empties the source', async () => {
    const oldAbs = `${MOVIES}/.abs-old`;
    const newAbs = `${MOVIES}/.abs-new`;
    const before = sha256(seedEntry(oldAbs, 'Absolute Entry'));

    const res = await relocate(oldAbs, newAbs);
    expect(res.ok(), `relocate status ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as RelocateResponse;
    expect(body.Failed).toBe(0);
    expect(body.Moved, 'the one seeded entry moved').toBe(1);

    expect(containerFileExists(`${newAbs}/Absolute Entry/payload.mkv`)).toBe(true);
    expect(sha256(`${newAbs}/Absolute Entry/payload.mkv`)).toBe(before);
    expect(containerDirExists(oldAbs), 'absolute source removed once empty').toBe(false);
  });

  test('abs → rel: absolute source drains into the first library’s relative target', async () => {
    const oldAbs = `${MOVIES}/.abs-old`;
    const before = sha256(seedEntry(oldAbs, 'From Absolute'));

    const res = await relocate(oldAbs, '.jellyfin-trash-2');
    expect(res.ok(), `relocate status ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as RelocateResponse;
    expect(body.Failed).toBe(0);
    expect(body.Moved, 'the absolute source moved once (to the first library)').toBe(1);

    // The controller relocates an absolute source WITHIN the library that contains
    // it (deterministic, source-driven - not "whichever library enumerated first").
    expect(containerFileExists(`${MOVIES}/.jellyfin-trash-2/From Absolute/payload.mkv`)).toBe(true);
    expect(sha256(`${MOVIES}/.jellyfin-trash-2/From Absolute/payload.mkv`)).toBe(before);
    expect(containerDirExists(oldAbs), 'absolute source removed once empty').toBe(false);
  });

  test('rel → abs: per-library relative trash merges into one absolute target', async () => {
    // Seed a distinctly-named entry in each library's default trash folder so the
    // merge target ends up holding both.
    seedEntry(`${MOVIES}/.jellyfin-trash`, 'Movie Trash Item');
    seedEntry(`${SHOWS}/.jellyfin-trash`, 'Show Trash Item');
    const dest = `${MOVIES}/.rel-abs-dest`;

    const res = await relocate('.jellyfin-trash', dest);
    expect(res.ok(), `relocate status ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as RelocateResponse;
    expect(body.Failed).toBe(0);
    expect(body.Moved, 'both libraries merged their entry into the absolute target').toBeGreaterThanOrEqual(2);

    const names = containerLs(dest);
    expect(names, 'merged target holds both entries').toEqual(
      expect.arrayContaining(['Movie Trash Item', 'Show Trash Item']),
    );
    expect(containerFileExists(`${dest}/Movie Trash Item/payload.mkv`)).toBe(true);
    expect(containerFileExists(`${dest}/Show Trash Item/payload.mkv`)).toBe(true);
    // Both source trash folders are emptied and removed.
    for (const lib of LIBS) {
      expect(containerDirExists(`${lib}/.jellyfin-trash`), `${lib} source removed`).toBe(false);
    }
  });
});

test.describe('Trash/CheckAccess success path', () => {
  test.beforeEach(() => {
    ensureCanariesPlanted();
  });
  test.afterEach(async () => {
    expect(verifyCanaries()).toEqual([]);
    await assertPluginActive(ctx);
  });

  test('a valid, writable relative trash path reports AllAccessible with per-library read/write probes', async () => {
    // Ensure a writable trash dir exists in each library.
    for (const lib of LIBS) {
      containerMkdir(`${lib}/.jellyfin-trash`);
    }
    const res = await ctx.post(p('Trash/CheckAccess'), {
      headers: { 'Content-Type': 'application/json' },
      data: { TrashFolderPath: '.jellyfin-trash' },
    });
    expect(res.ok(), `CheckAccess status ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as {
      AllAccessible: boolean;
      Results: Array<{ Path: string; LibraryRoot?: string; Exists: boolean; CanRead: boolean; CanWrite: boolean; HasFullAccess: boolean }>;
    };
    expect(body.AllAccessible, 'a writable trash dir in every library must be fully accessible').toBe(true);
    expect(body.Results.length, 'at least one result per seeded library').toBeGreaterThanOrEqual(LIBS.length);
    for (const r of body.Results) {
      expect(r.Exists).toBe(true);
      expect(r.CanRead).toBe(true);
      expect(r.CanWrite).toBe(true);
      expect(r.HasFullAccess).toBe(true);
    }
    // Cleanup.
    for (const lib of LIBS) {
      containerRm(`${lib}/.jellyfin-trash`);
    }
  });
});
