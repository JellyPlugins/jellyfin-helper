/**
 * Per-stage TaskMode BEHAVIOUR proof: does each mode value actually DO what it
 * promises, at STAGE granularity?
 *
 *   Deactivate → stage is skipped entirely (no work, no dry-run log); orphan survives.
 *   DryRun     → stage RUNS and logs what it WOULD do, but changes nothing on disk.
 *   Activate   → stage performs the real delete / trash move.
 *
 * The existing cleanup-fs spec proves Activate's on-disk effect and that an
 * ALL-DryRun pass deletes nothing. It cannot distinguish Deactivate from DryRun
 * (both leave files on disk) nor prove the modes are honoured INDEPENDENTLY per
 * stage in a single mixed pass. That is this file's job:
 *
 *   - Deactivate vs DryRun differ in the plugin LOG (skip line vs dry-run line),
 *     which is the only observable that tells the two "nothing changed on disk"
 *     modes apart. Asserted via GET /JellyfinHelper/Logs?source=HelperCleanup.
 *   - A mixed Activate+DryRun+Deactivate pass proves modes apply per-stage, not
 *     globally: only the Activated stage's orphan disappears.
 *   - DryRun with UseTrash:true creates NO trash (the cleanup-fs DryRun test used
 *     UseTrash:false, so it never proved the trash move is suppressed); Activate
 *     with UseTrash:true DOES route the orphan into trash.
 *   - Seerr stage: DryRun logs "would delete" and deletes nothing; Activate logs
 *     the real delete. (seerr-cleanup proves the COUNT; here we prove the mode
 *     WORDING contract the operator relies on to trust a dry run.)
 *
 * FS assertions require the container (docker exec) and skip LOUDLY without it.
 * The log/Seerr assertions need no container.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask, assertPluginActive } from '../setup/api-client.ts';
import {
  ensureCanariesPlanted,
  regenFixtures,
  verifyCanaries,
  hasDocker,
  containerExists,
  containerDirExists,
  containerFileExists,
  containerFindCount,
  containerRm,
} from '../setup/fs-assert.ts';

const M = '/media/Movies';
const TRASH = `${M}/.jellyfin-trash`;
const MOCK = process.env.MOCK_SEERR_PUBLIC_URL ?? 'http://localhost:5055';

// Fixture orphans (from gen-media.sh, also used by cleanup-fs), each in its own
// folder so a per-stage assertion is attributable to exactly one stage.
const TRICKPLAY_ORPHAN = `${M}/Ghost Movie (2010)/Ghost Movie (2010).trickplay`;
const SUBTITLE_ORPHAN = `${M}/Mixed Bag (2018)/Ghost Subtitle (2001).en.srt`;
const EMPTYFOLDER_ORPHAN = `${M}/Lonely Sub (2012)`;

interface LogEntry {
  Timestamp: string;
  Level: string;
  Source: string;
  Message: string;
}

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  // Restore benign shared state so later specs don't inherit a half-deactivated
  // backend or a leftover trash folder from this suite.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      TrickplayTaskMode: 'DryRun', EmptyMediaFolderTaskMode: 'DryRun',
      OrphanedSubtitleTaskMode: 'DryRun', LinkRepairTaskMode: 'DryRun',
      SeerrCleanupTaskMode: 'DryRun', RecommendationsTaskMode: 'DryRun',
      UseTrash: false, OrphanMinAgeDays: 30, ExcludedLibraries: '',
    },
  }).catch(() => undefined);
  if (hasDocker()) containerRm(TRASH);
  await ctx.dispose();
});

async function putConfig(body: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: body,
  });
  expect(res.ok(), `config update failed: ${res.status()}`).toBeTruthy();
}

/** Set every cleanup stage to `Deactivate`, then apply overrides. */
async function allStages(overrides: Record<string, unknown>) {
  await putConfig({
    TrickplayTaskMode: 'Deactivate', EmptyMediaFolderTaskMode: 'Deactivate',
    OrphanedSubtitleTaskMode: 'Deactivate', LinkRepairTaskMode: 'Deactivate',
    SeerrCleanupTaskMode: 'Deactivate', RecommendationsTaskMode: 'Deactivate',
    UseTrash: false, OrphanMinAgeDays: 0, ...overrides,
  });
}

/** Clear the plugin log buffer so the next run's HelperCleanup lines stand alone. */
async function clearLogs() {
  const res = await ctx.delete(p('Logs'));
  expect([200, 204], `Logs DELETE status ${res.status()}`).toContain(res.status());
}

/** All HelperCleanup-sourced log messages, newest-first, since the last clear. */
async function cleanupLog(): Promise<string[]> {
  const res = await ctx.get(p('Logs?source=HelperCleanup&limit=2000'));
  expect(res.ok(), `GET Logs failed: ${res.status()}`).toBeTruthy();
  const body = (await res.json()) as { Entries: LogEntry[] };
  return body.Entries.map((e) => e.Message);
}

/** All SeerrCleanup-sourced log messages since the last clear. */
async function seerrLog(): Promise<string[]> {
  const res = await ctx.get(p('Logs?source=SeerrCleanup&limit=2000'));
  expect(res.ok(), `GET Logs failed: ${res.status()}`).toBeTruthy();
  const body = (await res.json()) as { Entries: LogEntry[] };
  return body.Entries.map((e) => e.Message);
}

async function resetSeerrMock() {
  const m = await pwRequest.newContext();
  const r = await m.get(`${MOCK}/reset`);
  expect(r.ok(), `mock /reset failed: ${r.status()}`).toBeTruthy();
  await m.dispose();
}
async function seerrCount(): Promise<number> {
  const m = await pwRequest.newContext();
  const r = await m.get(`${MOCK}/count`);
  expect(r.ok(), `mock /count failed: ${r.status()}`).toBeTruthy();
  const body = (await r.json()) as { count: number };
  await m.dispose();
  return body.count;
}

// ---------------------------------------------------------------------------
// Log-observable behaviour (no container needed). The plugin log is the ONLY
// place Deactivate and DryRun are distinguishable when both leave disk untouched.
// ---------------------------------------------------------------------------
test.describe.serial('TaskMode is observable in the plugin log', () => {
  test('Deactivate emits a skip line; DryRun emits a dry-run start line - never mixed up', async () => {
    // Trickplay Deactivated, Empty-folder DryRun, in the SAME pass.
    await allStages({ TrickplayTaskMode: 'Deactivate', EmptyMediaFolderTaskMode: 'DryRun' });
    await clearLogs();
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    const log = (await cleanupLog()).join('\n');
    // Deactivated stage: skipped, and NEVER "Started".
    expect(log, 'deactivated stage must log a skip').toContain(
      'Skipping Trickplay Cleanup (deactivated in settings).',
    );
    expect(log, 'deactivated stage must not be started').not.toContain('Starting Trickplay Cleanup');
    // DryRun stage: started in Dry Run, and NOT skipped.
    expect(log, 'dry-run stage must start in Dry Run').toContain(
      'Starting Empty Media Folder Cleanup (Dry Run)...',
    );
    expect(log).not.toContain('Skipping Empty Media Folder Cleanup');
    await assertPluginActive(ctx);
  });

  test('Activate emits an Active start line (distinct from Dry Run)', async () => {
    await allStages({ TrickplayTaskMode: 'Activate' });
    await clearLogs();
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    const log = (await cleanupLog()).join('\n');
    expect(log).toContain('Starting Trickplay Cleanup (Active)...');
    expect(log).not.toContain('Starting Trickplay Cleanup (Dry Run)');
    expect(log).not.toContain('Skipping Trickplay Cleanup');
    await assertPluginActive(ctx);
  });
});

// ---------------------------------------------------------------------------
// On-disk behaviour (container required). Proves each mode's real effect and
// that modes are honoured PER STAGE in a single pass.
// ---------------------------------------------------------------------------
test.describe.serial('TaskMode drives the real on-disk outcome, per stage', () => {
  test.beforeEach(() => {
    ensureCanariesPlanted(); // skips loudly w/o docker; guarantees a canary exists
    regenFixtures();
    containerRm(TRASH);
  });
  test.afterEach(() => {
    expect(verifyCanaries(), 'nothing outside /media may be touched').toEqual([]);
  });

  test('mixed pass: only the Activated stage deletes; DryRun and Deactivate leave their orphans', async () => {
    // Trickplay=Activate, Subtitle=DryRun, Empty-folder=Deactivate - one pass.
    await allStages({
      TrickplayTaskMode: 'Activate',
      OrphanedSubtitleTaskMode: 'DryRun',
      EmptyMediaFolderTaskMode: 'Deactivate',
    });
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    // Activated stage → its orphan is really gone.
    expect(containerExists(TRICKPLAY_ORPHAN), 'Activated trickplay orphan must be deleted').toBe(false);
    // DryRun stage → its orphan survives (logged, not deleted).
    expect(containerFileExists(SUBTITLE_ORPHAN), 'DryRun subtitle orphan must survive').toBe(true);
    // Deactivated stage → its orphan survives (never processed).
    expect(containerDirExists(EMPTYFOLDER_ORPHAN), 'Deactivated empty-folder orphan must survive').toBe(true);
    await assertPluginActive(ctx);
  });

  test('DryRun with UseTrash:true creates NO trash; a following Activate routes the orphan INTO trash', async () => {
    // Phase 1 - DryRun. Even with trash enabled, a dry run must not move anything.
    await allStages({ TrickplayTaskMode: 'DryRun', UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 });
    let result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    expect(containerExists(TRICKPLAY_ORPHAN), 'DryRun must not remove the orphan').toBe(true);
    expect(containerExists(TRASH), 'DryRun must not create a trash folder').toBe(false);

    // Phase 2 - Activate with the same trash config. Now the orphan is removed
    // from its folder AND a trash folder appears holding the moved item.
    await allStages({ TrickplayTaskMode: 'Activate', UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 });
    result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    expect(containerExists(TRICKPLAY_ORPHAN), 'Activate must remove the orphan from its folder').toBe(false);
    expect(containerDirExists(TRASH), 'Activate+UseTrash must create the trash folder').toBe(true);
    expect(containerFindCount(TRASH), 'the removed orphan must land in trash').toBeGreaterThan(0);
    await assertPluginActive(ctx);
  });
});

// ---------------------------------------------------------------------------
// Seerr stage mode wording - the "it only logged, it didn't touch anything"
// contract an operator reads to trust a dry run. Count is proven in
// seerr-cleanup.api.spec.ts; here we prove the LOG says the right thing.
// ---------------------------------------------------------------------------
test.describe.serial('Seerr cleanup mode wording matches its behaviour', () => {
  test.beforeEach(async () => {
    await resetSeerrMock();
  });

  test('DryRun logs "no requests will be deleted" / "Would delete" and deletes nothing', async () => {
    const before = await seerrCount();
    await allStages({
      SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'e2e-seerr-key',
      SeerrCleanupTaskMode: 'DryRun', SeerrCleanupAgeDays: 30,
    });
    await clearLogs();
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    const log = (await seerrLog()).join('\n');
    expect(log, 'dry run must announce no deletion').toContain('Dry Run). No requests will be deleted.');
    expect(log, 'dry run reports would-delete, not deleted').toContain('Would delete:');
    expect(log).not.toContain('Deleted:');
    expect(await seerrCount(), 'DryRun must not delete on the mock').toBe(before);
    await assertPluginActive(ctx);
  });

  test('Activate logs a real "Deleted:" summary and the mock count drops', async () => {
    const before = await seerrCount();
    await allStages({
      SeerrUrl: 'http://mock-seerr:5055', SeerrApiKey: 'e2e-seerr-key',
      SeerrCleanupTaskMode: 'Activate', SeerrCleanupAgeDays: 30,
    });
    await clearLogs();
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');

    const log = (await seerrLog()).join('\n');
    expect(log, 'active run reports a real Deleted count').toContain('Deleted:');
    expect(log).not.toContain('No requests will be deleted');
    expect(await seerrCount(), 'Activate must actually delete expired requests').toBeLessThan(before);
    await assertPluginActive(ctx);
  });
});
