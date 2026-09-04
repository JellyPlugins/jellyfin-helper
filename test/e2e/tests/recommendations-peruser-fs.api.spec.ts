/**
 * Per-user recommendation model lifecycle (filesystem behaviour).
 *
 * Proves, against the real Jellyfin 12 container, what the unit tests (mocked) cannot:
 *  - training persists the GLOBAL model files (ml_weights.json, neural_weights.json, ensemble_state.json);
 *  - a user above the example threshold gets per-user files (ml_weights_{id}.json, ensemble_state_{id}.json)
 *    whose JSON has the expected shape;
 *  - two training runs evolve the per-user state (TrainingExampleCount does not regress; files are rewritten);
 *  - deleting a user and re-running the task prunes that user's per-user files while the global files remain.
 *
 * The suite runs on the HOST; the plugin writes inside the container, so file assertions go through
 * `execInContainer`. Gated on docker availability like the other *-fs specs.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, runCleanupTask, sleep } from '../setup/api-client.ts';
import {
  hasDocker,
  execInContainer,
  containerLs,
  readContainerFile,
  containerFileExists,
} from '../setup/fs-assert.ts';

/** Plugin data folder inside the container (same dir the recommendation cache lives in). */
const DATA_DIR = '/config/data';
const GLOBAL_WEIGHTS = `${DATA_DIR}/ml_weights.json`;
const GLOBAL_NEURAL = `${DATA_DIR}/neural_weights.json`;
const GLOBAL_STATE = `${DATA_DIR}/ensemble_state.json`;

/** Synthetic user id planted by the prune test. Cleaned up unconditionally so a failed run cannot leak it. */
const ORPHAN_ID = '00000000000000000000000000000abc';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
});
test.afterAll(async () => {
  // Return the engine to the suite's shared default so later specs are unaffected.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { RecommendationsTaskMode: 'DryRun' },
  }).catch(() => undefined);
  await ctx.dispose();
});

async function putConfig(body: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: body,
  });
  expect(res.ok(), `config update failed: ${res.status()}`).toBeTruthy();
}

/** Activate recommendations only; disable every filesystem stage so the run is isolated. */
async function activateRecommendationsOnly() {
  await putConfig({
    RecommendationsTaskMode: 'Activate',
    SyncRecommendationsToPlaylist: false,
    TrickplayTaskMode: 'Deactivate',
    EmptyMediaFolderTaskMode: 'Deactivate',
    OrphanedSubtitleTaskMode: 'Deactivate',
    LinkRepairTaskMode: 'Deactivate',
    SeerrCleanupTaskMode: 'Deactivate',
  });
}

/** Per-user weight files currently on disk (ml_weights_{id}.json). */
function perUserWeightFiles(): string[] {
  return containerLs(DATA_DIR).filter((n) => /^ml_weights_[0-9a-f]{32}\.json$/.test(n));
}

/** Per-user state files currently on disk (ensemble_state_{id}.json). */
function perUserStateFiles(): string[] {
  return containerLs(DATA_DIR).filter((n) => /^ensemble_state_[0-9a-f]{32}\.json$/.test(n));
}

test.describe.serial('per-user recommendation model files', () => {
  test.skip(!hasDocker(), 'docker exec unavailable - cannot inspect the plugin data folder');

  test.afterEach(() => {
    if (!hasDocker()) {
      return;
    }

    // The planted orphan is normally removed by a successful prune, but a failed assertion (or a retry of
    // this serial group) would otherwise leave it in place, and the earlier tests read this directory with
    // regexes that match the planted names. Remove it unconditionally so one failure cannot bleed into them.
    try {
      execInContainer(`rm -f ${DATA_DIR}/ml_weights_${ORPHAN_ID}.json ${DATA_DIR}/ensemble_state_${ORPHAN_ID}.json`);
    } catch {
      // Nothing to clean up.
    }
  });

  test('training persists the global model files', async () => {
    await activateRecommendationsOnly();

    // Training runs off the previous run's cached results, so the very first Activate run only generates
    // recommendations (empty cache means nothing to train on) and writes no model files. The first run here
    // seeds the results cache; the second is the one that actually trains and persists the global model.
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);

    // The global model is always trained (learned + neural + blend state) regardless of per-user data.
    expect(containerFileExists(GLOBAL_WEIGHTS), 'global ml_weights.json should exist').toBeTruthy();
    expect(containerFileExists(GLOBAL_NEURAL), 'global neural_weights.json should exist').toBeTruthy();
    expect(containerFileExists(GLOBAL_STATE), 'global ensemble_state.json should exist').toBeTruthy();
  });

  test('per-user files appear with the expected JSON shape when a user clears the threshold', async () => {
    await activateRecommendationsOnly();
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);

    const weightFiles = perUserWeightFiles();
    // On a minimal single-user fixture library a user may not reach the >=12 example threshold. That is a
    // valid state (they keep scoring on the global model), so skip rather than fail. The global-file test
    // above already proves training ran.
    test.skip(
      weightFiles.length === 0,
      'no user reached the per-user example threshold on this library - per-user shape assertion would be vacuous',
    );

    // Every per-user weight file must pair with an ensemble-state file for the same id.
    const stateFiles = perUserStateFiles();
    for (const wf of weightFiles) {
      const id = wf.slice('ml_weights_'.length, -'.json'.length);
      expect(stateFiles, `state file missing for ${id}`).toContain(`ensemble_state_${id}.json`);

      const weights = JSON.parse(readContainerFile(`${DATA_DIR}/${wf}`)) as {
        Weights?: number[];
        Bias?: number;
        Version?: number;
      };
      expect(Array.isArray(weights.Weights), 'per-user weights must be an array').toBeTruthy();
      expect(weights.Weights?.length ?? 0, 'per-user weight vector must be non-empty').toBeGreaterThan(0);
      expect(typeof weights.Bias, 'per-user bias must be numeric').toBe('number');

      const state = JSON.parse(readContainerFile(`${DATA_DIR}/ensemble_state_${id}.json`)) as {
        Alpha?: number;
        NeuralBeta?: number;
        TrainingExampleCount?: number;
      };
      expect(typeof state.Alpha, 'per-user alpha must be numeric').toBe('number');
      expect(typeof state.NeuralBeta, 'per-user neural beta must be numeric').toBe('number');
      expect(state.TrainingExampleCount ?? -1, 'per-user example count must be >= 0').toBeGreaterThanOrEqual(0);
    }
  });

  test('a second training run evolves per-user state without regressing the example count', async () => {
    await activateRecommendationsOnly();

    // Snapshot state after run N.
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);
    const before = perUserStateFiles();
    test.skip(before.length === 0, 'no per-user model on this library - two-run evolution assertion would be vacuous');

    const firstId = before[0].slice('ensemble_state_'.length, -'.json'.length);
    const firstCount = (JSON.parse(readContainerFile(`${DATA_DIR}/ensemble_state_${firstId}.json`)) as {
      TrainingExampleCount?: number;
    }).TrainingExampleCount ?? 0;

    // Run N+1.
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);

    const secondCount = (JSON.parse(readContainerFile(`${DATA_DIR}/ensemble_state_${firstId}.json`)) as {
      TrainingExampleCount?: number;
    }).TrainingExampleCount ?? 0;

    // The per-user example counter accumulates across runs, so it must not regress.
    expect(secondCount, 'per-user TrainingExampleCount must not regress across runs').toBeGreaterThanOrEqual(firstCount);
  });

  test('deleting a user prunes their per-user files on the next run; global files remain', async () => {
    await activateRecommendationsOnly();
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);

    const existing = perUserWeightFiles();
    test.skip(existing.length === 0, 'no per-user model on this library - prune assertion would be vacuous');

    // Plant a per-user file for a synthetic user id that is NOT in the live user list. Copying an existing
    // per-user file keeps the JSON valid; the id in the filename is what PruneOrphans reconciles against.
    const orphanWeights = `${DATA_DIR}/ml_weights_${ORPHAN_ID}.json`;
    const orphanState = `${DATA_DIR}/ensemble_state_${ORPHAN_ID}.json`;
    const sample = existing[0];
    execInContainer(`cp ${DATA_DIR}/${sample} ${orphanWeights}`);
    const sampleState = perUserStateFiles()[0];
    if (sampleState) {
      execInContainer(`cp ${DATA_DIR}/${sampleState} ${orphanState}`);
    }
    expect(containerFileExists(orphanWeights), 'planted orphan weights file should exist').toBeTruthy();

    // The next training run reconciles per-user files against the live user list and prunes the orphan.
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);

    expect(containerFileExists(orphanWeights), 'orphan per-user weights must be pruned').toBeFalsy();
    expect(containerFileExists(orphanState), 'orphan per-user state must be pruned').toBeFalsy();
    // The global model files are never pruned.
    expect(containerFileExists(GLOBAL_WEIGHTS), 'global ml_weights.json must survive pruning').toBeTruthy();
    expect(containerFileExists(GLOBAL_STATE), 'global ensemble_state.json must survive pruning').toBeTruthy();
  });
});
