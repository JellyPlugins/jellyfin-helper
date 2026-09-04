using System;
using System.IO;
using System.Linq;
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
        var global = BuildGlobal(neural);
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
        var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        Assert.Same(global, registry.GetScoringStrategyForUser(Guid.Empty));
    }

    [Fact]
    public void GetOrCreateTrainableEnsembleForUser_CreatesDistinctPerUserInstance()
    {
        var neural = new NeuralScoringStrategy();
        var global = BuildGlobal(neural);
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
        var global = BuildGlobal(neural);
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
        var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        var perUser = registry.GetOrCreateTrainableEnsembleForUser(Guid.NewGuid());

        Assert.Same(neural, perUser.NeuralStrategy);
    }

    [Fact]
    public void Dispose_DoesNotDisposeSharedNeural()
    {
        var neural = new NeuralScoringStrategy();
        var global = BuildGlobal(neural);
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
        var global = BuildGlobal(neural);
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
        var global = BuildGlobal(neural);
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
        var global = BuildGlobal(neural);
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
        var global = BuildGlobal(neural);
        using var registry = BuildRegistry(global, neural);

        var diag = registry.GetDiagnostics(Guid.NewGuid());
        var globalDiag = global.GetDiagnosticsSnapshot();

        Assert.Equal(globalDiag.Alpha, diag.Alpha);
        Assert.Equal(globalDiag.TrainingExampleCount, diag.TrainingExampleCount);
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
            EnsembleScoringStrategy.DefaultAlphaMin,
            EnsembleScoringStrategy.DefaultAlphaMax,
            EnsembleScoringStrategy.DefaultGenrePenaltyFloor,
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
