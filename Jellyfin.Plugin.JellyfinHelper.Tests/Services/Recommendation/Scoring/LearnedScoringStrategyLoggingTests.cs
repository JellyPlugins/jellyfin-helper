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
        // A fresh strategy has _featureMeans == null. Training >= MinExamplesForStandardization examples flips useStandardization true, so the standardization mode changes and the model must reset to defaults - and say so at Information level.
        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath: null, logger: logger.Object);

        Assert.True(strategy.Train(GenerateExamples(LearnedScoringStrategy.MinExamplesForStandardization)));

        VerifyLog(logger, LogLevel.Information, "Reset weights to defaults after standardization mode change");

        // The reset branch must have run (not merely logged): weights stay a full FeatureCount
        // vector, having been re-seeded from DefaultWeights before SGD refined them.
        Assert.Equal(CandidateFeatures.FeatureCount, strategy.GetCurrentWeights().Length);
    }

    [Fact]
    public void Train_WithDebugDisabledLogger_SkipsFeatureImportanceLogging()
    {
        // IsEnabled(Debug) == false must short-circuit LogFeatureImportance before any Debug Log call - the guard exists precisely to avoid building the ranked string when nobody will read it.
        var logger = TestMockFactory.CreateDisabledLogger();
        var strategy = new LearnedScoringStrategy(weightsPath: null, logger: logger.Object);

        Assert.True(strategy.Train(GenerateExamples(LearnedScoringStrategy.MinExamplesForStandardization)));

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
        // With Debug enabled the importance dump must emit the ranked, |w|-sorted list. Asserting both the header and a concrete FeatureIndex name proves the list was actually built and formatted (not just an empty placeholder).
        var logger = TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath: null, logger: logger.Object);

        Assert.True(strategy.Train(GenerateExamples(LearnedScoringStrategy.MinExamplesForStandardization)));

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
