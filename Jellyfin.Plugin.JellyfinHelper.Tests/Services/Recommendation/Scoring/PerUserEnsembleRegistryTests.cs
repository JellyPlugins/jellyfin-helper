using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="PerUserEnsembleRegistry"/>: cold-start fallback to the global instance, lazy
///     per-user materialization, warm-start, the shared-neural-not-disposed invariant, orphan pruning, and
///     schema-version invalidation of per-user files. Uses a real temp directory so the persistence paths
///     exercised match production exactly.
/// </summary>
public sealed class PerUserEnsembleRegistryTests : IDisposable
{
    private readonly string _dataPath;
    private readonly Mock<IPluginLogService> _pluginLog = new();

    public PerUserEnsembleRegistryTests()
    {
        _dataPath = Path.Join(Path.GetTempPath(), "jfh-peruser-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void GetScoringStrategyForUser_ColdStartUser_ReturnsExactGlobalInstance()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        // No per-user weights file exists, so a fresh user must fall back to the SAME global instance
        // (reference identity) - that is what guarantees byte-identical cold-start scores.
        var resolved = registry.GetScoringStrategyForUser(Guid.NewGuid());

        Assert.Same(global, resolved);
    }

    [Fact]
    public void GetScoringStrategyForUser_EmptyUserId_ReturnsGlobal()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        Assert.Same(global, registry.GetScoringStrategyForUser(Guid.Empty));
    }

    [Fact]
    public void GetOrCreateTrainableEnsembleForUser_CreatesDistinctPerUserInstance()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);

        Assert.NotSame(global, perUser);
        // Same user resolves to the same cached instance.
        Assert.Same(perUser, registry.GetOrCreateTrainableEnsembleForUser(userId));
        // The per-user heuristic keeps the genre-penalty-disabled invariant (ctor would have thrown otherwise).
        Assert.Equal(1.0, perUser.HeuristicStrategy.GenrePenaltyFloor);
    }

    [Fact]
    public void GetOrCreateTrainableEnsembleForUser_WarmStartsFromGlobalWeights()
    {
        using var neural = new NeuralScoringStrategy();
        // Train the global learned model so it has non-default weights + standardization stats to seed from.
        using var global = BuildGlobal(neural);
        Assert.True(global.LearnedStrategy.Train(GenerateExamples(60)));

        using var registry = BuildRegistry(global, neural);
        var perUser = registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());

        // Freshly created per-user learned weights equal the global fit (warm start), not cold defaults.
        Assert.Equal(global.LearnedStrategy.GetCurrentWeights(), perUser.LearnedStrategy.GetCurrentWeights());
        Assert.Equal(global.LearnedStrategy.GetFeatureMeans(), perUser.LearnedStrategy.GetFeatureMeans());
    }

    [Fact]
    public void PerUserEnsemble_SharesGlobalNeuralByReference()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());

        Assert.Same(neural, perUser.NeuralStrategy);
    }

    [Fact]
    public void Dispose_DoesNotDisposeSharedNeural()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        var registry = BuildRegistry(global, neural);
        registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());

        registry.Dispose();

        // The shared neural must survive the registry disposing its per-user ensembles - scoring still works
        // (a disposed ReaderWriterLockSlim would throw ObjectDisposedException here).
        var score = neural.Score(new CandidateFeatures { GenreSimilarity = 0.5 });
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void HasPerUserModel_TrueOnlyAfterFileExists()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        Assert.False(registry.HasPerUserModel(userId));

        // Creating + training a per-user ensemble persists ml_weights_{id}.json.
        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.LearnedStrategy.Train(GenerateExamples(30)));

        Assert.True(registry.HasPerUserModel(userId));
        Assert.False(registry.HasPerUserModel(Guid.Empty));
    }

    [Fact]
    public void PruneOrphans_DeletesFilesForRemovedUsersOnly()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        var keep = Guid.NewGuid();
        var remove = Guid.NewGuid();
        foreach (var id in new[] { keep, remove })
        {
            var e = registry.GetOrCreateTrainableEnsembleForUser(id);
            Assert.True(e.LearnedStrategy.Train(GenerateExamples(30)));
        }

        Assert.True(File.Exists(Path.Join(_dataPath, $"ml_weights_{remove:N}.json")));

        registry.PruneOrphans([keep]);

        Assert.True(File.Exists(Path.Join(_dataPath, $"ml_weights_{keep:N}.json")));
        Assert.False(File.Exists(Path.Join(_dataPath, $"ml_weights_{remove:N}.json")));
        Assert.False(File.Exists(Path.Join(_dataPath, $"ensemble_state_{remove:N}.json")));
    }

    [Fact]
    public void PruneOrphans_LeavesGlobalFilesUntouched()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        // Plant the unsuffixed global files - the per-user globs must never match these.
        var globalWeights = Path.Join(_dataPath, "ml_weights.json");
        var globalState = Path.Join(_dataPath, "ensemble_state.json");
        File.WriteAllText(globalWeights, "{}");
        File.WriteAllText(globalState, "{}");

        registry.PruneOrphans([Guid.NewGuid()]);

        Assert.True(File.Exists(globalWeights));
        Assert.True(File.Exists(globalState));
    }

    [Fact]
    public void PruneOrphans_MalformedPerUserFilename_LeftUntouched()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        // A file that matches the per-user glob but whose embedded id is not a hex GUID must fail the id parse
        // and be skipped rather than throwing or being deleted.
        var malformed = Path.Join(_dataPath, "ml_weights_notahexid.json");
        File.WriteAllText(malformed, "{}");

        var ex = Record.Exception(() => registry.PruneOrphans([Guid.NewGuid()]));

        Assert.Null(ex);
        Assert.True(File.Exists(malformed));
    }

    [Fact]
    public void GetScoringStrategyForUser_WeightsFileDeletedAfterExistsCheck_FallsBackToGlobalAndDoesNotCache()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        // Materialize + train a per-user model so its weights file exists on disk and the user resolves to a
        // distinct per-user instance.
        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.LearnedStrategy.Train(GenerateExamples(30)));
        var weightsFile = Path.Join(_dataPath, $"ml_weights_{userId:N}.json");
        Assert.True(File.Exists(weightsFile));

        // Simulate the eviction race: the weights file is deleted directly on disk, and the session-cached entry
        // is dropped so the read path re-checks the file. With the file gone the stray entry must be discarded and
        // the user falls back to the exact global instance rather than lingering on a per-user model.
        File.Delete(weightsFile);
        Assert.True(registry.EvictPerUserModel(userId));

        var resolved = registry.GetScoringStrategyForUser(userId);

        Assert.Same(global, resolved);
        // The fallback must not have re-cached a per-user model for this user.
        Assert.False(registry.HasPerUserModel(userId));
    }

    [Fact]
    public void GetDiagnostics_ColdStartUser_ReturnsGlobalSnapshot()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        var diag = registry.GetDiagnostics(Guid.NewGuid());
        var globalDiag = global.GetDiagnosticsSnapshot();

        Assert.Equal(globalDiag.Alpha, diag.Alpha);
        Assert.Equal(globalDiag.TrainingExampleCount, diag.TrainingExampleCount);
    }

    [Fact]
    public void GetOrCreateTrainableEnsembleForUser_ConcurrentSameUser_BuildsExactlyOneInstance()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        // Fire many concurrent GetOrCreate calls for one user. The registry stores a Lazy per user, so the
        // ensemble must be built exactly once and every caller must observe the same reference. Before the
        // Lazy guard, a racing GetOrAdd could build several throw-away ensembles and hand different ones out.
        const int concurrency = 16;
        var results = new EnsembleScoringStrategy[concurrency];
        Parallel.For(0, concurrency, i => results[i] = registry.GetOrCreateTrainableEnsembleForUser(userId));

        var first = results[0];
        Assert.NotSame(global, first);
        foreach (var resolved in results)
        {
            Assert.Same(first, resolved);
        }
    }

    [Fact]
    public void PerUserEnsembles_ShareSingleHeuristicInstance()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        var a = registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());
        var b = registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());

        // One stateless genre-penalty-disabled heuristic is shared across every per-user ensemble rather than
        // each ensemble constructing its own copy - distinct ensembles must reference the same instance.
        Assert.Same(a.HeuristicStrategy, b.HeuristicStrategy);
    }

    [Fact]
    public void GetUserModelDiagnostics_ColdStartThenTrained_FlipsIsPerUserAtomically()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        // Cold-start: no per-user file, so the snapshot must be the global fallback flagged IsPerUser=false.
        var (coldDiag, coldIsPerUser) = registry.GetUserModelDiagnostics(userId);
        Assert.False(coldIsPerUser);
        Assert.Equal(global.GetDiagnosticsSnapshot().Alpha, coldDiag.Alpha);

        // Creating + training a per-user model persists ml_weights_{id}.json.
        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.LearnedStrategy.Train(GenerateExamples(30)));

        // The snapshot and the per-user flag are resolved together, so once the model exists the flag flips.
        var (warmDiag, warmIsPerUser) = registry.GetUserModelDiagnostics(userId);
        Assert.True(warmIsPerUser);
        Assert.Equal(perUser.GetDiagnosticsSnapshot().Alpha, warmDiag.Alpha);
    }

    [Fact]
    public void Reconfigure_UpdatesAlreadyMaterializedPerUserEnsembleBounds()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        // Materialize a per-user ensemble, which is built from the registry's current blend bounds
        // (AlphaMax defaults to EnsembleScoringStrategy.DefaultAlphaMax).
        var perUser = registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());
        Assert.Equal(EnsembleScoringStrategy.DefaultAlphaMax, perUser.GetDiagnosticsSnapshot().AlphaMax);

        // A configuration change reconfigures the registry, which must push the new bounds onto the already
        // built per-user ensemble rather than leaving it on its construction-time bounds until a restart.
        registry.Reconfigure(new EnsembleBlendBounds(0.2, 0.6, 0.25));

        Assert.Equal(0.6, perUser.GetDiagnosticsSnapshot().AlphaMax);
        Assert.Equal(0.2, perUser.GetDiagnosticsSnapshot().AlphaMin);
    }

    [Fact]
    public void PerUserEnsemble_WarmStartedFromGlobal_ComputesAlphaFromOwnExampleCount()
    {
        using var neural = new NeuralScoringStrategy();
        // Train the global on a large set so it carries a high example count and a high blend alpha.
        using var global = BuildGlobal(neural);
        Assert.True(global.Train(GenerateExamples(60)));
        var globalDiag = global.GetDiagnosticsSnapshot();

        using var registry = BuildRegistry(global, neural);
        var perUser = registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());

        // Warm-start seeds the learned weights but NOT the blend state. Training the per-user ensemble on a
        // small set must compute its own alpha from ITS example count (15), not inherit the global's higher
        // blend confidence from 60 examples.
        Assert.True(perUser.Train(GenerateExamples(15)));
        var perUserDiag = perUser.GetDiagnosticsSnapshot();

        Assert.Equal(15, perUserDiag.TrainingExampleCount);
        Assert.NotEqual(globalDiag.TrainingExampleCount, perUserDiag.TrainingExampleCount);

        // Alpha grows monotonically with the example count via the sigmoid, so the data-poor user's alpha must
        // not exceed the data-rich global's alpha. This is the invariant the SeedFrom-does-not-copy-blend fix protects.
        Assert.True(
            perUserDiag.Alpha <= globalDiag.Alpha,
            $"per-user alpha ({perUserDiag.Alpha}) must not exceed global alpha ({globalDiag.Alpha})");
    }

    [Fact]
    public void EvictPerUserModel_ExistingModel_DeletesFilesAndFallsBackToGlobal()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.LearnedStrategy.Train(GenerateExamples(30)));
        Assert.True(File.Exists(Path.Join(_dataPath, $"ml_weights_{userId:N}.json")));
        Assert.NotSame(global, registry.GetScoringStrategyForUser(userId));

        var evicted = registry.EvictPerUserModel(userId);

        Assert.True(evicted);
        Assert.False(File.Exists(Path.Join(_dataPath, $"ml_weights_{userId:N}.json")));
        Assert.False(File.Exists(Path.Join(_dataPath, $"ensemble_state_{userId:N}.json")));
        Assert.False(registry.HasPerUserModel(userId));
        // After eviction the user scores on the shared global instance again (reference identity).
        Assert.Same(global, registry.GetScoringStrategyForUser(userId));
    }

    [Fact]
    public void EvictPerUserModel_NoModel_ReturnsFalseAndLeavesGlobalUntouched()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        Assert.False(registry.EvictPerUserModel(Guid.NewGuid()));
        Assert.False(registry.EvictPerUserModel(Guid.Empty));
    }

    [Fact]
    public void EvictStaleModels_ModelOlderThanCutoff_IsEvictedAndFallsBackToGlobal()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.Train(GenerateExamples(30)));
        Assert.True(File.Exists(Path.Join(_dataPath, $"ml_weights_{userId:N}.json")));

        // Pin the last-trained time to a fixed point in the past so the sweep is deterministic and does not
        // depend on the wall clock at test time.
        PinLastTrained(userId, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var evicted = registry.EvictStaleModels(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, evicted);
        Assert.False(File.Exists(Path.Join(_dataPath, $"ml_weights_{userId:N}.json")));
        Assert.False(File.Exists(Path.Join(_dataPath, $"ensemble_state_{userId:N}.json")));
        Assert.Same(global, registry.GetScoringStrategyForUser(userId));
    }

    [Fact]
    public void EvictStaleModels_ModelNewerThanCutoff_IsKept()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.Train(GenerateExamples(30)));

        // A user trained after the cutoff has an up-to-date model and must survive the sweep untouched, which
        // is what stops a single quiet cycle from evicting a still-active user.
        PinLastTrained(userId, new DateTime(2020, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var evicted = registry.EvictStaleModels(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, evicted);
        Assert.True(File.Exists(Path.Join(_dataPath, $"ml_weights_{userId:N}.json")));
        Assert.True(registry.HasPerUserModel(userId));
    }

    [Fact]
    public void EvictStaleModels_NoModels_ReturnsZero()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        Assert.Equal(0, registry.EvictStaleModels(DateTime.UtcNow));
    }

    [Fact]
    public void EvictStaleModels_MalformedPerUserFilename_LeftUntouchedAndNotCounted()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        // A file matching the weights glob but carrying a non-hex id fails the id parse, so the sweep must skip
        // it rather than throwing or counting it as an eviction.
        var malformed = Path.Join(_dataPath, "ml_weights_notahexid.json");
        File.WriteAllText(malformed, "{}");

        var evicted = registry.EvictStaleModels(DateTime.UtcNow);

        Assert.Equal(0, evicted);
        Assert.True(File.Exists(malformed));
    }

    [Fact]
    public void EvictStaleModels_StateFileWithoutWeights_RemovedButNotCounted()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        // A state file with no companion weights file is leftover metadata: the read path keys on the weights
        // file, so it can never rebuild a model from this alone. The weights walk never sees this id and orphan
        // pruning keeps files for live users, so the sweep must clean it up on its own. It is not a retired
        // model, so it must not be counted.
        var orphanState = Path.Join(_dataPath, $"ensemble_state_{userId:N}.json");
        File.WriteAllText(orphanState, "{}");

        var evicted = registry.EvictStaleModels(DateTime.UtcNow);

        Assert.Equal(0, evicted);
        Assert.False(File.Exists(orphanState));
    }

    [Fact]
    public void EvictStaleModels_StateFileWithWeights_KeptDuringOrphanSweep()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        // The counterpart to the orphan case: a state file whose weights file still exists belongs to a real
        // model that is fresh, so the orphan sweep must leave both in place. Train the user and stamp the state
        // after the cutoff so the age check keeps it, then confirm neither file is removed.
        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.Train(GenerateExamples(30)));
        var weightsFile = Path.Join(_dataPath, $"ml_weights_{userId:N}.json");
        var stateFile = Path.Join(_dataPath, $"ensemble_state_{userId:N}.json");
        PinLastTrained(userId, new DateTime(2020, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var evicted = registry.EvictStaleModels(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, evicted);
        Assert.True(File.Exists(weightsFile));
        Assert.True(File.Exists(stateFile));
    }

    [Fact]
    public void EvictStaleModels_StateFileMissing_UsesWeightsFileWriteTime()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.Train(GenerateExamples(30)));
        var weightsFile = Path.Join(_dataPath, $"ml_weights_{userId:N}.json");

        // With no state file the age decision falls back to the weights file's own write time. Delete the state
        // file and stamp the weights file well before the cutoff so the fallback drives an eviction.
        File.Delete(Path.Join(_dataPath, $"ensemble_state_{userId:N}.json"));
        File.SetLastWriteTimeUtc(weightsFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var evicted = registry.EvictStaleModels(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, evicted);
        Assert.False(File.Exists(weightsFile));
    }

    [Fact]
    public void EvictStaleModels_StateFileWriteTimeAfterCutoff_KeepsModel()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.Train(GenerateExamples(30)));
        var weightsFile = Path.Join(_dataPath, $"ml_weights_{userId:N}.json");

        // Symmetric to the missing-state eviction: with the weights file stamped after the cutoff the fallback
        // keeps the model, proving the write-time branch drives both outcomes.
        File.Delete(Path.Join(_dataPath, $"ensemble_state_{userId:N}.json"));
        File.SetLastWriteTimeUtc(weightsFile, new DateTime(2020, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var evicted = registry.EvictStaleModels(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, evicted);
        Assert.True(File.Exists(weightsFile));
    }

    [Fact]
    public void EvictStaleModels_UnparseableStateStamp_FallsBackToWeightsFileWriteTime()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.Train(GenerateExamples(30)));
        var weightsFile = Path.Join(_dataPath, $"ml_weights_{userId:N}.json");

        // A state file whose UpdatedAt cannot be parsed must not be trusted; the sweep falls back to the weights
        // file write time, so an old weights file is still evicted.
        SetStateStamp(userId, "not-a-timestamp");
        File.SetLastWriteTimeUtc(weightsFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var evicted = registry.EvictStaleModels(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, evicted);
        Assert.False(File.Exists(weightsFile));
    }

    [Fact]
    public void EvictStaleModels_CorruptStateJson_DoesNotThrowAndFallsBackToWeightsFileWriteTime()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);
        var userId = Guid.NewGuid();

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(userId);
        Assert.True(perUser.Train(GenerateExamples(30)));
        var weightsFile = Path.Join(_dataPath, $"ml_weights_{userId:N}.json");

        // A state file that is not valid JSON must be swallowed and the sweep must still complete off the weights
        // file write time rather than aborting the whole pass.
        File.WriteAllText(Path.Join(_dataPath, $"ensemble_state_{userId:N}.json"), "not json {{{");
        File.SetLastWriteTimeUtc(weightsFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var evicted = 0;
        var ex = Record.Exception(() => evicted = registry.EvictStaleModels(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Null(ex);
        Assert.Equal(1, evicted);
    }

    [Fact]
    public void EvictStaleModels_NoDataPath_ReturnsZero()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        // A registry with no data path cannot enumerate weight files, so the sweep is a no-op returning zero.
        using var registry = new PerUserEnsembleRegistry(
            global,
            neural,
            dataPath: null,
            new EnsembleBlendBounds(
                EnsembleScoringStrategy.DefaultAlphaMin,
                EnsembleScoringStrategy.DefaultAlphaMax,
                EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
            _pluginLog.Object);

        Assert.Equal(0, registry.EvictStaleModels(DateTime.UtcNow));
    }

    // Rewrites the persisted UpdatedAt stamp so the age sweep reads a fixed last-trained time instead of the
    // now-stamp a real training save would write.
    private void PinLastTrained(Guid userId, DateTime lastTrainedUtc) =>
        SetStateStamp(userId, lastTrainedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    // Writes a raw UpdatedAt value into the persisted state so tests can exercise both valid stamps and the
    // unparseable-stamp fallback path.
    private void SetStateStamp(Guid userId, string rawStamp)
    {
        var statePath = Path.Join(_dataPath, $"ensemble_state_{userId:N}.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!;
        node["UpdatedAt"] = rawStamp;
        File.WriteAllText(statePath, node.ToJsonString());
    }

    [Fact]
    public void Reconfigure_ThenBuild_UsesTheNewBoundsForTheFreshEnsemble()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        // Reconfigure before any per-user ensemble is materialized, then build one: it must pick up the new
        // bounds at construction, not the registry's original construction-time bounds.
        registry.Reconfigure(new EnsembleBlendBounds(0.15, 0.55, 0.3));
        var perUser = registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());
        var diag = perUser.GetDiagnosticsSnapshot();

        Assert.Equal(0.15, diag.AlphaMin);
        Assert.Equal(0.55, diag.AlphaMax);
    }

    [Fact]
    public void EvictStaleModels_EnumerateThrows_SwallowedAndReturnsZero()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);

        // The eviction sweep enumerates the data folder; an unexpected file-system failure there must be
        // swallowed so a single broken sweep never brings down the training run. Drive that outer catch by
        // making enumeration throw while the folder still reports as existing.
        var directory = new Mock<IDirectory>();
        directory.Setup(d => d.Exists(_dataPath)).Returns(true);
        directory.Setup(d => d.EnumerateFiles(_dataPath, "ml_weights_*.json"))
            .Throws(new IOException("enumeration failed"));
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(new FileSystem().File);

        using var registry = BuildRegistryWithFileSystem(global, neural, fileSystem.Object);

        Assert.Equal(0, registry.EvictStaleModels(DateTime.UtcNow));
    }

    [Fact]
    public void EvictStaleModels_LastTrainedTimeUnreadable_SkipsWithoutEvicting()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        var userId = Guid.NewGuid();
        var weightsFile = Path.Join(_dataPath, $"ml_weights_{userId:N}.json");

        // With no readable state stamp the sweep falls back to the weights file write time. If even that read
        // fails the model has no trustworthy age, and deleting it would throw away a possibly healthy model on
        // a momentarily unreadable file system. Enumerate the one weights file, report no state file, and make
        // the write-time read throw; the model must be left in place and never deleted.
        var directory = new Mock<IDirectory>();
        directory.Setup(d => d.Exists(_dataPath)).Returns(true);
        directory.Setup(d => d.EnumerateFiles(_dataPath, "ml_weights_*.json")).Returns([weightsFile]);
        directory.Setup(d => d.EnumerateFiles(_dataPath, "ensemble_state_*.json")).Returns([]);
        var statePath = Path.Join(_dataPath, $"ensemble_state_{userId:N}.json");
        var file = new Mock<IFile>();
        file.Setup(f => f.Exists(weightsFile)).Returns(true);
        file.Setup(f => f.Exists(statePath)).Returns(false);
        file.Setup(f => f.GetLastWriteTimeUtc(weightsFile)).Throws(new IOException("stat failed"));
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);

        using var registry = BuildRegistryWithFileSystem(global, neural, fileSystem.Object);

        var evicted = registry.EvictStaleModels(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, evicted);
        file.Verify(f => f.Delete(weightsFile), Times.Never);
    }

    [Fact]
    public void EvictStaleModels_WeightsDeleteFails_NotCountedAsEvicted()
    {
        using var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        var userId = Guid.NewGuid();
        var weightsFile = Path.Join(_dataPath, $"ml_weights_{userId:N}.json");
        var statePath = Path.Join(_dataPath, $"ensemble_state_{userId:N}.json");

        // A stale model whose weights file cannot be deleted is not actually retired: the read path can rebuild
        // it. The delete throws and the file stays present, so the sweep must swallow the failure and count zero
        // rather than logging a retirement that did not happen.
        var directory = new Mock<IDirectory>();
        directory.Setup(d => d.Exists(_dataPath)).Returns(true);
        directory.Setup(d => d.EnumerateFiles(_dataPath, "ml_weights_*.json")).Returns([weightsFile]);
        directory.Setup(d => d.EnumerateFiles(_dataPath, "ensemble_state_*.json")).Returns([]);
        var file = new Mock<IFile>();
        file.Setup(f => f.Exists(weightsFile)).Returns(true);
        file.Setup(f => f.Exists(statePath)).Returns(false);
        file.Setup(f => f.Delete(weightsFile)).Throws(new UnauthorizedAccessException("locked"));
        file.Setup(f => f.GetLastWriteTimeUtc(weightsFile)).Returns(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);

        using var registry = BuildRegistryWithFileSystem(global, neural, fileSystem.Object);

        var evicted = registry.EvictStaleModels(new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, evicted);
    }

    private EnsembleScoringStrategy BuildGlobal(NeuralScoringStrategy neural) =>
        new(
            new LearnedScoringStrategy(Path.Join(_dataPath, "ml_weights.json")),
            new HeuristicScoringStrategy(genrePenaltyFloor: 1.0),
            neural,
            Path.Join(_dataPath, "ensemble_state.json"));

    private PerUserEnsembleRegistry BuildRegistry(EnsembleScoringStrategy global, NeuralScoringStrategy neural) =>
        new(
            global,
            neural,
            _dataPath,
            new EnsembleBlendBounds(
                EnsembleScoringStrategy.DefaultAlphaMin,
                EnsembleScoringStrategy.DefaultAlphaMax,
                EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
            _pluginLog.Object);

    private PerUserEnsembleRegistry BuildRegistryWithFileSystem(
        EnsembleScoringStrategy global, NeuralScoringStrategy neural, IFileSystem fileSystem) =>
        new(
            global,
            neural,
            _dataPath,
            new EnsembleBlendBounds(
                EnsembleScoringStrategy.DefaultAlphaMin,
                EnsembleScoringStrategy.DefaultAlphaMax,
                EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
            _pluginLog.Object,
            fileSystem: fileSystem);

    private static System.Collections.Generic.List<TrainingExample> GenerateExamples(int count)
    {
        var rng = new Random(7);
        var examples = new System.Collections.Generic.List<TrainingExample>(count);
        for (var i = 0; i < count; i++)
        {
            var genreSim = rng.NextDouble();
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = genreSim,
                    CombinedCriticScore = rng.NextDouble(),
                    RecencyScore = rng.NextDouble()
                },
                Label = genreSim > 0.5 ? 1.0 : 0.0
            });
        }

        return examples;
    }
}
