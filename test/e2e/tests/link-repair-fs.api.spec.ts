/**
 * Behavioural filesystem tests for link repair (.strm + symlinks). Link repair
 * never touches the deletion counter, so the current suite says NOTHING about
 * whether a link was actually rewritten. These read the real files via
 * `docker exec` to prove the repair happened / didn't / was refused.
 *
 * Also exercises the containment fix: absolute / traversal .strm targets that
 * point outside the library are classified InvalidContent and left byte-for-byte
 * unchanged (never rewritten toward a host-FS file).
 *
 * Requires the container FS; skips loudly when Docker is unreachable.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  regenFixtures,
  readContainerFile,
  readContainerSymlink,
  containerFileExists,
  verifyCanaries,
} from '../setup/fs-assert.ts';

const M = '/media/Movies';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    // Reset LinkRepairTaskMode too — this suite activates it, and leaving it on
    // would hand a destructive stage to later specs that run cleanup without
    // setting it explicitly.
    data: { LinkRepairTaskMode: 'DryRun', RecommendationsTaskMode: 'DryRun', UseTrash: false, OrphanMinAgeDays: 30 },
  }).catch(() => undefined);
  await ctx.dispose();
});

async function setLinkRepair(mode: 'Activate' | 'DryRun' | 'Deactivate') {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      LinkRepairTaskMode: mode,
      TrickplayTaskMode: 'Deactivate',
      EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate',
      RecommendationsTaskMode: 'Deactivate',
      UseTrash: false,
      OrphanMinAgeDays: 0,
    },
  });
  expect(res.ok(), `config update failed: ${res.status()}`).toBeTruthy();
}

const strm = (dir: string, file: string) => `${M}/${dir}/${file}`;

test.describe.serial('link repair rewrites / refuses correctly', () => {
  test.beforeEach(() => {
    ensureCanariesPlanted(); // skips loudly w/o docker; guarantees a canary exists
    regenFixtures();
  });

  test.afterEach(() => {
    expect(verifyCanaries(), 'canary files outside /media must be intact').toEqual([]);
  });

  test('DryRun does NOT modify a repairable .strm; Activate then rewrites it', async () => {
    const path = strm('Repairable Link (2020)', 'Repairable Link (2020).strm');
    const before = readContainerFile(path);
    expect(before).toContain('Old Name (2020).mkv');

    await setLinkRepair('DryRun');
    await runCleanupTask(ctx);
    expect(readContainerFile(path), 'DryRun must not touch the file').toBe(before);

    await setLinkRepair('Activate');
    await runCleanupTask(ctx);
    const after = readContainerFile(path);
    expect(after, 'Activate should rewrite to the lone sibling').toContain('Actual File (2020).mkv');
    expect(after).not.toContain('Old Name (2020).mkv');
    // Repair must not delete/move the target media.
    expect(containerFileExists(strm('Repairable Link (2020)', 'Actual File (2020).mkv'))).toBe(true);
  });

  test('ambiguous (2+ candidate videos) leaves the .strm untouched', async () => {
    const path = strm('Ambiguous Link (2020)', 'Ambiguous Link (2020).strm');
    const before = readContainerFile(path);
    await setLinkRepair('Activate');
    await runCleanupTask(ctx);
    expect(readContainerFile(path), 'must not guess between candidates').toBe(before);
    // Both candidates survive.
    expect(containerFileExists(strm('Ambiguous Link (2020)', 'Candidate A (2020).mkv'))).toBe(true);
    expect(containerFileExists(strm('Ambiguous Link (2020)', 'Candidate B (2020).mkv'))).toBe(true);
  });

  test('broken-unrepairable .strm is left intact (not fabricated, not deleted)', async () => {
    const path = strm('Broken Link (2020)', 'Broken Link (2020).strm');
    const before = readContainerFile(path);
    await setLinkRepair('Activate');
    await runCleanupTask(ctx);
    expect(readContainerFile(path)).toBe(before);
    expect(containerFileExists(path)).toBe(true);
  });

  test('URL .strm target is inert (Valid, never rewritten)', async () => {
    const path = strm('Stream Link (2020)', 'Stream Link (2020).strm');
    const before = readContainerFile(path);
    await setLinkRepair('Activate');
    await runCleanupTask(ctx);
    expect(readContainerFile(path)).toBe(before);
  });

  test('relative-traversal .strm (../../etc/passwd) is InvalidContent, unchanged', async () => {
    const path = strm('Escape Link (2020)', 'Escape Link (2020).strm');
    const before = readContainerFile(path);
    await setLinkRepair('Activate');
    await runCleanupTask(ctx);
    expect(readContainerFile(path), 'traversal target must not be repaired').toBe(before);
  });

  test('absolute .strm target outside the library (/etc/passwd) is refused, unchanged', async () => {
    // Verifies the containment fix: absolute targets are now validated too, so
    // link repair never enumerates or rewrites toward a host-FS directory.
    const path = strm('Abs Escape (2020)', 'Abs Escape (2020).strm');
    const before = readContainerFile(path);
    await setLinkRepair('Activate');
    await runCleanupTask(ctx);
    expect(readContainerFile(path), 'absolute out-of-library target must not be repaired').toBe(before);
    // The canary check (afterEach) proves nothing outside /media was touched.
  });

  test('broken symlink is repaired to the lone renamed sibling; valid symlink unchanged', async () => {
    const brokenLink = strm('Broken Symlink (2020)', 'Broken Symlink (2020).mkv');
    const validLink = strm('Valid Symlink (2020)', 'Valid Symlink (2020).mkv');
    const validBefore = readContainerSymlink(validLink);
    expect(validBefore).toContain('Real Target (2020).mkv');

    await setLinkRepair('Activate');
    await runCleanupTask(ctx);

    // Broken symlink now points at the only renamed sibling in its dir.
    expect(readContainerSymlink(brokenLink)).toContain('Renamed Actual (2020).mkv');
    // Valid symlink is untouched.
    expect(readContainerSymlink(validLink)).toBe(validBefore);
  });
});
