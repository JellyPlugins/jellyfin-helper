/**
 * Authorization gating — every admin controller carries
 * [Authorize(Policy = "RequiresElevation")], but no other spec ever calls them
 * as a non-admin. A regression that relaxes a policy would pass silently. This
 * asserts the whole matrix denies a normal (non-elevated) user, with an admin
 * positive control so a blanket "everything 403s" (e.g. broken auth) also fails.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, normalUserContext, requireNormalUser, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

const auth = loadAuth();

let admin: APIRequestContext;
let user: APIRequestContext | null;

test.beforeAll(async () => {
  admin = await apiContext(auth);
  user = await normalUserContext(auth);
});

test.afterAll(async () => {
  await admin.dispose();
  await user?.dispose();
});

const userId = auth.userId;

const getGated = [
  p('Configuration'),
  p('Configuration/Libraries'),
  p('Configuration/LibraryPaths'),
  p('Configuration/BrowseFolders'),
  p('Backup/Export?includeSecrets=true'),
  p('Trash/Summary'),
  p('Trash/Folders'),
  p('Trash/Contents'),
  p('ArrIntegration/Compare/Radarr'),
  p('Discovery'),
  p('Discovery/Users'),
  p('Discovery/Services/radarr'),
  p('Recommendations'),
  p(`Recommendations/${userId}`),
  p('Recommendations/WatchProfiles'),
  p('UserActivity/Latest'),
  p(`UserActivity/User/${userId}`),
  p('MediaStatistics/Latest'),
  p('GrowthTimeline'),
  p('LibraryInsights'),
  p('CleanupStatistics'),
  p('Logs'),
];

const postGated: Array<{ path: string; body: unknown }> = [
  { path: p('Backup/Import'), body: { backupVersion: 1 } },
  { path: p('Trash/CheckAccess'), body: { TrashFolderPath: '.jellyfin-trash' } },
  // The two most destructive gated mutations — a relaxed policy here would let a
  // non-admin move/scan trash folders. Previously absent from this sweep.
  { path: p('Trash/Relocate'), body: { OldTrashPath: '.jellyfin-trash', NewTrashPath: '.jellyfin-trash-2' } },
  { path: p('Trash/FoldersForPath'), body: { TrashFolderPath: '.jellyfin-trash' } },
  { path: p('ArrIntegration/TestConnection'), body: { Url: 'http://mock-arr:9000', ApiKey: 'k' } },
  { path: p('Seerr/Test'), body: { Url: 'http://mock-seerr:5055', ApiKey: 'k' } },
  { path: p('Discovery/Request'), body: { TmdbId: 27205, MediaType: 'movie' } },
];

test('non-admin GET is denied on every elevated endpoint', async () => {
  requireNormalUser(user);
  for (const path of getGated) {
    const res = await user!.get(path);
    expect([401, 403], `${path} must deny non-admin`).toContain(res.status());
  }
  await assertPluginActive(admin);
});

test('non-admin PUT/DELETE is denied on elevated Configuration + Logs', async () => {
  requireNormalUser(user);
  const put = await user!.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Language: 'de' },
  });
  expect([401, 403]).toContain(put.status());

  const putLevel = await user!.put(p('Configuration/LogLevel'), {
    headers: { 'Content-Type': 'application/json' },
    data: { PluginLogLevel: 'DEBUG' },
  });
  expect([401, 403]).toContain(putLevel.status());

  const del = await user!.delete(p('Logs'));
  expect([401, 403]).toContain(del.status());

  const delTrash = await user!.delete(p('Trash/Folders'));
  expect([401, 403]).toContain(delTrash.status());
  await assertPluginActive(admin);
});

test('non-admin POST is denied on every elevated mutation', async () => {
  requireNormalUser(user);
  for (const { path, body } of postGated) {
    const res = await user!.post(path, {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify(body),
    });
    expect([401, 403], `${path} must deny non-admin`).toContain(res.status());
  }
  await assertPluginActive(admin);
});

test('admin positive control: elevated GET is allowed (not a blanket 403)', async () => {
  const res = await admin.get(p('Configuration'));
  expect(res.status()).toBe(200);
  await assertPluginActive(admin);
});

test('Translations is AllowAnonymous: reachable with no auth header', async () => {
  const anon = await pwRequest.newContext({ baseURL: auth.baseUrl });
  try {
    const res = await anon.get(p('Translations?lang=en'));
    expect(res.status(), 'anonymous translations must not be 401/403').not.toBe(401);
    expect(res.status()).not.toBe(403);
    expect(res.status()).toBeLessThan(500);
  } finally {
    await anon.dispose();
  }
});

test('anonymous caller is denied on an elevated endpoint (401)', async () => {
  const anon = await pwRequest.newContext({ baseURL: auth.baseUrl });
  try {
    const res = await anon.get(p('Configuration'));
    expect(res.status()).toBe(401);
  } finally {
    await anon.dispose();
  }
});
