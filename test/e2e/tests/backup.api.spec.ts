/**
 * Backup export -> tamper -> import round-trip, plus the documented gotchas:
 *   - Backup is JSON (distinct from the plugin's XML config file).
 *   - Export redacts API keys unless includeSecrets=true.
 *   - Some fields are intentionally NOT exported (MaxRecommendationsPerUser,
 *     ensemble tuning, cumulative stats) - a round-trip must not claim to
 *     restore them.
 *   - Import validates: garbage/oversized/broken JSON is rejected (400), and
 *     the server + plugin survive it (hardening).
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive, sleep } from '../setup/api-client.ts';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

async function exportBackup(includeSecrets = false): Promise<any> {
  const res = await ctx.get(p(`Backup/Export?includeSecrets=${includeSecrets}`));
  expect(res.ok(), `export failed: ${res.status()}`).toBeTruthy();
  return JSON.parse(await res.text());
}

async function importBackup(body: unknown) {
  return ctx.post(p('Backup/Import'), {
    headers: { 'Content-Type': 'application/json' },
    data: typeof body === 'string' ? body : JSON.stringify(body),
  });
}

/** Seed/mutate config and fail loudly if the save is rejected (mirrors hardening.api.spec.ts). */
async function putConfig(data: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data,
  });
  expect(res.ok(), `setup putConfig failed: ${res.status()}`).toBeTruthy();
  return res;
}

test('export produces a valid backup with redacted secrets by default', async () => {
  // Seed a known config first.
  await putConfig({ Language: 'de', OrphanMinAgeDays: 12, SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'topsecret' });

  const backup = await exportBackup(false);
  expect(backup.language).toBe('de');
  expect(backup.orphanMinAgeDays).toBe(12);
  // Secret redacted (empty), and flagged as not containing secrets.
  expect(backup.seerrApiKey === '' || backup.seerrApiKey == null).toBeTruthy();
  expect(backup.containsSecrets).toBeFalsy();
});

test('export with includeSecrets=true includes the key and flags it', async () => {
  const backup = await exportBackup(true);
  expect(backup.containsSecrets).toBe(true);
  expect(backup.seerrApiKey).toBe('topsecret');
});

test('round-trip: export → change config → import restores exported values', async () => {
  const backup = await exportBackup(true);

  // Mutate config away from the backup.
  await putConfig({ Language: 'fr', OrphanMinAgeDays: 99 });
  const changed = await ctx.get(p('Configuration')).then((r) => r.json());
  expect(changed.Language).toBe('fr');

  // Import the earlier backup - should restore language + age.
  const res = await importBackup(backup);
  expect(res.ok(), `import failed: ${res.status()} ${await res.text()}`).toBeTruthy();
  const summary = (await res.json()) as { summary: { ConfigurationRestored: boolean } };
  expect(summary.summary.ConfigurationRestored).toBe(true);

  const restored = await ctx.get(p('Configuration')).then((r) => r.json());
  expect(restored.Language).toBe('de');
  expect(restored.OrphanMinAgeDays).toBe(12);
  await assertPluginActive(ctx);
});

test('tampered task mode in backup restores the tampered (valid) value', async () => {
  const backup = await exportBackup(true);
  backup.trickplayTaskMode = 'Activate';
  backup.emptyMediaFolderTaskMode = 'Deactivate';

  const res = await importBackup(backup);
  expect(res.ok()).toBeTruthy();

  const cfg = await ctx.get(p('Configuration')).then((r) => r.json());
  expect(cfg.TrickplayTaskMode).toBe('Activate');
  expect(cfg.EmptyMediaFolderTaskMode).toBe('Deactivate');
});

test('unknown task-mode string falls back safely (no crash)', async () => {
  const backup = await exportBackup(true);
  backup.trickplayTaskMode = 'TotallyInvalidMode';

  const res = await importBackup(backup);
  // Sanitizer/validator either repairs to a default or rejects - must not 500.
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(ctx);
});

// --- hardening: broken import payloads must be rejected, server survives ----

test('import rejects non-JSON garbage with 400, plugin stays Active', async () => {
  const res = await importBackup('this is not json at all }{');
  expect(res.status()).toBe(400);
  await assertPluginActive(ctx);
});

test('import rejects empty body', async () => {
  const res = await importBackup('');
  expect(res.status()).toBeLessThan(500);
  expect(res.status()).toBeGreaterThanOrEqual(400);
  await assertPluginActive(ctx);
});

test('import rejects a JSON array (wrong shape)', async () => {
  const res = await importBackup('[1,2,3]');
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(ctx);
});

test('import tolerates/repairs missing fields without crashing', async () => {
  // A minimal object with only backupVersion - sanitizer should fill defaults.
  const res = await importBackup({ backupVersion: 1 });
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(ctx);
});

test('HARDENING: backup with negative trends values is sanitized (clamped to 0), never corrupt', async () => {
  // A cumulative byte size / file count is physically non-negative. BackupSanitizer
  // clamps negatives to 0 on import (the validator only warns), so a hostile/corrupt
  // backup can never plant a negative point that surfaces on GET GrowthTimeline.
  const backup = await exportBackup(true);
  backup.growthTimeline = {
    granularity: 'Daily',
    dataPoints: [
      { date: '2025-01-01T00:00:00Z', cumulativeSize: -5000, cumulativeFileCount: -3 },
    ],
  };

  const res = await importBackup(backup);
  try {
    expect(res.status(), 'must not throw a server error on negative values').toBeLessThan(500);
    await assertPluginActive(ctx);

    // The trends endpoint must not surface negative garbage - the sanitizer clamped
    // the imported -5000 / -3 to 0 before they were ever persisted to the cache.
    const trends = await ctx.get(p('GrowthTimeline'));
    if (trends.ok()) {
      const tbody = (await trends.json()) as { DataPoints?: any[]; dataPoints?: any[] };
      for (const pt of tbody.DataPoints ?? tbody.dataPoints ?? []) {
        const size = pt.CumulativeSize ?? pt.cumulativeSize ?? 0;
        const count = pt.CumulativeFileCount ?? pt.cumulativeFileCount ?? 0;
        expect(size, 'sanitizer clamped negative size to 0 on import').toBeGreaterThanOrEqual(0);
        expect(count, 'sanitizer clamped negative count to 0 on import').toBeGreaterThanOrEqual(0);
      }
    }
  } finally {
    // Belt-and-suspenders hygiene: recompute a fresh timeline from the real library so
    // downstream specs read library-derived data rather than this test's single planted
    // (now clamped-to-0) point. Tolerate the 30s recompute rate-limit (429) with one retry.
    // NOTE: recompute alone would NOT purge a negative if one had persisted - the
    // append-only path keeps historical points; the real guarantee is the import-time clamp.
    let refresh = await ctx.get(p('GrowthTimeline?forceRefresh=true'));
    if (refresh.status() === 429) {
      await sleep(31_000);
      refresh = await ctx.get(p('GrowthTimeline?forceRefresh=true'));
    }
    expect(refresh.ok(), `timeline recompute failed: ${refresh.status()}`).toBeTruthy();
  }
});

test('redacted re-import preserves the live Seerr key (empty value = leave in place)', async () => {
  await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'topsecret' });

  const redacted = await exportBackup(false);
  expect(redacted.seerrApiKey === '' || redacted.seerrApiKey == null).toBeTruthy();
  expect(redacted.containsSecrets).toBeFalsy();

  const res = await importBackup(redacted);
  expect(res.ok()).toBeTruthy();
  const summary = (await res.json()) as { summary: { CredentialsChanged: boolean } };
  expect(summary.summary.CredentialsChanged).toBeFalsy();

  // The key survived the empty-value restore branch.
  const withSecrets = await exportBackup(true);
  expect(withSecrets.seerrApiKey).toBe('topsecret');
  await assertPluginActive(ctx);
});

test('import defangs a traversal trash path (UseTrash off) to the default', async () => {
  const backup = await exportBackup(true);
  backup.useTrash = false;
  backup.trashFolderPath = '../../etc';

  const res = await importBackup(backup);
  expect(res.ok()).toBeTruthy();
  const cfg = await ctx.get(p('Configuration')).then((r) => r.json());
  expect(cfg.TrashFolderPath).toBe('.jellyfin-trash');
  await assertPluginActive(ctx);
});

test('import rejects an invalid SeerrUrl scheme with 400 (hard validator error)', async () => {
  for (const url of ['file:///etc/passwd', 'javascript:alert(1)']) {
    const backup = await exportBackup(true);
    backup.seerrUrl = url;
    const res = await importBackup(backup);
    expect(res.status(), `url=${url}`).toBe(400);
    await assertPluginActive(ctx);
  }
});

test('import clamps out-of-range numeric fields to succeed (not 400)', async () => {
  const backup = await exportBackup(true);
  backup.orphanMinAgeDays = 999999;
  backup.trashRetentionDays = -10;

  const res = await importBackup(backup);
  expect(res.status()).toBe(200);
  const cfg = await ctx.get(p('Configuration')).then((r) => r.json());
  expect(cfg.OrphanMinAgeDays).toBeLessThanOrEqual(3650);
  expect(cfg.TrashRetentionDays).toBeGreaterThanOrEqual(0);
  await assertPluginActive(ctx);
});

test('import success summary is a PascalCase four-field object; CredentialsChanged flips on new key', async () => {
  await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'oldkey' });
  const backup = await exportBackup(true);
  backup.seerrApiKey = 'a-different-key';

  const res = await importBackup(backup);
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as {
    warnings: unknown[];
    summary: { ConfigurationRestored: boolean; TimelineRestored: boolean; BaselineRestored: boolean; CredentialsChanged: boolean };
  };
  expect(Array.isArray(body.warnings)).toBe(true);
  for (const k of ['ConfigurationRestored', 'TimelineRestored', 'BaselineRestored', 'CredentialsChanged'] as const) {
    expect(typeof body.summary[k]).toBe('boolean');
  }
  expect(body.summary.CredentialsChanged).toBe(true);
  await assertPluginActive(ctx);
});

test('import with wrong Content-Type is rejected before body read (no 500)', async () => {
  const res = await ctx.post(p('Backup/Import'), {
    headers: { 'Content-Type': 'text/plain' },
    data: JSON.stringify({ backupVersion: 1 }),
  });
  expect([400, 415]).toContain(res.status());
  await assertPluginActive(ctx);
});

// --- full round-trip: prove restore actually restores (not just "no 500") ---

async function getConfig(): Promise<any> {
  const res = await ctx.get(p('Configuration'));
  expect(res.ok()).toBeTruthy();
  return res.json();
}

test('full config field-set round-trips through export → mutate → import', async () => {
  const backup = await exportBackup(true);
  // Mutate a broad set of fields with real restore logic to distinct known values.
  Object.assign(backup, {
    language: 'de',
    excludedLibraries: 'Music,Home Videos',
    orphanMinAgeDays: 9,
    useTrash: true,
    trashFolderPath: '.custom-trash',
    trashRetentionDays: 7,
    seerrCleanupAgeDays: 0, // the "0 is applied" (not null=absent) case
    trickplayTaskMode: 'Activate',
    emptyMediaFolderTaskMode: 'DryRun',
    orphanedSubtitleTaskMode: 'Deactivate',
    linkRepairTaskMode: 'DryRun',
    recommendationsTaskMode: 'Activate',
    syncRecommendationsToPlaylist: true,
    discoveryUserAccessEnabled: true,
    pluginLogLevel: 'DEBUG',
  });
  const res = await importBackup(backup);
  expect(res.ok(), `import failed: ${res.status()}`).toBeTruthy();

  const cfg = await getConfig();
  expect(cfg.Language).toBe('de');
  expect(cfg.ExcludedLibraries).toBe('Music,Home Videos');
  expect(cfg.OrphanMinAgeDays).toBe(9);
  expect(cfg.UseTrash).toBe(true);
  expect(cfg.TrashFolderPath).toBe('.custom-trash');
  expect(cfg.TrashRetentionDays).toBe(7);
  expect(cfg.SeerrCleanupAgeDays).toBe(0);
  expect(cfg.TrickplayTaskMode).toBe('Activate');
  expect(cfg.EmptyMediaFolderTaskMode).toBe('DryRun');
  expect(cfg.OrphanedSubtitleTaskMode).toBe('Deactivate');
  expect(cfg.LinkRepairTaskMode).toBe('DryRun');
  expect(cfg.RecommendationsTaskMode).toBe('Activate');
  expect(cfg.SyncRecommendationsToPlaylist).toBe(true);
  expect(cfg.DiscoveryUserAccessEnabled).toBe(true);
  // Backup restore DOES apply PluginLogLevel (unlike PUT /Configuration, which ignores it).
  expect(cfg.PluginLogLevel).toBe('DEBUG');
  await assertPluginActive(ctx);
});

test('timeline round-trips: exported data points come back via GET GrowthTimeline', async () => {
  // Ensure a timeline exists (tolerate the 429 recompute rate-limit).
  const warm = await ctx.get(p('GrowthTimeline?forceRefresh=true'));
  if (warm.status() === 429) await new Promise((r) => setTimeout(r, 31_000));

  const backup = await exportBackup(true);
  const points = backup.growthTimeline?.dataPoints ?? backup.growthTimeline?.DataPoints;
  test.skip(!Array.isArray(points) || points.length === 0, 'no timeline data points to round-trip yet');

  const res = await importBackup(backup);
  expect(res.ok(), `import failed: ${res.status()}`).toBeTruthy();
  const summary = (await res.json()).summary as { TimelineRestored: boolean };
  expect(summary.TimelineRestored).toBe(true);

  // GET without forceRefresh reads the persisted (just-restored) file.
  const after = await ctx.get(p('GrowthTimeline'));
  expect(after.ok()).toBeTruthy();
  const body = (await after.json()) as { DataPoints?: unknown[]; dataPoints?: unknown[] };
  const restored = body.DataPoints ?? body.dataPoints ?? [];
  expect(restored).toHaveLength(points.length);
  await assertPluginActive(ctx);
});

test('Arr credential preserve (redacted import) then change (new key) round-trips', async () => {
  // Seed a known Radarr instance with a real key.
  await putConfig({ RadarrInstances: [{ Name: 'R1', Url: 'http://mock-arr:9000', ApiKey: 'realkey' }] });

  try {
    // PRESERVE: a redacted export (empty key) imported back must keep the live key.
    const redacted = await exportBackup(false);
    const preserve = await importBackup(redacted);
    expect(preserve.ok()).toBeTruthy();
    expect((await preserve.json()).summary.CredentialsChanged, 'empty key = preserve').toBe(false);
    let secretExport = await exportBackup(true);
    let r1 = (secretExport.radarrInstances ?? []).find((i: any) => i.Name === 'R1' || i.name === 'R1');
    expect(r1?.apiKey ?? r1?.ApiKey).toBe('realkey');

    // CHANGE: a secrets export with a new key must flip CredentialsChanged true.
    secretExport = await exportBackup(true);
    const instances = secretExport.radarrInstances ?? [];
    if (instances[0]) {
      if ('apiKey' in instances[0]) instances[0].apiKey = 'brand-new-key';
      else instances[0].ApiKey = 'brand-new-key';
    }
    const changed = await importBackup(secretExport);
    expect(changed.ok()).toBeTruthy();
    expect((await changed.json()).summary.CredentialsChanged, 'new key = changed').toBe(true);
    await assertPluginActive(ctx);
  } finally {
    // Restore a known-good instance for later tests even if an assertion threw.
    await ctx
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: { RadarrInstances: [{ Name: 'Mock Radarr', Url: 'http://mock-arr:9000', ApiKey: 'radarr-key' }] },
      })
      .catch(() => undefined);
  }
});
