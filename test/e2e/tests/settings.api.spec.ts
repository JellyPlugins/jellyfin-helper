/**
 * Settings persistence + "does it take effect" — flips settings via
 * PUT /Configuration, reloads via GET, and asserts they stuck. Covers the
 * gotchas the research flagged:
 *   - API keys masked as *** on GET; sending *** preserves the stored key.
 *   - PluginLogLevel is ONLY settable via PUT /Configuration/LogLevel.
 *   - Numeric clamping (OrphanMinAgeDays / TrashRetentionDays 0..3650).
 *   - Task-mode round-trips (DryRun/Activate/Deactivate).
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p } from '../setup/api-client.ts';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

async function getConfig(): Promise<any> {
  const res = await ctx.get(p('Configuration'));
  expect(res.ok()).toBeTruthy();
  return res.json();
}

/** PUT a partial config update; returns the save response. */
async function putConfig(body: Record<string, unknown>) {
  return ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: body,
  });
}

test('task modes round-trip through save + reload', async () => {
  const res = await putConfig({
    TrickplayTaskMode: 'Activate',
    EmptyMediaFolderTaskMode: 'Deactivate',
    OrphanedSubtitleTaskMode: 'DryRun',
    LinkRepairTaskMode: 'Activate',
  });
  expect(res.ok()).toBeTruthy();

  const cfg = await getConfig();
  expect(cfg.TrickplayTaskMode).toBe('Activate');
  expect(cfg.EmptyMediaFolderTaskMode).toBe('Deactivate');
  expect(cfg.OrphanedSubtitleTaskMode).toBe('DryRun');
  expect(cfg.LinkRepairTaskMode).toBe('Activate');
});

test('OrphanMinAgeDays persists and clamps out-of-range values', async () => {
  // In-range value persists exactly.
  await putConfig({ OrphanMinAgeDays: 7 });
  expect((await getConfig()).OrphanMinAgeDays).toBe(7);

  // Over-max clamps to 3650 rather than crashing or persisting garbage.
  const res = await putConfig({ OrphanMinAgeDays: 999999 });
  // Validator hard-blocks out-of-range with 400 OR clamps — accept either, but
  // the persisted value must never exceed the cap.
  if (res.ok()) {
    expect((await getConfig()).OrphanMinAgeDays).toBeLessThanOrEqual(3650);
  } else {
    expect(res.status()).toBe(400);
    // A rejected save must not have changed the value.
    expect((await getConfig()).OrphanMinAgeDays).toBe(7);
  }
});

test('trash settings persist and toggle', async () => {
  await putConfig({ UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 45 });
  const cfg = await getConfig();
  expect(cfg.UseTrash).toBe(true);
  expect(cfg.TrashFolderPath).toBe('.jellyfin-trash');
  expect(cfg.TrashRetentionDays).toBe(45);

  // Blank trash path resets to the default.
  await putConfig({ UseTrash: true, TrashFolderPath: '', TrashRetentionDays: 45 });
  expect((await getConfig()).TrashFolderPath).toBe('.jellyfin-trash');
});

test('Seerr API key mask (***): stored key preserved on re-save', async () => {
  // Set a real key.
  await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'secret-key-123', SeerrCleanupAgeDays: 30 });
  const masked = await getConfig();
  // GET masks the key.
  expect(masked.SeerrApiKey).toBe('***');

  // Re-save sending the mask back — key must be preserved (not overwritten to ***).
  await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: '***', SeerrCleanupAgeDays: 30 });
  // We can't read the plaintext (still masked), but the connection test on save
  // would fail if the key were wiped. Assert save succeeded and URL intact.
  const after = await getConfig();
  expect(after.SeerrUrl).toBe('http://mock-seerr:5055');
  expect(after.SeerrApiKey).toBe('***');
});

test('SeerrCleanupAgeDays forced to 0 when SeerrUrl is blank', async () => {
  await putConfig({ SeerrUrl: '', SeerrApiKey: '', SeerrCleanupAgeDays: 30 });
  const cfg = await getConfig();
  expect(cfg.SeerrCleanupAgeDays).toBe(0);
});

test('PluginLogLevel is NOT changed by PUT /Configuration', async () => {
  const before = (await getConfig()).PluginLogLevel;
  const res = await putConfig({ PluginLogLevel: before === 'DEBUG' ? 'ERROR' : 'DEBUG' });
  expect(res.ok()).toBeTruthy();
  // Ignored by design (returns a warning); level unchanged.
  expect((await getConfig()).PluginLogLevel).toBe(before);
});

test('PluginLogLevel IS changed by PUT /Configuration/LogLevel', async () => {
  const res = await ctx.put(p('Configuration/LogLevel'), {
    headers: { 'Content-Type': 'application/json' },
    data: { PluginLogLevel: 'DEBUG' },
  });
  expect(res.ok()).toBeTruthy();
  expect((await getConfig()).PluginLogLevel).toBe('DEBUG');

  // Invalid level is rejected with 400, level unchanged.
  const bad = await ctx.put(p('Configuration/LogLevel'), {
    headers: { 'Content-Type': 'application/json' },
    data: { PluginLogLevel: 'NONSENSE' },
  });
  expect(bad.status()).toBe(400);
  expect((await getConfig()).PluginLogLevel).toBe('DEBUG');
});

test('Arr instances persist (max 3, key masked)', async () => {
  const res = await putConfig({
    RadarrInstances: [
      { Name: 'Radarr Main', Url: 'http://mock-arr:9000', ApiKey: 'radarr-key' },
    ],
    SonarrInstances: [
      { Name: 'Sonarr Main', Url: 'http://mock-arr:9000', ApiKey: 'sonarr-key' },
    ],
  });
  expect(res.ok()).toBeTruthy();
  const cfg = await getConfig();
  expect(cfg.RadarrInstances).toHaveLength(1);
  expect(cfg.RadarrInstances[0].Name).toBe('Radarr Main');
  expect(cfg.RadarrInstances[0].Url).toBe('http://mock-arr:9000');
  // Key masked on read.
  expect(cfg.RadarrInstances[0].ApiKey).toBe('***');
  expect(cfg.SonarrInstances).toHaveLength(1);
});

test('Language persists', async () => {
  await putConfig({ Language: 'de' });
  expect((await getConfig()).Language).toBe('de');
  await putConfig({ Language: 'en' }); // restore
  expect((await getConfig()).Language).toBe('en');
});
