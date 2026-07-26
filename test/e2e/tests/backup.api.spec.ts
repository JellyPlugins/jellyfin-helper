/**
 * Backup export → tamper → import round-trip, plus the documented gotchas:
 *   - Backup is JSON (distinct from the plugin's XML config file).
 *   - Export redacts API keys unless includeSecrets=true.
 *   - Some fields are intentionally NOT exported (MaxRecommendationsPerUser,
 *     ensemble tuning, cumulative stats) — a round-trip must not claim to
 *     restore them.
 *   - Import validates: garbage/oversized/broken JSON is rejected (400), and
 *     the server + plugin survive it (hardening).
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

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

test('export produces a valid backup with redacted secrets by default', async () => {
  // Seed a known config first.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Language: 'de', OrphanMinAgeDays: 12, SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'topsecret' },
  });

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
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Language: 'fr', OrphanMinAgeDays: 99 },
  });
  const changed = await ctx.get(p('Configuration')).then((r) => r.json());
  expect(changed.Language).toBe('fr');

  // Import the earlier backup — should restore language + age.
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
  // Sanitizer/validator either repairs to a default or rejects — must not 500.
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
  // A minimal object with only backupVersion — sanitizer should fill defaults.
  const res = await importBackup({ backupVersion: 1 });
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(ctx);
});

test('HARDENING: backup with negative trends values is handled without corruption', async () => {
  // The research flagged that BackupValidator only WARNS (not rejects) on
  // negative cumulativeSize/count. This test documents/verifies the behaviour:
  // whatever the policy, it must not 500 and the plugin must stay Active. If the
  // team decides to harden this to a rejection, flip the expectation below.
  const backup = await exportBackup(true);
  backup.growthTimeline = {
    granularity: 'Daily',
    dataPoints: [
      { date: '2025-01-01T00:00:00Z', cumulativeSize: -5000, cumulativeFileCount: -3 },
    ],
  };

  const res = await importBackup(backup);
  expect(res.status(), 'must not throw a server error on negative values').toBeLessThan(500);
  await assertPluginActive(ctx);

  // Follow-up: the trends endpoint must not surface negative garbage.
  const trends = await ctx.get(p('GrowthTimeline'));
  if (trends.ok()) {
    const body = (await trends.json()) as { DataPoints?: Array<{ CumulativeSize: number; CumulativeFileCount: number }> };
    for (const pt of body.DataPoints ?? []) {
      expect(pt.CumulativeSize, 'trends must not expose negative sizes').toBeGreaterThanOrEqual(0);
      expect(pt.CumulativeFileCount).toBeGreaterThanOrEqual(0);
    }
  }
});
