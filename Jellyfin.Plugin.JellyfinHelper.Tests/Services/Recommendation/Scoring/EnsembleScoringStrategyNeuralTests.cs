using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for the neural sub-strategy integration in <see cref="EnsembleScoringStrategy"/>:
///     the injected-instance getter, the beta ramp past the activation threshold, the
///     poor-quality and failed-training decay branches, and disposal of the composed neural.
/// </summary>
public sealed class EnsembleScoringStrategyNeuralTests
{
    // Cleanly separable data so both learned and neural generalize well and beta ramps.
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

    private static EnsembleScoringStrategy BuildActivatedEnsemble(NeuralScoringStrategy neural)
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);
        ensemble.Train(CleanExamples(120));
        ensemble.Train(CleanExamples(120));
        return ensemble;
    }

    [Fact]
    public void NeuralStrategy_WhenInjected_ExposesSameInstance()
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy();

        var withNeural = new EnsembleScoringStrategy(learned, heuristic, neural);
        Assert.Same(neural, withNeural.NeuralStrategy);

        // The convenience ctor wires no neural strategy.
        var withoutNeural = new EnsembleScoringStrategy();
        Assert.Null(withoutNeural.NeuralStrategy);
    }

    [Fact]
    public void Train_NeuralQualityGood_RampsBetaAfterActivationThreshold()
    {
        var neural = new NeuralScoringStrategy();
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        // First round crosses the 75-example activation threshold.
        ensemble.Train(CleanExamples(120));
        var betaFirst = ensemble.CurrentNeuralBeta;
        Assert.True(betaFirst > 0, $"Beta should activate past the threshold, was {betaFirst:F4}");

        // More cumulative examples advance the linear ramp -> beta increases (never exceeds the cap).
        ensemble.Train(CleanExamples(120));
        var betaSecond = ensemble.CurrentNeuralBeta;

        Assert.True(betaSecond >= betaFirst,
            $"Beta should ramp up with more data: {betaFirst:F4} -> {betaSecond:F4}");
        Assert.InRange(betaSecond, 0.0, EnsembleScoringStrategy.NeuralMaxBetaFraction);
    }

    [Fact]
    public void Train_NeuralFailsToTrainWhileLearnedSucceeds_DecaysBeta()
    {
        var neural = new NeuralScoringStrategy();
        var ensemble = BuildActivatedEnsemble(neural);
        var betaBefore = ensemble.CurrentNeuralBeta;
        Assert.True(betaBefore > 0, $"Neural beta must have activated, was {betaBefore:F4}");

        // 8 examples: >= LearnedScoringStrategy.MinTrainingExamples (5) so learned trains,
        // but < NeuralScoringStrategy.MinTrainingExamples (12) so the neural fails to train.
        // With beta > 0 this hits the learned-success / neural-fail decay branch.
        Assert.True(ensemble.Train(CleanExamples(8)));

        var betaAfter = ensemble.CurrentNeuralBeta;
        Assert.True(betaAfter < betaBefore,
            $"Neural-fail round must decay beta: {betaBefore:F4} -> {betaAfter:F4}");

        // Either a clean halving, or a snap to zero when the halved value is below the floor.
        var halved = betaBefore * 0.5;
        var expected = halved < EnsembleScoringStrategy.NeuralBetaMinFloor ? 0.0 : halved;
        Assert.Equal(expected, betaAfter, 6);
    }

    [Fact]
    public void Dispose_WithNeuralStrategy_DisposesNeural()
    {
        var neural = new NeuralScoringStrategy();
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        ensemble.Dispose();

        // Disposal is idempotent - a second call must not throw.
        var second = Record.Exception(() => ensemble.Dispose());
        Assert.Null(second);

        // The composed neural was disposed; it degrades to the neutral baseline instead of throwing.
        Assert.Equal(0.5, neural.Score(new CandidateFeatures()), 10);
    }

    [Fact]
    public void Train_LearnedFailsWithActiveBeta_DecaysBetaToZeroAndCapsHistory()
    {
        var neural = new NeuralScoringStrategy();
        var ensemble = BuildActivatedEnsemble(neural);
        var betaStart = ensemble.CurrentNeuralBeta;
        Assert.True(betaStart > 0, $"Neural beta must have activated, was {betaStart:F4}");

        // Fewer than LearnedScoringStrategy.MinTrainingExamples (5) so learned training FAILS
        // every round, driving the cold-start else-branch that halves beta and records a
        // placeholder snapshot. Fifteen rounds is enough to both cross the zero-floor and
        // overflow the 10-row history cap.
        var previousBeta = betaStart;
        var sawStrictDecrease = false;
        for (var round = 0; round < 15; round++)
        {
            Assert.False(ensemble.Train(CleanExamples(4)),
                "Learned training must fail with < 5 examples");

            var current = ensemble.CurrentNeuralBeta;
            if (previousBeta > 0 && current > 0)
            {
                Assert.True(current < previousBeta,
                    $"Beta must strictly halve while above the floor: {previousBeta:F5} -> {current:F5}");
                sawStrictDecrease = true;
            }

            previousBeta = current;
        }

        Assert.True(sawStrictDecrease, "Beta should have strictly decreased before snapping to zero");
        Assert.Equal(0.0, ensemble.CurrentNeuralBeta);
        Assert.Equal(10, ensemble.MetricsHistoryCount);
    }

    [Fact]
    public void Train_NeuralFailsRepeatedly_SnapsBetaToZeroBelowFloor()
    {
        var neural = new NeuralScoringStrategy();
        var ensemble = BuildActivatedEnsemble(neural);
        var betaStart = ensemble.CurrentNeuralBeta;
        Assert.True(betaStart > 0, $"Neural beta must have activated, was {betaStart:F4}");

        // 8 examples: >= 5 so learned keeps succeeding, but < 12 so the neural fails to train.
        // Each learned-success/neural-fail round halves beta; once a halved value drops below
        // NeuralBetaMinFloor the branch must snap it to exactly 0.0 rather than leaving a ghost.
        for (var round = 0; round < 12; round++)
        {
            Assert.True(ensemble.Train(CleanExamples(8)),
                "Learned training must succeed with >= 5 examples");

            var beta = ensemble.CurrentNeuralBeta;
            Assert.True(beta == 0.0 || beta >= EnsembleScoringStrategy.NeuralBetaMinFloor,
                $"Beta must never linger between zero and the floor, was {beta:F6}");
        }

        Assert.Equal(0.0, ensemble.CurrentNeuralBeta);
    }
}
