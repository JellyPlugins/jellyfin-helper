/**
 * E2E: sourcing Trakt personal recommendations through the OFFICIAL Trakt plugin.
 *
 * When the official Jellyfin Trakt plugin is installed and holds a token for a user, the Helper reads that token
 * (read-only, from the plugin's Trakt.xml) and fetches /recommendations/* itself, so a free-account user does not
 * need a second colliding Trakt app. global-setup stages the official plugin and seeds its config with a known
 * token for the normal user; mock-trakt requires that exact Bearer on /recommendations/*, so a populated result
 * here proves the seeded official token actually flowed through.
 *
 * Only runs when the external plugins (which include the official Trakt plugin) are staged.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { apiContext, authHeader, normalUserContext, requireNormalUser, loadAuth, p } from '../setup/api-client.ts';
import { OFFICIAL_TRAKT_GUID, SEEDED_OFFICIAL_TRAKT_TOKEN, ensureDiscoveryConfigured } from '../setup/discovery-config.ts';
import {
  seedNormalUserWatchProfile,
  clearNormalUserWatchProfile,
  type WatchProfileSeed,
} from '../setup/watch-profile.ts';

const MOCK_TRAKT_PUBLIC = process.env.MOCK_TRAKT_PUBLIC_URL ?? 'http://localhost:9100';

const auth = loadAuth();
let admin: APIRequestContext;
let user: APIRequestContext | null;
let profileSeed: WatchProfileSeed | null = null;

test.describe.serial('Trakt via official plugin', () => {
  test.skip(process.env.JFH_E2E_EXTERNAL_PLUGINS !== '1', 'external plugins (incl. official Trakt) not staged');

  test.beforeAll(async () => {
    admin = await apiContext(auth);
    user = await normalUserContext(auth);

    // Earlier api specs PUT /Configuration and can leave DiscoveryUserAccessEnabled=false by the time this spec
    // runs (shared backend, workers:1). Re-assert discovery config so the user-facing Trakt endpoints are not
    // 403'd by the access gate, then poll until the probing user actually sees non-403 before any test body.
    await ensureDiscoveryConfigured(admin, (m) => console.log(`[trakt-official] ${m}`));
    await waitForTraktAccessible();

    // Keep a real watch profile for the normal user's trending checks. The token-provenance test below
    // uses its own fresh linked account, so an earlier scheduled task or readiness GET cannot warm its cache.
    requireNormalUser(user);
    profileSeed = await seedNormalUserWatchProfile(admin, auth.userId, auth.normalUser!.userId);
  });

  test.afterAll(async () => {
    if (profileSeed) {
      await clearNormalUserWatchProfile(admin, auth.userId, auth.normalUser!.userId, profileSeed);
    }

    await admin?.dispose();
    await user?.dispose();
  });

  /** Poll Discovery/My/Trakt as the normal user until it stops returning 403 (access-gate trample settled). */
  async function waitForTraktAccessible(): Promise<void> {
    requireNormalUser(user);
    for (let attempt = 1; attempt <= 10; attempt++) {
      const res = await user!.get(p('Discovery/My/Trakt'));
      if (res.status() !== 403) {
        return;
      }
      await new Promise((r) => setTimeout(r, 1000));
    }
    throw new Error('Discovery/My/Trakt stayed 403 after re-asserting discovery config (access-gate trample did not settle)');
  }

  /** Read a mock-trakt test hook from the host. */
  async function traktHookJson<T>(path: string): Promise<T> {
    const ctx = await pwRequest.newContext();
    try {
      const res = await ctx.get(`${MOCK_TRAKT_PUBLIC}${path}`);
      expect(res.ok(), `mock hook ${path}: ${res.status()}`).toBeTruthy();
      return (await res.json()) as T;
    } finally {
      await ctx.dispose();
    }
  }

  async function resetTraktMock(): Promise<void> {
    const ctx = await pwRequest.newContext();
    try {
      const reset = await ctx.post(`${MOCK_TRAKT_PUBLIC}/reset`);
      expect(reset.ok(), `reset mock Trakt: ${reset.status()}`).toBeTruthy();
    } finally {
      await ctx.dispose();
    }
  }

  /** A unique linked account guarantees a cache miss without weakening the production cache or the Bearer assertion. */
  async function withFreshOfficialTraktUser(run: (fresh: APIRequestContext, userId: string) => Promise<void>): Promise<void> {
    const configUrl = `/Plugins/${OFFICIAL_TRAKT_GUID}/Configuration`;
    const current = await admin.get(configUrl);
    expect(current.ok(), `read official Trakt configuration: ${current.status()}`).toBeTruthy();
    const original = await current.json() as { TraktUsers?: unknown[] };
    const name = `jfh-trakt-${randomUUID().slice(0, 8)}`;
    const password = `Jfh-${randomUUID()}`;
    const created = await admin.post('/Users/New', { data: { Name: name, Password: password } });
    expect(created.ok(), `new token-provenance account: ${created.status()}`).toBeTruthy();
    const account = await created.json() as { Id: string };
    let anonymous: APIRequestContext | undefined;
    let fresh: APIRequestContext | undefined;
    try {
      anonymous = await pwRequest.newContext({ baseURL: auth.baseUrl });
      const login = await anonymous.post('/Users/AuthenticateByName', {
        headers: { Authorization: authHeader() }, data: { Username: name, Pw: password },
      });
      expect(login.ok(), `token-provenance login: ${login.status()}`).toBeTruthy();
      const session = await login.json() as { AccessToken: string; User: { Id: string } };
      expect(session.User.Id).toBe(account.Id);
      fresh = await pwRequest.newContext({ baseURL: auth.baseUrl, extraHTTPHeaders: { Authorization: authHeader(session.AccessToken), Accept: 'application/json' } });
      const linked = await admin.post(configUrl, {
        data: { ...original, TraktUsers: [...(original.TraktUsers ?? []), {
          LinkedMbUserId: account.Id,
          AccessToken: SEEDED_OFFICIAL_TRAKT_TOKEN,
          RefreshToken: 'seeded-official-refresh',
          AccessTokenExpiration: '2099-01-01T00:00:00',
        }] },
      });
      expect(linked.ok(), `link isolated official Trakt account: ${linked.status()}`).toBeTruthy();
      await run(fresh, account.Id);
    } finally {
      try {
        const restored = await admin.post(configUrl, { data: original });
        expect(restored.ok(), `restore official Trakt configuration: ${restored.status()}`).toBeTruthy();
      } finally {
        try {
          await fresh?.dispose();
          await anonymous?.dispose();
        } finally {
          expect((await admin.delete(`/Users/${account.Id}`)).ok(), 'token-provenance account cleanup failed').toBeTruthy();
        }
      }
    }
  }

  test('personal recommendations are sourced via the official plugin token', async () => {
    await withFreshOfficialTraktUser(async (fresh, userId) => {
      // No personal request or task has ever warmed this unique account. Clearing the mock therefore
      // cannot erase the only evidence while leaving a pre-existing Helper cache entry behind.
      await resetTraktMock();
      expect((await traktHookJson<{ bearer: string | null }>('/last-recommendation-bearer')).bearer).toBeNull();

      const res = await fresh.get(p('Discovery/My/Trakt'));
      expect(res.ok(), `GET Discovery/My/Trakt: ${res.status()}`).toBeTruthy();
      const body = (await res.json()) as { Linked?: boolean; Result?: { UserId?: string; Recommendations?: unknown[] } | null };

      expect(body.Linked).toBe(true);
      expect(body.Result?.UserId).toBe(userId);
      expect(body.Result?.Recommendations?.length ?? 0).toBeGreaterThan(0);

      const seen = await traktHookJson<{ bearer: string | null }>('/last-recommendation-bearer');
      expect(seen.bearer).toBe(SEEDED_OFFICIAL_TRAKT_TOKEN);
    });
  });

  test('warm personal recommendations keep their user ownership without another upstream Bearer request', async () => {
    await withFreshOfficialTraktUser(async (fresh, userId) => {
      await resetTraktMock();
      const first = await fresh.get(p('Discovery/My/Trakt'));
      expect(first.ok()).toBeTruthy();
      const original = await first.json() as { Linked?: boolean; Result?: { UserId?: string; GeneratedAt?: string; Recommendations?: Array<{ TmdbId: number; MediaType: string }> } | null };
      expect(original.Linked).toBe(true);
      expect(original.Result?.UserId).toBe(userId);
      expect(original.Result?.GeneratedAt).toBeTruthy();
      expect(original.Result?.Recommendations?.length ?? 0).toBeGreaterThan(0);
      expect((await traktHookJson<{ bearer: string | null }>('/last-recommendation-bearer')).bearer).toBe(SEEDED_OFFICIAL_TRAKT_TOKEN);

      // Reproduce the old test's reset-with-a-warm-cache sequence. A cache hit is valid, and must
      // neither move results to another user nor pretend it made a new authenticated Trakt request.
      await resetTraktMock();
      const cached = await fresh.get(p('Discovery/My/Trakt'));
      expect(cached.ok()).toBeTruthy();
      const repeated = await cached.json() as typeof original;
      expect(repeated.Linked).toBe(true);
      expect(repeated.Result?.UserId).toBe(userId);
      expect(repeated.Result?.GeneratedAt).toBe(original.Result?.GeneratedAt);
      expect(repeated.Result?.Recommendations?.map(item => [item.MediaType, item.TmdbId])).toEqual(original.Result?.Recommendations?.map(item => [item.MediaType, item.TmdbId]));
      expect((await traktHookJson<{ bearer: string | null }>('/last-recommendation-bearer')).bearer).toBeNull();
    });
  });

  test('trending is available (client-id only, no linking)', async () => {
    requireNormalUser(user);
    const trending = await user!.get(p('Discovery/My/Trakt/Trending'));
    expect(trending.ok(), `trending: ${trending.status()}`).toBeTruthy();
  });

  test('admin status endpoint reports the official plugin present', async () => {
    const res = await admin.get(p('Trakt/OfficialPluginStatus'));
    expect(res.ok(), `GET Trakt/OfficialPluginStatus: ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as { Present?: boolean };
    expect(body.Present).toBe(true);
  });
});
