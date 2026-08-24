/**
 * Idempotency - repeating the SAME mutation must converge to the same state,
 * not accumulate or drift. This surface had no coverage before.
 *
 * Each block asserts a LOAD-BEARING signal, deliberately avoiding the vacuous
 * traps the source review flagged:
 *   - Backup re-import: CredentialsChanged flips true->false on the 2nd import of
 *     the same secrets backup (run 1 sees a new key, run 2 the key already
 *     matches). Asserting ConfigurationRestored===true would be vacuous - it is
 *     true on every valid import regardless of change. We also prove the stored
 *     config values are identical after both imports.
 *   - Config PUT: two identical PUTs -> identical GET state. Keys are sent MASKED
 *     (the mask sentinel) so no live Arr/Seerr connection test runs (its Warnings[] are
 *     network-dependent and would be flaky - we assert stored state, not warnings).
 *   - Discovery (admin) Request: the plugin does NOT dedupe the Seerr submission.
 *     The correct, non-vacuous assertion is that a repeated request reaches the
 *     mock AGAIN (count increments) - a test expecting dedupe would be wrong.
 *   - Trash/Relocate: a 2nd relocate of an already-drained source is a clean
 *     no-op (Moved:0, Failed:0, 200). Requires the container FS; skips loudly.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive, API_KEY_MASK } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  verifyCanaries,
  containerDirExists,
  containerFileExists,
  containerWriteFile,
  containerRm,
} from '../setup/fs-assert.ts';

const MOCK = process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  await ctx.dispose();
});

async function exportBackup(includeSecrets = false): Promise<any> {
  const res = await ctx.get(p(`Backup/Export?includeSecrets=${includeSecrets}`));
  expect(res.ok(), `export failed: ${res.status()}`).toBeTruthy();
  return JSON.parse(await res.text());
}

function importBackup(body: unknown) {
  return ctx.post(p('Backup/Import'), {
    headers: { 'Content-Type': 'application/json' },
    data: typeof body === 'string' ? body : JSON.stringify(body),
  });
}

async function putConfig(data: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data,
  });
  expect(res.ok(), `putConfig failed: ${res.status()}`).toBeTruthy();
  return res;
}

async function getConfig(): Promise<any> {
  const res = await ctx.get(p('Configuration'));
  expect(res.ok(), `get config failed: ${res.status()}`).toBeTruthy();
  return res.json();
}

interface ImportSummary {
  ConfigurationRestored: boolean;
  TimelineRestored: boolean;
  BaselineRestored: boolean;
  CredentialsChanged: boolean;
}

// --- backup re-import ------------------------------------------------------

test.describe.serial('Backup/Import is idempotent (2nd import of the same file stabilizes)', () => {
  test('CredentialsChanged flips true→false and config values are identical on re-import', async () => {
    // Seed a KNOWN prior Seerr key so the backup's key is genuinely different on
    // the first import (-> CredentialsChanged true), then identical on the second.
    await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'old-key-before' });

    const backup = await exportBackup(true); // secrets export carries the real key
    backup.seerrApiKey = 'brand-new-idem-key';
    // Also pin a couple of scalar fields so we can prove value-stability.
    backup.language = 'de';
    backup.orphanMinAgeDays = 17;

    try {
      const first = await importBackup(backup);
      expect(first.ok(), `1st import failed: ${first.status()}`).toBeTruthy();
      const firstSummary = ((await first.json()) as { summary: ImportSummary }).summary;
      expect(firstSummary.CredentialsChanged, '1st import sees a NEW key → changed').toBe(true);

      const cfgAfterFirst = await getConfig();

      const second = await importBackup(backup);
      expect(second.ok(), `2nd import failed: ${second.status()}`).toBeTruthy();
      const secondSummary = ((await second.json()) as { summary: ImportSummary }).summary;
      // The load-bearing idempotency signal: the SAME key is now already stored, so
      // the repeat reports no credential change.
      expect(secondSummary.CredentialsChanged, '2nd import: key already matches → NOT changed').toBe(false);

      const cfgAfterSecond = await getConfig();
      // Every restored scalar is identical after the 2nd import (pure overwrite, no drift).
      expect(cfgAfterSecond.Language).toBe(cfgAfterFirst.Language);
      expect(cfgAfterSecond.Language).toBe('de');
      expect(cfgAfterSecond.OrphanMinAgeDays).toBe(cfgAfterFirst.OrphanMinAgeDays);
      expect(cfgAfterSecond.OrphanMinAgeDays).toBe(17);
      expect(cfgAfterSecond.SeerrUrl).toBe(cfgAfterFirst.SeerrUrl);
      await assertPluginActive(ctx);
    } finally {
      // Restore a benign baseline for later specs even if an assertion threw.
      await putConfig({ Language: 'en', SeerrApiKey: 'seerr-key' });
    }
  });
});

// --- config PUT ------------------------------------------------------------

test('PUT /Configuration twice with the same body yields identical stored state', async () => {
  // Masked keys (the mask sentinel) make the save skip the live Arr/Seerr connection tests,
  // so the result is deterministic and independent of mock reachability.
  const body = {
    Language: 'en',
    OrphanMinAgeDays: 21,
    UseTrash: true,
    TrashFolderPath: '.jellyfin-trash',
    TrashRetentionDays: 14,
    SeerrUrl: 'http://mock-seerr:5055',
    SeerrApiKey: API_KEY_MASK,
  };
  await putConfig(body);
  const afterFirst = await getConfig();
  await putConfig(body);
  const afterSecond = await getConfig();

  try {
    for (const k of ['Language', 'OrphanMinAgeDays', 'UseTrash', 'TrashFolderPath', 'TrashRetentionDays', 'SeerrUrl'] as const) {
      expect(afterSecond[k], `${k} stable across identical PUTs`).toEqual(afterFirst[k]);
    }
    // And the values actually took (not just "equal to each other").
    expect(afterSecond.OrphanMinAgeDays).toBe(21);
    expect(afterSecond.TrashRetentionDays).toBe(14);
    await assertPluginActive(ctx);
  } finally {
    await putConfig({ UseTrash: false, OrphanMinAgeDays: 30, TrashRetentionDays: 30 });
  }
});

// --- discovery request: NO dedupe (the correct behavior) -------------------

test.describe.serial('Discovery admin request does NOT dedupe (repeat forwards again)', () => {
  async function resetMock(): Promise<void> {
    const m = await pwRequest.newContext();
    try {
      const r = await m.get(`${MOCK}/reset`);
      expect(r.ok(), `mock /reset failed: ${r.status()}`).toBeTruthy();
    } finally {
      await m.dispose();
    }
  }

  async function mockRequestCount(): Promise<number> {
    const m = await pwRequest.newContext();
    try {
      const r = await m.get(`${MOCK}/last-request`);
      expect(r.ok(), `mock /last-request failed: ${r.status()}`).toBeTruthy();
      return ((await r.json()) as { count: number }).count;
    } finally {
      await m.dispose();
    }
  }

  test.beforeAll(async () => {
    const cfg = await putConfig({ SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'seerr-key' });
    expect(cfg.ok()).toBeTruthy();
  });

  test('the same admin request submitted twice reaches Seerr twice (no server-side dedupe)', async () => {
    await resetMock();
    const reqBody = { TmdbId: 27205, MediaType: 'movie' };

    const first = await ctx.post(p('Discovery/Request'), {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify(reqBody),
    });
    // The mock is a HARD dependency here, not an optional one: its reachability is
    // already proven loudly elsewhere in a green run (settings.api.spec.ts asserts
    // res.ok() + recorded===1 for the same admin Discovery/Request forward). So a
    // 502 here is NOT "mock unreachable" - it is a regression in the forward wiring
    // that would otherwise silently drop this file's sole no-dedupe assertion. Fail
    // loudly instead of test.skip, matching tasks.api.spec.ts's treatment of the mock.
    expect(first.status(), `1st request status ${first.status()} - mock must be reachable for the dedupe proof`).not.toBe(502);
    expect(first.ok(), '1st admin request should succeed against the mock').toBeTruthy();
    expect(await mockRequestCount(), 'one request forwarded after the 1st call').toBe(1);

    const second = await ctx.post(p('Discovery/Request'), {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify(reqBody),
    });
    expect(second.ok(), '2nd identical admin request also succeeds').toBeTruthy();
    // The plugin performs NO dedupe of the Seerr submission - the correct behavior
    // is that the identical request is forwarded AGAIN. (Local cache/feedback
    // bookkeeping dedupes, but the upstream submission does not.)
    expect(await mockRequestCount(), 'the repeated request is forwarded again (no dedupe)').toBe(2);
    await assertPluginActive(ctx);
  });
});

// --- trash relocate no-op (filesystem) -------------------------------------

test.describe.serial('Trash/Relocate is a clean no-op when the source is already drained', () => {
  const OLD = '/media/Movies/.idem-old';
  const NEW = '/media/Movies/.idem-new';

  interface RelocateResponse { Moved: number; Failed: number }

  test.beforeAll(async () => {
    const cfg = await putConfig({ UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 });
    expect(cfg.ok()).toBeTruthy();
  });
  test.afterAll(async () => {
    await putConfig({ UseTrash: false }).catch(() => undefined);
  });
  test.beforeEach(() => {
    ensureCanariesPlanted(); // skips loudly w/o docker
    containerRm(OLD);
    containerRm(NEW);
  });
  test.afterEach(() => {
    expect(verifyCanaries(), 'relocate must never touch anything outside /media').toEqual([]);
  });

  function relocate(oldPath: string, newPath: string) {
    return ctx.post(p('Trash/Relocate'), {
      headers: { 'Content-Type': 'application/json' },
      data: { OldTrashPath: oldPath, NewTrashPath: newPath },
    });
  }

  test('relocating an already-moved (now-empty) source returns Moved:0, Failed:0', async () => {
    // Seed one entry and relocate it once - this drains + removes the source.
    containerWriteFile(`${OLD}/Entry (2001)/payload.mkv`, 'IDEM-CONTENT');

    const first = await relocate(OLD, NEW);
    expect(first.ok(), `1st relocate status ${first.status()}`).toBeTruthy();
    const firstBody = (await first.json()) as RelocateResponse;
    expect(firstBody.Failed).toBe(0);
    expect(firstBody.Moved, 'the seeded entry moved on the first relocate').toBe(1);
    expect(containerFileExists(`${NEW}/Entry (2001)/payload.mkv`), 'content at destination').toBe(true);
    expect(containerDirExists(OLD), 'source removed once drained').toBe(false);

    // Relocate the SAME (now absent) source again - a clean no-op, not an error.
    const second = await relocate(OLD, NEW);
    expect(second.ok(), `2nd relocate status ${second.status()}`).toBeTruthy();
    const secondBody = (await second.json()) as RelocateResponse;
    expect(secondBody.Moved, 'nothing left to move on the repeat').toBe(0);
    expect(secondBody.Failed, 'a no-op relocate reports no failures').toBe(0);
    // The destination is untouched by the no-op repeat.
    expect(containerFileExists(`${NEW}/Entry (2001)/payload.mkv`), 'destination unchanged').toBe(true);
    await assertPluginActive(ctx);

    // Cleanup scratch dirs.
    containerRm(NEW);
  });
});
