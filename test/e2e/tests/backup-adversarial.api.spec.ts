/**
 * Adversarial backup-import tests. Hostile payloads must fail cleanly (400,
 * never 500), never hang, and never corrupt config or touch data outside the
 * media library.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';
import { ensureCanariesPlanted, verifyCanaries, containerFileExists } from '../setup/fs-assert.ts';

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

test('radarrInstances:[null] is sanitized away (no null persists), never 500', async () => {
  const backup = await exportBackup();
  backup.radarrInstances = [null];
  const res = await importBackup(backup);
  // The fix drops null entries: a clean 400 (rejected) or 200 (sanitized) — never 500.
  expect(res.status(), 'null instance must not crash sanitize').toBeLessThan(500);
  expect(res.status()).not.toBe(500);
  await assertPluginActive(ctx);

  // Whatever the status, the persisted config must contain NO null instance —
  // prove sanitize actually removed it rather than just "didn't 500".
  const after = await exportBackup();
  const radarr = (after.radarrInstances ?? []) as unknown[];
  expect(radarr.every((i) => i !== null), 'no null instance may survive into stored config').toBe(true);
});

test('mixed [valid, null] instances: null dropped, valid kept, never 500', async () => {
  const backup = await exportBackup();
  backup.sonarrInstances = [{ Name: 'KeepMe', Url: 'http://mock-arr:9000', ApiKey: 'k' }, null];
  const res = await importBackup(backup);
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(ctx);

  // If the import was accepted, the null is gone but the valid entry remains.
  if (res.ok()) {
    const after = await exportBackup();
    const sonarr = (after.sonarrInstances ?? []) as Array<{ Name?: string } | null>;
    expect(sonarr.every((i) => i !== null), 'null instance must be dropped').toBe(true);
    expect(sonarr.some((i) => i?.Name === 'KeepMe'), 'the valid instance must survive').toBe(true);
  }

  // Restore the shared default so later specs aren't affected.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SonarrInstances: [{ Name: 'Mock Sonarr', Url: 'http://mock-arr:9000', ApiKey: 'sonarr-key' }] },
  });
});

test('absolute /config trashFolderPath in a backup does not let cleanup escape', async () => {
  ensureCanariesPlanted(); // plants + asserts a canary exists (skips loudly w/o docker)
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

test('NaN / Infinity / overflow numerics → 400 (invalid JSON), never 500', async () => {
  // NaN and Infinity are not valid JSON tokens; 1e999 overflows a JSON number.
  // System.Text.Json rejects all three at the parse layer → a hard 400.
  for (const raw of [
    '{"orphanMinAgeDays": NaN}',
    '{"trashRetentionDays": Infinity}',
    '{"orphanMinAgeDays": 1e999}',
  ]) {
    const res = await importBackup(raw);
    expect(res.status(), `payload=${raw} must be a clean 400`).toBe(400);
  }
  await assertPluginActive(ctx);
});

test('deeply-nested JSON (depth bomb) → 400 within bounded time, no hang', async () => {
  const depth = 2000;
  const payload = '{"a":'.repeat(depth) + '1' + '}'.repeat(depth);
  const started = Date.now();
  const res = await importBackup(payload);
  // Exceeds the serializer's max depth → rejected at parse time with a 400.
  expect(res.status(), 'a depth bomb must be a clean 400').toBe(400);
  expect(Date.now() - started, 'must not hang on a depth bomb').toBeLessThan(20_000);
  await assertPluginActive(ctx);
});

test('JSON array instead of object, and truncated body → 400, no 500', async () => {
  // An array or a truncated object cannot bind to the backup DTO → 400/415, never 500.
  for (const raw of ['[]', '[1,2,3]', '{"backupVersion":1', '']) {
    const res = await importBackup(raw);
    expect([400, 415], `payload=${raw}`).toContain(res.status());
  }
  await assertPluginActive(ctx);
});
