/**
 * Upstream DOWN - resilience under a genuinely unreachable Arr/Seerr.
 *
 * This is distinct from the "reachable-but-errors" coverage elsewhere:
 *   - integrations.api.spec.ts drives `ApiKey:'force-fail'` against the REACHABLE
 *     mock, which returns HTTP 500 -> the plugin's upstream-parsed-failure branch.
 *   - integrations-adversarial.api.spec.ts `force-slow` touches the timeout branch.
 *
 * Here we point the plugin at a dead port (127.0.0.1:1, nothing listening) so the
 * HTTP client raises a connection-refused (HttpRequestException) - a SEPARATE catch
 * branch in ArrIntegrationService / SeerrController from the upstream-500 path
 * (ArrIntegrationService.cs conn-refused -> (false,...) -> 502; SeerrController.cs
 * HttpRequestException -> 502). The contract we assert: behaviour DEGRADES (a clean
 * 4xx/5xx status, bounded in time) but NOTHING BREAKS - never a 500 or a hang, and
 * the plugin stays Active after every hostile call.
 *
 * hardening.api.spec.ts already covers Seerr/Test against a dead port; this file adds
 * the two gaps that had no coverage: (1) Arr Compare against a dead host, and (2) a
 * Seerr-dependent READ path (Discovery/Services) against a dead host.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

// A port with (almost certainly) nothing listening -> immediate connection refused.
const DEAD_URL = 'http://127.0.0.1:1';
const ARR_URL = process.env.MOCK_ARR_URL ?? 'http://mock-arr:9000';
const SEERR_URL = process.env.MOCK_SEERR_URL ?? 'http://mock-seerr:5055';

// A dead upstream must never make an endpoint hang: cap every call well under any
// sane HTTP client timeout so a regression that blocks shows up as a test timeout.
const CALL_TIMEOUT = 20_000;

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});

test.afterAll(async () => {
  // Restore the working mock config so later specs that assume a reachable Arr/Seerr
  // don't inherit the dead-port pointing. (beforeAll of integrations.api.spec.ts also
  // re-establishes it, but leaving shared state coherent is the contract here.)
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      RadarrInstances: [{ Name: 'Mock Radarr', Url: ARR_URL, ApiKey: 'radarr-key' }],
      SonarrInstances: [{ Name: 'Mock Sonarr', Url: ARR_URL, ApiKey: 'sonarr-key' }],
      SeerrUrl: SEERR_URL,
      SeerrApiKey: 'seerr-key',
    },
  }).catch(() => { /* best-effort restore */ });
  await ctx.dispose();
});

test('Arr TestConnection against a dead host degrades cleanly (not 500/hang)', async () => {
  const res = await ctx.post(p('ArrIntegration/TestConnection'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Url: DEAD_URL, ApiKey: 'k' },
    timeout: CALL_TIMEOUT,
  });
  // Scheme is valid (loopback allowed by design), so this reaches the network layer
  // and comes back as a failed connection test: 200 with Success:false OR a 502.
  expect(res.status(), `unexpected status ${res.status()}`).toBeLessThan(503);
  expect(res.status()).not.toBe(500);
  const body = (await res.json()) as { Success: boolean; Message: string };
  expect(body.Success).toBe(false);
  // Generic, non-oracle message - never leaks the raw socket error.
  expect(body.Message.length).toBeGreaterThan(0);
  await assertPluginActive(ctx);
});

test('Arr Compare against a dead host returns 502 naming the instance (not 500/hang)', async () => {
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { RadarrInstances: [{ Name: 'DeadBox', Url: DEAD_URL, ApiKey: 'k' }] },
  });
  const res = await ctx.get(p('ArrIntegration/Compare/Radarr?index=0'), { timeout: CALL_TIMEOUT });
  // Connection-refused flows through the same aggregation as an upstream error:
  // the compare cannot complete -> 502 Bad Gateway naming the offending instance.
  expect(res.status()).toBe(502);
  expect(await res.text()).toContain('DeadBox');
  await assertPluginActive(ctx);
});

test('Seerr-dependent Discovery/Services against a dead host degrades cleanly (not 500/hang)', async () => {
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: DEAD_URL, SeerrApiKey: 'k' },
  });
  const res = await ctx.get(p('Discovery/Services/radarr'), { timeout: CALL_TIMEOUT });
  // A dependent READ path against a dead upstream must not 500 or hang: either an
  // empty/graceful 200, or a transient-upstream status (502/503/504). The key
  // invariant is "no 500, bounded time, plugin survives".
  expect(res.status(), `unexpected status ${res.status()}`).not.toBe(500);
  expect([200, 204, 502, 503, 504]).toContain(res.status());
  if (res.ok()) {
    const services = await res.json();
    expect(Array.isArray(services)).toBeTruthy();
  }
  await assertPluginActive(ctx);
});
