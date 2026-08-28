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

    private static string SerializeState(object state)
    {
        var options = new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        return JsonSerializer.Serialize(state, options);
    }

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
        var stateInCwd = Path.Combine(".", "ensemble_state.json");
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

    [Fact]
    public void Constructor_SchemaVersionMismatch_ResetsToDefaults()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = 999,
            TrainingExampleCount = 123,
            Alpha = 0.6,
            NeuralBeta = 0.0,
            QualityGateFrozen = false,
            SigmoidMidpointOffset = 0.0,
            UpdatedAt = "2026-01-01T00:00:00.0000000Z",
            MetricsHistory = Array.Empty<object>()
        }));

        var sut = BuildSut(logger);

        Assert.Equal(0, sut.TrainingExampleCount);
        Assert.Equal(AlphaMin, sut.CurrentAlpha, 4);
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

        var neural = new NeuralScoringStrategy();
        var sut = BuildSut(logger, neural);

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

    private static List<TrainingExample> CleanExamples(int count, int seed = 42)
    {
        var examples = new List<TrainingExample>(count);
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var positive = i % 2 == 0;
            examples.Add(new TrainingExample
            {
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
