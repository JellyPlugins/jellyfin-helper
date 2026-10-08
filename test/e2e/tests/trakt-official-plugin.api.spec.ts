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
import { apiContext, normalUserContext, requireNormalUser, loadAuth, p } from '../setup/api-client.ts';
import { SEEDED_OFFICIAL_TRAKT_TOKEN } from '../setup/discovery-config.ts';

const MOCK_TRAKT_PUBLIC = process.env.MOCK_TRAKT_PUBLIC_URL ?? 'http://localhost:9100';

const auth = loadAuth();
let admin: APIRequestContext;
let user: APIRequestContext | null;

test.describe('Trakt via official plugin', () => {
  test.skip(process.env.JFH_E2E_EXTERNAL_PLUGINS !== '1', 'external plugins (incl. official Trakt) not staged');

  test.beforeAll(async () => {
    admin = await apiContext(auth);
    user = await normalUserContext(auth);
  });

  test.afterAll(async () => {
    await admin.dispose();
    if (user) {
      await user.dispose();
    }
  });

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

  test('personal recommendations are sourced via the official plugin token', async () => {
    requireNormalUser(user);

    // Clear the mock's recorded bearer so the provenance assert can only pass on a fresh fetch made during
    // this test, never on residue from an earlier spec.
    const resetCtx = await pwRequest.newContext();
    try {
      await resetCtx.post(`${MOCK_TRAKT_PUBLIC}/reset`);
    } finally {
      await resetCtx.dispose();
    }

    // The normal user is NOT linked via our own device flow, so source resolution falls through to the official
    // plugin's seeded token. Disconnect clears any residual own-link AND drops the Helper's personal cache, so
    // the GET below cannot be served from a warm own-flow pool (which would falsify the bearer-provenance check).
    await user!.post(p('Discovery/My/Trakt/Device/Disconnect'), {
      headers: { 'Content-Type': 'application/json' },
      data: {},
    });

    const res = await user!.get(p('Discovery/My/Trakt'));
    expect(res.ok(), `GET Discovery/My/Trakt: ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as { Linked?: boolean; Result?: { Recommendations?: unknown[] } | null };

    // Linked is true because the official plugin supplies a usable token even though the user never ran our
    // own device flow.
    expect(body.Linked).toBe(true);
    expect(body.Result?.Recommendations?.length ?? 0).toBeGreaterThan(0);

    // Decisive proof: mock-trakt only answers /recommendations/* for a valid Bearer, and the last one it saw
    // must be the token we seeded into the official plugin's config - not our own-flow token.
    const seen = await traktHookJson<{ bearer: string | null }>('/last-recommendation-bearer');
    expect(seen.bearer).toBe(SEEDED_OFFICIAL_TRAKT_TOKEN);
  });

  test('admin status endpoint reports the official plugin present', async () => {
    const res = await admin.get(p('Trakt/OfficialPluginStatus'));
    expect(res.ok(), `GET Trakt/OfficialPluginStatus: ${res.status()}`).toBeTruthy();
    const body = (await res.json()) as { Present?: boolean };
    expect(body.Present).toBe(true);
  });
});
