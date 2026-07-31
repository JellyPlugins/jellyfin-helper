/**
 * Adversarial trash tests - prove misuse can't touch data outside the media
 * library. These exercise the containment fix directly: an absolute trash path
 * pointing at a sensitive dir (e.g. Jellyfin's /config) must be REFUSED, and the
 * library-external canaries must survive every attempt.
 *
 * Requires the container FS; skips loudly when Docker is unreachable.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  verifyCanaries,
  containerFileExists,
  containerMkdir,
} from '../setup/fs-assert.ts';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  // Leave trash config in a benign state for later specs.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { UseTrash: false, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 },
  }).catch(() => undefined);
  await ctx.dispose();
});

test.describe.serial('trash operations never escape the media library', () => {
  test.beforeEach(() => {
    // Plants every canary (incl. /config/jfh-canary/marker.txt) and asserts at
    // least one exists, so verifyCanaries() below can't pass vacuously; skips
    // loudly when Docker is unreachable.
    ensureCanariesPlanted();
  });

  test.afterEach(() => {
    expect(verifyCanaries(), 'nothing outside /media may be touched').toEqual([]);
  });

  test('an absolute /config trash path is refused at save, and DELETE never wipes config', async () => {
    // Containment now happens at CONFIG-SAVE time: persisting an absolute sensitive
    // trash path (Jellyfin's own /config) is rejected with 400, so the dangerous value
    // never lands - a strictly stronger defense than catching it later at delete time.
    const put = await ctx.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { UseTrash: true, TrashFolderPath: '/config', TrashRetentionDays: 30 },
    });
    expect(put.status(), 'sensitive absolute trash path must be rejected at save').toBe(400);

    // Because it was never persisted, a subsequent delete operates on the safe default
    // path and must NOT recursively delete Jellyfin's config dir.
    const res = await ctx.delete(p('Trash/Folders'));
    expect(res.status(), 'delete runs against the safe default, never /config').not.toBe(500);
    expect(containerFileExists('/config/jfh-canary/marker.txt'), 'config canary intact').toBe(true);
    await assertPluginActive(ctx);
  });

  test('Trash/Relocate with an absolute /config source is refused (no drain into library)', async () => {
    const res = await ctx.post(p('Trash/Relocate'), {
      headers: { 'Content-Type': 'application/json' },
      data: { OldTrashPath: '/config', NewTrashPath: '/media/Movies/.jellyfin-trash' },
    });
    expect(res.status()).toBe(400);
    expect(containerFileExists('/config/jfh-canary/marker.txt')).toBe(true);
    await assertPluginActive(ctx);
  });

  test('Trash/Relocate old-absolute-sensitive / new-relative is refused', async () => {
    const res = await ctx.post(p('Trash/Relocate'), {
      headers: { 'Content-Type': 'application/json' },
      data: { OldTrashPath: '/config', NewTrashPath: '.jellyfin-trash' },
    });
    expect(res.status()).toBe(400);
    expect(containerFileExists('/config/jfh-canary/marker.txt')).toBe(true);
    await assertPluginActive(ctx);
  });

  test('Trash/Relocate into an absolute /config destination is refused', async () => {
    // Seed a real relative trash source so only the DESTINATION is the problem. A
    // relative old + absolute new routes to the "old-relative/new-absolute" branch
    // where only the destination guard fires - genuinely exercising the target
    // rejection instead of duplicating the source-guard tests above.
    containerMkdir('/media/Movies/.jellyfin-trash');
    const res = await ctx.post(p('Trash/Relocate'), {
      headers: { 'Content-Type': 'application/json' },
      data: { OldTrashPath: '.jellyfin-trash', NewTrashPath: '/config/stolen' },
    });
    expect(res.status()).toBe(400);
    expect(containerFileExists('/config/jfh-canary/marker.txt')).toBe(true);
    await assertPluginActive(ctx);
  });

  test('traversal / overlong CheckAccess still rejected (400) with canary intact', async () => {
    for (const bad of ['a/../b', 'a'.repeat(600)]) {
      const res = await ctx.post(p('Trash/CheckAccess'), {
        headers: { 'Content-Type': 'application/json' },
        data: { TrashFolderPath: bad },
      });
      expect(res.status(), `path=${bad.slice(0, 12)}`).toBe(400);
    }
    expect(containerFileExists('/config/jfh-canary/marker.txt')).toBe(true);
    await assertPluginActive(ctx);
  });

  test('Windows/UNC path strings on a Linux container do not create odd media dirs', async () => {
    for (const p2 of ['C:\\Windows', '\\\\host\\share\\trash']) {
      const res = await ctx.post(p('Trash/Relocate'), {
        headers: { 'Content-Type': 'application/json' },
        data: { OldTrashPath: p2, NewTrashPath: '.jellyfin-trash' },
      });
      expect(res.status(), `path=${p2}`).toBeLessThan(500);
    }
    expect(verifyCanaries()).toEqual([]);
    await assertPluginActive(ctx);
  });
});
