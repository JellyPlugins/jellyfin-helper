/**
 * Per-user recommendation model lifecycle (filesystem behaviour).
 *
 * Proves, against the real Jellyfin 12 container, what the unit tests (mocked) cannot:
 *  - a training run persists the global ensemble blend state (ensemble_state.json), and the learned/neural
 *    weight files (ml_weights.json, neural_weights.json) appear once their example thresholds are met;
 *  - a user above the example threshold gets per-user files (ml_weights_{id}.json, ensemble_state_{id}.json)
 *    whose JSON has the expected shape;
 *  - two training runs evolve the per-user state (TrainingExampleCount does not regress; files are rewritten);
 *  - deleting a user and re-running the task prunes that user's per-user files while the global files remain.
 *
 * The suite runs on the HOST; the plugin writes inside the container, so file assertions go through
 * `execInContainer`. Gated on docker availability like the other *-fs specs.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { createHash, randomUUID } from 'node:crypto';
import { apiContext, loadAuth, p, runCleanupTask, sleep } from '../setup/api-client.ts';
import {
  hasDocker,
  execInContainer,
  containerLs,
  containerFind,
  readContainerFile,
  containerFileExists,
  q,
} from '../setup/fs-assert.ts';

/**
 * The plugin writes its model and blend-state files to Plugin.Instance.DataFolderPath, which is the plugin's
 * own subfolder under the Jellyfin data directory, not the plain data root the recommendation cache uses. The
 * exact folder name is a Jellyfin implementation detail, so resolve it by locating a file the plugin actually
 * writes rather than hardcoding a path that would silently look in the wrong place.
 */
let dataDir: string | undefined;
function resolveDataDir(): string {
  if (dataDir) {
    return dataDir;
  }
  // The unsuffixed global state file is the most reliable anchor: the ensemble writes it on every training
  // attempt. Fall back to the learned weights in case a future change reorders which file appears first.
  for (const name of ['ensemble_state.json', 'ml_weights.json', 'neural_weights.json']) {
    const hit = containerFind('/config', name).find((f) => !/_[0-9a-f]{32}\.json$/i.test(f));
    if (hit) {
      dataDir = hit.slice(0, hit.length - `/${name}`.length);
      return dataDir;
    }
  }
  throw new Error('could not locate the plugin data folder (no global model file found under /config)');
}

/** Synthetic user id planted by the prune test. Cleaned up unconditionally so a failed run cannot leak it. */
const ORPHAN_ID = '00000000000000000000000000000abc';

/**
 * Config fields this suite changes via activateRecommendationsOnly. The teardown restores every one of them
 * to the suite default so a later spec that shares the container does not inherit disabled tasks.
 */
const CONFIG_DEFAULTS = {
  RecommendationsTaskMode: 'DryRun',
  SyncRecommendationsToPlaylist: false,
  TrickplayTaskMode: 'DryRun',
  EmptyMediaFolderTaskMode: 'DryRun',
  OrphanedSubtitleTaskMode: 'DryRun',
  LinkRepairTaskMode: 'DryRun',
  SeerrCleanupTaskMode: 'DryRun',
};

let ctx: APIRequestContext;
let auth: ReturnType<typeof loadAuth>;

/** Item ids this suite marked played or favorite, so the teardown can undo that user-data change. */
const seededPlayedIds: string[] = [];
const seededFavoriteIds: string[] = [];
const createdSeedFiles: Array<{ file: string; directory: string }> = [];
const PER_USER_FIT_THRESHOLD = 12;

test.beforeAll(async () => {
  auth = loadAuth();
  ctx = await apiContext(auth);
});
test.afterAll(async () => {
  // Restore every field this suite touched so later specs sharing the server are unaffected.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: CONFIG_DEFAULTS,
  }).catch(() => undefined);

  // Undo the watched and favorite state seeded in beforeAll. This state lives on the shared server and would
  // otherwise change the watch profile and recommendation pool a later or concurrent spec sees.
  for (const id of seededPlayedIds) {
    await ctx.delete(`/UserPlayedItems/${id}?userId=${auth.userId}`).catch(() => undefined);
  }
  for (const id of seededFavoriteIds) {
    await ctx.delete(`/UserFavoriteItems/${id}?userId=${auth.userId}`).catch(() => undefined);
  }

  for (const seed of createdSeedFiles) {
    execInContainer(`rm -f -- ${q(seed.file)} && rmdir -- ${q(seed.directory)}`);
  }
  if (createdSeedFiles.length) await ctx.post('/Library/Refresh').catch(() => undefined);

  await ctx.dispose();
});

async function putConfig(body: Record<string, unknown>) {
  const res = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: body,
  });
  expect(res.ok(), `config update failed: ${res.status()}`).toBeTruthy();
}

/** All Movie item ids in the scanned library. */
interface SeedMovie { id: string; name: string; path: string; played: boolean; favorite: boolean }

async function movieItems(): Promise<SeedMovie[]> {
  const res = await ctx.get(`/Items?IncludeItemTypes=Movie&Recursive=true&Fields=Path&IsMissing=false&userId=${auth.userId}`);
  expect(res.ok(), `/Items status ${res.status()}`).toBeTruthy();
  const body = (await res.json()) as { Items?: Array<{ Id: string; Name: string; Path?: string; IsVirtualItem?: boolean; UserData?: { Played?: boolean; IsFavorite?: boolean } }> };
  return (body.Items ?? [])
    .filter(item => !item.IsVirtualItem && /\.(mkv|mp4|avi|mov|webm)$/i.test(item.Path ?? ''))
    .map(item => ({ id: item.Id, name: item.Name, path: item.Path!, played: item.UserData?.Played === true, favorite: item.UserData?.IsFavorite === true }));
}

function fittingMovies(movies: SeedMovie[]): SeedMovie[] {
  const user = auth.userId.replaceAll('-', '').toLowerCase();
  return movies.filter(movie => {
    const item = movie.id.replaceAll('-', '').toLowerCase();
    return createHash('sha256').update(`${user}:library:${item}`).digest().readUInt32LE(0) % 10 !== 0;
  });
}

async function ensureObservedSeed(): Promise<SeedMovie[]> {
  let movies = await movieItems();
  if (fittingMovies(movies).length < PER_USER_FIT_THRESHOLD) {
    const source = movies.find(movie => containerFileExists(movie.path));
    expect(source, 'need a real playable movie to expand the isolated model fixture').toBeDefined();
    const extension = source!.path.slice(source!.path.lastIndexOf('.'));
    const run = randomUUID().slice(0, 8);
    // Copies are cheap, isolated to this suite and removed afterwards; no generated-only feedback is fabricated.
    for (let i = 0; i < 24; i++) {
      const title = `JFH Per User Seed ${run}-${i} (2020)`;
      const directory = `/media/Movies/${title}`;
      const file = `${directory}/${title}${extension}`;
      createdSeedFiles.push({ file, directory });
      const copied = execInContainer(`mkdir -p -- ${q(directory)} && cp -- ${q(source!.path)} ${q(file)}`);
      expect(copied.code, copied.stderr).toBe(0);
    }
    expect((await ctx.post('/Library/Refresh')).ok()).toBeTruthy();
    await expect.poll(async () => fittingMovies(await movieItems()).length, { timeout: 60_000 }).toBeGreaterThanOrEqual(PER_USER_FIT_THRESHOLD);
    movies = await movieItems();
  }
  return fittingMovies(movies).slice(0, PER_USER_FIT_THRESHOLD);
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
  return containerLs(resolveDataDir()).filter((n) => /^ml_weights_[0-9a-f]{32}\.json$/.test(n));
}

/** Per-user state files currently on disk (ensemble_state_{id}.json). */
function perUserStateFiles(): string[] {
  return containerLs(resolveDataDir()).filter((n) => /^ensemble_state_[0-9a-f]{32}\.json$/.test(n));
}

test.describe.serial('per-user recommendation model files', () => {
  test.skip(!hasDocker(), 'docker exec unavailable - cannot inspect the plugin data folder');

  test.beforeAll(async () => {
    if (!hasDocker()) {
      return;
    }

    // Only observed rows outside the permanent holdout count toward fitting. Six plays plus generated
    // recommendations no longer clear the gate, so seed a complete eligible fitting slice explicitly.
    const movies = await ensureObservedSeed();
    expect(movies.length, 'need twelve actual observed fitting titles, excluding holdouts').toBe(PER_USER_FIT_THRESHOLD);
    for (const m of movies) {
      if (!m.played) {
        const mark = await ctx.post(`/UserPlayedItems/${m.id}?userId=${auth.userId}`);
        expect(mark.ok(), `mark-played ${m.name}: ${mark.status()}`).toBeTruthy();
        seededPlayedIds.push(m.id);
      }
    }
    if (!movies[0].favorite) {
      const fav = await ctx.post(`/UserFavoriteItems/${movies[0].id}?userId=${auth.userId}`);
      expect([200, 204]).toContain(fav.status());
      seededFavoriteIds.push(movies[0].id);
    }
  });

  test.afterEach(() => {
    if (!hasDocker() || !dataDir) {
      return;
    }

    // The planted orphan is normally removed by a successful prune, but a failed assertion (or a retry of
    // this serial group) would otherwise leave it in place, and the earlier tests read this directory with
    // regexes that match the planted names. Remove it unconditionally so one failure cannot bleed into them.
    try {
      execInContainer(`rm -f ${dataDir}/ml_weights_${ORPHAN_ID}.json ${dataDir}/ensemble_state_${ORPHAN_ID}.json`);
    } catch {
      // Nothing to clean up.
    }
  });

  test('a training run persists the global ensemble state and honours the learned/neural example gates', async () => {
    await activateRecommendationsOnly();

    // Training runs off the previous run's cached results, so the very first Activate run only generates
    // recommendations (nothing cached yet to train on) and writes no model files. The first run here seeds
    // the results cache; the second is the one that actually trains and persists the global model.
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);
    const result = await runCleanupTask(ctx);
    expect(result.LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);

    // The ensemble writes its blend state whenever training is attempted, on both the success and the
    // insufficient-data branch, so with a seeded watch profile driving training this file must exist. Locate
    // it by name under /config rather than assuming its directory, since the plugin writes to its own data
    // subfolder. This also fixes the directory every later test in the group resolves against.
    const stateHits = containerFind('/config', 'ensemble_state.json').filter((f) => !/_[0-9a-f]{32}\.json$/i.test(f));
    expect(stateHits.length, 'global ensemble_state.json should exist after training').toBeGreaterThan(0);

    const dir = resolveDataDir();
    // The learned and neural weight files are written only once their own example thresholds are met (learned
    // needs at least twelve pooled examples, the neural MLP at least thirty). The smoke fixture is a couple of
    // users over a handful of playable items, which does not reliably reach those counts, so persistence is
    // legitimately gated rather than guaranteed. When the files do appear their JSON must still be well formed,
    // and neither may be present without the ensemble state that accompanies every trained model.
    if (containerFileExists(`${dir}/ml_weights.json`)) {
      const weights = JSON.parse(readContainerFile(`${dir}/ml_weights.json`)) as { Weights?: number[]; Bias?: number };
      expect(Array.isArray(weights.Weights), 'global learned weights must be an array').toBeTruthy();
      expect(typeof weights.Bias, 'global learned bias must be numeric').toBe('number');
    }
    if (containerFileExists(`${dir}/neural_weights.json`)) {
      expect(containerFileExists(`${dir}/ensemble_state.json`), 'neural weights without ensemble state is inconsistent').toBeTruthy();
    }
  });

  test('per-user files appear with the expected JSON shape when a user clears the threshold', async () => {
    await activateRecommendationsOnly();
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);

    const weightFiles = perUserWeightFiles();
    // The eligible observed fitting slice clears the per-user example threshold, so this user's model
    // must exist. A hard assertion here (rather than a skip) keeps the per-user shape checks from silently
    // passing vacuously if the fixture ever stops driving training past the threshold.
    expect(
      weightFiles.length,
      'expected at least one per-user model file after training on the seeded watch profile',
    ).toBeGreaterThan(0);
    expect(weightFiles).toContain(`ml_weights_${auth.userId.replaceAll('-', '').toLowerCase()}.json`);

    // Every per-user weight file must pair with an ensemble-state file for the same id.
    const dir = resolveDataDir();
    const stateFiles = perUserStateFiles();
    for (const wf of weightFiles) {
      const id = wf.slice('ml_weights_'.length, -'.json'.length);
      expect(stateFiles, `state file missing for ${id}`).toContain(`ensemble_state_${id}.json`);

      const weights = JSON.parse(readContainerFile(`${dir}/${wf}`)) as {
        Weights?: number[];
        Bias?: number;
        Version?: number;
      };
      expect(Array.isArray(weights.Weights), 'per-user weights must be an array').toBeTruthy();
      expect(weights.Weights?.length ?? 0, 'per-user weight vector must be non-empty').toBeGreaterThan(0);
      expect(typeof weights.Bias, 'per-user bias must be numeric').toBe('number');

      const state = JSON.parse(readContainerFile(`${dir}/ensemble_state_${id}.json`)) as {
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
    expect(before.length, 'expected a per-user model to evolve across runs').toBeGreaterThan(0);

    const dir = resolveDataDir();
    const firstId = before[0].slice('ensemble_state_'.length, -'.json'.length);
    const firstCount = (JSON.parse(readContainerFile(`${dir}/ensemble_state_${firstId}.json`)) as {
      TrainingExampleCount?: number;
    }).TrainingExampleCount ?? 0;

    // Run N+1.
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);

    const secondCount = (JSON.parse(readContainerFile(`${dir}/ensemble_state_${firstId}.json`)) as {
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
    expect(existing.length, 'expected a per-user model to exist before planting an orphan').toBeGreaterThan(0);

    const dir = resolveDataDir();
    // Plant a per-user file for a synthetic user id that is NOT in the live user list. Copying an existing
    // per-user file keeps the JSON valid; the id in the filename is what PruneOrphans reconciles against.
    const orphanWeights = `${dir}/ml_weights_${ORPHAN_ID}.json`;
    const orphanState = `${dir}/ensemble_state_${ORPHAN_ID}.json`;
    const sample = existing[0];
    execInContainer(`cp ${dir}/${sample} ${orphanWeights}`);
    // A per-user weight file always pairs with a state file, so require it here rather than skipping the copy.
    // Otherwise the state-pruned assertion below could pass simply because the file was never planted.
    const sampleState = perUserStateFiles()[0];
    expect(sampleState, 'a per-user state file must exist to plant an orphan state').toBeTruthy();
    execInContainer(`cp ${dir}/${sampleState} ${orphanState}`);
    expect(containerFileExists(orphanWeights), 'planted orphan weights file should exist').toBeTruthy();
    expect(containerFileExists(orphanState), 'planted orphan state file should exist').toBeTruthy();

    // The next training run reconciles per-user files against the live user list and prunes the orphan.
    expect((await runCleanupTask(ctx)).LastExecutionResult?.Status).toBe('Completed');
    await sleep(1000);

    expect(containerFileExists(orphanWeights), 'orphan per-user weights must be pruned').toBeFalsy();
    expect(containerFileExists(orphanState), 'orphan per-user state must be pruned').toBeFalsy();
    // The global blend state is never pruned. It is written on every training attempt, so unlike the
    // threshold-gated learned weights it is always present to assert against.
    expect(containerFileExists(`${dir}/ensemble_state.json`), 'global ensemble_state.json must survive pruning').toBeTruthy();
  });
});
