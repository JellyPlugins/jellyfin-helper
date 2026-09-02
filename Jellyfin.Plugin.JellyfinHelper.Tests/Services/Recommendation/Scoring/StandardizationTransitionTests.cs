using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for the Gap C standardization mode-flip warm-start. Crossing
///     <see cref="LearnedScoringStrategy" />'s MinExamplesForStandardization (20) used to hard-reset the
///     learned weights to defaults; it now rescales them into the new feature space so the decision function
///     is preserved as the warm start. These tests pin the observable properties of that transform.
/// </summary>
public sealed class StandardizationTransitionTests
{
    // A candidate the trained model should rank highly (high genre similarity) versus a poor one.
    private static CandidateFeatures StrongCandidate() => new()
    {
        GenreSimilarity = 0.95,
        CombinedCriticScore = 0.9,
        RecencyScore = 0.8,
        YearProximityScore = 0.8,
        GenreCount = 3
    };

    private static CandidateFeatures WeakCandidate() => new()
    {
        GenreSimilarity = 0.02,
        CombinedCriticScore = 0.1,
        RecencyScore = 0.1,
        YearProximityScore = 0.1,
        GenreCount = 1
    };

    // Examples where the label tracks genre similarity, so the model learns to prefer high-similarity items.
    private static List<TrainingExample> GenerateExamples(int count, int seed = 42)
    {
        var rng = new Random(seed);
        var examples = new List<TrainingExample>(count);
        for (var i = 0; i < count; i++)
        {
            var genreSim = rng.NextDouble();
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = genreSim,
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

    [Fact]
    public void CrossingStandardizationThreshold_ProducesFiniteWeightsAndScores()
    {
        var strategy = new LearnedScoringStrategy();

        // Below threshold: trains in raw feature space (no standardization yet).
        Assert.True(strategy.Train(GenerateExamples(19)));

        // Crossing the threshold triggers the raw -> standardized transition (previously a hard reset).
        Assert.True(strategy.Train(GenerateExamples(25)));

        Assert.All(strategy.GetCurrentWeights(), w => Assert.True(double.IsFinite(w), $"weight not finite: {w}"));
        Assert.True(double.IsFinite(strategy.Score(StrongCandidate())));
        Assert.True(double.IsFinite(strategy.Score(WeakCandidate())));
    }

    [Fact]
    public void WarmStart_PreservesLearnedRankingAcrossTransition()
    {
        var strategy = new LearnedScoringStrategy();
        Assert.True(strategy.Train(GenerateExamples(19)));
        Assert.True(strategy.Train(GenerateExamples(25)));

        // The learned preference (high genre similarity scores higher) must survive the mode change. A hard
        // reset to defaults could erase this; the warm-start rescale preserves the decision function.
        Assert.True(strategy.Score(StrongCandidate()) > strategy.Score(WeakCandidate()));
    }

    [Fact]
    public void CrossingThreshold_LogsWarmStartRescale_NotReset()
    {
        // Discriminates the warm-start wiring from the old hard-reset: the transition MUST log the rescale
        // message and MUST NOT log the reset message. Guards against the rescale method silently becoming
        // dead code again (the transition block failing to call it).
        var logger = TestFixtures.TestMockFactory.CreateLogger();
        var strategy = new LearnedScoringStrategy(weightsPath: null, logger: logger.Object);

        Assert.True(strategy.Train(GenerateExamples(25)));

        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Information,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("warm start")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce());

        logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Information,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Reset weights to defaults")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never());
    }

    [Fact]
    public void ConstantFeature_ZeroVariance_DoesNotProduceNaN()
    {
        // A feature with zero variance across all examples yields stdDev 0; the rescale must skip it (mirror
        // StandardizeSingleVector's 1e-8 guard) rather than divide by zero.
        var strategy = new LearnedScoringStrategy();
        var examples = GenerateExamples(25);
        foreach (var e in examples)
        {
            e.Features.RecencyScore = 0.5; // constant across the whole set
        }

        Assert.True(strategy.Train(examples));
        Assert.All(strategy.GetCurrentWeights(), w => Assert.True(double.IsFinite(w)));
        Assert.True(double.IsFinite(strategy.Score(StrongCandidate())));
    }

    [Fact]
    public void BelowThresholdRetrain_NoTransition_IsDeterministic()
    {
        // Two runs that both stay below the standardization threshold never flip modes, so a fixed
        // candidate's score is reproducible from the same seed (no spurious reset/rescale in between).
        var a = new LearnedScoringStrategy();
        Assert.True(a.Train(GenerateExamples(19, seed: 7)));
        var scoreA = a.Score(StrongCandidate());

        var b = new LearnedScoringStrategy();
        Assert.True(b.Train(GenerateExamples(19, seed: 7)));
        var scoreB = b.Score(StrongCandidate());

        Assert.Equal(scoreA, scoreB, 12);
    }

    [Fact]
    public void FeatureMeans_NullBeforeStandardizedTraining_PopulatedAfter()
    {
        var strategy = new LearnedScoringStrategy();

        // No standardized training yet -> no persisted means.
        Assert.Null(strategy.GetFeatureMeans());

        // Training at/above the standardization threshold persists the per-feature means.
        Assert.True(strategy.Train(GenerateExamples(25)));

        var means = strategy.GetFeatureMeans();
        Assert.NotNull(means);
        Assert.Equal(CandidateFeatures.FeatureCount, means!.Count);
        Assert.All(means, m => Assert.True(double.IsFinite(m)));
    }

    [Fact]
    public void StandardizedToRawTransition_ReverseRescale_ProducesFiniteWeights()
    {
        // Train above the threshold (standardized space, means persisted), then retrain below it so the
        // mode flips back to raw. This exercises the reverse rescale direction (w = w'/sigma, b -= sum(w*mu)),
        // which the raw->standardized tests do not reach.
        var strategy = new LearnedScoringStrategy();
        Assert.True(strategy.Train(GenerateExamples(25)));
        Assert.NotNull(strategy.GetFeatureMeans());

        Assert.True(strategy.Train(GenerateExamples(15, seed: 11)));

        // Back in raw space: means are cleared and weights/scores stay finite (no divide-by-zero blow-up).
        Assert.Null(strategy.GetFeatureMeans());
        Assert.All(strategy.GetCurrentWeights(), w => Assert.True(double.IsFinite(w), $"weight not finite: {w}"));
        Assert.True(double.IsFinite(strategy.Score(StrongCandidate())));
        Assert.True(double.IsFinite(strategy.Score(WeakCandidate())));
    }
}
