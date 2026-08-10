/**
 * Adversarial user-facing Discovery tests - access-control and write-side abuse.
 * The gate must never leak to Seerr when disabled/denied, must reject malformed
 * input with 400 (not 500), and must not let a user spoof another identity.
 *
 * Uses the mock's request-recording hook (/last-request) to prove what actually
 * reached Seerr. Non-admin-dependent cases skip cleanly if no normal user was
 * provisioned.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, normalUserContext, requireNormalUser, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

const MOCK = process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';
const auth = loadAuth();

let admin: APIRequestContext;
let user: APIRequestContext | null;

test.beforeAll(async () => {
  admin = await apiContext(auth);
  user = await normalUserContext(auth);
  const seed = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'seerr-key' },
  });
  expect(seed.ok(), `mock-Seerr seed failed: ${seed.status()}`).toBeTruthy();
});
test.afterAll(async () => {
  // The last test enables DiscoveryUserAccessEnabled; reset it so the gate doesn't
  // bleed into later specs that assume the default (disabled) state.
  await admin
    .put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { DiscoveryUserAccessEnabled: false },
    })
    .catch(() => undefined);
  await admin.dispose();
  await user?.dispose();
});

async function setAccess(enabled: boolean) {
  const res = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      RecommendationsTaskMode: 'Activate',
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: '***',
      DiscoveryUserAccessEnabled: enabled,
    },
  });
  expect(res.ok(), `toggle failed: ${res.status()}`).toBeTruthy();
}

async function mockReset() {
  const m = await pwRequest.newContext();
  await m.get(`${MOCK}/reset`).catch(() => undefined);
  await m.dispose();
}

async function lastRequests(): Promise<{ count: number; requests: any[] }> {
  const m = await pwRequest.newContext();
  const r = await m.get(`${MOCK}/last-request`);
  const body = r.ok() ? await r.json() : { count: 0, requests: [] };
  await m.dispose();
  return body;
}

test.describe.serial('Discovery/My write-side access control', () => {
  test('write endpoints 403 when access is DISABLED, and nothing reaches Seerr', async () => {
    requireNormalUser(user);
    await setAccess(false);
    await mockReset();

    const req = await user!.post(p('Discovery/My/Request'), {
      headers: { 'Content-Type': 'application/json' },
      data: { TmdbId: 27205, MediaType: 'movie' },
    });
    expect(req.status(), '/My/Request must be 403 when disabled').toBe(403);

    const dis = await user!.post(p('Discovery/My/Dismiss'), {
      headers: { 'Content-Type': 'application/json' },
      data: { TmdbId: 27205, MediaType: 'movie' },
    });
    expect(dis.status(), '/My/Dismiss must be 403 when disabled').toBe(403);

    // Crucially: a denied request must NOT have been forwarded to Seerr.
    expect((await lastRequests()).count, 'no submission should reach Seerr when denied').toBe(0);
    await assertPluginActive(admin);
  });

  test('adversarial /My/Dismiss inputs are 4xx, never 500', async () => {
    requireNormalUser(user);
    await setAccess(true);
    const bad: unknown[] = [
      { TmdbId: 0, MediaType: 'movie' },
      { TmdbId: -5, MediaType: 'movie' },
      { TmdbId: 2147483648, MediaType: 'movie' },
      { TmdbId: 27205, MediaType: 'BOGUS' },
      {},
    ];
    for (const data of bad) {
      const res = await user!.post(p('Discovery/My/Dismiss'), {
        headers: { 'Content-Type': 'application/json' },
        data: JSON.stringify(data),
      });
      expect(res.status(), `payload=${JSON.stringify(data)}`).toBeLessThan(500);
    }
    await assertPluginActive(admin);
  });

  test('a non-admin cannot spoof another identity on /My/Request', async () => {
    requireNormalUser(user);
    await setAccess(true);
    await mockReset();

    // Submit with a SeerrUserId that is NOT the caller's - the plugin must ignore
    // the body value and resolve the CALLER's own identity (or reject).
    const res = await user!.post(p('Discovery/My/Request'), {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify({ TmdbId: 27205, MediaType: 'movie', SeerrUserId: 99999 }),
    });
    expect(res.status(), 'must not 500').toBeLessThan(500);

    const submitted = await lastRequests();
    // If a request reached Seerr, it must NOT carry the spoofed 99999 identity.
    for (const r of submitted.requests) {
      expect(r.userId, 'spoofed SeerrUserId must not be forwarded').not.toBe(99999);
    }
    await assertPluginActive(admin);
  });
});
