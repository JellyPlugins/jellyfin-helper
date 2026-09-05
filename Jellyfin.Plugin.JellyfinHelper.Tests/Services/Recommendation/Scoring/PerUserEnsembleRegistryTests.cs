using System;
using System.IO;
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
        _dataPath = Path.Combine(Path.GetTempPath(), "jfh-peruser-tests-" + Guid.NewGuid().ToString("N"));
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
        var neural = new NeuralScoringStrategy();
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
        var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        Assert.Same(global, registry.GetScoringStrategyForUser(Guid.Empty));
    }

    [Fact]
    public void GetOrCreateTrainableEnsembleForUser_CreatesDistinctPerUserInstance()
    {
        var neural = new NeuralScoringStrategy();
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
        var neural = new NeuralScoringStrategy();
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
        var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());

        Assert.Same(neural, perUser.NeuralStrategy);
    }

    [Fact]
    public void Dispose_DoesNotDisposeSharedNeural()
    {
        var neural = new NeuralScoringStrategy();
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
        var neural = new NeuralScoringStrategy();
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
        var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        var keep = Guid.NewGuid();
        var remove = Guid.NewGuid();
        foreach (var id in new[] { keep, remove })
        {
            var e = registry.GetOrCreateTrainableEnsembleForUser(id);
            Assert.True(e.LearnedStrategy.Train(GenerateExamples(30)));
        }

        Assert.True(File.Exists(Path.Combine(_dataPath, $"ml_weights_{remove:N}.json")));

        registry.PruneOrphans([keep]);

        Assert.True(File.Exists(Path.Combine(_dataPath, $"ml_weights_{keep:N}.json")));
        Assert.False(File.Exists(Path.Combine(_dataPath, $"ml_weights_{remove:N}.json")));
        Assert.False(File.Exists(Path.Combine(_dataPath, $"ensemble_state_{remove:N}.json")));
    }

    [Fact]
    public void PruneOrphans_LeavesGlobalFilesUntouched()
    {
        var neural = new NeuralScoringStrategy();
        using var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        // Plant the unsuffixed global files - the per-user globs must never match these.
        var globalWeights = Path.Combine(_dataPath, "ml_weights.json");
        var globalState = Path.Combine(_dataPath, "ensemble_state.json");
        File.WriteAllText(globalWeights, "{}");
        File.WriteAllText(globalState, "{}");

        registry.PruneOrphans([Guid.NewGuid()]);

        Assert.True(File.Exists(globalWeights));
        Assert.True(File.Exists(globalState));
    }

    [Fact]
    public void GetDiagnostics_ColdStartUser_ReturnsGlobalSnapshot()
    {
        var neural = new NeuralScoringStrategy();
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
        var neural = new NeuralScoringStrategy();
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
        var neural = new NeuralScoringStrategy();
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
        var neural = new NeuralScoringStrategy();
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
        var neural = new NeuralScoringStrategy();
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
        Assert.True(File.Exists(Path.Combine(_dataPath, $"ml_weights_{userId:N}.json")));
        Assert.NotSame(global, registry.GetScoringStrategyForUser(userId));

        var evicted = registry.EvictPerUserModel(userId);

        Assert.True(evicted);
        Assert.False(File.Exists(Path.Combine(_dataPath, $"ml_weights_{userId:N}.json")));
        Assert.False(File.Exists(Path.Combine(_dataPath, $"ensemble_state_{userId:N}.json")));
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

    private EnsembleScoringStrategy BuildGlobal(NeuralScoringStrategy neural) =>
        new(
            new LearnedScoringStrategy(Path.Combine(_dataPath, "ml_weights.json")),
            new HeuristicScoringStrategy(genrePenaltyFloor: 1.0),
            neural,
            Path.Combine(_dataPath, "ensemble_state.json"));

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
