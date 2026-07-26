/**
 * User-facing request submission — POST /Discovery/My/Request authorization.
 *
 * This is the security-critical path that stops a non-admin user from routing a
 * download to an arbitrary quality profile / root folder. The controller
 * (UserDiscoveryController.SubmitMyRequest) enforces, in order:
 *   - a per-user 10s rate limit (429 + Retry-After; a rejected call does NOT
 *     extend the window);
 *   - ServerId and ProfileId must be supplied together (else 400);
 *   - the (ServerId, ProfileId) pair must be one the user is actually allowed
 *     (else 403);
 *   - the RootFolder must match the matched profile's root (else 403).
 * A valid submission forwards to Seerr with the CALLER's resolved SeerrUserId.
 *
 * The non-admin user is linked in global-setup to the mock's second Seerr user
 * (Bob) with the Request permission (bit 32), so it has exactly ONE allowed
 * profile — the server default. We read that allowed profile from
 * RequestPermissions rather than hard-coding the mock's ids, so the assertions
 * stay correct if the mock's profile fixture changes.
 *
 * The 10s rate limit is keyed by the caller's Jellyfin GUID in a process-wide
 * static, and any call that passes the rate check (even one that then 400/403s)
 * opens the window. So every request-submitting step here is spaced past the
 * window; the suite runs serially (workers: 1), so this is deterministic.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, normalUserContext, requireNormalUser, loadAuth, p, assertPluginActive, sleep } from '../setup/api-client.ts';

const MOCK = process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';
// 10s window + margin; the controller uses Math.Ceiling so a 10.0s wait can still
// round to a 1s Retry-After — wait comfortably past it.
const RATE_WINDOW_MS = 11_500;

const auth = loadAuth();

let admin: APIRequestContext;
let user: APIRequestContext | null;

interface AllowedProfile { ServerId: number; ProfileId: number; RootFolder: string }
interface PermissionResult { CanRequest: boolean; IsTransient: boolean; Profiles: AllowedProfile[] }

async function resetMock(): Promise<void> {
  const m = await pwRequest.newContext();
  try {
    const r = await m.get(`${MOCK}/reset`);
    expect(r.ok(), `mock /reset failed: ${r.status()}`).toBeTruthy();
  } finally {
    await m.dispose();
  }
}

async function lastRequestCount(): Promise<{ count: number; requests: Array<{ mediaId?: number; mediaType?: string; userId?: number }> }> {
  const m = await pwRequest.newContext();
  try {
    const r = await m.get(`${MOCK}/last-request`);
    expect(r.ok()).toBeTruthy();
    return (await r.json()) as { count: number; requests: Array<{ mediaId?: number; mediaType?: string; userId?: number }> };
  } finally {
    await m.dispose();
  }
}

function submitMyRequest(body: Record<string, unknown>) {
  return user!.post(p('Discovery/My/Request'), {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify(body),
  });
}

test.beforeAll(async () => {
  admin = await apiContext(auth);
  user = await normalUserContext(auth);
  // Enable user discovery (needs Recommendations active + Seerr configured).
  const cfg = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      RecommendationsTaskMode: 'Activate',
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: 'seerr-key',
      DiscoveryUserAccessEnabled: true,
    },
  });
  expect(cfg.ok(), `enable discovery failed: ${cfg.status()}`).toBeTruthy();
});

test.afterAll(async () => {
  // Leave discovery access off so later specs see the default state.
  await admin
    .put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { DiscoveryUserAccessEnabled: false },
    })
    .catch(() => undefined);
  await admin.dispose();
  await user?.dispose();
});

test.describe.serial('POST /Discovery/My/Request — authorization branches', () => {
  let allowed: AllowedProfile;

  test('the non-admin user resolves to exactly one allowed (default) profile', async () => {
    requireNormalUser(user);
    const res = await user!.get(p('Discovery/My/RequestPermissions/radarr?mediaType=movie'));
    expect(res.ok(), `RequestPermissions failed: ${res.status()}`).toBeTruthy();
    const perms = (await res.json()) as PermissionResult;
    // If the mock link/permission seeding didn't take, this user can't request —
    // fail loudly rather than assert vacuously on an empty profile set.
    expect(perms.CanRequest, `user cannot request (transient=${perms.IsTransient}) — seed-user2 link/permission missing`).toBe(true);
    expect(perms.Profiles.length, 'a Request-only user should get exactly the default profile').toBeGreaterThan(0);
    allowed = perms.Profiles[0];
    expect(typeof allowed.ProfileId).toBe('number');
    expect(allowed.RootFolder.length, 'the mock default profile has a root folder').toBeGreaterThan(0);
  });

  test('ServerId without ProfileId → 400 (must be supplied together)', async () => {
    requireNormalUser(user);
    await sleep(RATE_WINDOW_MS);
    const res = await submitMyRequest({ TmdbId: 27205, MediaType: 'movie', ServerId: allowed.ServerId });
    expect(res.status(), 'ServerId alone must be a 400').toBe(400);
    const body = (await res.json()) as { Success: boolean; Message: string };
    expect(body.Success).toBe(false);
    expect(body.Message).toMatch(/ServerId and ProfileId/i);
    await assertPluginActive(admin);
  });

  test('unmatched (ServerId, ProfileId) → 403 (not an allowed profile)', async () => {
    requireNormalUser(user);
    await sleep(RATE_WINDOW_MS);
    const res = await submitMyRequest({
      TmdbId: 27205, MediaType: 'movie',
      ServerId: allowed.ServerId, ProfileId: allowed.ProfileId + 9999,
    });
    expect(res.status(), 'an un-allowed profile must be 403').toBe(403);
    const body = (await res.json()) as { Success: boolean; Message: string };
    expect(body.Success).toBe(false);
    expect(body.Message).toMatch(/not authorized to use this quality profile/i);
    await assertPluginActive(admin);
  });

  test('matched profile but wrong RootFolder → 403 (root must match)', async () => {
    requireNormalUser(user);
    await sleep(RATE_WINDOW_MS);
    const res = await submitMyRequest({
      TmdbId: 27205, MediaType: 'movie',
      ServerId: allowed.ServerId, ProfileId: allowed.ProfileId,
      RootFolder: '/definitely/not/the/allowed/root',
    });
    expect(res.status(), 'a wrong root folder must be 403').toBe(403);
    const body = (await res.json()) as { Success: boolean; Message: string };
    expect(body.Success).toBe(false);
    expect(body.Message).toMatch(/not authorized to use this root folder/i);
    await assertPluginActive(admin);
  });

  test('valid override submission → success, forwarded to Seerr with the caller identity', async () => {
    requireNormalUser(user);
    await resetMock();
    await sleep(RATE_WINDOW_MS);
    const res = await submitMyRequest({
      TmdbId: 27205, MediaType: 'movie',
      ServerId: allowed.ServerId, ProfileId: allowed.ProfileId, RootFolder: allowed.RootFolder,
    });
    expect(res.ok(), `a valid override submission must succeed: ${res.status()}`).toBeTruthy();

    // The submission actually reached Seerr with the right media, exactly once.
    const last = await lastRequestCount();
    expect(last.count, 'exactly one request must reach the mock').toBe(1);
    expect(last.requests[0].mediaId, 'forwarded TmdbId').toBe(27205);
    expect(last.requests[0].mediaType, 'forwarded mediaType').toBe('movie');
    // The caller's resolved Seerr user id (Bob = mock id 4) is forwarded, never a
    // spoofed/absent id — the identity guard.
    expect(last.requests[0].userId, 'forwarded caller SeerrUserId').toBe(4);
    await assertPluginActive(admin);
  });

  test('no-override submission → success (uses Seerr server defaults)', async () => {
    requireNormalUser(user);
    await resetMock();
    await sleep(RATE_WINDOW_MS);
    const res = await submitMyRequest({ TmdbId: 27205, MediaType: 'movie' });
    expect(res.ok(), `a no-override submission must succeed: ${res.status()}`).toBeTruthy();
    const last = await lastRequestCount();
    expect(last.count, 'the default-profile request reaches the mock once').toBe(1);
    expect(last.requests[0].userId, 'caller identity forwarded').toBe(4);
    await assertPluginActive(admin);
  });
});

test.describe.serial('POST /Discovery/My/Request — per-user rate limit', () => {
  test('a second request inside the 10s window is 429 with Retry-After, and does not extend the window', async () => {
    requireNormalUser(user);
    await resetMock();
    // Open a fresh window (wait out anything the branch tests left behind).
    await sleep(RATE_WINDOW_MS);

    const first = await submitMyRequest({ TmdbId: 27205, MediaType: 'movie' });
    expect(first.ok(), `first request should be accepted: ${first.status()}`).toBeTruthy();

    const second = await submitMyRequest({ TmdbId: 27205, MediaType: 'movie' });
    expect(second.status(), 'a rapid second request must be rate-limited').toBe(429);
    const retryAfter = Number(second.headers()['retry-after']);
    expect(Number.isFinite(retryAfter), 'Retry-After header present and numeric').toBe(true);
    expect(retryAfter, 'Retry-After within the 10s window').toBeGreaterThan(0);
    expect(retryAfter, 'Retry-After no larger than the window').toBeLessThanOrEqual(10);

    // A rejected request must NOT reset/extend the window: a third immediate call
    // still reports a Retry-After that is not LARGER than the second's (the window
    // keeps counting down from the first accepted call, it did not restart).
    const third = await submitMyRequest({ TmdbId: 27205, MediaType: 'movie' });
    expect(third.status()).toBe(429);
    const retryAfter3 = Number(third.headers()['retry-after']);
    expect(retryAfter3, 'window did not restart on a rejected request').toBeLessThanOrEqual(retryAfter);

    // Only the single accepted request reached Seerr.
    const last = await lastRequestCount();
    expect(last.count, 'only the first (accepted) request reached the mock').toBe(1);
    await assertPluginActive(admin);
  });
});
