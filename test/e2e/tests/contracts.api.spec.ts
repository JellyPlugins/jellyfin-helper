/**
 * Contract assertions for endpoints that the rest of the suite only *routes*
 * (smoke) or *tolerates a status class* for (hardening's [200,400,404,503]).
 * Those loose checks let a regression that flips a 400 into a silent 200 - or a
 * 503 guard into a 200 - pass unnoticed. Here we PIN the documented status +
 * body for each branch, verified against the controller source.
 *
 * State handling: several endpoints gate on RecommendationsTaskMode. Each block
 * sets the mode it needs in a beforeAll and restores a neutral mode after, so
 * the blocks are order-independent under the single serial worker.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p } from '../setup/api-client.ts';

const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

let ctx: APIRequestContext;
let adminUserId: string;

test.beforeAll(async () => {
  const auth = loadAuth();
  adminUserId = auth.userId;
  ctx = await apiContext(auth);
});
test.afterAll(async () => {
  await ctx.dispose();
});

async function setRecsMode(mode: 'Deactivate' | 'DryRun' | 'Activate'): Promise<void> {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { RecommendationsTaskMode: mode },
  });
  expect(res.ok(), `setRecsMode(${mode}) failed: ${res.status()}`).toBeTruthy();
}

// --- Recommendations / UserActivity: 503 Deactivate guard -------------------
// Both controllers return 503 "…disabled in plugin configuration." when
// RecommendationsTaskMode == Deactivate. smoke tolerates [200,503] so this
// specific guard is never actually asserted anywhere.
test.describe('Deactivate guards return 503', () => {
  test.beforeAll(() => setRecsMode('Deactivate'));
  test.afterAll(() => setRecsMode('DryRun'));

  test('Recommendations endpoints 503 when Deactivate', async () => {
    for (const path of [
      'Recommendations',
      `Recommendations/${adminUserId}`,
      `Recommendations/WatchProfile/${adminUserId}`,
      'Recommendations/WatchProfiles',
    ]) {
      const res = await ctx.get(p(path));
      expect(res.status(), `${path} should 503 when Deactivate`).toBe(503);
      expect(await res.text()).toContain('disabled');
    }
  });

  test('UserActivity endpoints 503 when Deactivate', async () => {
    for (const path of ['UserActivity/Latest', `UserActivity/User/${adminUserId}`]) {
      const res = await ctx.get(p(path));
      expect(res.status(), `${path} should 503 when Deactivate`).toBe(503);
      expect(await res.text()).toContain('disabled');
    }
  });
});

// --- empty-GUID → 400 (pinned, not tolerated) -------------------------------
test.describe('empty-GUID user routes return 400', () => {
  test.beforeAll(() => setRecsMode('DryRun'));

  test('Recommendations/{empty} → 400', async () => {
    const res = await ctx.get(p(`Recommendations/${EMPTY_GUID}`));
    expect(res.status()).toBe(400);
    expect(await res.text()).toContain('valid, non-empty userId');
  });

  test('Recommendations/WatchProfile/{empty} → 400', async () => {
    const res = await ctx.get(p(`Recommendations/WatchProfile/${EMPTY_GUID}`));
    expect(res.status()).toBe(400);
    expect(await res.text()).toContain('valid, non-empty userId');
  });

  test('UserActivity/User/{empty} → 400', async () => {
    const res = await ctx.get(p(`UserActivity/User/${EMPTY_GUID}`));
    expect(res.status()).toBe(400);
    expect(await res.text()).toContain('valid, non-empty userId');
  });
});

// --- UserActivity: unknown-but-valid user → 404; maxResults clamp -----------
test.describe('UserActivity/User behavior', () => {
  test.beforeAll(() => setRecsMode('DryRun'));

  test('valid-format unknown user → 404', async () => {
    // A well-formed GUID that is not a real user resolves past the empty-GUID
    // guard and fails _userManager.GetUserById → 404.
    const res = await ctx.get(p('UserActivity/User/11111111-2222-3333-4444-555555555555'));
    expect(res.status()).toBe(404);
  });

  test('maxResults is clamped and the call succeeds for a real user', async () => {
    // maxResults is clamped to 1..200; an out-of-range value must not error.
    const res = await ctx.get(p(`UserActivity/User/${adminUserId}?maxResults=9999`));
    // 200 with cache, or 503 if the activity cache is not populated in this run.
    expect([200, 503]).toContain(res.status());
    if (res.status() === 200) {
      const body = (await res.json()) as unknown[];
      expect(Array.isArray(body)).toBe(true);
      expect(body.length).toBeLessThanOrEqual(200);
    }
  });
});

// --- Discovery/Request: validation 400s -------------------------------------
// The DTO's DataAnnotations ([Range]/[RegularExpression]) are enforced by
// [ApiController]'s automatic model validation, which short-circuits with an
// RFC9110 ValidationProblemDetails envelope ({title, status, errors:{Field:[…]}})
// BEFORE the action body's hand-built {Success:false} path runs. We assert the
// actual, observable contract - the DataAnnotation ErrorMessage still surfaces.
test.describe('Discovery/Request validation', () => {
  test('TmdbId 0 → 400 problem-details with the TmdbId message', async () => {
    const res = await ctx.post(p('Discovery/Request'), {
      headers: { 'Content-Type': 'application/json' },
      data: { TmdbId: 0, MediaType: 'movie' },
    });
    expect(res.status()).toBe(400);
    const body = (await res.json()) as { errors?: Record<string, string[]> };
    expect(JSON.stringify(body.errors ?? body)).toContain('TmdbId must be greater than 0');
  });

  test('bad MediaType → 400 problem-details with the MediaType message', async () => {
    const res = await ctx.post(p('Discovery/Request'), {
      headers: { 'Content-Type': 'application/json' },
      data: { TmdbId: 123, MediaType: 'bogus' },
    });
    expect(res.status()).toBe(400);
    const body = (await res.json()) as { errors?: Record<string, string[]> };
    expect(JSON.stringify(body.errors ?? body)).toContain('MediaType must be either');
  });

  test('null body → 400 (rejected by [ApiController] model validation)', async () => {
    // A literal `null` / empty JSON body is a required-body violation: [ApiController]
    // auto-validation short-circuits with an RFC9110 ValidationProblemDetails
    // envelope ({title, status, errors}) BEFORE the action's own {Success:false}
    // null-guard can run (that guard is only reachable via direct in-process calls).
    // Pin the observable wire contract, like the TmdbId/MediaType cases above.
    const res = await ctx.post(p('Discovery/Request'), {
      headers: { 'Content-Type': 'application/json' },
      data: 'null',
    });
    expect(res.status()).toBe(400);
    const body = (await res.json()) as { title?: string; status?: number; errors?: Record<string, string[]> };
    // No {Success} field on this path; assert the problem-details shape instead.
    expect(body.errors ?? body.title, 'problem-details envelope expected').toBeTruthy();
    expect(JSON.stringify(body)).toMatch(/body|required/i);
  });
});

// --- Ping: liveness body contract -------------------------------------------
test('Ping returns the {Ok, Plugin, Version} liveness contract', async () => {
  const res = await ctx.get(p('Ping'));
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as { Ok: boolean; Plugin: string; Version: string };
  expect(body.Ok).toBe(true);
  expect(body.Plugin).toBe('JellyfinHelper');
  expect(typeof body.Version).toBe('string');
  expect(body.Version.length).toBeGreaterThan(0);
});

// --- Translations: no-lang fallback + malformed → 400 (pinned) --------------
test.describe('Translations contract', () => {
  test('no lang param → configured-language fallback returns a non-empty map', async () => {
    const res = await ctx.get(p('Translations'));
    expect(res.ok()).toBeTruthy();
    const body = (await res.json()) as Record<string, string>;
    expect(Object.keys(body).length).toBeGreaterThan(0);
  });

  test('malformed lang → 400 with the exact contract message', async () => {
    const res = await ctx.get(p('Translations?lang=en-US-INVALID-TOO-LONG'));
    expect(res.status()).toBe(400);
    const body = (await res.json()) as { message: string };
    expect(body.message).toContain('Invalid language code');
  });
});

// --- Configuration/Libraries + LibraryPaths: response shape -----------------
test('Configuration/Libraries returns {Libraries[]} with Name + CollectionType', async () => {
  const res = await ctx.get(p('Configuration/Libraries'));
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as { Libraries: Array<{ Name: string; CollectionType?: string }> };
  expect(Array.isArray(body.Libraries)).toBe(true);
  for (const lib of body.Libraries) {
    expect(typeof lib.Name).toBe('string');
    // Music/boxset libraries are filtered out server-side.
    expect(lib.Name.toLowerCase()).not.toContain('boxset');
  }
});

test('Configuration/LibraryPaths returns {LibraryPaths[]} with Name + Path', async () => {
  const res = await ctx.get(p('Configuration/LibraryPaths'));
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as { LibraryPaths: Array<{ Name: string; Path: string }> };
  expect(Array.isArray(body.LibraryPaths)).toBe(true);
  for (const entry of body.LibraryPaths) {
    expect(entry.Name.length).toBeGreaterThan(0);
    expect(entry.Path.length).toBeGreaterThan(0);
  }
});

// --- GrowthTimeline forceRefresh recompute path -----------------------------
test('GrowthTimeline?forceRefresh=true recomputes (200 or 429), data stays coherent', async () => {
  const res = await ctx.get(p('GrowthTimeline?forceRefresh=true'));
  // The recompute path is rate-limited; a 429 with Retry-After is a valid,
  // documented outcome. Anything else must be a clean 200.
  expect([200, 429]).toContain(res.status());
  if (res.status() === 429) {
    expect(res.headers()['retry-after']).toBeTruthy();
    return;
  }
  const body = (await res.json()) as { DataPoints?: Array<{ CumulativeFileCount?: number; CumulativeSize?: number }> };
  const points = body.DataPoints ?? [];
  for (const pt of points) {
    if (typeof pt.CumulativeFileCount === 'number') expect(pt.CumulativeFileCount).toBeGreaterThanOrEqual(0);
    if (typeof pt.CumulativeSize === 'number') expect(pt.CumulativeSize).toBeGreaterThanOrEqual(0);
  }
});
