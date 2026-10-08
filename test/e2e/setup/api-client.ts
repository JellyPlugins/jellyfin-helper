/** * Shared helpers for the E2E tests: a thin Jellyfin API client that knows how * to authenticate, drive scheduled tasks, and reach the plugin's own endpoints * (everything under the `JellyfinHelper/` route prefix). */
import { APIRequestContext, request as pwRequest, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));

export const PLUGIN_GUID = '0c737645-5cbb-4bd8-80c7-d377b560aaa4';
export const CLEANUP_TASK_KEY = 'HelperCleanup';

/** * The fixed-length API-key mask sentinel emitted by GET /Configuration in place of any stored * key (Seerr + every Radarr/Sonarr instance), and treated as "keep the stored key" on save. */
export const API_KEY_MASK = '********';

export interface NormalUser {
  token: string;
  userId: string;
  userName: string;
}

export interface AuthInfo {
  baseUrl: string;
  token: string;
  userId: string;
  userName: string;
  /** Non-admin user for user-facing (Discovery/My) tests; null if unprovisioned. */
  normalUser: NormalUser | null;
}

/** Load the auth info persisted by global-setup. */
export function loadAuth(): AuthInfo {
  const raw = readFileSync(join(__dirname, '..', 'setup', 'auth.json'), 'utf-8');
  return JSON.parse(raw) as AuthInfo;
}

/** Create a Playwright request context authenticated as the NON-admin user. */
export async function normalUserContext(auth: AuthInfo): Promise<APIRequestContext | null> {
  if (!auth.normalUser) return null;
  return pwRequest.newContext({
    baseURL: auth.baseUrl,
    extraHTTPHeaders: {
      Authorization: authHeader(auth.normalUser.token),
      Accept: 'application/json',
    },
  });
}

/** * Guard for tests that require the provisioned non-admin user. Without one these * assertions cannot run - but silently skipping would let the most important * authorization / user-facing checks vanish green if the fixture ever breaks. */
export function requireNormalUser(user: APIRequestContext | null): void {
  if (user) return;
  if (process.env.E2E_REQUIRE_NORMAL_USER === '1') {
    throw new Error(
      'non-admin user was not provisioned, but E2E_REQUIRE_NORMAL_USER=1 - this test ' +
        'must not be allowed to skip. Check the global-setup provisioning logs.',
    );
  }
  test.skip(true, 'no non-admin user provisioned (set E2E_REQUIRE_NORMAL_USER=1 to fail instead)');
}

/**
 * The MediaBrowser authorization header value. Jellyfin accepts client
 * identification either before login (no token) or after (with token).
 * Header NAME + exact token layout are set from the startup-API research.
 */
export function authHeader(token?: string): string {
  const parts = [
    'MediaBrowser Client="jfh-e2e"',
    'Device="e2e-runner"',
    'DeviceId="jfh-e2e-device"',
    'Version="1.0.0"',
  ];
  if (token) parts.push(`Token="${token}"`);
  return parts.join(', ');
}

/** Create a Playwright request context pre-authenticated as the admin. */
export async function apiContext(auth: AuthInfo): Promise<APIRequestContext> {
  return pwRequest.newContext({
    baseURL: auth.baseUrl,
    extraHTTPHeaders: {
      Authorization: authHeader(auth.token),
      Accept: 'application/json',
    },
  });
}

/** Build a plugin route path from a suffix, e.g. p('Configuration'). */
export function p(suffix: string): string {
  return `/JellyfinHelper/${suffix.replace(/^\/+/, '')}`;
}

// --- scheduled task control ------------------------------------------------

interface TaskInfo {
  Id: string;
  Key: string;
  State: 'Idle' | 'Running' | 'Cancelling';
  LastExecutionResult?: { Status?: string; ErrorMessage?: string; EndTimeUtc?: string; StartTimeUtc?: string };
}

/** Resolve the internal task Id for a task Key (e.g. HelperCleanup). */
export async function findTaskId(ctx: APIRequestContext, key: string): Promise<string> {
  const res = await ctx.get('/ScheduledTasks');
  if (!res.ok()) throw new Error(`GET /ScheduledTasks failed: ${res.status()}`);
  const tasks = (await res.json()) as TaskInfo[];
  const task = tasks.find((t) => t.Key === key);
  if (!task) throw new Error(`Scheduled task with Key="${key}" not found`);
  return task.Id;
}

/**
 * Start the given scheduled task and poll until it returns to Idle.
 * Returns the LastExecutionResult so callers can assert Completed vs Failed.
 */
export async function runTaskToCompletion(
  ctx: APIRequestContext,
  taskId: string,
  { timeoutMs = 60_000, pollMs = 1000 }: { timeoutMs?: number; pollMs?: number } = {},
): Promise<TaskInfo> {
  // Capture the prior run's end time so we can detect a NEW completion even if
  // the task is too fast to ever be observed in the Running state.
  const prior = await ctx.get(`/ScheduledTasks/${taskId}`).then((r) => (r.ok() ? r.json() : null));
  const priorEnd = (prior as TaskInfo | null)?.LastExecutionResult?.EndTimeUtc ?? '';

  const start = await ctx.post(`/ScheduledTasks/Running/${taskId}`);
  if (!start.ok() && start.status() !== 204) {
    throw new Error(`Failed to start task ${taskId}: ${start.status()}`);
  }

  const deadline = Date.now() + timeoutMs;
  // Accept Idle as "completed" only once we've EITHER observed a Running state OR seen a fresh LastExecutionResult.EndTimeUtc (newer than before we started).
  await sleep(500);
  let sawRunning = false;
  while (Date.now() < deadline) {
    const res = await ctx.get(`/ScheduledTasks/${taskId}`);
    if (res.ok()) {
      const task = (await res.json()) as TaskInfo;
      if (task.State === 'Running' || task.State === 'Cancelling') sawRunning = true;
      const end = task.LastExecutionResult?.EndTimeUtc ?? '';
      const finishedFresh = end !== '' && end !== priorEnd;
      if (task.State === 'Idle' && (sawRunning || finishedFresh)) return task;
    }
    await sleep(pollMs);
  }
  throw new Error(`Task ${taskId} did not complete within ${timeoutMs}ms`);
}

/** Convenience: run the plugin's HelperCleanup task to completion. */
export async function runCleanupTask(ctx: APIRequestContext, timeoutMs = 90_000): Promise<TaskInfo> {
  const id = await findTaskId(ctx, CLEANUP_TASK_KEY);
  return runTaskToCompletion(ctx, id, { timeoutMs });
}

/** * Run Jellyfin's built-in "Scan All Libraries" (RefreshLibrary) task to completion * so that files freshly written to disk become visible in Jellyfin's item model - * which is what the plugin's statistics / insights / growth-timeline read from * (they analyze what Jellyfin. */
export async function runLibraryScan(ctx: APIRequestContext, timeoutMs = 120_000): Promise<void> {
  const res = await ctx.get('/ScheduledTasks');
  if (res.ok()) {
    const tasks = (await res.json()) as TaskInfo[];
    const scan = tasks.find((t) => t.Key === 'RefreshLibrary');
    if (scan) {
      await runTaskToCompletion(ctx, scan.Id, { timeoutMs });
      await sleep(3000);
      return;
    }
  }
  await ctx.post('/Library/Refresh').catch(() => undefined);
  await sleep(3000);
}

export function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

/**
 * Assert the plugin is still loaded and Active (not Malfunctioned) - the key
 * "did this edge case take the server down?" check, run after hardening tests.
 */
export async function assertPluginActive(ctx: APIRequestContext): Promise<void> {
  const res = await ctx.get('/Plugins');
  if (!res.ok()) throw new Error(`GET /Plugins failed: ${res.status()}`);
  const plugins = (await res.json()) as Array<{ Id: string; Name: string; Status: string }>;
  const plugin = plugins.find((pl) => pl.Id.replaceAll('-', '') === PLUGIN_GUID.replaceAll('-', ''));
  if (!plugin) throw new Error('Jellyfin Helper plugin not found in /Plugins');
  if (plugin.Status && plugin.Status !== 'Active') {
    throw new Error(`Plugin status is "${plugin.Status}", expected Active`);
  }
}
