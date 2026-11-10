using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for the training-driven alpha/beta schedules in EnsembleScoringStrategy: the validation-loss quality gate (full progression, soft dampening, freeze), the neural trend decay, and the training-complete diagnostics log.
/// </summary>
public sealed class EnsembleScoringStrategyTrainingTests
{
    private const double AlphaMin = EnsembleScoringStrategy.DefaultAlphaMin;
    private const double AlphaMax = EnsembleScoringStrategy.DefaultAlphaMax;

    [Fact]
    public void ColdKFoldBaseline_UniformFoldDoesNotPoisonOtherObservedEvidence()
    {
        var examples = Enumerable.Range(0, 60).Select(i => new TrainingExample
        {
            Features = new CandidateFeatures { GenreSimilarity = i < 2 ? 0.1 : 0.9 },
            Label = i < 2 ? 0.1 : 0.9
        }).ToArray();
        var learned = new LearnedScoringStrategy();
        Assert.True(learned.Train(examples));
        Assert.True(double.IsFinite(learned.LastBaselineValidationLoss));
        Assert.True(double.IsFinite(learned.LastValidationLoss));
        var uniform = new LearnedScoringStrategy();
        Assert.True(uniform.Train(examples.Select(example => new TrainingExample { Features = example.Features.Copy(), Label = 0.9 }).ToArray()));
        Assert.True(double.IsNaN(uniform.LastBaselineValidationLoss));
    }

    // Cleanly separable data -> low validation loss (passes the quality gate).
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

    // Labels uncorrelated with features -> the model cannot separate, so validation loss
    // lands well above the quality-gate threshold (drives the dampening/freeze branches).
    private static List<TrainingExample> NoisyExamples(int count, int seed = 7)
    {
        var examples = new List<TrainingExample>(count);
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = rng.NextDouble(),
                    CombinedCriticScore = rng.NextDouble(),
                    RecencyScore = rng.NextDouble(),
                    CollaborativeScore = rng.NextDouble(),
                    GenreCount = rng.Next(1, 6)
                },
                // Random label with no relationship to the features above.
                Label = rng.NextDouble() < 0.5 ? 0.0 : 1.0
            });
        }

        return examples;
    }

    [Fact]
    public void Train_PartialImprovementOverIndependentBaseline_DampensAlphaProportionally()
    {
        using var ensemble = new EnsembleScoringStrategy();
        var holdout = CleanExamples(40, seed: 99);
        foreach (var example in holdout) example.Label = example.Label > 0.5 ? 0.8 : 0.2;
        Assert.True(ensemble.Train(CleanExamples(60), holdout));
        var loss = ensemble.LearnedStrategy.LastValidationLoss;
        var baseline = ensemble.LearnedStrategy.LastBaselineValidationLoss;
        var qualityFactor = Math.Clamp(1.0 - loss / baseline, 0.0, 1.0);
        var sigmoidAlpha = EnsembleScoringStrategy.ComputeSigmoidAlpha(ensemble.TrainingExampleCount, AlphaMin, AlphaMax);
        Assert.True(qualityFactor is > 0 and < 1);
        Assert.Equal(AlphaMin + ((sigmoidAlpha - AlphaMin) * qualityFactor), ensemble.CurrentAlpha, 10);
        Assert.False(ensemble.IsQualityGateFrozen);
    }

    [Fact]
    public void Train_WithInformationLogger_EmitsTrainingCompleteLog()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, logger: logger.Object);

        Assert.True(ensemble.Train(CleanExamples(30)));

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString()!.Contains("Training complete", StringComparison.Ordinal)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Train_DegradingTrend_DecaysNeuralBetaTowardZero()
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy();
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        // Ramp beta above zero with clean data past the activation threshold.
        ensemble.Train(CleanExamples(160), CleanExamples(40, seed: 99));
        ensemble.Train(CleanExamples(160), CleanExamples(40, seed: 99));
        var betaBefore = ensemble.CurrentNeuralBeta;
        Assert.True(betaBefore > 0, $"Neural beta must have activated, was {betaBefore:F4}");

        // Feed rounds of increasingly noisy data so validation loss rises across snapshots,
        // eventually producing a Degrading trend that decays beta.
        double betaAfter = betaBefore;
        for (var round = 0; round < 8; round++)
        {
            ensemble.Train(NoisyExamples(60, seed: 100 + round), NoisyExamples(40, seed: 2000 + round));
            betaAfter = ensemble.CurrentNeuralBeta;
            if (ensemble.LastTrend == EnsembleScoringStrategy.MetricsTrend.Degrading)
            {
                break;
            }
        }

        Assert.Equal(EnsembleScoringStrategy.MetricsTrend.Degrading, ensemble.LastTrend);
        Assert.True(betaAfter < betaBefore,
            $"Degrading trend must decay neural beta: before={betaBefore:F4}, after={betaAfter:F4}");
        Assert.InRange(betaAfter, 0.0, betaBefore);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void EnsembleScore_5000Candidates_CompletesWithin2Seconds()
    {
        using var ensemble = new EnsembleScoringStrategy();
        var rng = new Random(42);
        var features = new List<CandidateFeatures>(5000);
        for (var i = 0; i < 5000; i++)
        {
            features.Add(new CandidateFeatures
            {
                GenreSimilarity = rng.NextDouble(),
                CombinedCriticScore = rng.NextDouble(),
                RecencyScore = rng.NextDouble(),
                CollaborativeScore = rng.NextDouble(),
                GenreCount = rng.Next(1, 6)
            });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sum = 0.0;
        foreach (var f in features)
        {
            sum += ensemble.Score(f);
        }

        sw.Stop();

        Assert.True(double.IsFinite(sum));
        if (Environment.GetEnvironmentVariable("RUN_PERF_ASSERTS") == "1")
        {
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Took {sw.ElapsedMilliseconds}ms");
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void EnsembleTrain_2000Examples_CompletesWithin15Seconds()
    {
        using var ensemble = new EnsembleScoringStrategy();
        var examples = CleanExamples(2000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(ensemble.Train(examples));
        sw.Stop();

        if (Environment.GetEnvironmentVariable("RUN_PERF_ASSERTS") == "1")
        {
            Assert.True(sw.ElapsedMilliseconds < 15000, $"Took {sw.ElapsedMilliseconds}ms");
        }
    }
}
