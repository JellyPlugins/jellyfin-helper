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
import { authHeader, runLibraryScan } from './api-client.ts';
import { hasDocker, plantCanaries, plantedCanaries } from './fs-assert.ts';

const __dirname = dirname(fileURLToPath(import.meta.url));

const BASE_URL = process.env.JELLYFIN_URL ?? 'http://localhost:8096';
const ADMIN_USER = process.env.JELLYFIN_ADMIN_USER ?? 'e2eadmin';
const ADMIN_PASS = process.env.JELLYFIN_ADMIN_PASS ?? 'E2ePassw0rd!';
const NORMAL_USER = process.env.JELLYFIN_USER ?? 'e2euser';
const NORMAL_PASS = process.env.JELLYFIN_USER_PASS ?? 'E2eUserPass1!';

async function globalSetup(_config: FullConfig): Promise<void> {
  const ctx = await pwRequest.newContext({ baseURL: BASE_URL });

  // Small helper: run a startup step, log its status, and don't hard-fail on a
  // 4xx (a re-used server returns those) — but DO surface the status so a real
  // wizard failure is visible in CI instead of silently swallowed.
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

  // --- 1. startup wizard ---------------------------------------------------
  // Source-verified JF12 flow: POST /Startup/User does NOT create a user — it
  // configures the pre-existing default admin (renames it + sets its password).
  // If that user already has a password it returns 403 and does nothing, so we
  // must NOT swallow the response. Order: GET user (forces init) → Configuration
  // → User (hard-checked) → RemoteAccess → Complete (finish user BEFORE Complete).

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
  //
  // Reused container (iterating with --keep, or a CI re-run against a warm
  // volume): the wizard is already complete, so the anon Startup/* endpoints
  // are closed and this returns 401 (endpoint locked) or 403 (user already has
  // a password). Both mean "already provisioned" — we skip wizard setup and go
  // straight to authenticating with the admin creds we expect. If those creds
  // don't match what the container was set up with, the auth step below fails
  // loudly with a diagnostic, so treating 401/403 as recoverable is safe.
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
    console.log('[global-setup] wizard already complete (reused container) — skipping to auth');
  }

  if (!wizardAlreadyDone) {
    // 12.0: EnableAutomaticPortMapping was removed — send only EnableRemoteAccess.
    await step('Startup/RemoteAccess', () =>
      ctx.post('/Startup/RemoteAccess', {
        headers: { 'Content-Type': 'application/json' },
        data: { EnableRemoteAccess: true },
      }),
    );

    // Finish the wizard LAST — after this, Startup/* stop accepting anon calls.
    await step('Startup/Complete', () => ctx.post('/Startup/Complete'));
  }

  // --- 2. authenticate (retry: Startup/Complete may need a moment) ---------
  const authenticate = async () =>
    ctx.post('/Users/AuthenticateByName', {
      headers: { 'Content-Type': 'application/json', Authorization: authHeader() },
      data: { Username: ADMIN_USER, Pw: ADMIN_PASS },
    });

  let authRes = await authenticate();
  for (let attempt = 1; attempt <= 5 && !authRes.ok(); attempt++) {
    // eslint-disable-next-line no-console
    console.log(`[global-setup] auth attempt ${attempt} -> ${authRes.status()}; retrying...`);
    await new Promise((r) => setTimeout(r, 2000));
    authRes = await authenticate();
  }
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

  // --- 5b. create a non-admin user for the Discovery/My (user-facing) tests -
  // These endpoints require a NON-elevated authenticated user + the
  // DiscoveryUserAccessEnabled toggle. We provision one and capture its token.
  let normalUser: { token: string; userId: string; userName: string } | null = null;
  try {
    const created = await admin.post('/Users/New', {
      headers: { 'Content-Type': 'application/json' },
      data: { Name: NORMAL_USER, Password: NORMAL_PASS },
    });
    // On a warm/reused container the user already exists and Users/New returns a
    // 4xx (e.g. 400 "user already exists"). That is NOT a provisioning failure —
    // we can still authenticate as the existing user. So authenticate whenever the
    // create succeeded OR the user plausibly already exists; only a 5xx (or a
    // network throw) is a hard failure. This keeps provisioning idempotent across
    // re-runs against a persistent volume.
    const createdOk = created.ok();
    const alreadyExists = !createdOk && created.status() >= 400 && created.status() < 500;
    if (!createdOk && !alreadyExists) {
      // eslint-disable-next-line no-console
      console.log(`[global-setup] Users/New -> ${created.status()} (unexpected; non-admin tests will skip)`);
    } else {
      // eslint-disable-next-line no-console
      console.log(`[global-setup] Users/New -> ${created.status()} (${createdOk ? 'created' : 'already exists — authenticating existing user'})`);
      const nAuth = await ctx.post('/Users/AuthenticateByName', {
        headers: { 'Content-Type': 'application/json', Authorization: authHeader() },
        data: { Username: NORMAL_USER, Pw: NORMAL_PASS },
      });
      if (nAuth.ok()) {
        const nj = (await nAuth.json()) as { AccessToken: string; User: { Id: string } };
        normalUser = { token: nj.AccessToken, userId: nj.User.Id, userName: NORMAL_USER };
        // eslint-disable-next-line no-console
        console.log(`[global-setup] non-admin user ${NORMAL_USER} ready (${nj.User.Id})`);

        // Link this non-admin user to the mock's SECOND Seerr user (Bob) and grant
        // the Request permission (bit 32) so the user-facing Discovery/My/Request
        // authorization branches are actually reachable. Seeded here — before any
        // spec runs and before the plugin populates its 5-min Seerr-user cache —
        // so the linkage is deterministic and not defeated by cache staleness.
        await pwRequest
          .newContext()
          .then((c) =>
            c.post(`${publicSeerrUrl()}/seed-user2`, {
              headers: { 'Content-Type': 'application/json' },
              data: { jellyfinUserId: nj.User.Id, permissions: 32 },
            }).catch(() => undefined),
          )
          .catch(() => undefined);
      } else {
        // Could not authenticate — do NOT report success silently. Log the
        // rejection so a broken provisioning path is visible; dependent tests skip
        // (normalUser stays null) rather than run against a half-provisioned user.
        // eslint-disable-next-line no-console
        console.log(
          `[global-setup] non-admin auth failed (Users/New was ${created.status()}): ` +
            `${nAuth.status()} ${(await nAuth.text()).slice(0, 200)} (Discovery/My tests will skip)`,
        );
      }
    }
  } catch (e) {
    // eslint-disable-next-line no-console
    console.log(`[global-setup] non-admin user provisioning failed: ${(e as Error).message}`);
  }

  // In CI we require the non-admin fixture so the authorization / user-facing
  // tests can't silently skip (E2E_REQUIRE_NORMAL_USER=1). Fail the whole run
  // here — at setup — with a clear message rather than letting each dependent
  // test skip and hide a broken provisioning path.
  if (!normalUser && process.env.E2E_REQUIRE_NORMAL_USER === '1') {
    throw new Error(
      'E2E_REQUIRE_NORMAL_USER=1 but the non-admin user could not be provisioned — ' +
        'see the [global-setup] logs above for the failing step (Users/New or AuthenticateByName).',
    );
  }

  // --- 6. persist for the tests -------------------------------------------
  const out = {
    baseUrl: BASE_URL,
    token,
    userId,
    userName: ADMIN_USER,
    normalUser, // null if provisioning failed → Discovery/My tests skip
  };
  writeFileSync(join(__dirname, 'auth.json'), JSON.stringify(out, null, 2));
  process.env.JELLYFIN_TOKEN = token;

  // eslint-disable-next-line no-console
  console.log(`[global-setup] ready: admin=${ADMIN_USER} userId=${userId}`);

  // --- 7. plant canary files outside the media library --------------------
  // Adversarial FS tests assert these survive every destructive case, proving
  // no misuse deletes/moves data outside /media. Best-effort: skipped cleanly
  // when Docker isn't reachable from the test host (FS tests then skip too).
  if (hasDocker()) {
    plantCanaries();
    // eslint-disable-next-line no-console
    console.log(`[global-setup] planted canaries: ${plantedCanaries().join(', ') || '(none writable)'}`);
  } else {
    // eslint-disable-next-line no-console
    console.log('[global-setup] docker exec unavailable — FS-assertion tests will skip');
  }

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

export default globalSetup;
