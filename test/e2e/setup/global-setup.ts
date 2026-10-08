/** * Playwright global setup - runs once before any test, after scripts/run.sh has * brought the stack up and generated the fake media. */
import { request as pwRequest, type FullConfig } from '@playwright/test';
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { authHeader, runLibraryScan } from './api-client.ts';
import { ensureDiscoveryConfigured, seedOfficialTraktPlugin } from './discovery-config.ts';
import { hasDocker, plantCanaries, plantedCanaries } from './fs-assert.ts';
import { seedGrowthTimeline } from './seed-timeline.ts';

const __dirname = dirname(fileURLToPath(import.meta.url));

const BASE_URL = process.env.JELLYFIN_URL ?? 'http://localhost:8096';
const ADMIN_USER = process.env.JELLYFIN_ADMIN_USER ?? 'e2eadmin';
const ADMIN_PASS = process.env.JELLYFIN_ADMIN_PASS ?? 'E2ePassw0rd!';
const NORMAL_USER = process.env.JELLYFIN_USER ?? 'e2euser';
const NORMAL_PASS = process.env.JELLYFIN_USER_PASS ?? 'E2eUserPass1!';

async function globalSetup(_config: FullConfig): Promise<void> {
  const ctx = await pwRequest.newContext({ baseURL: BASE_URL });

  // Small helper: run a startup step, log its status, and don't hard-fail on a 4xx (a re-used server returns those) - but DO surface the status so a real wizard failure is visible in CI instead of silently swallowed.
  const step = async (label: string, fn: () => Promise<{ status: () => number; text: () => Promise<string> }>) => {
    try {
      const res = await fn();
      const s = res.status();
      // eslint-disable-next-line no-console
      console.log(`[global-setup] ${label} -> ${s}`);
      if (s >= 500) {
        // eslint-disable-next-line no-console
        console.log(`[global-setup]   body: ${(await res.text()).slice(0, 300)}`);
      }
      return s;
    } catch (e) {
      // eslint-disable-next-line no-console
      console.log(`[global-setup] ${label} -> threw: ${(e as Error).message}`);
      return -1;
    }
  };

  // --- 1. startup wizard --------------------------------------------------- Source-verified JF12 flow: POST /Startup/User does NOT create a user - it configures the pre-existing default admin (renames it + sets its password).

  // GET forces _userManager.InitializeAsync() so the default user exists, and
  // tells us its current name.
  const firstUserRes = await ctx.get('/Startup/User');
  // eslint-disable-next-line no-console
  console.log(`[global-setup] GET Startup/User -> ${firstUserRes.status()}`);

  await step('Startup/Configuration', () =>
    ctx.post('/Startup/Configuration', {
      headers: { 'Content-Type': 'application/json' },
      data: {
        UICulture: 'en-US',
        MetadataCountryCode: 'US',
        PreferredMetadataLanguage: 'en',
        ServerName: 'jfh-e2e',
      },
    }),
  );

  // Configure the first user. 204 = success on a fresh container.
  const userRes = await ctx.post('/Startup/User', {
    headers: { 'Content-Type': 'application/json' },
    data: { Name: ADMIN_USER, Password: ADMIN_PASS },
  });
  // eslint-disable-next-line no-console
  console.log(`[global-setup] POST Startup/User -> ${userRes.status()}`);
  const wizardAlreadyDone = userRes.status() === 401 || userRes.status() === 403;
  if (userRes.status() !== 204 && !userRes.ok() && !wizardAlreadyDone) {
    throw new Error(`Startup/User failed: ${userRes.status()} ${await userRes.text()}`);
  }
  if (wizardAlreadyDone) {
    // eslint-disable-next-line no-console
    console.log('[global-setup] wizard already complete (reused container) - skipping to auth');
  }

  if (!wizardAlreadyDone) {
    // 12.0: EnableAutomaticPortMapping was removed - send only EnableRemoteAccess.
    await step('Startup/RemoteAccess', () =>
      ctx.post('/Startup/RemoteAccess', {
        headers: { 'Content-Type': 'application/json' },
        data: { EnableRemoteAccess: true },
      }),
    );

    // Finish the wizard LAST - after this, Startup/* stop accepting anon calls.
    await step('Startup/Complete', () => ctx.post('/Startup/Complete'));
  }

  // --- 2. authenticate (retry: Startup/Complete may need a moment) ---------
  const authenticate = () =>
    ctx.post('/Users/AuthenticateByName', {
      headers: { 'Content-Type': 'application/json', Authorization: authHeader() },
      data: { Username: ADMIN_USER, Pw: ADMIN_PASS },
    });

  const authRes = await authenticateWithRetry(authenticate, 5);
  if (!authRes.ok()) {
    // Dump the current user list to help diagnose (which users actually exist?).
    const usersDump = await ctx
      .get('/Users/Public')
      .then((r) => r.text())
      .catch(() => '<none>');
    throw new Error(
      `AuthenticateByName failed: ${authRes.status()} ${await authRes.text()}\n` +
        `Public users on server: ${usersDump.slice(0, 500)}`,
    );
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
  // Books library (CollectionType "books"), proves eBooks are TRACKED in stats yet NEVER deleted by cleanup (CleanupConfigHelper marks books ineligible).
  await ensureLibrary(admin, 'Books', 'books', '/media/Books');

  // --- 4. scan and wait ----------------------------------------------------
  await runLibraryScan(admin);

  // --- 4b. seed a multi-year daily growth timeline -------------------------
  // Every fake media file is written at scan time, so the plugin's timeline (built from
  // file creation dates) would collapse to a single day and the chart would have nothing
  // to pan across. Seed a realistic backdated series; the UI chart spec re-seeds before it
  // runs because api specs calling forceRefresh=true overwrite this cache.
  const seededPoints = seedGrowthTimeline();
  // eslint-disable-next-line no-console
  console.log(`[global-setup] seeded growth timeline: ${seededPoints} daily points`);

  // Fail fast if the seed did not land where the plugin reads it (DataPath = /config/data).
  // The controller serves the cached file verbatim without forceRefresh, so the served point
  // count must match what we just wrote. A mismatch means the mount path is wrong and would
  // otherwise surface as an opaque chart-pan failure many minutes into the run.
  const seededTimeline = await admin.get(`/JellyfinHelper/GrowthTimeline`);
  if (seededTimeline.ok()) {
    const body = (await seededTimeline.json()) as { dataPoints?: unknown[]; DataPoints?: unknown[] };
    const served = (body.dataPoints ?? body.DataPoints ?? []).length;
    // eslint-disable-next-line no-console
    console.log(`[global-setup] served timeline has ${served} points (seeded ${seededPoints})`);
    if (served !== seededPoints) {
      throw new Error(
        `Growth timeline seed did not take: served ${served} points, seeded ${seededPoints}. ` +
          `Check the DataPath mount (expected /config/data -> runtime/config/data).`,
      );
    }
  } else {
    throw new Error(
      `Growth timeline verify GET failed: ${seededTimeline.status()} ${await seededTimeline.text()}`,
    );
  }

  // --- 5. link the mock Seerr user to the real Jellyfin GUID ---------------
  // Uses the mock's test hook so Discovery user-matching resolves.
  await seedSeerr('/seed-user', { jellyfinUserId: userId });

  // --- 5b. create a non-admin user for the Discovery/My (user-facing) tests - These endpoints require a NON-elevated authenticated user + the DiscoveryUserAccessEnabled toggle.
  const normalUser = await provisionNormalUser(admin, ctx, {
    userName: NORMAL_USER,
    password: NORMAL_PASS,
    // The normal user is linked to the mock's SECOND Seerr user (Bob) with the Request permission
    // (bit 32) so the Discovery/My/Request authorization branches are reachable.
    seerrHook: { path: '/seed-user2', permissions: 32 },
  });

  // --- 5c. configure the Discovery custom tab (only when the external plugins
  // are staged). Enables the user-access toggle so the sidebar + tab are live,
  // and registers a Custom Tab whose HTML content is our marker div, exactly as
  // an admin would per the in-app setup hint. The custom-tab UI spec reads
  // JFH_E2E_EXTERNAL_PLUGINS to decide whether to run or skip.
  await ensureDiscoveryConfigured(admin, (m) => console.log(`[global-setup] ${m}`));

  // --- 5d. seed the official Trakt plugin with a token for the normal user, so the Helper's "source Trakt
  // through the official plugin" mode has a real token to read from Trakt.xml. The normal user is NOT linked via
  // our own device flow, so source resolution falls through to the official plugin exactly as intended.
  if (normalUser) {
    await seedOfficialTraktPlugin(admin, normalUser.userId, (m) => console.log(`[global-setup] ${m}`));
  }

  // In CI we require the non-admin fixture so the authorization / user-facing tests can't silently skip (E2E_REQUIRE_NORMAL_USER=1).
  if (!normalUser && process.env.E2E_REQUIRE_NORMAL_USER === '1') {
    throw new Error(
      'E2E_REQUIRE_NORMAL_USER=1 but the non-admin user could not be provisioned - ' +
        'see the [global-setup] logs above for the failing step (Users/New or AuthenticateByName).',
    );
  }

  // --- 6. persist for the tests -------------------------------------------
  const out = {
    baseUrl: BASE_URL,
    token,
    userId,
    userName: ADMIN_USER,
    normalUser, // null if provisioning failed -> Discovery/My tests skip
  };
  writeFileSync(join(__dirname, 'auth.json'), JSON.stringify(out, null, 2));
  process.env.JELLYFIN_TOKEN = token;

  // eslint-disable-next-line no-console
  console.log(`[global-setup] ready: admin=${ADMIN_USER} userId=${userId}`);

  // --- 7. plant canary files outside the media library -------------------- Adversarial FS tests assert these survive every destructive case, proving no misuse deletes/moves data outside /media.
  if (hasDocker()) {
    plantCanaries();
    // eslint-disable-next-line no-console
    console.log(`[global-setup] planted canaries: ${plantedCanaries().join(', ') || '(none writable)'}`);
  } else {
    // eslint-disable-next-line no-console
    console.log('[global-setup] docker exec unavailable - FS-assertion tests will skip');
  }

  await ctx.dispose();
  await admin.dispose();
}

type ProvisionCtx = Awaited<ReturnType<typeof pwRequest.newContext>>;
type AuthResponse = Awaited<ReturnType<ProvisionCtx['post']>>;

/** Resolve after `ms`, without an inline Promise executor at the call site. */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Authenticate, retrying up to `remaining` more times with a 2s backoff while
 * the response is not ok. The retry is intentionally sequential (the server may
 * need a moment after Startup/Complete); expressing it as bounded recursion
 * keeps that ordering without an await-in-loop.
 */
async function authenticateWithRetry(
  authenticate: () => Promise<AuthResponse>,
  remaining: number,
): Promise<AuthResponse> {
  const res = await authenticate();
  if (res.ok() || remaining <= 0) {
    return res;
  }
  // eslint-disable-next-line no-console
  console.log(`[global-setup] auth attempt -> ${res.status()}; retrying (${remaining} left)...`);
  await delay(2000);
  return authenticateWithRetry(authenticate, remaining - 1);
}

/**
 * Create (or reuse) a non-admin test user and authenticate as it. Returns the captured token/userId, or null
 * if provisioning failed (dependent specs then skip). `opts.seerrHook`, when given, links the user to a mock
 * Seerr user with the stated permissions; omit it for a user that needs no Seerr linkage (e.g. the pure
 * own-device-flow user). Extracted from globalSetup to keep the top-level flow flat.
 */
async function provisionNormalUser(
  admin: ProvisionCtx,
  ctx: ProvisionCtx,
  opts: { userName: string; password: string; seerrHook?: { path: string; permissions: number } },
): Promise<{ token: string; userId: string; userName: string } | null> {
  const { userName, password, seerrHook } = opts;
  try {
    const created = await admin.post('/Users/New', {
      headers: { 'Content-Type': 'application/json' },
      data: { Name: userName, Password: password },
    });
    // On a warm/reused container the user already exists and Users/New returns a 4xx (e.g. 400 "user already exists").
    const createdOk = created.ok();
    const alreadyExists = !createdOk && created.status() >= 400 && created.status() < 500;
    if (!createdOk && !alreadyExists) {
      // eslint-disable-next-line no-console
      console.log(`[global-setup] Users/New (${userName}) -> ${created.status()} (unexpected; dependent tests will skip)`);
      return null;
    }
    // eslint-disable-next-line no-console
    console.log(`[global-setup] Users/New (${userName}) -> ${created.status()} (${createdOk ? 'created' : 'already exists - authenticating existing user'})`);
    const nAuth = await ctx.post('/Users/AuthenticateByName', {
      headers: { 'Content-Type': 'application/json', Authorization: authHeader() },
      data: { Username: userName, Pw: password },
    });
    if (!nAuth.ok()) {
      // Could not authenticate - do NOT report success silently. Log the rejection so a broken provisioning path is visible; dependent tests skip (return null) rather than run against a half-provisioned user.
      console.log(
        `[global-setup] non-admin auth failed for ${userName} (Users/New was ${created.status()}): ` +
          `${nAuth.status()} ${(await nAuth.text()).slice(0, 200)} (dependent tests will skip)`,
      );
      return null;
    }
    const nj = (await nAuth.json()) as { AccessToken: string; User: { Id: string } };
    // eslint-disable-next-line no-console
    console.log(`[global-setup] non-admin user ${userName} ready (${nj.User.Id})`);

    // Link to a mock Seerr user only when requested (the own-device-flow user needs none).
    if (seerrHook) {
      await seedSeerr(seerrHook.path, { jellyfinUserId: nj.User.Id, permissions: seerrHook.permissions });
    }
    return { token: nj.AccessToken, userId: nj.User.Id, userName };
  } catch (e) {
    // eslint-disable-next-line no-console
    console.log(`[global-setup] non-admin user provisioning failed (${userName}): ${(e as Error).message}`);
    return null;
  }
}

/** From the host, the mock is reachable on localhost; inside compose it's mock-seerr. */
function publicSeerrUrl(): string {
  return process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';
}

/** * Best-effort POST to a mock-Seerr test hook, always disposing the throwaway * request context. */
async function seedSeerr(path: string, data: unknown): Promise<void> {
  let c: Awaited<ReturnType<typeof pwRequest.newContext>> | undefined;
  try {
    c = await pwRequest.newContext();
    await c.post(`${publicSeerrUrl()}${path}`, {
      headers: { 'Content-Type': 'application/json' },
      data,
    });
  } catch {
    // best-effort seeding
  } finally {
    await c?.dispose().catch(() => undefined);
  }
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

export default globalSetup;
