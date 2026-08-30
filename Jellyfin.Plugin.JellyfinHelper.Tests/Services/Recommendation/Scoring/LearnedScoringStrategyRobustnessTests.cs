using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Load/save robustness for LearnedScoringStrategy: oversized-file guard, corrupt/NaN weights, mismatched or versioned standardization stats, and I/O failures on both the read and the write path.
/// </summary>
public sealed class LearnedScoringStrategyRobustnessTests : IDisposable
{
    private readonly string _tempDir;

    public LearnedScoringStrategyRobustnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"learned_robust_{Guid.NewGuid():N}");
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
    }

    [Fact]
    public void TryLoadWeights_OversizedFile_SkipsLoadAndKeepsDefaults()
    {
        // A weights file over the 5 MB ceiling must not be read into memory at all - the guard exists to stop a corrupted/replaced huge file from becoming a DoS.
        var weightsPath = Path.Join(_tempDir, "oversized.json");
        using (var fs = new FileStream(weightsPath, FileMode.CreateNew))
        {
            fs.SetLength((5 * 1024 * 1024) + 1);
        }

        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath, logger.Object);

        Assert.Equal(DefaultWeights.CreateWeightArray(), strategy.GetCurrentWeights());
        VerifyWarning(logger, "Skipping load");
    }

    [Fact]
    public void TryLoadWeights_NaNInWeights_DiscardsAndResetsToDefaults()
    {
        // A structurally-valid file whose first weight is corrupted to NaN must never load into the model - a NaN weight would silently poison every score.
        var weightsPath = Path.Join(_tempDir, "nan_weight.json");
        var seed = new LearnedScoringStrategy(weightsPath);
        Assert.True(seed.Train(GenerateExamples(25)));

        var json = File.ReadAllText(weightsPath);
        var corrupted = System.Text.RegularExpressions.Regex.Replace(
            json,
            @"""Weights""\s*:\s*\[([^,\]]+)",
            m => m.Value.Replace(m.Groups[1].Value, "NaN"),
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(2));
        File.WriteAllText(weightsPath, corrupted);

        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath, logger.Object);

        Assert.Equal(DefaultWeights.CreateWeightArray(), strategy.GetCurrentWeights());
        var score = strategy.Score(new CandidateFeatures { GenreSimilarity = 0.7 });
        Assert.True(double.IsFinite(score));
        Assert.InRange(score, 0.0, 1.0);
        VerifyWarning(logger, "LearnedScoringStrategy");
    }

    [Fact]
    public void TryLoadWeights_MeansPresentButStdDevsNull_DiscardsWeightsAndStats()
    {
        // Standardization stats must be all-or-nothing: means present but stddevs null means the persisted weights (trained in standardized space) cannot be safely reused.
        var weightsPath = Path.Join(_tempDir, "half_stats.json");
        var data = new LearnedScoringStrategy.WeightsData
        {
            Weights = DefaultWeights.CreateWeightArray(),
            Bias = DefaultWeights.Bias,
            FeatureMeans = new double[CandidateFeatures.FeatureCount],
            FeatureStdDevs = null,
            TrainingGeneration = 7,
            Version = LearnedScoringStrategy.CurrentWeightsVersion
        };
        File.WriteAllText(weightsPath, JsonSerializer.Serialize(data));

        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath, logger.Object);

        Assert.Equal(DefaultWeights.CreateWeightArray(), strategy.GetCurrentWeights());
        VerifyWarning(logger, "mismatched standardization stats");
    }

    [Fact]
    public void TryLoadWeights_VersionMismatch_DiscardsPersistedWeights()
    {
        // A file from an older schema version must be discarded even if its weight array is the
        // right length - the version bump signals incompatible weight semantics.
        var weightsPath = Path.Join(_tempDir, "old_version.json");
        var data = new LearnedScoringStrategy.WeightsData
        {
            Weights = DefaultWeights.CreateWeightArray(),
            Bias = 0.99,
            TrainingGeneration = 3,
            Version = LearnedScoringStrategy.CurrentWeightsVersion - 1
        };
        File.WriteAllText(weightsPath, JsonSerializer.Serialize(data));

        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath, logger.Object);

        Assert.Equal(DefaultWeights.CreateWeightArray(), strategy.GetCurrentWeights());
        VerifyWarning(logger, "Discarding persisted weights");
    }

    [Fact]
    public void TryLoadWeights_IoErrorReadingFile_FallsBackToDefaultsAndLogs()
    {
        // The file exists but is held with an exclusive lock, so File.ReadAllText throws inside
        // the try. Construction must swallow it, log a warning, and keep default weights.
        var weightsPath = Path.Join(_tempDir, "locked.json");
        File.WriteAllText(weightsPath, "{}");

        var logger = TestMockFactory.CreateLogger();
        using (new FileStream(weightsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var strategy = new LearnedScoringStrategy(weightsPath, logger.Object);

            Assert.Equal(DefaultWeights.CreateWeightArray(), strategy.GetCurrentWeights());
            var score = strategy.Score(new CandidateFeatures { GenreSimilarity = 0.5 });
            Assert.InRange(score, 0.0, 1.0);
        }

        VerifyWarning(logger, "Failed to load weights");
    }

    [Fact]
    public void TrySaveWeights_ParentPathIsAFile_LogsIoErrorWithoutThrowing()
    {
        // Point the weights path under an existing regular file so Directory.CreateDirectory(dir) faults (cannot create a directory beneath a file).
        var blockingFile = Path.Join(_tempDir, "blocker");
        File.WriteAllText(blockingFile, "x");
        var weightsPath = Path.Join(blockingFile, "weights.json");

        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath, logger.Object);

        Assert.True(strategy.Train(GenerateExamples(25)));
        VerifyWarning(logger, "Failed to save weights");
    }

    [Fact]
    public void TryLoadWeights_InfinityInWeights_DiscardsAndResetsToDefaults()
    {
        // A structurally valid file (right length + version) whose first weight is an out-of-range magnitude deserializes to +Infinity rather than throwing a parse error.
        var weightsPath = Path.Join(_tempDir, "inf_weight.json");
        var seed = new LearnedScoringStrategy(weightsPath);
        Assert.True(seed.Train(GenerateExamples(25)));

        var json = File.ReadAllText(weightsPath);
        var corrupted = System.Text.RegularExpressions.Regex.Replace(
            json,
            @"""Weights""\s*:\s*\[([^,\]]+)",
            m => m.Value.Replace(m.Groups[1].Value, "1e400"),
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(2));
        File.WriteAllText(weightsPath, corrupted);

        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath, logger.Object);

        Assert.Equal(DefaultWeights.CreateWeightArray(), strategy.GetCurrentWeights());
        var score = strategy.Score(new CandidateFeatures { GenreSimilarity = 0.7 });
        Assert.True(double.IsFinite(score));
        Assert.InRange(score, 0.0, 1.0);
        VerifyWarning(logger, "NaN/Infinity");
    }

    [Fact]
    public void TrySaveWeights_InvalidPathCharacters_LogsWithoutThrowing()
    {
        // A weights path with a NUL in a directory component survives Path.GetDirectoryName but makes Directory.CreateDirectory throw ArgumentException.
        var weightsPath = Path.Join(_tempDir, "ab\0cd", "weights.json");

        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath, logger.Object);

        Assert.True(strategy.Train(GenerateExamples(25)));
        VerifyWarning(logger, "invalid path");
    }

    private static void VerifyWarning(Mock<ILogger> logger, string messagePart)
    {
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePart)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce());
    }

    private static List<TrainingExample> GenerateExamples(int count)
    {
        var rng = new Random(42);
        var examples = new List<TrainingExample>(count);
        for (var i = 0; i < count; i++)
        {
            var genreSim = rng.NextDouble();
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = genreSim,
                    CollaborativeScore = rng.NextDouble(),
                    CombinedCriticScore = rng.NextDouble(),
                    RecencyScore = rng.NextDouble(),
                    YearProximityScore = rng.NextDouble(),
                    GenreCount = rng.Next(0, 6),
                    IsSeries = rng.NextDouble() > 0.5
                },
                Label = genreSim > 0.5 ? 1.0 : 0.0
            });
        }

        return examples;
    }
}
