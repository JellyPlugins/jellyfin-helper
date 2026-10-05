/**
 * User-facing Trakt discovery (Discovery/My/Trakt*) end to end against the mock-trakt server.
 * Exercises the device-link state machine (start -> pending poll -> arm -> linked), the personal and
 * trending surfaces, and the TraktEnabled gate. Requires an authenticated non-admin user and TraktEnabled.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, normalUserContext, loadAuth, p } from '../setup/api-client.ts';

// The mock is published to the host on loopback; the plugin container reaches it as mock-trakt.
const MOCK_TRAKT_PUBLIC = process.env.MOCK_TRAKT_PUBLIC_URL ?? 'http://localhost:9100';

const auth = loadAuth();
let admin: APIRequestContext;
let user: APIRequestContext | null;

interface ConfigSnapshot {
  TraktEnabled?: boolean;
  TraktClientId?: string;
}
let snapshot: ConfigSnapshot = {};

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
    snapshot = { TraktEnabled: c.TraktEnabled, TraktClientId: c.TraktClientId };
  }

  // Ensure Trakt is enabled and pointed (via env on the container) at the mock.
  const seed = await admin.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      DiscoveryUserAccessEnabled: true,
      TraktEnabled: true,
      TraktClientId: 'mock-trakt-client-id',
      TraktClientSecret: 'mock-trakt-client-secret',
    },
  });
  expect(seed.ok(), `enable Trakt failed: ${seed.status()}`).toBeTruthy();
});

test.afterAll(async () => {
  if (snapshot.TraktEnabled !== undefined) {
    await admin.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { TraktEnabled: snapshot.TraktEnabled, TraktClientId: snapshot.TraktClientId ?? '' },
    });
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
  expect(user, 'normal user required').toBeTruthy();
  const res = await user!.post(p('Discovery/My/Trakt/Device/Disconnect'), { headers: { 'Content-Type': 'application/json' }, data: {} });
  expect(res.ok()).toBeTruthy();
  expect((await res.json()).Success).toBe(true);
});
