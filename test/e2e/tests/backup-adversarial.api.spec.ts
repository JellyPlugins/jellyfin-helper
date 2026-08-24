/**
 * Adversarial backup-import tests. Hostile payloads must fail cleanly (400,
 * never 500), never hang, and never corrupt config or touch data outside the
 * media library.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive, runCleanupTask, runLibraryScan } from '../setup/api-client.ts';
import { ensureCanariesPlanted, verifyCanaries, containerFileExists, regenFixtures } from '../setup/fs-assert.ts';

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
  // The fix drops null entries: a clean 400 (rejected) or 200 (sanitized) - never 500.
  expect(res.status(), 'null instance must not crash sanitize').toBeLessThan(500);
  expect(res.status()).not.toBe(500);
  await assertPluginActive(ctx);

  // Whatever the status, the persisted config must contain NO null instance -
  // prove sanitize actually removed it rather than just "didn't 500".
  const after = await exportBackup();
  const radarr = (after.radarrInstances ?? []) as unknown[];
  expect(radarr.every((i) => i !== null), 'no null instance may survive into stored config').toBe(true);

  // Restore a known-good Radarr instance so later specs inherit a working one
  // (the [null] import may have left RadarrInstances empty).
  await ctx
    .put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { RadarrInstances: [{ Name: 'Mock Radarr', Url: 'http://mock-arr:9000', ApiKey: 'radarr-key' }] },
    })
    .catch(() => undefined);
});

test('mixed [valid, null] instances: null dropped, valid kept, never 500', async () => {
  const backup = await exportBackup();
  backup.sonarrInstances = [{ Name: 'KeepMe', Url: 'http://mock-arr:9000', ApiKey: 'k' }, null];
  const res = await importBackup(backup);
  try {
    expect(res.status()).toBeLessThan(500);
    await assertPluginActive(ctx);

    // If the import was accepted, the null is gone but the valid entry remains.
    if (res.ok()) {
      const after = await exportBackup();
      // Export serializes with the camelCase policy (+ [JsonPropertyName("name")]),
      // so the round-tripped instance key is `name`, not `Name`.
      const sonarr = (after.sonarrInstances ?? []) as Array<{ name?: string } | null>;
      expect(sonarr.every((i) => i !== null), 'null instance must be dropped').toBe(true);
      expect(sonarr.some((i) => i?.name === 'KeepMe'), 'the valid instance must survive').toBe(true);
    }
  } finally {
    // Restore the shared default so later specs aren't affected, even on failure.
    await ctx
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: { SonarrInstances: [{ Name: 'Mock Sonarr', Url: 'http://mock-arr:9000', ApiKey: 'sonarr-key' }] },
      })
      .catch(() => undefined);
  }
});

test('absolute /config trashFolderPath in a backup does not let cleanup escape', async () => {
  ensureCanariesPlanted(); // plants + asserts a canary exists (skips loudly w/o docker)

  // Drive the ENTIRE hostile setup through the backup IMPORT, not a PUT. The import
  // guard (BackupService.RestoreConfiguration) only strips `..` traversal - it does
  // NOT reject an absolute sensitive path - so it persists trashFolderPath='/config'
  // verbatim AND applies the task modes from the backup. A PUT /Configuration with
  // a sensitive absolute TrashFolderPath + UseTrash:true is (correctly) rejected 400
  // by ValidateTrashPathStrict, so it can't be used to arm this test. This asymmetry
  // - weaker import guard vs stricter PUT guard - is exactly what the test proves:
  // even when /config slips in via import with the destructive FS stages ACTIVE,
  // cleanup must still refuse to touch anything under /config.
  // (Task-mode keys are camelCase so System.Text.Json binds them onto BackupData.)
  const backup = await exportBackup();
  Object.assign(backup, {
    useTrash: true,
    trashFolderPath: '/config',
    trashRetentionDays: 30,
    trickplayTaskMode: 'Activate',
    emptyMediaFolderTaskMode: 'Activate',
    orphanedSubtitleTaskMode: 'Activate',
    linkRepairTaskMode: 'Activate',
    seerrCleanupTaskMode: 'Deactivate',
    recommendationsTaskMode: 'Deactivate',
  });
  const res = await importBackup(backup);
  expect(res.status(), 'hostile /config import must not 500').toBeLessThan(500);

  try {
    // Cleanup now runs with the destructive FS stages ACTIVE and trash pointed at
    // /config. Even so, nothing under /config may be deleted or moved.
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status, 'cleanup task must not have crashed').toBe('Completed');
    expect(containerFileExists('/config/jfh-canary/marker.txt')).toBe(true);
    expect(verifyCanaries()).toEqual([]);
  } finally {
    // ALWAYS restore a SAFE, relative trash path and re-deactivate the destructive
    // stages - even if an assertion threw. A relative path ('.jellyfin-trash') is not
    // sensitive, so this PUT is accepted (never 400) by ValidateTrashPathStrict.
    await ctx
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: {
          UseTrash: false, TrashFolderPath: '.jellyfin-trash',
          TrickplayTaskMode: 'Deactivate', EmptyMediaFolderTaskMode: 'Deactivate',
          OrphanedSubtitleTaskMode: 'Deactivate', LinkRepairTaskMode: 'Deactivate',
        },
      })
      .catch(() => undefined);
    // This is the first spec that runs the destructive FS stages GENUINELY active
    // against /media (the old PUT-based version 400'd and never truly activated them).
    // Those stages consume the shared /media fixtures, so rebuild them and re-scan -
    // otherwise later FS specs that DON'T regenerate (growth-timeline-fs, insights-fs,
    // media-stats-fs) would see a depleted library.
    regenFixtures();
    await runLibraryScan(ctx).catch(() => undefined);
  }
  await assertPluginActive(ctx);
});

test('NaN / Infinity / overflow numerics → 400 (invalid JSON), never 500', async () => {
  // NaN and Infinity are not valid JSON tokens; 1e999 overflows a JSON number.
  // System.Text.Json rejects all three at the parse layer -> a hard 400.
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
  // Exceeds the serializer's max depth -> rejected at parse time with a 400.
  expect(res.status(), 'a depth bomb must be a clean 400').toBe(400);
  expect(Date.now() - started, 'must not hang on a depth bomb').toBeLessThan(20_000);
  await assertPluginActive(ctx);
});

test('JSON array instead of object, and truncated body → 400, no 500', async () => {
  // An array or a truncated object cannot bind to the backup DTO -> 400/415, never 500.
  for (const raw of ['[]', '[1,2,3]', '{"backupVersion":1', '']) {
    const res = await importBackup(raw);
    expect([400, 415], `payload=${raw}`).toContain(res.status());
  }
  await assertPluginActive(ctx);
});
