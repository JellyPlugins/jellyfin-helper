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
import { request as pwRequest } from '@playwright/test';
import { apiContext, loadAuth, p } from '../setup/api-client.ts';

const MOCK_SEERR_PUBLIC = process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';

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

test('Seerr API key mask (***): stored key is preserved on re-save (functionally proven)', async () => {
  // The admin Discovery/Request path submits to the mock using the STORED key and
  // returns a non-2xx if that key is rejected — a cache-immune, per-call probe
  // (unlike Discovery/Users, which caches for 5 min and swallows upstream 401s).
  // The mock now 401s a literal '***' (mocks/seerr-server.js), so a wipe-to-mask
  // is detectable: the submission would fail AND never reach the mock.
  const mock = await pwRequest.newContext();
  const resetAndSubmit = async (): Promise<{ recorded: number }> => {
    const reset = await mock.get(`${MOCK_SEERR_PUBLIC}/reset`);
    expect(reset.ok(), `mock /reset failed: ${reset.status()}`).toBeTruthy();
    const res = await ctx.post(p('Discovery/Request'), {
      headers: { 'Content-Type': 'application/json' },
      data: { TmdbId: 27205, MediaType: 'movie' },
    });
    expect(res.ok(), `Discovery/Request should reach the mock with the stored key: ${res.status()}`).toBeTruthy();
    const last = await mock.get(`${MOCK_SEERR_PUBLIC}/last-request`);
    expect(last.ok()).toBeTruthy();
    return { recorded: ((await last.json()) as { count: number }).count };
  };

  try {
    // Set a REAL key the mock accepts, and confirm GET masks it.
    const set = await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'seerr-key', SeerrCleanupAgeDays: 30 });
    expect(set.ok(), `initial key save failed: ${set.status()}`).toBeTruthy();
    expect((await getConfig()).SeerrApiKey, 'GET must mask the stored key').toBe('***');

    // Baseline: the stored key authenticates to the mock (a request is recorded).
    expect((await resetAndSubmit()).recorded, 'stored key must reach the mock (baseline)').toBe(1);

    // Re-save echoing the mask: '***' must mean "keep the stored key", never
    // persist the literal mask. Prove it — a submission must still reach the mock.
    const resaveMask = await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: '***', SeerrCleanupAgeDays: 30 });
    expect(resaveMask.ok(), `mask re-save failed: ${resaveMask.status()}`).toBeTruthy();
    expect((await getConfig()).SeerrApiKey).toBe('***');
    expect((await resetAndSubmit()).recorded, "stored key was WIPED by a '***' re-save").toBe(1);

    // Same guarantee for a whitespace-padded mask ' *** ' (server trims before the
    // sentinel compare — ConfigurationController preserves the key here too).
    const resavePadded = await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: ' *** ', SeerrCleanupAgeDays: 30 });
    expect(resavePadded.ok(), `padded-mask re-save failed: ${resavePadded.status()}`).toBeTruthy();
    expect((await resetAndSubmit()).recorded, "stored key was WIPED by a ' *** ' re-save").toBe(1);
  } finally {
    await mock.dispose();
  }
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
  try {
    await putConfig({ Language: 'de' });
    expect((await getConfig()).Language).toBe('de');
  } finally {
    await putConfig({ Language: 'en' }); // restore even if the assertion threw
  }
  expect((await getConfig()).Language).toBe('en');
});

test('unsupported / injection Language coerces to en on save', async () => {
  try {
    for (const lang of ['xx', '<script>', '']) {
      const res = await putConfig({ Language: lang });
      expect(res.ok(), `lang=${lang}`).toBeTruthy();
      expect((await getConfig()).Language).toBe('en');
    }
  } finally {
    await putConfig({ Language: 'en' });
  }
});

test('ensemble alpha values always persist within [0,1] and min <= max', async () => {
  for (const body of [
    { EnsembleAlphaMin: 0.9, EnsembleAlphaMax: 0.2, EnsembleGenrePenaltyFloor: 2.0 },
    { EnsembleAlphaMin: -0.5, EnsembleAlphaMax: -0.1 },
    { EnsembleAlphaMin: 0.3, EnsembleAlphaMax: 0.8 },
  ]) {
    const res = await putConfig(body);
    expect(res.ok()).toBeTruthy();
    const cfg = await getConfig();
    expect(cfg.EnsembleAlphaMin).toBeGreaterThanOrEqual(0);
    expect(cfg.EnsembleAlphaMax).toBeLessThanOrEqual(1);
    expect(cfg.EnsembleAlphaMin).toBeLessThanOrEqual(cfg.EnsembleAlphaMax);
    expect(cfg.EnsembleGenrePenaltyFloor).toBeGreaterThanOrEqual(0);
    expect(cfg.EnsembleGenrePenaltyFloor).toBeLessThanOrEqual(1);
  }
});

test('Arr instance validation: no-key rejected, >3 rejected, overlong name rejected, blank row skipped', async () => {
  try {
    const noKey = await putConfig({ RadarrInstances: [{ Name: 'X', Url: 'http://mock-arr:9000', ApiKey: '' }] });
    expect(noKey.status()).toBe(400);
    expect((await noKey.json() as { message: string }).message).toContain('no API key');

    const four = await putConfig({
      RadarrInstances: Array.from({ length: 4 }, (_, i) => ({ Name: `R${i}`, Url: 'http://mock-arr:9000', ApiKey: 'k' })),
    });
    expect(four.status()).toBe(400);

    const longName = await putConfig({ RadarrInstances: [{ Name: 'a'.repeat(101), Url: 'http://mock-arr:9000', ApiKey: 'k' }] });
    expect(longName.status()).toBe(400);

    const blankRow = await putConfig({ RadarrInstances: [{ Name: '', Url: '', ApiKey: '' }] });
    expect(blankRow.ok(), 'a fully-blank instance row is skipped, not an error').toBeTruthy();
  } finally {
    // Restore a known-good instance even if an assertion above throws, so the
    // shared backend isn't left in a rejected/undefined Radarr state for the
    // tests that run after this one.
    await putConfig({ RadarrInstances: [{ Name: 'Radarr Main', Url: 'http://mock-arr:9000', ApiKey: 'radarr-key' }] });
  }
});

test('Seerr URL with blank key is rejected and does not mutate stored URL', async () => {
  await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'realkey' });
  const before = (await getConfig()).SeerrUrl;

  const res = await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: '', SeerrCleanupAgeDays: 30 });
  expect(res.status()).toBe(400);
  expect((await getConfig()).SeerrUrl).toBe(before);
});

test('invalid Seerr URL scheme is rejected without mutating stored URL', async () => {
  await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: '***' });
  const before = (await getConfig()).SeerrUrl;
  for (const url of ['ftp://x', 'javascript:alert(1)', 'file:///etc/passwd']) {
    const res = await putConfig({ SeerrUrl: url, SeerrApiKey: 'k' });
    expect(res.status(), `url=${url}`).toBe(400);
    expect((await getConfig()).SeerrUrl).toBe(before);
  }
});

test('config-save strictly blocks traversal / invalid / blank-when-enabled trash paths', async () => {
  await putConfig({ UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 });
  for (const [path, useTrash] of [['../etc', true], ['bad|name', true], ['', true]] as Array<[string, boolean]>) {
    const res = await putConfig({ UseTrash: useTrash, TrashFolderPath: path, TrashRetentionDays: 30 });
    expect(res.status(), `path=${JSON.stringify(path)}`).toBe(400);
    expect((await getConfig()).TrashFolderPath).toBe('.jellyfin-trash');
  }
});

test('LogLevel-differing save returns a non-empty warnings array and leaves level unchanged', async () => {
  const before = (await getConfig()).PluginLogLevel;
  const res = await putConfig({ PluginLogLevel: before === 'DEBUG' ? 'ERROR' : 'DEBUG' });
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as { Warnings?: string[]; warnings?: string[] };
  const warnings = body.Warnings ?? body.warnings ?? [];
  expect(warnings.length).toBeGreaterThan(0);
  expect(warnings.some((w) => /LogLevel|ignored/i.test(w))).toBe(true);
  expect((await getConfig()).PluginLogLevel).toBe(before);
});
