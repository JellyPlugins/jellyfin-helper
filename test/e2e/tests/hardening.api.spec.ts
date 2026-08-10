/**
 * Hardening / edge cases - inputs that could crash or 500 the plugin. After
 * each, we assert the plugin is still Active and the server still answers.
 * The point is graceful degradation, never an unhandled throw.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive, runCleanupTask } from '../setup/api-client.ts';

let ctx: APIRequestContext;
let savedArr: { RadarrInstances: unknown; SonarrInstances: unknown } | null = null;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
  // Snapshot the Arr integration config so the tests below that wipe/replace
  // RadarrInstances/SonarrInstances can restore it - otherwise a later spec would
  // inherit an empty SonarrInstances and a throwaway Radarr instance.
  const cfg = await ctx.get(p('Configuration')).then((r) => (r.ok() ? r.json() : null));
  if (cfg) {
    savedArr = { RadarrInstances: cfg.RadarrInstances ?? [], SonarrInstances: cfg.SonarrInstances ?? [] };
  }
});
test.afterAll(async () => {
  if (savedArr) {
    await ctx
      .put(p('Configuration'), { headers: { 'Content-Type': 'application/json' }, data: savedArr })
      .catch(() => undefined);
  }
  await ctx.dispose();
});

// Setup helper: applies a config change and FAILS LOUDLY if the save itself
// didn't succeed - so a broken precondition surfaces as a clear setup error
// instead of an unrelated downstream assertion failure.
async function putConfig(body: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: body,
  });
  expect(res.ok(), `setup putConfig failed: ${res.status()}`).toBeTruthy();
  return res;
}

test('invalid Arr URL is rejected or degrades, never 500', async () => {
  for (const url of ['not-a-url', 'ftp://evil', 'javascript:alert(1)', 'http://', ' ']) {
    const res = await ctx.post(p('ArrIntegration/TestConnection'), {
      headers: { 'Content-Type': 'application/json' },
      data: { Url: url, ApiKey: 'x' },
    });
    expect(res.status(), `url=${url}`).toBeLessThan(500);
  }
  await assertPluginActive(ctx);
});

test('Arr Compare with no configured instances returns 400 (not 500)', async () => {
  await putConfig({ RadarrInstances: [], SonarrInstances: [] });
  const res = await ctx.get(p('ArrIntegration/Compare/Radarr'));
  expect([400, 502]).toContain(res.status());
  await assertPluginActive(ctx);
});

test('Arr Compare with out-of-range index is handled', async () => {
  await putConfig({ RadarrInstances: [{ Name: 'R', Url: 'http://mock-arr:9000', ApiKey: 'k' }] });
  const res = await ctx.get(p('ArrIntegration/Compare/Radarr?index=99'));
  expect(res.status()).toBeLessThan(500);
  await assertPluginActive(ctx);
});

test('Seerr test with unreachable URL degrades cleanly (502/504, not 500)', async () => {
  const res = await ctx.post(p('Seerr/Test'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Url: 'http://127.0.0.1:1', ApiKey: 'x' }, // nothing listening
  });
  expect([400, 502, 504]).toContain(res.status());
  await assertPluginActive(ctx);
});

test('trash path traversal attempts are rejected', async () => {
  for (const badPath of ['../etc', '..', './..', 'a/../../b']) {
    const res = await ctx.post(p('Trash/FoldersForPath'), {
      headers: { 'Content-Type': 'application/json' },
      data: { TrashFolderPath: badPath },
    });
    expect(res.status(), `path=${badPath}`).toBe(400);
  }
  await assertPluginActive(ctx);
});

test('overlong trash path is rejected', async () => {
  const res = await ctx.post(p('Trash/CheckAccess'), {
    headers: { 'Content-Type': 'application/json' },
    data: { TrashFolderPath: 'a'.repeat(5000) },
  });
  expect(res.status()).toBe(400);
  await assertPluginActive(ctx);
});

test('Unicode / special-character library exclusion persists without corruption', async () => {
  const weird = 'Filmé 4K • Kids 子供 🎬,Ünïcødé';
  const res = await putConfig({ ExcludedLibraries: weird });
  try {
    expect(res.status()).toBeLessThan(500);
    if (res.ok()) {
      const cfg = await ctx.get(p('Configuration')).then((r) => r.json());
      expect(cfg.ExcludedLibraries).toBe(weird);
    }
    await assertPluginActive(ctx);
  } finally {
    await ctx
      .put(p('Configuration'), { headers: { 'Content-Type': 'application/json' }, data: { ExcludedLibraries: '' } })
      .catch(() => undefined);
  }
});

test('translations endpoint rejects malformed language codes', async () => {
  for (const lang of ['en-US-INVALID-TOO-LONG', '../../etc', '<script>']) {
    const res = await ctx.get(p(`Translations?lang=${encodeURIComponent(lang)}`));
    expect(res.status(), `lang=${lang}`).toBeLessThan(500);
  }
  await assertPluginActive(ctx);
});

test('recommendations for empty/invalid user GUID is handled', async () => {
  const res = await ctx.get(p('Recommendations/00000000-0000-0000-0000-000000000000'));
  expect([200, 400, 404, 503]).toContain(res.status());
  await assertPluginActive(ctx);
});

test('concurrent HelperCleanup triggers do not corrupt state', async () => {
  await putConfig({
    TrickplayTaskMode: 'DryRun',
    EmptyMediaFolderTaskMode: 'DryRun',
    OrphanedSubtitleTaskMode: 'DryRun',
    LinkRepairTaskMode: 'DryRun',
  });
  // Fire the task, then immediately try to run it again while it may be running.
  // Jellyfin serialises task execution; the second trigger should be a no-op,
  // and the first must still complete cleanly.
  const first = runCleanupTask(ctx, 90_000);
  // A second start attempt (best-effort) - we don't await its own completion.
  const list = await ctx.get('/ScheduledTasks').then((r) => r.json());
  const task = (list as Array<{ Id: string; Key: string }>).find((t) => t.Key === 'HelperCleanup');
  if (task) await ctx.post(`/ScheduledTasks/Running/${task.Id}`).catch(() => undefined);

  const result = await first;
  expect(result.LastExecutionResult?.Status).toBe('Completed');
  await assertPluginActive(ctx);
});

test('rate-limited scan endpoint returns 429 (not 500) when hammered', async () => {
  // Two rapid scans; the second may be rate-limited.
  const a = await ctx.get(p('MediaStatistics/ScanLibraries'));
  const b = await ctx.get(p('MediaStatistics/ScanLibraries'));
  for (const res of [a, b]) {
    expect([200, 429]).toContain(res.status());
    if (res.status() === 429) {
      // Must advertise Retry-After so clients can back off.
      expect(res.headers()['retry-after']).toBeTruthy();
    }
  }
  await assertPluginActive(ctx);
});
