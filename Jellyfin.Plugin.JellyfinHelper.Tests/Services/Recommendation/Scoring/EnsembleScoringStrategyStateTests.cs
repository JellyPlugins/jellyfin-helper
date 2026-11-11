using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for ensemble state persistence and restoration in EnsembleScoringStrategy: the state-path derivation fallback, the load-time guards (size, null, schema mismatch, empty state, over-ceiling neural beta), the I/O and JSON error fallbacks, and the non-critical save-failure path.
/// </summary>
public sealed class EnsembleScoringStrategyStateTests : IDisposable
{
    private const double AlphaMin = EnsembleScoringStrategy.DefaultAlphaMin;

    private static readonly JsonSerializerOptions StateSerializerOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly string _tempDir;

    public EnsembleScoringStrategyStateTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jf-helper-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (DirectoryNotFoundException)
        {
            // best-effort cleanup - directory may already be gone
        }
        catch (IOException)
        {
            // best-effort cleanup - file may be locked on CI
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup - permission edge cases on CI
        }

        GC.SuppressFinalize(this);
    }

    private string WeightsPath => Path.Join(_tempDir, "ml_weights.json");

    private string StatePath => Path.Join(_tempDir, "ensemble_state.json");

    private EnsembleScoringStrategy BuildSut(Mock<ILogger>? logger = null, NeuralScoringStrategy? neural = null)
    {
        var learned = new LearnedScoringStrategy(WeightsPath);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        return new EnsembleScoringStrategy(learned, heuristic, neural, statePath: StatePath, logger: logger?.Object);
    }

    // A per-user ensemble shares the global neural by reference and passes ownsNeural:false, so its beta
    // restore takes the !_ownsNeural branch that accepts any in-range persisted beta without gating on the
    // (global) example count.
    private EnsembleScoringStrategy BuildPerUserSut(NeuralScoringStrategy neural)
    {
        var learned = new LearnedScoringStrategy(WeightsPath);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        return new EnsembleScoringStrategy(learned, heuristic, neural, statePath: StatePath, ownsNeural: false);
    }

    private static string SerializeState(object state) => JsonSerializer.Serialize(state, StateSerializerOptions);

    private static void VerifyWarningLogged(Mock<ILogger> logger)
    {
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void Constructor_WeightsPathWithoutDirectory_UsesCurrentDirectoryStatePath()
    {
        // A bare filename has no directory component, so DeriveStatePath falls back to "."
        // and the state file lands in the current working directory.
        var stateInCwd = Path.Join(".", "ensemble_state.json");
        var backup = File.Exists(stateInCwd) ? File.ReadAllText(stateInCwd) : null;
        var preexisting = backup is not null;
        try
        {
            var weightsFile = "jf-helper-bare-weights-" + Guid.NewGuid().ToString("N")[..8] + ".json";
            var ensemble = new EnsembleScoringStrategy(weightsFile);

            Assert.True(ensemble.Train(CleanExamples(30)));
            Assert.True(File.Exists(stateInCwd),
                "The '.' fallback must produce a writable state path in the current directory.");

            File.Delete(weightsFile);
        }
        finally
        {
            if (preexisting)
            {
                File.WriteAllText(stateInCwd, backup!);
            }
            else if (File.Exists(stateInCwd))
            {
                File.Delete(stateInCwd);
            }
        }
    }

    [Fact]
    public void Constructor_OversizedStateFile_SkipsLoadAndWarns()
    {
        // A file above the 10 MB ceiling must be skipped before any read into memory.
        using (var fs = new FileStream(StatePath, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength((10L * 1024 * 1024) + 1);
        }

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var sut = BuildSut(logger);

        Assert.Equal(AlphaMin, sut.CurrentAlpha, 4);
        Assert.Equal(0, sut.TrainingExampleCount);
        VerifyWarningLogged(logger);
    }

    [Fact]
    public void Constructor_StateFileLiteralNull_ReturnsWithoutApplying()
    {
        File.WriteAllText(StatePath, "null");

        var sut = BuildSut();

        Assert.Equal(AlphaMin, sut.CurrentAlpha, 4);
        Assert.Equal(0, sut.TrainingExampleCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(999)]
    public void Constructor_SchemaVersionMismatch_ResetsToDefaults(int schemaVersion)
    {
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = schemaVersion,
            TrainingExampleCount = 123,
            ObservedTrainingIds = new[] { Guid.NewGuid() },
            Alpha = 0.6,
            NeuralBeta = 0.0,
            QualityGateFrozen = false,
            SigmoidMidpointOffset = 0.0,
            UpdatedAt = "2026-01-01T00:00:00.0000000Z",
            MetricsHistory = Array.Empty<object>()
        }));

        var storedState = File.ReadAllText(StatePath);
        using var sut = BuildSut(logger);

        Assert.Equal(0, sut.TrainingExampleCount);
        Assert.Equal(0, sut.MetricsHistoryCount);
        Assert.Equal(0.0, sut.CurrentNeuralBeta);
        Assert.Equal(AlphaMin, sut.CurrentAlpha, 4);
        Assert.Equal(storedState, File.ReadAllText(StatePath));
        VerifyWarningLogged(logger);
    }

    [Fact]
    public void Constructor_EmptyState_RejectedOnLoad()
    {
        // Correct schema, but no training happened and no history -> the empty-state guard
        // returns early and nothing is restored.
        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = 0,
            Alpha = 0.7,
            NeuralBeta = 0.0,
            QualityGateFrozen = false,
            SigmoidMidpointOffset = 0.0,
            UpdatedAt = "2026-01-01T00:00:00.0000000Z",
            MetricsHistory = Array.Empty<object>()
        }));

        var sut = BuildSut();

        Assert.Equal(0, sut.MetricsHistoryCount);
        Assert.Equal(0, sut.TrainingExampleCount);
    }

    [Fact]
    public void Constructor_PersistedNeuralBetaAboveCeiling_DiscardedAndLogged()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = 100,
            Alpha = AlphaMin,
            NeuralBeta = EnsembleScoringStrategy.NeuralMaxBetaFraction + 0.2,
            QualityGateFrozen = false,
            SigmoidMidpointOffset = 0.0,
            UpdatedAt = "2026-01-01T00:00:00.0000000Z",
            MetricsHistory = Array.Empty<object>()
        }));

        using var neural = new NeuralScoringStrategy();
        Assert.True(neural.Train(CleanExamples(30)));
        using var sut = BuildSut(logger, neural);

        // An over-ceiling persisted beta is discarded so the ramp restarts from zero.
        Assert.Equal(0.0, sut.CurrentNeuralBeta);
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Constructor_PerUserInRangeNeuralBeta_RestoredVerbatim()
    {
        // A per-user ensemble (ownsNeural:false) does not own the shared MLP, so its persisted TrainingExampleCount
        // cannot gate the beta ramp. Any in-range persisted beta must restore exactly as stored; the next per-user
        // training run recomputes it from the real per-user example count.
        var beta = EnsembleScoringStrategy.NeuralMaxBetaFraction / 2.0;
        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = 5,
            Alpha = AlphaMin,
            NeuralBeta = beta,
            QualityGateFrozen = false,
            SigmoidMidpointOffset = 0.0,
            UpdatedAt = "2026-01-01T00:00:00.0000000Z",
            MetricsHistory = Array.Empty<object>()
        }));

        using var neural = new NeuralScoringStrategy();
        Assert.True(neural.Train(CleanExamples(30)));
        using var sut = BuildPerUserSut(neural);

        Assert.Equal(beta, sut.CurrentNeuralBeta, 6);
    }

    [Fact]
    public void Constructor_PerUserOutOfRangeNeuralBeta_ResetsToZero()
    {
        // An out-of-range persisted beta on a per-user model is not clamped but reset to zero so the ramp restarts
        // rather than restoring an impossible value.
        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = 5,
            Alpha = AlphaMin,
            NeuralBeta = EnsembleScoringStrategy.NeuralMaxBetaFraction + 0.5,
            QualityGateFrozen = false,
            SigmoidMidpointOffset = 0.0,
            UpdatedAt = "2026-01-01T00:00:00.0000000Z",
            MetricsHistory = Array.Empty<object>()
        }));

        using var neural = new NeuralScoringStrategy();
        Assert.True(neural.Train(CleanExamples(30)));
        using var sut = BuildPerUserSut(neural);

        Assert.Equal(0.0, sut.CurrentNeuralBeta);
    }

    [Fact]
    public void RestoreWithoutAnyNeuralStrategy_DoesNotRetainPersistedBeta()
    {
        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = 300,
            ObservedTrainingIds = Enumerable.Range(0, 300).Select(index => TrainingExample.CreateObservationId(Guid.Empty, "restore:" + index)).ToArray(),
            Alpha = 0.6,
            NeuralBeta = EnsembleScoringStrategy.NeuralMaxBetaFraction
        }));
        using var sut = new EnsembleScoringStrategy(new LearnedScoringStrategy(null), new HeuristicScoringStrategy(genrePenaltyFloor: 1.0), statePath: StatePath, ownsNeural: false);
        Assert.Equal(0.0, sut.CurrentNeuralBeta);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("missing", false)]
    [InlineData("corrupt", true)]
    [InlineData("corrupt", false)]
    [InlineData("version", true)]
    [InlineData("version", false)]
    [InlineData("shape", true)]
    [InlineData("shape", false)]
    [InlineData("valid", true)]
    [InlineData("valid", false)]
    public void RestoreBeta_RequiresUsableV5NeuralWeightsWithoutDiscardingValidLearnedWeights(string fileState, bool ownsNeural)
    {
        var neuralPath = Path.Join(_tempDir, "neural_weights.json");
        var examples = CleanExamples(300);
        var validation = CleanExamples(40, seed: 99);
        var learned = new LearnedScoringStrategy(WeightsPath);
        Assert.True(learned.Train(examples, validation));
        if (fileState is "version" or "shape" or "valid")
        {
            using var fitted = new NeuralScoringStrategy(neuralPath);
            Assert.True(fitted.Train(CleanExamples(30)));
            Assert.True(fitted.HasTrainedWeights);
            var payload = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(neuralPath))!;
            Assert.Equal(5, payload["Version"]!.GetValue<int>());
            if (fileState == "version") payload["Version"] = 999;
            if (fileState == "shape") payload["WeightsIH"] = new System.Text.Json.Nodes.JsonArray();
            File.WriteAllText(neuralPath, payload.ToJsonString());
        }
        if (fileState == "corrupt") File.WriteAllText(neuralPath, "not json {{{");
        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = examples.Count,
            ObservedTrainingIds = examples.Select(example => example.ObservationId).ToArray(),
            Alpha = 0.6,
            NeuralBeta = EnsembleScoringStrategy.NeuralMaxBetaFraction
        }));
        var persistedState = File.ReadAllText(StatePath);
        var persistedLearned = File.ReadAllText(WeightsPath);
        using var neural = new NeuralScoringStrategy(neuralPath);
        using var sut = new EnsembleScoringStrategy(new LearnedScoringStrategy(WeightsPath), new HeuristicScoringStrategy(genrePenaltyFloor: 1.0), neural, StatePath, ownsNeural: ownsNeural);
        using var fallback = BuildSut();

        Assert.Equal(fileState == "valid", neural.HasTrainedWeights);
        Assert.Equal(fileState == "valid" ? EnsembleScoringStrategy.NeuralMaxBetaFraction : 0.0, sut.CurrentNeuralBeta);
        Assert.Equal(0.6, sut.CurrentAlpha);
        Assert.Equal(examples.Count, sut.TrainingExampleCount);
        if (fileState != "valid")
        {
            Assert.All(validation, example =>
            {
                Assert.Equal(fallback.Score(example.Features), sut.Score(example.Features));
                Assert.Equal(fallback.ScoreWithExplanation(example.Features).FinalScore, sut.ScoreWithExplanation(example.Features).FinalScore);
            });
        }
        Assert.Equal(persistedState, File.ReadAllText(StatePath));
        Assert.Equal(persistedLearned, File.ReadAllText(WeightsPath));
    }

    [Fact]
    public void MissingNeuralWeights_RemainDisabledUntilSuccessfulTrainingPassesTheNormalQualityGate()
    {
        var examples = CleanExamples(300);
        var validation = CleanExamples(40, seed: 99);
        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = examples.Count,
            ObservedTrainingIds = examples.Select(example => example.ObservationId).ToArray(),
            Alpha = 0.6,
            NeuralBeta = EnsembleScoringStrategy.NeuralMaxBetaFraction
        }));
        using var neural = new NeuralScoringStrategy(Path.Join(_tempDir, "missing_neural.json"));
        using var sut = BuildSut(neural: neural);
        Assert.Equal(0.0, sut.CurrentNeuralBeta);
        Assert.False(neural.HasTrainedWeights);
        Assert.True(sut.Train(examples, validation, trainNeural: false));
        Assert.Equal(0.0, sut.CurrentNeuralBeta);
        Assert.False(neural.HasTrainedWeights);

        Assert.True(sut.Train(examples, validation));

        Assert.True(neural.HasTrainedWeights);
        Assert.True(TrainingMath.QualityFactor(neural.LastValidationLoss, neural.LastBaselineValidationLoss) > 0);
        Assert.True(sut.CurrentNeuralBeta > 0);
    }

    [Fact]
    public void Constructor_LockedStateFile_IOExceptionSwallowedWithWarning()
    {
        // Valid state, but the file is held with an exclusive lock so File.ReadAllText raises
        // an IOException (sharing violation) - the load must fall back to defaults gracefully.
        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = 100,
            Alpha = 0.6,
            NeuralBeta = 0.0,
            QualityGateFrozen = false,
            SigmoidMidpointOffset = 0.0,
            UpdatedAt = "2026-01-01T00:00:00.0000000Z",
            MetricsHistory = Array.Empty<object>()
        }));

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        using var locked = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Record.Exception(() => BuildSut(logger));

        Assert.Null(ex);
        VerifyWarningLogged(logger);
    }

    [Fact]
    public void Constructor_CorruptStateJson_JsonExceptionSwallowedWithWarning()
    {
        // Passes the exists/size guards but is not valid JSON -> JsonException catch.
        File.WriteAllText(StatePath, "not json {{{");

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        EnsembleScoringStrategy sut = null!;
        var ex = Record.Exception(() => sut = BuildSut(logger));

        Assert.Null(ex);
        Assert.Equal(AlphaMin, sut.CurrentAlpha, 4);
        VerifyWarningLogged(logger);
    }

    [Fact]
    public void Train_StatePathUnwritable_SaveIOExceptionSwallowed()
    {
        // A file occupies a path segment that the save needs to be a directory, so Directory.CreateDirectory / the atomic write raises IOException.
        var blockingFile = Path.Join(_tempDir, "blocker");
        File.WriteAllText(blockingFile, "x");
        var unwritableStatePath = Path.Join(blockingFile, "nested", "ensemble_state.json");

        var learned = new LearnedScoringStrategy(Path.Join(_tempDir, "uw_weights.json"));
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var sut = new EnsembleScoringStrategy(
            learned, heuristic, statePath: unwritableStatePath, logger: logger.Object);

        bool trained = false;
        var ex = Record.Exception(() => trained = sut.Train(CleanExamples(30)));

        Assert.Null(ex);
        Assert.True(trained, "Training must succeed even when persisting state fails");
        VerifyWarningLogged(logger);
    }

    [Fact]
    public void Train_ReadOnlyStateFile_SaveUnauthorizedAccessSwallowed()
    {
        // Force the save to fail in a way that is denied on BOTH Windows and Linux. A read-only file attribute is not a reliable cross-platform denial: on Linux, especially as root in CI, the write is not blocked and no warning would fire.
        Directory.CreateDirectory(StatePath);

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var sut = BuildSut(logger);

        bool trained = false;
        var ex = Record.Exception(() => trained = sut.Train(CleanExamples(30)));

        Assert.Null(ex);
        Assert.True(trained, "Training must succeed even when persisting state fails");
        VerifyWarningLogged(logger);
    }

    [Fact]
    public void Train_WeightsSaveFails_DoesNotWriteEnsembleState()
    {
        // If the learned weights cannot be persisted, the ensemble must not stamp a fresh last-trained time,
        // because that would leave the old weights on disk looking freshly trained and let the idle sweep keep
        // a model that was never saved. Point the learned weights at an unwritable path (a file occupies a
        // segment that must be a directory) while the state path is writable, then prove no state file appears.
        var blockingFile = Path.Join(_tempDir, "wblocker");
        File.WriteAllText(blockingFile, "x");
        var unwritableWeightsPath = Path.Join(blockingFile, "nested", "ml_weights.json");

        var learned = new LearnedScoringStrategy(unwritableWeightsPath);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var sut = new EnsembleScoringStrategy(learned, heuristic, statePath: StatePath, logger: logger.Object);
        using (sut)
        {
            var trained = sut.Train(CleanExamples(30));

            Assert.True(trained, "Training still succeeds even when the weights save fails");
            Assert.False(learned.LastWeightsSaveSucceeded, "The unwritable weights path must report a failed save");
            Assert.False(File.Exists(StatePath), "No ensemble state must be written when the weights were not persisted");
        }
    }

    [Fact]
    public void Train_WeightsSaveSucceeds_WritesEnsembleState()
    {
        // The positive counterpart: with a writable weights path the save succeeds and the ensemble state is
        // persisted, so the last-trained stamp is refreshed as normal.
        using var sut = BuildSut();

        var trained = sut.Train(CleanExamples(30));

        Assert.True(trained);
        Assert.True(File.Exists(StatePath), "Ensemble state must be written after a successful weights save");
    }

    [Fact]
    public void Reload_PreservesUniqueObservationMaturityWithoutCountingReplayAgain()
    {
        var examples = CleanExamples(60);
        var validation = CleanExamples(20, seed: 99);
        using (var first = BuildSut())
        {
            Assert.True(first.Train(examples, validation));
            Assert.Equal(60, first.TrainingExampleCount);
        }

        using var reloaded = BuildSut();
        Assert.Equal(60, reloaded.TrainingExampleCount);
        Assert.True(reloaded.Train(examples, validation));
        Assert.Equal(60, reloaded.TrainingExampleCount);
    }

    [Fact]
    public void InflatedPersistedCount_CannotActivateNeuralWithoutDistinctObservedIds()
    {
        var id = Guid.NewGuid();
        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = 10000,
            ObservedTrainingIds = new[] { id, id, Guid.Empty },
            Alpha = AlphaMin,
            NeuralBeta = EnsembleScoringStrategy.NeuralMaxBetaFraction,
            MetricsHistory = Array.Empty<object>()
        }));
        using var neural = new NeuralScoringStrategy();
        Assert.True(neural.Train(CleanExamples(30)));
        using var ensemble = BuildSut(neural: neural);

        Assert.Equal(1, ensemble.TrainingExampleCount);
        Assert.Equal(0.0, ensemble.CurrentNeuralBeta);
    }

    private static List<TrainingExample> CleanExamples(int count, int seed = 42)
    {
        var examples = new List<TrainingExample>(count);
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var positive = i % 2 == 0;
            examples.Add(new TrainingExample
            {
                HasStableObservationId = true,
                Features = new CandidateFeatures
                {
                    GenreSimilarity = positive ? 0.9 : 0.05,
                    CombinedCriticScore = positive ? 0.85 : 0.1,
                    RecencyScore = positive ? 0.7 : 0.2,
                    CollaborativeScore = rng.NextDouble() * 0.1,
                    GenreCount = positive ? 4 : 1
                },
                Label = positive ? 1.0 : 0.0
            });
        }

        return examples;
    }
}
