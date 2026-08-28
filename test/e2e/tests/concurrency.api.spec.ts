/** * Concurrency - invariants that must hold under genuinely concurrent requests * (Promise.all against the one shared server process; the suite is workers:1 so * files are serial, but requests inside a test race for real). */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  // Restore the default log level BEFORE disposing, so a thrown assertion in the racing-LogLevel test can't leak a non-default level into later specs (the restore used to be a trailing statement, skipped on failure).
  await ctx
    .put(p('Configuration/LogLevel'), {
      headers: { 'Content-Type': 'application/json' },
      data: { PluginLogLevel: 'INFO' },
    })
    .catch(() => undefined);
  await ctx.dispose();
});

test('concurrent GrowthTimeline forceRefresh: at most one recomputes, the rest 429 with Retry-After', async () => {
  // Fire several forceRefresh recomputes at once. The controller's semaphore + 30s throttle must let AT MOST ONE through (200); every rejected one is a 429 carrying Retry-After.
  const responses = await Promise.all(
    Array.from({ length: 4 }, () => ctx.get(p('GrowthTimeline?forceRefresh=true'))),
  );
  const statuses = responses.map((r) => r.status());

  // Invariant 1: never more than one concurrent recompute succeeds.
  const okCount = statuses.filter((s) => s === 200).length;
  expect(okCount, `at most one 200 (got statuses ${statuses.join(',')})`).toBeLessThanOrEqual(1);

  // Invariant 2: every non-200 is a throttle 429 with a numeric Retry-After - not a
  // 500 or any other error (a broken/absent semaphore could 500 or let two compute).
  for (const r of responses) {
    if (r.status() === 200) continue;
    expect(r.status(), `non-200 must be a throttle 429 (got ${r.status()})`).toBe(429);
    const retryAfter = Number(r.headers()['retry-after']);
    expect(Number.isFinite(retryAfter), '429 carries a numeric Retry-After').toBe(true);
    expect(retryAfter, 'Retry-After within the 30s window').toBeGreaterThan(0);
    expect(retryAfter).toBeLessThanOrEqual(30);
  }

  // Invariant 3: the persisted timeline read back (cache, no refresh) is coherent -
  // no torn write from the race: parseable, and cumulative size is non-decreasing.
  const cached = await ctx.get(p('GrowthTimeline'));
  if (cached.ok()) {
    const body = (await cached.json()) as { DataPoints?: any[]; dataPoints?: any[] };
    const points = body.DataPoints ?? body.dataPoints ?? [];
    let prev = -1;
    for (const pt of points) {
      const size = Number(pt.CumulativeSize ?? pt.cumulativeSize ?? 0);
      expect(size, 'cumulative size never negative (coherent write)').toBeGreaterThanOrEqual(0);
      expect(size, 'cumulative series is non-decreasing (no torn/merged write)').toBeGreaterThanOrEqual(prev);
      prev = size;
    }
  }
  await assertPluginActive(ctx);
});

test('racing PUT /Configuration/LogLevel between two valid levels leaves exactly one, never garbage', async () => {
  // ReadAndMutate serializes each write. Alternate DEBUG/ERROR across N concurrent PUTs; the stored level must be one of the two (last-writer-wins) - never a torn value, never invalid, never a 500.
  const levels = ['DEBUG', 'ERROR'] as const;
  const puts = Array.from({ length: 10 }, (_, i) =>
    ctx.put(p('Configuration/LogLevel'), {
      headers: { 'Content-Type': 'application/json' },
      data: { PluginLogLevel: levels[i % 2] },
    }),
  );
  const results = await Promise.all(puts);
  for (const r of results) {
    expect(r.status(), `each racing LogLevel PUT succeeds (got ${r.status()})`).toBe(200);
  }

  const cfgRes = await ctx.get(p('Configuration'));
  expect(cfgRes.ok(), `config readback failed: ${cfgRes.status()}`).toBeTruthy();
  const cfg = await cfgRes.json();
  expect(levels as readonly string[], `stored level is exactly one submitted value (got ${cfg.PluginLogLevel})`)
    .toContain(cfg.PluginLogLevel);
  await assertPluginActive(ctx);
  // Restore happens in afterAll so it runs even if an assertion above throws.
});
