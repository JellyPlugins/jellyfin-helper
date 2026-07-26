/**
 * Concurrency — invariants that must hold under genuinely concurrent requests
 * (Promise.all against the one shared server process; the suite is workers:1 so
 * files are serial, but requests inside a test race for real).
 *
 * Every assertion here is an INVARIANT that holds under ANY interleaving — never
 * a probabilistic "usually" check. The hazards these guard were named in the
 * coverage audit (GrowthTimeline overlapping compute/double-write; serialized
 * config mutation). Assertions verified against source:
 *   - GrowthTimelineController serializes forceRefresh through a process-static
 *     semaphore + a 30s MinRefreshInterval, so at most ONE concurrent recompute
 *     runs; the rest get 429 + Retry-After. (We assert the interleaving-safe form,
 *     NOT a strict [200,429] pair — _lastRefreshTime is process-static and a prior
 *     spec's refresh in the last 30s could make both 429.)
 *   - PUT /Configuration/LogLevel serializes through ReadAndMutate, so racing
 *     writes of two valid levels leave exactly one of them stored, never garbage.
 *
 * Deliberately NOT tested (flagged flaky/vacuous during research): a strict
 * [200,429] pair without a settle; Discovery cache/feedback races (gated behind a
 * successful mock-Seerr submit + no HTTP read of the store); CandidateSnapshot
 * publish order (in-memory, not HTTP-observable); scan-vs-config (no shared write).
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

test('concurrent GrowthTimeline forceRefresh: at most one recomputes, the rest 429 with Retry-After', async () => {
  // Fire several forceRefresh recomputes at once. The controller's semaphore +
  // 30s throttle must let AT MOST ONE through (200); every rejected one is a 429
  // carrying Retry-After. This holds regardless of prior process state: if a
  // recompute already ran in the last 30s, ALL of these may 429 — still valid.
  const responses = await Promise.all(
    Array.from({ length: 4 }, () => ctx.get(p('GrowthTimeline?forceRefresh=true'))),
  );
  const statuses = responses.map((r) => r.status());

  // Invariant 1: never more than one concurrent recompute succeeds.
  const okCount = statuses.filter((s) => s === 200).length;
  expect(okCount, `at most one 200 (got statuses ${statuses.join(',')})`).toBeLessThanOrEqual(1);

  // Invariant 2: every non-200 is a throttle 429 with a numeric Retry-After — not a
  // 500 or any other error (a broken/absent semaphore could 500 or let two compute).
  for (const r of responses) {
    if (r.status() === 200) continue;
    expect(r.status(), `non-200 must be a throttle 429 (got ${r.status()})`).toBe(429);
    const retryAfter = Number(r.headers()['retry-after']);
    expect(Number.isFinite(retryAfter), '429 carries a numeric Retry-After').toBe(true);
    expect(retryAfter, 'Retry-After within the 30s window').toBeGreaterThan(0);
    expect(retryAfter).toBeLessThanOrEqual(30);
  }

  // Invariant 3: the persisted timeline read back (cache, no refresh) is coherent —
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
  // ReadAndMutate serializes each write. Alternate DEBUG/ERROR across N concurrent
  // PUTs; the stored level must be one of the two (last-writer-wins) — never a torn
  // value, never invalid, never a 500.
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

  const cfg = await ctx.get(p('Configuration')).then((r) => r.json());
  expect(levels as readonly string[], `stored level is exactly one submitted value (got ${cfg.PluginLogLevel})`)
    .toContain(cfg.PluginLogLevel);
  await assertPluginActive(ctx);

  // Restore the default so later specs see a clean level.
  await ctx.put(p('Configuration/LogLevel'), {
    headers: { 'Content-Type': 'application/json' },
    data: { PluginLogLevel: 'INFO' },
  });
});
