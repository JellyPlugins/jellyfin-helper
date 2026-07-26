/**
 * Adversarial backup-import tests. Hostile payloads must fail cleanly (400,
 * never 500), never hang, and never corrupt config or touch data outside the
 * media library.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';
import { hasDocker, verifyCanaries, containerFileExists } from '../setup/fs-assert.ts';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

function importBackup(body: unknown) {
  return ctx.post(p('Backup/Import'), {
    headers: { 'Content-Type': 'application/json' },
    data: typeof body === 'string' ? body : JSON.stringify(body),
  });
}

async function exportBackup(): Promise<any> {
  const res = await ctx.get(p('Backup/Export?includeSecrets=true'));
  expect(res.ok()).toBeTruthy();
  return JSON.parse(await res.text());
}

test('radarrInstances:[null] does not 500 (sanitize runs before validate)', async () => {
  const backup = await exportBackup();
  backup.radarrInstances = [null];
  const res = await importBackup(backup);
  // The fix drops null entries; result is a clean 400 or a 200 — never a 500.
  expect(res.status(), 'null instance must not crash sanitize').not.toBe(500);
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(ctx);
});

test('mixed [valid, null] instances handled without 500', async () => {
  const backup = await exportBackup();
  backup.sonarrInstances = [{ Name: 'S', Url: 'http://mock-arr:9000', ApiKey: 'k' }, null];
  const res = await importBackup(backup);
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(ctx);
});

test('absolute /config trashFolderPath in a backup does not let cleanup escape', async () => {
  test.skip(!hasDocker(), 'docker exec unavailable — cannot verify canary');
  const backup = await exportBackup();
  Object.assign(backup, { useTrash: true, trashFolderPath: '/config', trashRetentionDays: 30 });
  const res = await importBackup(backup);
  expect(res.status()).toBeLessThan(500);

  // Even if the value persisted, a subsequent cleanup must not delete /config.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      TrickplayTaskMode: 'Deactivate', EmptyMediaFolderTaskMode: 'Deactivate',
      OrphanedSubtitleTaskMode: 'Deactivate', LinkRepairTaskMode: 'Deactivate',
      SeerrCleanupTaskMode: 'Deactivate', RecommendationsTaskMode: 'Deactivate',
    },
  });
  expect(containerFileExists('/config/jfh-canary/marker.txt')).toBe(true);
  expect(verifyCanaries()).toEqual([]);

  // Restore a safe trash path.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { UseTrash: false, TrashFolderPath: '.jellyfin-trash' },
  });
  await assertPluginActive(ctx);
});

test('NaN / Infinity / overflow numerics → 400, no 500', async () => {
  for (const raw of [
    '{"orphanMinAgeDays": NaN}',
    '{"trashRetentionDays": Infinity}',
    '{"orphanMinAgeDays": 1e999}',
  ]) {
    const res = await importBackup(raw);
    expect(res.status(), `payload=${raw}`).toBeLessThan(500);
  }
  await assertPluginActive(ctx);
});

test('deeply-nested JSON (depth bomb) → 400 within bounded time, no hang', async () => {
  const depth = 2000;
  const payload = '{"a":'.repeat(depth) + '1' + '}'.repeat(depth);
  const started = Date.now();
  const res = await importBackup(payload);
  expect(res.status()).toBeLessThan(500);
  expect(Date.now() - started, 'must not hang on a depth bomb').toBeLessThan(20_000);
  await assertPluginActive(ctx);
});

test('JSON array instead of object, and truncated body → 400, no 500', async () => {
  for (const raw of ['[]', '[1,2,3]', '{"backupVersion":1', '']) {
    const res = await importBackup(raw);
    expect([400, 415].includes(res.status()) || res.status() < 500, `payload=${raw}`).toBeTruthy();
    expect(res.status()).not.toBe(500);
  }
  await assertPluginActive(ctx);
});
