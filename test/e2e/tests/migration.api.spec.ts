/**
 * Backup version-gating & config schema-evolution - the "what happens to an old
 * or forward-dated backup / an older-shaped config" surface, which had ZERO
 * behavioral coverage before.
 *
 * Verified against source (cited inline):
 *   - The ONLY accepted backupVersion is {1} (BackupValidator MaxBackupVersion=1).
 *     2/999/-1/0 → 400 with an errors[] naming the unsupported version.
 *   - A MISSING backupVersion deserializes to the C# default (1) → accepted (200).
 *   - A non-numeric backupVersion fails JSON parse → a DISTINCT 400 body.
 *   - An older-shaped backup (newer fields absent) restores with safe defaults:
 *     DiscoveryUserAccessEnabled=false, SyncRecommendationsToPlaylist=false,
 *     RecommendationsTaskMode→"DryRun" (ParseTaskMode fallback), and a null
 *     SeerrCleanupAgeDays leaves the live value unchanged.
 *   - Unknown/removed fields (in both import and PUT /Configuration) are silently
 *     ignored (System.Text.Json default; no JsonUnmappedMemberHandling.Disallow).
 *
 * NOT covered here (and why): the restore partial-failure / "manual recovery"
 * branch (BackupService.RestoreBackup) can only trigger when a file write
 * succeeds and a later step throws - and validated HTTP input can't make the
 * config-restore step throw (every value is clamped/sanitized first). It needs
 * filesystem/permission tampering, so it is deliberately out of scope for an
 * HTTP-only spec rather than faked with a vacuous assertion.
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

function importBackup(body: unknown) {
  return ctx.post(p('Backup/Import'), {
    headers: { 'Content-Type': 'application/json' },
    data: typeof body === 'string' ? body : JSON.stringify(body),
  });
}

async function putConfig(data: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data,
  });
  expect(res.ok(), `setup putConfig failed: ${res.status()}`).toBeTruthy();
  return res;
}

async function getConfig(): Promise<any> {
  const res = await ctx.get(p('Configuration'));
  expect(res.ok(), `get config failed: ${res.status()}`).toBeTruthy();
  return res.json();
}

// --- backupVersion gating ---------------------------------------------------

test('a forward-dated backupVersion (2) is rejected with 400 and a version error', async () => {
  const backup = await exportBackup(true);
  backup.backupVersion = 2; // only {1} is accepted (BackupValidator MaxBackupVersion=1)
  const res = await importBackup(backup);
  expect(res.status(), 'a future version must be a hard 400').toBe(400);
  const body = (await res.json()) as { message: string; errors?: string[] };
  // Distinguish the validation-error 400 (has errors[]) from the parse-error 400.
  expect(body.message).toMatch(/validation failed/i);
  expect(Array.isArray(body.errors), 'validation 400 carries an errors[] array').toBe(true);
  expect(body.errors!.some((e) => /unsupported backup version/i.test(e)), 'errors name the version')
    .toBe(true);
  await assertPluginActive(ctx);
});

test('absurd / negative / zero backupVersion values are all rejected with 400', async () => {
  for (const v of [999, -1, 0]) {
    const backup = await exportBackup(true);
    backup.backupVersion = v;
    const res = await importBackup(backup);
    expect(res.status(), `version ${v} must be 400`).toBe(400);
    const body = (await res.json()) as { errors?: string[] };
    expect(body.errors?.some((e) => /unsupported backup version/i.test(e)), `version ${v} named`)
      .toBe(true);
  }
  await assertPluginActive(ctx);
});

test('a MISSING backupVersion is accepted (defaults to 1), not rejected', async () => {
  const backup = await exportBackup(true);
  delete backup.backupVersion; // absent → deserializes to the C# default of 1 → valid
  const res = await importBackup(backup);
  expect(res.status(), 'a missing version must be treated as v1 and succeed').toBe(200);
  const body = (await res.json()) as { summary: { ConfigurationRestored: boolean } };
  expect(body.summary.ConfigurationRestored, 'config was restored on the defaulted-v1 import').toBe(true);
  await assertPluginActive(ctx);
});

test('a non-numeric backupVersion fails to parse with a DISTINCT 400 body', async () => {
  // A JSON string where an int is expected throws at the deserialize layer, which
  // returns a different 400 message than the version-range validation error.
  const raw = '{"backupVersion":"abc","language":"en"}';
  const res = await importBackup(raw);
  expect(res.status(), 'unparseable version → 400').toBe(400);
  const body = (await res.json()) as { message: string; errors?: string[] };
  expect(body.message).toMatch(/could not parse/i);
  // This branch is the parse failure, NOT the validation branch - so no errors[].
  expect(body.errors, 'parse-error 400 has no validation errors[] array').toBeUndefined();
  await assertPluginActive(ctx);
});

// --- older-shaped backup restores with safe defaults ------------------------

test('an older-shaped backup (newer fields absent) restores with safe defaults', async () => {
  // Seed a distinct prior state so we can prove "absent field → left unchanged"
  // for the null-preserving field, and "absent field → hard default" for the rest.
  // NOTE: PUT /Configuration only applies SeerrCleanupAgeDays when SeerrUrl is set
  // (ConfigurationController: `string.IsNullOrEmpty(config.SeerrUrl) ? 0 : clamp(...)`),
  // so the seed MUST include a SeerrUrl or the 42 silently becomes 0.
  await putConfig({
    SeerrUrl: 'http://mock-seerr:5055',
    SeerrApiKey: 'seerr-key',
    DiscoveryUserAccessEnabled: true,
    SyncRecommendationsToPlaylist: true,
    RecommendationsTaskMode: 'Activate',
    SeerrCleanupAgeDays: 42,
  });

  const backup = await exportBackup(true);
  // Simulate a backup produced by an OLDER plugin build that never had these keys.
  delete backup.discoveryUserAccessEnabled;
  delete backup.syncRecommendationsToPlaylist;
  delete backup.recommendationsTaskMode;
  delete backup.seerrCleanupAgeDays;

  const res = await importBackup(backup);
  expect(res.status(), 'an older-shaped backup must import cleanly').toBe(200);

  const cfg = await getConfig();
  // Booleans absent from an old backup default to false on restore.
  expect(cfg.DiscoveryUserAccessEnabled, 'absent bool → false default').toBe(false);
  expect(cfg.SyncRecommendationsToPlaylist, 'absent bool → false default').toBe(false);
  // An absent task mode falls back through ParseTaskMode to "DryRun".
  expect(cfg.RecommendationsTaskMode, 'absent task mode → DryRun fallback').toBe('DryRun');
  // A null/absent SeerrCleanupAgeDays means "leave the live value unchanged".
  expect(cfg.SeerrCleanupAgeDays, 'absent nullable → prior live value preserved').toBe(42);
  await assertPluginActive(ctx);

  // Restore a clean baseline so later specs aren't affected.
  await putConfig({
    DiscoveryUserAccessEnabled: false,
    SyncRecommendationsToPlaylist: false,
    RecommendationsTaskMode: 'Deactivate',
  });
});

// --- unknown / removed fields are tolerated ---------------------------------

test('an unknown extra field in a backup is ignored, not rejected', async () => {
  const backup = await exportBackup(true);
  (backup as Record<string, unknown>).someFieldFromAFuturePlugin = { nested: [1, 2, 3] };
  (backup as Record<string, unknown>).obsoleteRecommendationStrategy = 'Hybrid';
  const res = await importBackup(backup);
  expect(res.status(), 'unknown fields are silently ignored on import').toBe(200);
  await assertPluginActive(ctx);
});

test('an unknown extra field in PUT /Configuration is ignored, not rejected', async () => {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Language: 'en', notARealConfigField: true, removedInV2: 'gone' },
  });
  expect(res.ok(), `unknown PUT fields must be ignored (got ${res.status()})`).toBeTruthy();
  const cfg = await getConfig();
  expect(cfg.Language, 'the known field still applied').toBe('en');
  await assertPluginActive(ctx);
});

test('GET /Configuration exposes an inert numeric ConfigVersion', async () => {
  // ConfigVersion exists in the response but drives no migration logic today. We
  // pin its presence + type so a future real migration can build on a known field
  // (and so an accidental removal of the field surfaces here).
  const cfg = await getConfig();
  expect(typeof cfg.ConfigVersion, 'ConfigVersion is exposed as a number').toBe('number');
});
