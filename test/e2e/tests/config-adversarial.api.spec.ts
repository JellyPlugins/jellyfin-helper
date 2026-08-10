/**
 * Adversarial configuration-save tests. Fat-finger / hostile inputs to
 * PUT /Configuration and PUT /Configuration/LogLevel must fail cleanly (400,
 * never 500), never silently apply, and never corrupt the stored config.
 *
 * HTTP-only - runs everywhere (no container FS needed).
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

async function getConfig(): Promise<any> {
  const res = await ctx.get(p('Configuration'));
  expect(res.ok()).toBeTruthy();
  return res.json();
}

test('PUT /Configuration/LogLevel with a null literal body → 400, not 500', async () => {
  // Regression for the NRE bug: a literal `null` binds request to null.
  const res = await ctx.put(p('Configuration/LogLevel'), {
    headers: { 'Content-Type': 'application/json' },
    data: 'null',
  });
  expect(res.status(), 'null body must be a clean 400').toBe(400);
  await assertPluginActive(ctx);
});

test('PUT /Configuration/LogLevel with an unknown level → 400, stored level unchanged', async () => {
  const before = (await getConfig()).PluginLogLevel;
  const res = await ctx.put(p('Configuration/LogLevel'), {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify({ PluginLogLevel: 'NONSENSE' }),
  });
  expect(res.status()).toBe(400);
  expect((await getConfig()).PluginLogLevel, 'invalid level must not persist').toBe(before);
  await assertPluginActive(ctx);
});

test('unknown task-mode enum on PUT /Configuration → 400, other fields untouched', async () => {
  const before = await getConfig();
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify({ TrickplayTaskMode: 'Bogus' }),
  });
  expect(res.status()).toBe(400);
  // Atomic reject: an unrelated field must not have changed.
  const after = await getConfig();
  expect(after.OrphanMinAgeDays).toBe(before.OrphanMinAgeDays);
  expect(after.TrickplayTaskMode).toBe(before.TrickplayTaskMode);
  await assertPluginActive(ctx);
});

test('oversized RadarrInstances array (10000) → 400, known-good list preserved, no hang', async () => {
  // Establish a known-good single instance first, so we can prove the rejected
  // oversized save did NOT wipe or corrupt the existing config (a `?? []` length
  // check alone passes vacuously when the field is cleared).
  const seed = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify({ RadarrInstances: [{ Name: 'Known Good', Url: 'http://mock-arr:9000', ApiKey: 'k' }] }),
  });
  expect(seed.ok(), `baseline save failed: ${seed.status()}`).toBeTruthy();

  const started = Date.now();
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify({
      RadarrInstances: Array.from({ length: 10_000 }, (_, i) => ({
        Name: `R${i}`, Url: 'http://mock-arr:9000', ApiKey: 'k',
      })),
    }),
  });
  try {
    expect([400, 413]).toContain(res.status());
    expect(Date.now() - started, 'must not hang').toBeLessThan(20_000);

    const cfg = await getConfig();
    const instances = (cfg.RadarrInstances ?? []) as Array<{ Name: string }>;
    expect(instances.length, 'stored list stays within the cap').toBeLessThanOrEqual(3);
    // The rejected save must not have wiped the pre-existing known-good instance.
    expect(instances.map((i) => i.Name), 'rejected oversized save must not corrupt existing config')
      .toContain('Known Good');
    await assertPluginActive(ctx);
  } finally {
    // Restore the shared default so later specs see a working Radarr instance -
    // even if an assertion above threw (else the rejected/huge state would bleed).
    await ctx.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify({ RadarrInstances: [{ Name: 'Mock Radarr', Url: 'http://mock-arr:9000', ApiKey: 'radarr-key' }] }),
    });
  }
});

test('XML-hostile ExcludedLibraries persists without corrupting the config on read-back', async () => {
  const hostile = ']]><evil>&amp;  control';
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify({ ExcludedLibraries: hostile }),
  });
  try {
    // Either accepted (and round-trips) or rejected (400) - never a 500 / corrupt state.
    expect(res.status()).toBeLessThan(500);
    // Config must still be readable afterwards (proves the XML file didn't corrupt).
    const cfg = await getConfig();
    expect(typeof cfg.ExcludedLibraries).toBe('string');
    await assertPluginActive(ctx);
  } finally {
    // Restore a clean value even if an assertion threw.
    await ctx.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify({ ExcludedLibraries: '' }),
    });
  }
});

test('concurrent racing PUTs leave the config as exactly one coherent set', async () => {
  const setA = { RadarrInstances: [{ Name: 'A', Url: 'http://mock-arr:9000', ApiKey: 'k' }] };
  const setB = {
    RadarrInstances: [
      { Name: 'B1', Url: 'http://mock-arr:9000', ApiKey: 'k' },
      { Name: 'B2', Url: 'http://mock-arr:9000', ApiKey: 'k' },
    ],
  };
  const puts = Array.from({ length: 10 }, (_, i) =>
    ctx.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify(i % 2 === 0 ? setA : setB),
    }),
  );
  const results = await Promise.all(puts);
  try {
    for (const r of results) expect(r.status()).toBeLessThan(500);

    // Final persisted list must be exactly one of the two submitted sets (no torn/merged list).
    const names = ((await getConfig()).RadarrInstances ?? []).map((i: any) => i.Name).sort();
    expect([['A'], ['B1', 'B2']]).toContainEqual(names);
    await assertPluginActive(ctx);
  } finally {
    // Restore even if an assertion threw.
    await ctx.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify({ RadarrInstances: [{ Name: 'Mock Radarr', Url: 'http://mock-arr:9000', ApiKey: 'radarr-key' }] }),
    });
  }
});
