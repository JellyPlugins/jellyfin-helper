/**
 * User-facing Trakt discovery (Discovery/My/Trakt*) end to end against the mock-trakt server.
 * Exercises the device-link state machine (start -> pending poll -> arm -> linked), the personal and
 * trending surfaces, and the TraktEnabled gate. Requires an authenticated non-admin user and TraktEnabled.
 */
import { test, expect, request as pwRequest, type APIRequestContext, type APIResponse } from '@playwright/test';
import { apiContext, normalUserContext, loadAuth, p, API_KEY_MASK } from '../setup/api-client.ts';
import {
  seedNormalUserWatchProfile,
  clearNormalUserWatchProfile,
  type WatchProfileSeed,
} from '../setup/watch-profile.ts';

// The mock is published to the host on loopback; the plugin container reaches it as mock-trakt.
const MOCK_TRAKT_PUBLIC = process.env.MOCK_TRAKT_PUBLIC_URL ?? 'http://localhost:9100';

// When the external plugins are staged, global-setup seeds the OFFICIAL Trakt plugin with a token for THIS same
// normal user. The Helper then legitimately reports that user as linked (sourced through the official plugin),
// so the two tests below that assert an UNLINKED starting state no longer hold for this user. They still run in
// the common no-external-plugins CI leg (own device flow only); the official-plugin path is covered end to end
// by trakt-official-plugin.api.spec.ts.
const OFFICIAL_PLUGIN_STAGED = process.env.JFH_E2E_EXTERNAL_PLUGINS === '1';

const auth = loadAuth();
let admin: APIRequestContext;
let user: APIRequestContext | null;

// Snapshot of the shared-backend Configuration fields this spec mutates, so afterAll can put them
// back verbatim and not leak Trakt/Seerr state into later specs that assume a pristine config.
interface ConfigSnapshot {
  TraktEnabled?: boolean;
  TraktClientId?: string;
  SeerrUrl?: string;
  DiscoveryUserAccessEnabled?: boolean;
  RecommendationsTaskMode?: string;
}
let snapshot: ConfigSnapshot = {};

// Non-admin watch-profile seed handle, unwound in afterAll so later specs (workers: 1 shares one backend)
// do not inherit a profile this spec created on the normal user.
let profileSeed: WatchProfileSeed | null = null;

/** Drive a mock-trakt test hook from the host. */
async function traktHook(path: string): Promise<void> {
  const ctx = await pwRequest.newContext();
  try {
    await ctx.post(`${MOCK_TRAKT_PUBLIC}${path}`);
  } finally {
    await ctx.dispose();
  }
}

test.beforeAll(async () => {
  admin = await apiContext(auth);
  user = await normalUserContext(auth);

  const current = await admin.get(p('Configuration'));
  if (current.ok()) {
    const c = (await current.json()) as ConfigSnapshot;
    snapshot = {
      TraktEnabled: c.TraktEnabled,
      TraktClientId: c.TraktClientId,
      SeerrUrl: c.SeerrUrl,
      DiscoveryUserAccessEnabled: c.DiscoveryUserAccessEnabled,
      RecommendationsTaskMode: c.RecommendationsTaskMode,
    };
  }

  // Enable Trakt (pointed at the mock via container env) AND configure Seerr at the mock with
  // Recommendations active: the Trakt personal surface reuses the Seerr-backed external scorer, which
  // returns null (-> Linked=false) unless Seerr is configured. Mirrors discovery-my.api.spec.ts.
  const seed = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      DiscoveryUserAccessEnabled: true,
      RecommendationsTaskMode: 'Activate',
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: 'seerr-key',
      TraktClientId: 'mock-trakt-client-id',
      TraktClientSecret: 'mock-trakt-client-secret',
    },
  });
  expect(seed.ok(), `enable Trakt failed: ${seed.status()}`).toBeTruthy();

  // The device-flow spec links as the non-admin user; its personal recommendations only score (and
  // thus report Linked=true) once that user has a genre watch profile.
  if (user) {
    profileSeed = await seedNormalUserWatchProfile(admin, auth.userId, auth.normalUser!.userId);
  }
});

test.afterAll(async () => {
  // Unwind the non-admin watch profile this spec created.
  if (profileSeed) {
    await clearNormalUserWatchProfile(admin, auth.userId, auth.normalUser!.userId, profileSeed);
  }

  // Restore the Configuration fields this spec mutated. GET masked the Seerr key, so re-send the mask
  // sentinel to preserve whatever key was stored before this spec ran. TraktEnabled is derived server-side
  // from the credentials, so restoring the client id (+ clearing the secret this spec set) is what resets it.
  if (Object.keys(snapshot).length > 0) {
    const restoredTraktId = snapshot.TraktClientId ?? '';
    await admin
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: {
          TraktClientId: restoredTraktId,
          // Only the mock secret existed before restore; GET never returns it. Clear it when the original
          // had no client id so the derived enabled flag matches the pre-spec state.
          TraktClientSecret: restoredTraktId === '' ? '' : API_KEY_MASK,
          SeerrUrl: snapshot.SeerrUrl ?? '',
          SeerrApiKey: API_KEY_MASK,
          DiscoveryUserAccessEnabled: snapshot.DiscoveryUserAccessEnabled ?? false,
          RecommendationsTaskMode: snapshot.RecommendationsTaskMode ?? 'Deactivate',
        },
      })
      .catch(() => undefined);
  }
  await admin.dispose();
  if (user) {
    await user.dispose();
  }
});

test.beforeEach(async () => {
  await traktHook('/reset');
});

test('personal Trakt starts unlinked, then links through the device flow', async () => {
  test.skip(OFFICIAL_PLUGIN_STAGED, 'official plugin seeds this user a token -> not unlinked; covered by trakt-official-plugin.api.spec.ts');
  expect(user, 'normal user required').toBeTruthy();

  // Before linking the personal endpoint reports Linked=false (the UI shows the connect panel).
  const before = await user!.get(p('Discovery/My/Trakt'));
  expect(before.ok()).toBeTruthy();
  expect((await before.json()).Linked).toBe(false);

  // Start the device flow.
  const start = await user!.post(p('Discovery/My/Trakt/Device/Start'), { headers: { 'Content-Type': 'application/json' }, data: {} });
  expect(start.ok(), `device start: ${start.status()}`).toBeTruthy();
  const device = await start.json();
  expect(device.user_code).toBeTruthy();

  // Poll once while the mock is still pending.
  const pending = await user!.post(p('Discovery/My/Trakt/Device/Poll'), {
    headers: { 'Content-Type': 'application/json' },
    data: { DeviceCode: device.device_code },
  });
  expect(pending.ok()).toBeTruthy();
  expect((await pending.json()).Status).toBe('Pending');

  // Arm the mock to approve, poll again (respecting the per-user 5s poll throttle).
  await traktHook('/arm-linked');
  await new Promise((r) => setTimeout(r, 6000));
  const linked = await user!.post(p('Discovery/My/Trakt/Device/Poll'), {
    headers: { 'Content-Type': 'application/json' },
    data: { DeviceCode: device.device_code },
  });
  expect(linked.ok()).toBeTruthy();
  expect((await linked.json()).Status).toBe('Linked');

  // After linking the personal endpoint returns a scored result envelope.
  const after = await user!.get(p('Discovery/My/Trakt'));
  expect(after.ok()).toBeTruthy();
  expect((await after.json()).Linked).toBe(true);
});

test('trending is available without linking', async () => {
  expect(user, 'normal user required').toBeTruthy();
  const trending = await user!.get(p('Discovery/My/Trakt/Trending'));
  expect(trending.ok(), `trending: ${trending.status()}`).toBeTruthy();
});

test('disconnect clears the link', async () => {
  test.skip(OFFICIAL_PLUGIN_STAGED, 'official plugin keeps this user linked after an own-flow disconnect; covered by trakt-official-plugin.api.spec.ts');
  expect(user, 'normal user required').toBeTruthy();
  await ensureLinkedForDisconnect();
  const res = await user!.post(p('Discovery/My/Trakt/Device/Disconnect'), { headers: { 'Content-Type': 'application/json' }, data: {} });
  expect(res.ok()).toBeTruthy();
  expect((await res.json()).Success).toBe(true);

  // The link is actually gone: the personal endpoint reports Linked:false again.
  const after = await user!.get(p('Discovery/My/Trakt'));
  expect(after.ok()).toBeTruthy();
  expect((await after.json()).Linked).toBe(false);
});

/**
 * Links the normal user unless already linked. The fallback path runs only when an earlier
 * test already spent the per-minute start / 5s poll windows, so both calls honor 429s via
 * the server's Retry-After instead of failing on the first throttled attempt.
 */
async function ensureLinkedForDisconnect(): Promise<void> {
  const cur = await user!.get(p('Discovery/My/Trakt'));
  expect(cur.ok()).toBeTruthy();
  if ((await cur.json()).Linked === true) {
    return;
  }
  const start = await postThrottled('Discovery/My/Trakt/Device/Start', {}, 'device start');
  expect(start.ok(), `device start: ${start.status()}`).toBeTruthy();
  const device = await start.json();
  await traktHook('/arm-linked');
  const linked = await postThrottled('Discovery/My/Trakt/Device/Poll', { DeviceCode: device.device_code }, 'device poll');
  expect(linked.ok()).toBeTruthy();
  expect((await linked.json()).Status).toBe('Linked');
}

/**
 * POST with 429 retries honoring the server's Retry-After header (seconds, plus a small
 * buffer). Bounded: each throttled attempt reports exactly how long to wait, so three
 * attempts cover even a freshly-spent per-minute start window.
 */
async function postThrottled(ep: string, data: Record<string, unknown>, label: string): Promise<APIResponse> {
  let last: APIResponse | null = null;
  for (let attempt = 1; attempt <= 3; attempt++) {
    last = await user!.post(p(ep), { headers: { 'Content-Type': 'application/json' }, data });
    if (last.status() !== 429) {
      return last;
    }
    const retryAfter = Number.parseInt(last.headers()['retry-after'] ?? '', 10);
    await new Promise((r) => setTimeout(r, (Number.isFinite(retryAfter) ? retryAfter : 5) * 1000 + 500));
  }
  expect(last!.status(), `${label}: still throttled after 3 attempts`).not.toBe(429);
  return last!;
}

test('Trakt endpoints are gated by the discovery-access toggle', async () => {
  expect(user, 'normal user required').toBeTruthy();

  // Turn user-level discovery access OFF. Trakt tabs are a feature of the Discovery sidebar, so every
  // /Discovery/My/Trakt* endpoint must now 403 even though Trakt itself stays configured (secret kept via
  // the mask sentinel). This is the contract the sidebar probes to hide the Trakt tabs.
  const off = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      DiscoveryUserAccessEnabled: false,
      SeerrUrl: 'http://mock-seerr:5055',
      SeerrApiKey: API_KEY_MASK,
      TraktClientId: 'mock-trakt-client-id',
      TraktClientSecret: API_KEY_MASK,
    },
  });
  expect(off.ok(), `disable discovery access failed: ${off.status()}`).toBeTruthy();

  try {
    const endpoints: Array<[string, 'get' | 'post']> = [
      ['Discovery/My/Trakt', 'get'],
      ['Discovery/My/Trakt/Trending', 'get'],
      ['Discovery/My/Trakt/Device/Start', 'post'],
      ['Discovery/My/Trakt/Device/Poll', 'post'],
      ['Discovery/My/Trakt/Device/Disconnect', 'post'],
    ];
    for (const [ep, method] of endpoints) {
      const res = method === 'get'
        ? await user!.get(p(ep))
        : await user!.post(p(ep), { headers: { 'Content-Type': 'application/json' }, data: {} });
      expect(res.status(), `${ep} should be 403 when discovery access is off`).toBe(403);
    }
  } finally {
    // Restore access so later tests (and other specs on the shared backend) see the enabled state.
    await admin
      .put(p('Configuration'), {
        headers: { 'Content-Type': 'application/json' },
        data: {
          DiscoveryUserAccessEnabled: true,
          RecommendationsTaskMode: 'Activate',
          SeerrUrl: 'http://mock-seerr:5055',
          SeerrApiKey: API_KEY_MASK,
          TraktClientId: 'mock-trakt-client-id',
          TraktClientSecret: API_KEY_MASK,
        },
      })
      .catch(() => undefined);
  }
});
