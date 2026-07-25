/**
 * Playwright global setup — runs once before any test, after scripts/run.sh has
 * brought the stack up and generated the fake media.
 *
 * Steps (all via Jellyfin's HTTP API, verified against the 12.0-rc source):
 *   1. Complete the first-run startup wizard (Configuration → User → RemoteAccess → Complete).
 *   2. Authenticate as the new admin → capture AccessToken + userId.
 *   3. Create Movies + Shows libraries pointing at the mounted fake media.
 *   4. Trigger a library scan (RefreshLibrary task) and wait for it to finish.
 *   5. Seed the mock-seerr user with the real Jellyfin admin GUID (so Discovery links up).
 *   6. Persist { baseUrl, token, userId, userName } to setup/auth.json for the tests.
 *
 * Idempotent-ish: if the wizard was already completed (re-run with --keep), the
 * startup POSTs will 4xx harmlessly and we fall through to authentication.
 */
import { request as pwRequest, type FullConfig } from '@playwright/test';
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { authHeader } from './api-client.ts';

const __dirname = dirname(fileURLToPath(import.meta.url));

const BASE_URL = process.env.JELLYFIN_URL ?? 'http://localhost:8096';
const ADMIN_USER = process.env.JELLYFIN_ADMIN_USER ?? 'e2eadmin';
const ADMIN_PASS = process.env.JELLYFIN_ADMIN_PASS ?? 'E2ePassw0rd!';
const MOCK_SEERR_URL = process.env.MOCK_SEERR_URL ?? 'http://mock-seerr:5055';

async function globalSetup(_config: FullConfig): Promise<void> {
  const ctx = await pwRequest.newContext({ baseURL: BASE_URL });

  // --- 1. startup wizard ---------------------------------------------------
  // These are best-effort: on a re-used server they 4xx (setup already done).
  await ctx.post('/Startup/Configuration', {
    headers: { 'Content-Type': 'application/json' },
    data: {
      UICulture: 'en-US',
      MetadataCountryCode: 'US',
      PreferredMetadataLanguage: 'en',
      ServerName: 'jfh-e2e',
    },
  }).catch(() => undefined);

  await ctx.post('/Startup/User', {
    headers: { 'Content-Type': 'application/json' },
    data: { Name: ADMIN_USER, Password: ADMIN_PASS },
  }).catch(() => undefined);

  // 12.0: EnableAutomaticPortMapping was removed — send only EnableRemoteAccess.
  await ctx.post('/Startup/RemoteAccess', {
    headers: { 'Content-Type': 'application/json' },
    data: { EnableRemoteAccess: true },
  }).catch(() => undefined);

  await ctx.post('/Startup/Complete').catch(() => undefined);

  // --- 2. authenticate -----------------------------------------------------
  const authRes = await ctx.post('/Users/AuthenticateByName', {
    headers: { 'Content-Type': 'application/json', Authorization: authHeader() },
    data: { Username: ADMIN_USER, Pw: ADMIN_PASS },
  });
  if (!authRes.ok()) {
    throw new Error(`AuthenticateByName failed: ${authRes.status()} ${await authRes.text()}`);
  }
  const auth = (await authRes.json()) as { AccessToken: string; User: { Id: string; Name: string } };
  const token = auth.AccessToken;
  const userId = auth.User.Id;

  // Authenticated context for the remaining setup calls.
  const admin = await pwRequest.newContext({
    baseURL: BASE_URL,
    extraHTTPHeaders: { Authorization: authHeader(token), Accept: 'application/json' },
  });

  // --- 3. create libraries -------------------------------------------------
  await ensureLibrary(admin, 'Movies', 'movies', '/media/Movies');
  await ensureLibrary(admin, 'Shows', 'tvshows', '/media/Shows');

  // --- 4. scan and wait ----------------------------------------------------
  await runLibraryScan(admin);

  // --- 5. link the mock Seerr user to the real Jellyfin GUID ---------------
  // Uses the mock's test hook so Discovery user-matching resolves.
  await pwRequest
    .newContext()
    .then((c) =>
      c.post(`${publicSeerrUrl()}/seed-user`, {
        headers: { 'Content-Type': 'application/json' },
        data: { jellyfinUserId: userId },
      }).catch(() => undefined),
    )
    .catch(() => undefined);

  // --- 6. persist for the tests -------------------------------------------
  const out = { baseUrl: BASE_URL, token, userId, userName: ADMIN_USER };
  writeFileSync(join(__dirname, 'auth.json'), JSON.stringify(out, null, 2));
  process.env.JELLYFIN_TOKEN = token;

  // eslint-disable-next-line no-console
  console.log(`[global-setup] ready: admin=${ADMIN_USER} userId=${userId}`);
  await ctx.dispose();
  await admin.dispose();
}

/** From the host, the mock is reachable on localhost; inside compose it's mock-seerr. */
function publicSeerrUrl(): string {
  return process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';
}

async function ensureLibrary(
  admin: Awaited<ReturnType<typeof pwRequest.newContext>>,
  name: string,
  collectionType: string,
  path: string,
): Promise<void> {
  const existing = await admin.get('/Library/VirtualFolders');
  if (existing.ok()) {
    const folders = (await existing.json()) as Array<{ Name: string }>;
    if (folders.some((f) => f.Name === name)) return; // already created (re-run)
  }
  const qs = new URLSearchParams({ name, collectionType, refreshLibrary: 'false' });
  qs.append('paths', path);
  const res = await admin.post(`/Library/VirtualFolders?${qs.toString()}`, {
    headers: { 'Content-Type': 'application/json' },
    data: { LibraryOptions: { PathInfos: [{ Path: path }] } },
  });
  if (!res.ok() && res.status() !== 204) {
    throw new Error(`Create library "${name}" failed: ${res.status()} ${await res.text()}`);
  }
}

async function runLibraryScan(
  admin: Awaited<ReturnType<typeof pwRequest.newContext>>,
): Promise<void> {
  const list = await admin.get('/ScheduledTasks');
  const tasks = (await list.json()) as Array<{ Id: string; Key: string }>;
  const scan = tasks.find((t) => t.Key === 'RefreshLibrary');
  if (!scan) {
    // Fall back to the blanket refresh endpoint.
    await admin.post('/Library/Refresh').catch(() => undefined);
  } else {
    await admin.post(`/ScheduledTasks/Running/${scan.Id}`).catch(() => undefined);
    await pollTaskIdle(admin, scan.Id, 120_000);
  }
  // Give Jellyfin a beat to persist the scanned items before tests query stats.
  await new Promise((r) => setTimeout(r, 3000));
}

async function pollTaskIdle(
  admin: Awaited<ReturnType<typeof pwRequest.newContext>>,
  taskId: string,
  timeoutMs: number,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  await new Promise((r) => setTimeout(r, 500));
  while (Date.now() < deadline) {
    const res = await admin.get(`/ScheduledTasks/${taskId}`);
    if (res.ok()) {
      const task = (await res.json()) as { State: string };
      if (task.State === 'Idle') return;
    }
    await new Promise((r) => setTimeout(r, 1500));
  }
  throw new Error(`Library scan task did not finish within ${timeoutMs}ms`);
}

export default globalSetup;
