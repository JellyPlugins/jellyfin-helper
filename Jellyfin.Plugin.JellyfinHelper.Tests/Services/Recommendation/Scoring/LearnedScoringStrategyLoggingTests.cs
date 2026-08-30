using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Covers the logger-guarded diagnostics in Train: the Information-level weight-reset message on the first standardized pass and the Debug-level feature-importance dump (including its skip branch when Debug is off).
/// </summary>
public sealed class LearnedScoringStrategyLoggingTests
{
    [Fact]
    public void Train_FirstStandardizedPass_LogsWeightResetAtInformation()
    {
        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath: null, logger: logger.Object);

        var count = Math.Max(LearnedScoringStrategy.MinTrainingExamples, LearnedScoringStrategy.MinExamplesForStandardization);
        Assert.True(strategy.Train(GenerateExamples(count)));

        VerifyLog(logger, LogLevel.Information, "Reset weights to defaults after standardization mode change");

        // The reset branch must have run (not merely logged): weights stay a full FeatureCount
        // vector, having been re-seeded from DefaultWeights before SGD refined them.
        Assert.Equal(CandidateFeatures.FeatureCount, strategy.GetCurrentWeights().Length);
    }

    [Fact]
    public void Train_WithDebugDisabledLogger_SkipsFeatureImportanceLogging()
    {
        var logger = TestMockFactory.CreateDisabledLogger();
        var strategy = new LearnedScoringStrategy(weightsPath: null, logger: logger.Object);

        var count = Math.Max(LearnedScoringStrategy.MinTrainingExamples, LearnedScoringStrategy.MinExamplesForStandardization);
        Assert.True(strategy.Train(GenerateExamples(count)));

        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never());
    }

    [Fact]
    public void Train_WithDebugEnabledLogger_LogsSortedFeatureWeights()
    {
        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath: null, logger: logger.Object);

        var count = Math.Max(LearnedScoringStrategy.MinTrainingExamples, LearnedScoringStrategy.MinExamplesForStandardization);
        Assert.True(strategy.Train(GenerateExamples(count)));

        VerifyLog(logger, LogLevel.Debug, "feature weights (sorted by |w|)");
        VerifyLog(logger, LogLevel.Debug, "GenreSimilarity=");
    }

    private static void VerifyLog(Mock<ILogger> logger, LogLevel level, string messagePart)
    {
        logger.Verify(
            l => l.Log(
                level,
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
