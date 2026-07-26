/**
 * Logs + Translations at the API layer — validation branches and response
 * contracts the UI binds to, which logs.ui.spec.ts drives visually but never
 * asserts on the wire (query-param validation, envelope shape, download
 * headers, anonymous translations).
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

test('GET Logs envelope: {TotalBuffered, Returned, Entries} with Returned === Entries.length', async () => {
  const res = await ctx.get(p('Logs'));
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as {
    TotalBuffered: number;
    Returned: number;
    Entries: Array<{ Timestamp: string; Level: string; Source: string; Message: string }>;
  };
  expect(typeof body.TotalBuffered).toBe('number');
  expect(typeof body.Returned).toBe('number');
  expect(Array.isArray(body.Entries)).toBe(true);
  expect(body.Returned).toBe(body.Entries.length);
  if (body.Entries.length > 0) {
    const e = body.Entries[0];
    expect(typeof e.Timestamp).toBe('string');
    expect(['DEBUG', 'INFO', 'WARN', 'ERROR']).toContain(e.Level);
    expect(typeof e.Message).toBe('string');
  }
});

test('GET Logs invalid minLevel is 400; lowercase minLevel is accepted', async () => {
  const bad = await ctx.get(p('Logs?minLevel=BOGUS'));
  expect(bad.status()).toBe(400);
  expect(((await bad.json()) as { message: string }).message).toContain('minLevel');

  const lower = await ctx.get(p('Logs?minLevel=error'));
  expect(lower.ok(), 'lowercase level accepted (OrdinalIgnoreCase)').toBeTruthy();
});

test('GET Logs limit is clamped, never 400/500', async () => {
  for (const limit of ['0', '-5', '999999']) {
    const res = await ctx.get(p(`Logs?limit=${limit}`));
    expect(res.ok(), `limit=${limit}`).toBeTruthy();
    const body = (await res.json()) as { Returned: number; Entries: unknown[] };
    expect(body.Returned).toBeGreaterThanOrEqual(0);
    expect(body.Entries.length).toBeLessThanOrEqual(2000);
  }
});

test('Logs source filter over 200 chars is rejected; 200 is the accepted boundary', async () => {
  const over = await ctx.get(p(`Logs?source=${'a'.repeat(201)}`));
  expect(over.status()).toBe(400);
  expect(((await over.json()) as { message: string }).message).toBe('source parameter too long.');

  const ok = await ctx.get(p(`Logs?source=${'a'.repeat(200)}`));
  expect(ok.ok()).toBeTruthy();
});

test('Logs/Download validates minLevel and serves a timestamped text file', async () => {
  // Bad minLevel is rejected. The endpoint is [Produces("text/plain")], so the
  // JSON 400 body is content-negotiated to 406 when text/plain is requested —
  // either rejection status is acceptable, never 200/500.
  const bad = await ctx.get(p('Logs/Download?minLevel=NOPE'), { headers: { Accept: 'text/plain' } });
  expect([400, 406]).toContain(bad.status());

  const res = await ctx.get(p('Logs/Download?minLevel=ERROR'), { headers: { Accept: 'text/plain' } });
  expect(res.ok()).toBeTruthy();
  expect(res.headers()['content-type'] ?? '').toContain('text/plain');
  expect(res.headers()['content-disposition'] ?? '').toMatch(/jellyfin-helper-logs-\d{8}-\d{6}\.txt/);
});

test('Translations happy path returns a non-empty string map for supported langs', async () => {
  for (const lang of ['en', 'de']) {
    const res = await ctx.get(p(`Translations?lang=${lang}`));
    expect(res.ok(), `lang=${lang}`).toBeTruthy();
    const map = (await res.json()) as Record<string, unknown>;
    const keys = Object.keys(map);
    expect(keys.length).toBeGreaterThan(0);
    for (const k of keys.slice(0, 20)) {
      expect(typeof map[k]).toBe('string');
    }
  }
  await assertPluginActive(ctx);
});
