/** * Smoke + endpoint coverage: the plugin loads as Active, and every controller * under JellyfinHelper/ responds without 404/500. */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, PLUGIN_GUID } from '../setup/api-client.ts';

let ctx: APIRequestContext;
let userId: string;

test.beforeAll(async () => {
  const auth = loadAuth();
  userId = auth.userId;
  ctx = await apiContext(auth);
});

test.afterAll(async () => {
  await ctx.dispose();
});

test('plugin is installed and Active (not Malfunctioned)', async () => {
  const res = await ctx.get('/Plugins');
  expect(res.ok()).toBeTruthy();
  const plugins = (await res.json()) as Array<{ Id: string; Name: string; Status: string; Version: string }>;
  const plugin = plugins.find((pl) => pl.Id.replace(/-/g, '') === PLUGIN_GUID.replace(/-/g, ''));
  expect(plugin, 'Jellyfin Helper plugin present in /Plugins').toBeTruthy();
  expect(plugin!.Status).toBe('Active');
  // Version is not hardcoded: run.sh exports PLUGIN_VERSION from
  // Directory.Build.props. If set, assert an exact match; otherwise just
  // require a non-empty version string.
  const expectedVersion = process.env.PLUGIN_VERSION;
  if (expectedVersion) {
    expect(plugin!.Version).toBe(expectedVersion);
  } else {
    expect(plugin!.Version).toMatch(/^\d+\.\d+/);
  }
});

test('plugin configuration page is registered', async () => {
  // The page is registered under the plugin's Name ("Jellyfin Helper", with a
  // space) via GetPages(); the ConfigurationPage route matches on that exact name.
  const res = await ctx.get(`/web/ConfigurationPage?name=${encodeURIComponent('Jellyfin Helper')}`);
  // Accept 200 or a redirect, but never 404/500.
  expect([200, 301, 302]).toContain(res.status());
});

// GET endpoints that must respond for an admin without server errors.
// (Behaviour is asserted elsewhere; here we guard routing + no-throw.)
const getEndpoints: Array<{ path: string; okStatuses?: number[] }> = [
  { path: p('Ping') },
  { path: p('Configuration') },
  { path: p('Configuration/Libraries') },
  { path: p('Configuration/LibraryPaths') },
  { path: p('Configuration/BrowseFolders') },
  { path: p('Backup/Export') },
  { path: p('Trash/Summary') },
  { path: p('Trash/Folders') },
  { path: p('Trash/Contents') },
  { path: p('Logs') },
  { path: p('Logs/Download') },
  { path: p('CleanupStatistics') },
  { path: p('LibraryInsights') },
  { path: p('Translations') },
  { path: p('Discovery') },
  // Cache-backed endpoints may legitimately answer 503 before their task runs,
  // or 204/200 depending on state - all are "not a routing/500 failure".
  { path: p('MediaStatistics/Latest'), okStatuses: [200, 204] },
  { path: p('GrowthTimeline') },
  { path: p('Recommendations'), okStatuses: [200, 503] },
  { path: p('Recommendations/WatchProfiles'), okStatuses: [200, 503] },
  { path: p('UserActivity/Latest'), okStatuses: [200, 503] },
];

for (const ep of getEndpoints) {
  test(`GET ${ep.path} responds without server error`, async () => {
    const res = await ctx.get(ep.path);
    const allowed = ep.okStatuses ?? [200];
    // Never 404 (route regression). Every endpoint must answer with a status in its declared allow-list, plus 429 (rate-limited scans) tolerated as non-fatal.
    expect(res.status(), `unexpected status for ${ep.path}`).not.toBe(404);
    expect([...allowed, 429], `unexpected status for ${ep.path}`).toContain(res.status());
  });
}

test('per-user recommendation + activity endpoints route correctly', async () => {
  for (const path of [
    p(`Recommendations/${userId}`),
    p(`Recommendations/WatchProfile/${userId}`),
    p(`UserActivity/User/${userId}`),
  ]) {
    const res = await ctx.get(path);
    expect(res.status(), path).not.toBe(404);
    // 503 = feature deactivated (a valid guard state), 429 = rate-limited, 400 =
    // validation - none are server errors. Only a real 5xx crash fails here.
    expect([200, 400, 429, 503], path).toContain(res.status());
  }
});
