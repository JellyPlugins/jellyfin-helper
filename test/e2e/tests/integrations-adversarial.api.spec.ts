/**
 * Adversarial Arr/Seerr integration tests: SSRF attempts, non-HTTP schemes,
 * high-byte API keys, and misbehaving upstreams (slow-loris, over-large body,
 * garbage JSON via the mock's sentinel keys). The plugin must degrade to
 * 400/502/504 — never 200 on an SSRF target, never a 500/crash/hang — and never
 * reflect internal content.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

const ARR = process.env.MOCK_ARR_URL ?? 'http://mock-arr:9000';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

// Internal targets an SSRF would try to reach. None should yield a 200 success.
const SSRF_TARGETS = [
  'http://169.254.169.254/latest/meta-data/',
  'http://localhost:8096/System/Info',
  'http://127.0.0.1:8096/',
  'http://[::1]:8096/',
  'http://0.0.0.0:8096/',
];

test('Arr TestConnection against SSRF targets never returns success, never 500, bounded time', async () => {
  for (const url of SSRF_TARGETS) {
    const started = Date.now();
    const res = await ctx.post(p('ArrIntegration/TestConnection'), {
      headers: { 'Content-Type': 'application/json' },
      data: { Url: url, ApiKey: 'k' },
    });
    // 502/504 (clean gateway degradation) is the CORRECT outcome for an
    // unreachable/hostile target; only a raw 500 crash is a failure.
    expect(res.status(), `url=${url} must not crash`).not.toBe(500);
    expect(Date.now() - started, `url=${url} must not hang`).toBeLessThan(20_000);
    if (res.ok()) {
      const body = (await res.json()) as { Success?: boolean };
      expect(body.Success, `SSRF target ${url} must not report a successful connection`).not.toBe(true);
    }
  }
  await assertPluginActive(ctx);
});

test('Seerr/Test rejects non-HTTP(S) schemes with the exact message (400)', async () => {
  for (const url of ['ftp://evil', 'file:///etc/passwd', 'javascript:alert(1)', 'gopher://x']) {
    const res = await ctx.post(p('Seerr/Test'), {
      headers: { 'Content-Type': 'application/json' },
      data: { Url: url, ApiKey: 'k' },
    });
    expect(res.status(), `url=${url}`).toBe(400);
    const body = (await res.json()) as { Success: boolean; Message: string };
    expect(body.Success).toBe(false);
    expect(body.Message).toBe('A valid HTTP(S) URL is required.');
  }
  await assertPluginActive(ctx);
});

test('ArrIntegration/TestConnection rejects non-HTTP(S) schemes with the exact message (400)', async () => {
  // The Arr endpoint enforces the SAME SSRF scheme guard as Seerr/Test, but was
  // previously only asserted to be <500. Pin the exact 400 + message + Success:false
  // so a regression that let a non-HTTP(S) scheme through (or 500'd) is caught.
  for (const url of ['ftp://evil', 'file:///etc/passwd', 'javascript:alert(1)', 'gopher://x']) {
    const res = await ctx.post(p('ArrIntegration/TestConnection'), {
      headers: { 'Content-Type': 'application/json' },
      data: { Url: url, ApiKey: 'k' },
    });
    expect(res.status(), `url=${url}`).toBe(400);
    const body = (await res.json()) as { Success: boolean; Message: string };
    expect(body.Success).toBe(false);
    expect(body.Message).toBe('A valid HTTP(S) URL is required.');
  }
  await assertPluginActive(ctx);
});

test('high-byte / control API keys degrade cleanly (never 500)', async () => {
  for (const key of ['kéy', '😀-key', 'ab']) {
    const arr = await ctx.post(p('ArrIntegration/TestConnection'), {
      headers: { 'Content-Type': 'application/json' },
      data: { Url: ARR, ApiKey: key },
    });
    expect(arr.status(), `arr key=${JSON.stringify(key)}`).not.toBe(500);
    const seerr = await ctx.post(p('Seerr/Test'), {
      headers: { 'Content-Type': 'application/json' },
      data: { Url: 'http://mock-seerr:5055', ApiKey: key },
    });
    expect(seerr.status(), `seerr key=${JSON.stringify(key)}`).not.toBe(500);
  }
  await assertPluginActive(ctx);
});

test('slow upstream (force-slow) resolves to an error within timeout, not a hang', async () => {
  const started = Date.now();
  const res = await ctx.post(p('ArrIntegration/TestConnection'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Url: ARR, ApiKey: 'force-slow' },
  });
  const elapsed = Date.now() - started;
  expect(res.status()).not.toBe(500);
  // The plugin's HttpClient timeout (~15s) must fire well before Playwright's 90s.
  expect(elapsed, `elapsed ${elapsed}ms`).toBeLessThan(30_000);
  await assertPluginActive(ctx);
});

test('over-large upstream response (force-giant) does not 500 or OOM the plugin', async () => {
  const res = await ctx.post(p('ArrIntegration/TestConnection'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Url: ARR, ApiKey: 'force-giant' },
  });
  expect(res.status()).not.toBe(500);
  // Plugin must remain responsive afterwards.
  await assertPluginActive(ctx);
});

test('garbage upstream body (force-garbage) → clean failure, never 500', async () => {
  const res = await ctx.post(p('Seerr/Test'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Url: 'http://mock-seerr:5055', ApiKey: 'force-garbage' },
  });
  expect(res.status()).not.toBe(500);
  await assertPluginActive(ctx);
});

test('Arr Compare index overflow / negative / non-numeric → all handled, never 500', async () => {
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { RadarrInstances: [{ Name: 'Mock Radarr', Url: ARR, ApiKey: 'radarr-key' }] },
  });
  for (const idx of ['-1', '2147483647', '2147483648', 'abc']) {
    const res = await ctx.get(p(`ArrIntegration/Compare/Radarr?index=${idx}`));
    expect(res.status(), `index=${idx}`).toBeLessThan(500);
  }
  await assertPluginActive(ctx);
});
