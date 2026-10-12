/**
 * Codec card order: the dedicated PUT /Configuration/CodecCardOrder endpoint (strict validation,
 * normalization), the GET reflecting the stored order, and the backup export/import round-trip
 * carrying the order as plaintext (not a secret).
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

let ctx: APIRequestContext;

const CANONICAL = [
  'containers', 'resolutions', 'dynamicRanges', 'videoCodecs', 'videoAudioCodecs',
  'videoBitrate', 'watched', 'audioLanguages', 'subtitleLanguages', 'musicAudioCodecs', 'bookFormats',
];

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
  await assertPluginActive(ctx);
});
test.afterAll(async () => {
  // Leave the server on the canonical order so other suites see a clean layout, then dispose.
  await putOrder(CANONICAL);
  await ctx.dispose();
});

function putOrder(order: unknown) {
  return ctx.put(p('Configuration/CodecCardOrder'), {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify({ codecCardOrder: order }),
  });
}

async function getStoredOrder(): Promise<string[]> {
  const cfg = await ctx.get(p('Configuration')).then((r) => r.json());
  return cfg.CodecCardOrder ?? cfg.codecCardOrder;
}

test('valid permutation is accepted and reflected by GET', async () => {
  const permuted = [...CANONICAL].reverse();
  const res = await putOrder(permuted);
  expect(res.ok(), `PUT failed: ${res.status()}`).toBeTruthy();

  const body = await res.json();
  const saved = body.CodecCardOrder ?? body.codecCardOrder;
  expect(saved).toEqual(permuted);

  // GET reflects the same order (persisted, not just echoed).
  expect(await getStoredOrder()).toEqual(permuted);
});

test('partial order is backfilled with the missing canonical ids', async () => {
  const res = await putOrder(['bookFormats', 'containers']);
  expect(res.ok()).toBeTruthy();

  const stored = await getStoredOrder();
  // First two honored, every canonical id still present exactly once.
  expect(stored.slice(0, 2)).toEqual(['bookFormats', 'containers']);
  expect([...stored].sort()).toEqual([...CANONICAL].sort());
  expect(stored.length).toBe(CANONICAL.length);
});

test('empty order is accepted and yields the canonical order', async () => {
  const res = await putOrder([]);
  expect(res.ok()).toBeTruthy();
  expect(await getStoredOrder()).toEqual(CANONICAL);
});

test('unknown id is rejected with 400', async () => {
  const res = await putOrder(['containers', 'not-a-real-card']);
  expect(res.status()).toBe(400);
});

test('duplicate id is rejected with 400', async () => {
  const res = await putOrder(['containers', 'containers']);
  expect(res.status()).toBe(400);
});

test('a reorder does not disturb unrelated config fields', async () => {
  // Seed an unrelated field via the full config PUT, then reorder, then confirm the field survived
  // (the dedicated endpoint must not touch anything but the order).
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify({ Language: 'de', OrphanMinAgeDays: 7 }),
  });

  await putOrder([...CANONICAL].reverse());

  const cfg = await ctx.get(p('Configuration')).then((r) => r.json());
  expect(cfg.Language).toBe('de');
  expect(cfg.OrphanMinAgeDays).toBe(7);
});

test('backup export/import round-trips the card order', async () => {
  const permuted = ['watched', 'containers', ...CANONICAL.filter((c) => c !== 'watched' && c !== 'containers')];
  await putOrder(permuted);

  const backup = await ctx.get(p('Backup/Export?includeSecrets=false')).then((r) => r.json());
  expect(backup.codecCardOrder).toEqual(permuted);

  // Change the live order away from the backup, then import and confirm it is restored.
  await putOrder([...CANONICAL].reverse());
  const imp = await ctx.post(p('Backup/Import'), {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify(backup),
  });
  expect(imp.ok(), `import failed: ${imp.status()}`).toBeTruthy();

  expect(await getStoredOrder()).toEqual(permuted);
});
