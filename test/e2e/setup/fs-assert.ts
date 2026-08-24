/**
 * Filesystem-assertion helpers for the E2E tests.
 *
 * The suite runs on the HOST (Playwright), but the plugin acts on the container's
 * filesystem. To prove a feature actually did the right thing on disk - deleted
 * the orphan, kept the valid file, moved an item into trash, rewrote a .strm,
 * left a canary outside the library untouched - we shell into the running
 * Jellyfin container with `docker compose exec`.
 *
 * Everything here is best-effort about environment: if Docker isn't reachable
 * from the Playwright worker (some CI layouts), `hasDocker()` returns false and
 * FS-dependent tests should `test.skip()` LOUDLY rather than silently pass.
 */
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { test, expect } from '@playwright/test';

const __dirname = dirname(fileURLToPath(import.meta.url));

/** Absolute path to the compose file (test/e2e/compose.yml). */
const COMPOSE_FILE = join(__dirname, '..', 'compose.yml');
/** The Jellyfin service name in compose.yml. */
const SERVICE = 'jellyfin';

export interface ExecResult {
  code: number;
  stdout: string;
  stderr: string;
}

/**
 * Run a shell command INSIDE the Jellyfin container and capture its result.
 * Never throws on a non-zero exit - returns the code so callers can assert on it
 * (e.g. `test -e` exit codes). Throws only if `docker` itself can't be invoked.
 */
export function execInContainer(cmd: string, timeoutMs = 20_000): ExecResult {
  try {
    const stdout = execFileSync(
      'docker',
      ['compose', '-f', COMPOSE_FILE, 'exec', '-T', SERVICE, 'sh', '-lc', cmd],
      { encoding: 'utf-8', timeout: timeoutMs, stdio: ['ignore', 'pipe', 'pipe'] },
    );
    return { code: 0, stdout, stderr: '' };
  } catch (e) {
    const err = e as { status?: number; stdout?: Buffer | string; stderr?: Buffer | string };
    return {
      code: typeof err.status === 'number' ? err.status : 1,
      stdout: err.stdout?.toString() ?? '',
      stderr: err.stderr?.toString() ?? '',
    };
  }
}

/**
 * True when `docker compose exec` reaches the container. Cached - the answer
 * can't change within a run. Use to gate FS-assertion tests.
 */
let _dockerOk: boolean | undefined;
export function hasDocker(): boolean {
  if (_dockerOk !== undefined) return _dockerOk;
  const res = execInContainer('echo jfh-docker-probe', 8000);
  _dockerOk = res.code === 0 && res.stdout.includes('jfh-docker-probe');
  return _dockerOk;
}

/** POSIX-quote a path for safe interpolation into a container `sh -lc` command. */
export function q(path: string): string {
  const escaped = path.replaceAll("'", String.raw`'\''`);
  return `'${escaped}'`;
}

/** True if a path exists in the container (file, dir, or symlink). */
export function containerExists(path: string): boolean {
  return execInContainer(`test -e ${q(path)}`).code === 0;
}

/** True if a path exists AND is a regular file. */
export function containerFileExists(path: string): boolean {
  return execInContainer(`test -f ${q(path)}`).code === 0;
}

/** True if a path exists AND is a directory. */
export function containerDirExists(path: string): boolean {
  return execInContainer(`test -d ${q(path)}`).code === 0;
}

/** True if a path exists AND is a symbolic link (broken or not). */
export function containerIsSymlink(path: string): boolean {
  return execInContainer(`test -L ${q(path)}`).code === 0;
}

/** Read a file's contents from the container. Throws if the read fails. */
export function readContainerFile(path: string): string {
  const res = execInContainer(`cat ${q(path)}`);
  if (res.code !== 0) {
    throw new Error(`readContainerFile(${path}) failed (code ${res.code}): ${res.stderr || res.stdout}`);
  }
  return res.stdout;
}

/** Return the target a symlink points at (via `readlink`), trimmed. Empty if not a link. */
export function readContainerSymlink(path: string): string {
  return execInContainer(`readlink ${q(path)} 2>/dev/null || true`).stdout.trim();
}

/** sha256 of a container file (hex), or null if it can't be hashed. */
export function sha256(path: string): string | null {
  const res = execInContainer(`sha256sum ${q(path)} 2>/dev/null | cut -d' ' -f1`);
  const hash = res.stdout.trim();
  return /^[0-9a-f]{64}$/.test(hash) ? hash : null;
}

/** List directory entry names (one per line), sorted. Empty array if the dir is absent. */
export function containerLs(dir: string): string[] {
  const res = execInContainer(`ls -1 ${q(dir)} 2>/dev/null | sort || true`);
  return res.stdout.split('\n').map((s) => s.trim()).filter(Boolean);
}

/** Count files (recursively) under a directory matching an optional glob. */
export function containerFindCount(dir: string, namePattern?: string): number {
  const nameArg = namePattern ? `-name ${q(namePattern)}` : '';
  const res = execInContainer(`find ${q(dir)} -type f ${nameArg} 2>/dev/null | wc -l`);
  return Number(res.stdout.trim()) || 0;
}

/** Make a directory (and parents) inside the container. */
export function containerMkdir(path: string): void {
  execInContainer(`mkdir -p ${q(path)}`);
}

/** Write text to a container file (creating parent dirs). */
export function containerWriteFile(path: string, contents: string): void {
  // For a filesystem-root path like "/CANARY_ROOT.txt" the regex strips to "", so
  // fall back to "/" - otherwise `mkdir -p ''` errors and the && short-circuits,
  // silently never writing the file (which would drop the root canary from the
  // containment proof without any failure).
  const dir = path.replace(/\/[^/]*$/, '') || '/';
  const b64 = Buffer.from(contents, 'utf-8').toString('base64');
  // base64 round-trip avoids any quoting/escaping hazard with arbitrary contents.
  execInContainer(`mkdir -p ${q(dir)} && printf %s ${q(b64)} | base64 -d > ${q(path)}`);
}

/** Remove a path (recursively) inside the container. */
export function containerRm(path: string): void {
  execInContainer(`rm -rf ${q(path)}`);
}

/**
 * Set a path's mtime to N days in the past, using the CONTAINER clock (so trash
 * retention/age tests are not affected by host/container clock skew).
 */
export function containerBackdateDays(path: string, days: number): void {
  execInContainer(`touch -d "${days} days ago" ${q(path)}`);
}

/** A container-clock timestamp string `yyyyMMdd-HHmmss`, offset by `daysAgo` days. */
export function containerTimestamp(daysAgo = 0): string {
  const res = execInContainer(`date -u -d "${daysAgo} days ago" +%Y%m%d-%H%M%S`);
  return res.stdout.trim();
}

// --- canary: prove nothing escaped the media library -----------------------

/**
 * Canary files planted OUTSIDE the media library. Any destructive test must
 * leave every one of these byte-for-byte intact - that is the real proof that
 * a misuse/abuse case did not delete or move data outside /media.
 */
export const CANARY_PATHS = [
  '/config/jfh-canary/marker.txt', // inside Jellyfin's own data dir - must NEVER be touched
  '/srv/jfh-canary/secret.txt', // an arbitrary host dir outside /media and /config
  '/CANARY_ROOT.txt', // near the filesystem root
] as const;

const CANARY_CONTENT = 'CANARY-DO-NOT-TOUCH';

/**
 * Plant the canary files (idempotent, best-effort). Call once in global-setup.
 * On CI the container may run as a non-root UID that can't write to `/` or
 * `/srv`; those are skipped and simply not verified later. `/config` is writable
 * (it's the plugin's own data mount) so the most important canary always lands.
 *
 * NOTE: This runs in the Playwright `globalSetup` PROCESS, which is separate from
 * the worker processes that run the specs. Module state therefore does NOT cross
 * that boundary - which is exactly why {@link verifyCanaries} and
 * {@link plantedCanaries} re-derive their answer by probing the container on disk
 * rather than trusting any in-memory list. Planting is an on-disk side effect, so
 * it is visible to the workers; a module-scoped "what did I plant" array would not
 * be, and relying on one made the whole canary assertion a no-op in the past.
 */
export function plantCanaries(): void {
  for (const path of CANARY_PATHS) {
    containerWriteFile(path, CANARY_CONTENT);
  }
}

/**
 * The canary paths that are actually present on the container's disk right now,
 * with the expected content. Derived by probing the container so it is correct in
 * any process (worker or global-setup), regardless of who planted them. Empty when
 * Docker is unreachable or nothing was writable this run.
 */
export function plantedCanaries(): readonly string[] {
  return CANARY_PATHS.filter((path) => {
    if (!containerFileExists(path)) return false;
    try {
      return readContainerFile(path).trim() === CANARY_CONTENT;
    } catch {
      return false;
    }
  });
}

/**
 * True when at least one canary is present on disk. Destructive specs assert this
 * in `beforeAll` so that {@link verifyCanaries} can never pass vacuously: if the
 * canaries were never planted (or Docker is unreachable), the containment proof is
 * meaningless and the spec must skip/fail loudly rather than assert on nothing.
 */
export function canariesPresent(): boolean {
  return plantedCanaries().length > 0;
}

/**
 * Verify every canary that is CURRENTLY present on disk still has its original
 * content. Returns the list of violated paths (empty = all intact). Because the
 * present-set is re-derived from the container, this works in worker processes
 * where {@link plantCanaries} never ran.
 *
 * Callers must first gate on {@link canariesPresent} (typically in `beforeAll`):
 * a destructive test could otherwise delete the sole canary and this would still
 * return `[]` (an intact path went missing but there is nothing left to compare).
 * The `beforeAll` guard proves canaries existed before the destructive action, so
 * a `[]` result here genuinely means "nothing outside /media was touched".
 */
export function verifyCanaries(): string[] {
  const violated: string[] = [];
  for (const path of CANARY_PATHS) {
    if (!containerFileExists(path)) {
      // Absent is only a violation if it was planted (writable) this run. We
      // can't distinguish "never planted" from "deleted" after the fact, so the
      // authoritative pre-condition is the canariesPresent() beforeAll guard.
      continue;
    }
    let content: string;
    try {
      content = readContainerFile(path).trim();
    } catch {
      violated.push(`${path} (unreadable)`);
      continue;
    }
    if (content !== CANARY_CONTENT) {
      violated.push(`${path} (content changed: "${content.slice(0, 40)}")`);
    }
  }
  return violated;
}

/** Regenerate the fake media library inside the container (idempotent wipe+recreate). */
export function regenFixtures(): void {
  const res = execInContainer('bash /media/.gen/gen-media.sh /media', 120_000);
  if (res.code !== 0) {
    throw new Error(`regenFixtures failed (code ${res.code}): ${res.stderr || res.stdout}`);
  }
}

/**
 * Destructive-spec `beforeAll` guard for the canary containment proof.
 *
 * global-setup plants the canaries once, but that runs in a separate process; a
 * worker that only ever *reads* module state would see none. Planting is an
 * idempotent on-disk write, so we (re-)plant here in the worker and then assert
 * at least one canary is present. This makes every later `verifyCanaries()`
 * assertion meaningful - it can no longer pass vacuously against an empty set.
 *
 * When Docker is unreachable from the worker there is nothing to prove, so the
 * whole spec skips LOUDLY (never a silent green). Call once per destructive spec:
 *
 *   test.beforeAll(() => ensureCanariesPlanted());
 */
export function ensureCanariesPlanted(): void {
  test.skip(!hasDocker(), 'docker exec unavailable - cannot plant/verify canaries');
  plantCanaries();
  expect(
    plantedCanaries().length,
    'canary containment proof is meaningless without at least one planted canary',
  ).toBeGreaterThan(0);
}
