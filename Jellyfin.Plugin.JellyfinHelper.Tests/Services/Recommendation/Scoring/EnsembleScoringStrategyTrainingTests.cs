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

    private static double ExpectedQualityFactor(double validationLoss)
    {
        if (double.IsNaN(validationLoss))
        {
            return 0.5;
        }

        return Math.Clamp(
            1.0 - ((validationLoss - EnsembleScoringStrategy.ValidationLossThreshold)
                   / (EnsembleScoringStrategy.ValidationLossCeiling - EnsembleScoringStrategy.ValidationLossThreshold)),
            0.0,
            1.0);
    }

    [Fact]
    public void Train_ValidationLossAboveThreshold_SoftDampensAlphaProportionally()
    {
        // Seek a deterministic noisy dataset whose learned validation loss lands in the soft-dampening band (threshold, ceiling).
        for (var seed = 1; seed <= 60; seed++)
        {
            var ensemble = new EnsembleScoringStrategy();
            Assert.True(ensemble.Train(NoisyExamples(60, seed)));
            var loss = ensemble.LearnedStrategy.LastValidationLoss;

            if (double.IsNaN(loss)
                || loss <= EnsembleScoringStrategy.ValidationLossThreshold
                || loss >= EnsembleScoringStrategy.ValidationLossCeiling)
            {
                continue;
            }

            var qualityFactor = ExpectedQualityFactor(loss);
            var sigmoidAlpha = EnsembleScoringStrategy.ComputeSigmoidAlpha(
                ensemble.TrainingExampleCount, AlphaMin, AlphaMax);
            var expectedAlpha = AlphaMin + ((sigmoidAlpha - AlphaMin) * qualityFactor);

            Assert.Equal(expectedAlpha, ensemble.CurrentAlpha, 4);
            // In-band loss keeps qualityFactor >= 0.01, so the gate is dampened, not frozen.
            Assert.False(ensemble.IsQualityGateFrozen);
            return;
        }

        Assert.Fail("No noisy seed produced a validation loss in the soft-dampening band.");
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
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Training complete", StringComparison.Ordinal)),
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
        ensemble.Train(CleanExamples(120));
        ensemble.Train(CleanExamples(120));
        var betaBefore = ensemble.CurrentNeuralBeta;
        Assert.True(betaBefore > 0, $"Neural beta must have activated, was {betaBefore:F4}");

        // Feed rounds of increasingly noisy data so validation loss rises across snapshots,
        // eventually producing a Degrading trend that decays beta.
        double betaAfter = betaBefore;
        for (var round = 0; round < 8; round++)
        {
            ensemble.Train(NoisyExamples(60, seed: 100 + round));
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
}
