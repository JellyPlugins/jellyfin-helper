using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for the neural sub-strategy integration in EnsembleScoringStrategy: the injected-instance getter, the beta ramp past the activation threshold, the poor-quality and failed-training decay branches, and disposal of the composed neural.
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
        ensemble.Train(CleanExamples(160));
        ensemble.Train(CleanExamples(160));
        return ensemble;
    }

    [Fact]
    public void NeuralStrategy_WhenInjected_ExposesSameInstance()
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy();

        using var withNeural = new EnsembleScoringStrategy(learned, heuristic, neural);
        Assert.Same(neural, withNeural.NeuralStrategy);

        // The convenience ctor wires no neural strategy.
        using var withoutNeural = new EnsembleScoringStrategy();
        Assert.Null(withoutNeural.NeuralStrategy);
    }

    [Fact]
    public void Train_NeuralQualityGood_RampsBetaAfterActivationThreshold()
    {
        var neural = new NeuralScoringStrategy();
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        using var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        // First round crosses the activation threshold.
        ensemble.Train(CleanExamples(160));
        var betaFirst = ensemble.CurrentNeuralBeta;
        Assert.True(betaFirst > 0, $"Beta should activate past the threshold, was {betaFirst:F4}");

        // More cumulative examples advance the linear ramp -> beta increases (never exceeds the cap).
        ensemble.Train(CleanExamples(160));
        var betaSecond = ensemble.CurrentNeuralBeta;

        Assert.True(betaSecond >= betaFirst,
            $"Beta should ramp up with more data: {betaFirst:F4} -> {betaSecond:F4}");
        Assert.InRange(betaSecond, 0.0, EnsembleScoringStrategy.NeuralMaxBetaFraction);
    }

    [Fact]
    public void Train_NeuralFailsToTrainWhileLearnedSucceeds_DecaysBeta()
    {
        var neural = new NeuralScoringStrategy();
        using var ensemble = BuildActivatedEnsemble(neural);
        var betaBefore = ensemble.CurrentNeuralBeta;
        Assert.True(betaBefore > 0, $"Neural beta must have activated, was {betaBefore:F4}");

        // 29 examples: learned trains but neural fails
        Assert.True(ensemble.Train(CleanExamples(29)));

        var betaAfter = ensemble.CurrentNeuralBeta;
        Assert.True(betaAfter < betaBefore,
            $"Neural-fail round must decay beta: {betaBefore:F4} -> {betaAfter:F4}");

        // Either a clean halving, or a snap to zero when the halved value is below the floor.
        var halved = betaBefore * 0.5;
        var expected = halved < EnsembleScoringStrategy.NeuralBetaMinFloor ? 0.0 : halved;
        Assert.Equal(expected, betaAfter, 6);
    }

    [Fact]
    public void Train_OwnsNeuralFalse_DataPoorUser_NeverActivatesNeural()
    {
        // A per-user ensemble borrows the shared MLP (ownsNeural:false) and never fits it (trainNeural:false).
        // With per-run counts below the activation threshold, the MLP would add noise not signal, so beta must
        // stay at zero however many rounds run. The per-run count is what gates this, not the accumulated total.
        var shared = new NeuralScoringStrategy();
        Assert.True(((ITrainableStrategy)shared).Train(CleanExamples(160)));

        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        using var perUser = new EnsembleScoringStrategy(learned, heuristic, shared, ownsNeural: false);

        // 40 examples per round: accumulated count climbs past 150 over rounds, but no single run reaches it.
        for (var round = 0; round < 15; round++)
        {
            Assert.True(perUser.Train(CleanExamples(40), heldOutForMetrics: null, trainNeural: false));
            Assert.Equal(0.0, perUser.CurrentNeuralBeta);
        }

        Assert.True(perUser.TrainingExampleCount >= EnsembleScoringStrategy.NeuralActivationThreshold,
            "The accumulated count must have crossed the threshold, proving the per-run count is what gates beta.");
        Assert.Equal(0.0, perUser.CurrentNeuralBeta);

        shared.Dispose();
    }

    [Fact]
    public void Train_OwnsNeuralFalse_DataRichUser_ActivatesNeuralFromFloorAndRamps()
    {
        // A data-rich per-user user (per-run count over the threshold) gets the globally-validated MLP dosed
        // in, starting at the per-user floor rather than zero, then climbing toward the shared cap with more
        // of the user's own data. It never overwhelms the learned weights (stays at or below the cap).
        var shared = new NeuralScoringStrategy();
        Assert.True(((ITrainableStrategy)shared).Train(CleanExamples(200)));

        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        using var perUser = new EnsembleScoringStrategy(learned, heuristic, shared, ownsNeural: false);

        // Exactly at the threshold: beta starts at the floor (not zero), the immediate small contribution.
        Assert.True(perUser.Train(CleanExamples(150), heldOutForMetrics: null, trainNeural: false));
        var betaAtThreshold = perUser.CurrentNeuralBeta;
        Assert.Equal(EnsembleScoringStrategy.NeuralPerUserBetaFloor, betaAtThreshold, 6);

        // Well past the threshold: beta has ramped up, still capped by the shared maximum.
        Assert.True(perUser.Train(CleanExamples(260), heldOutForMetrics: null, trainNeural: false));
        var betaHigh = perUser.CurrentNeuralBeta;
        Assert.True(betaHigh > betaAtThreshold, $"Beta must ramp with more data: {betaAtThreshold:F4} -> {betaHigh:F4}");
        Assert.InRange(betaHigh, EnsembleScoringStrategy.NeuralPerUserBetaFloor, EnsembleScoringStrategy.NeuralMaxBetaFraction);

        shared.Dispose();
    }

    [Fact]
    public void Train_OwnsNeuralFalse_UserCountDips_BetaDoesNotFallBack()
    {
        // Once a data-rich user's neural contribution has ramped up, a later run where their own count is
        // lower must not yank beta back down, whether that later run is still above the threshold or drops
        // below it. The dosing tracks the high-water mark, so the footnote percentage does not flicker when a
        // user's per-run example count fluctuates.
        var shared = new NeuralScoringStrategy();
        Assert.True(((ITrainableStrategy)shared).Train(CleanExamples(200)));

        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        using var perUser = new EnsembleScoringStrategy(learned, heuristic, shared, ownsNeural: false);

        // A rich run drives beta to the cap.
        Assert.True(perUser.Train(CleanExamples(260), heldOutForMetrics: null, trainNeural: false));
        var betaHigh = perUser.CurrentNeuralBeta;
        Assert.Equal(EnsembleScoringStrategy.NeuralMaxBetaFraction, betaHigh, 6);

        // A leaner run, still above the threshold, whose bare ramp target would sit well below the cap.
        Assert.True(perUser.Train(CleanExamples(160), heldOutForMetrics: null, trainNeural: false));
        Assert.Equal(betaHigh, perUser.CurrentNeuralBeta, 6);

        // A run below the activation threshold on an already-activated user holds the established beta rather
        // than resetting it to zero: the user has proven data maturity, so one lean batch must not blank out
        // the neural say only to snap it back on the next rich run.
        Assert.True(perUser.Train(CleanExamples(40), heldOutForMetrics: null, trainNeural: false));
        Assert.Equal(betaHigh, perUser.CurrentNeuralBeta, 6);

        shared.Dispose();
    }

    [Fact]
    public void Train_OwnsNeuralFalse_TrainNeuralTrue_LeavesSharedNeuralUntouched()
    {
        // The two-argument Train overload delegates with trainNeural:true. A per-user ensemble borrows the
        // shared MLP (ownsNeural:false), so even through that overload it must not re-fit the shared MLP on
        // one user's slice. The guard is what enforces this; without it the shared neural would be overwritten.
        var shared = new NeuralScoringStrategy();
        Assert.True(((ITrainableStrategy)shared).Train(CleanExamples(200)));

        var probe = new CandidateFeatures { GenreSimilarity = 0.9, CombinedCriticScore = 0.85, RecencyScore = 0.7, GenreCount = 4 };
        var scoreBefore = shared.Score(probe);
        var lossBefore = shared.LastValidationLoss;

        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var perUser = new EnsembleScoringStrategy(learned, heuristic, shared, ownsNeural: false);

        // Inverted labels: had this retrained the shared MLP, its probe score would move noticeably.
        var inverted = CleanExamples(200).Select(e => new TrainingExample { Features = e.Features, Label = 1.0 - e.Label }).ToList();
        Assert.True(perUser.Train(inverted));

        Assert.Equal(scoreBefore, shared.Score(probe), 10);
        Assert.Equal(lossBefore, shared.LastValidationLoss, 10);

        perUser.Dispose();
        shared.Dispose();
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
        using var ensemble = BuildActivatedEnsemble(neural);
        var betaStart = ensemble.CurrentNeuralBeta;
        Assert.True(betaStart > 0, $"Neural beta must have activated, was {betaStart:F4}");

        // Fewer than learned threshold so training fails every round, driving the cold-start branch that halves beta.
        var previousBeta = betaStart;
        var sawStrictDecrease = false;
        for (var round = 0; round < 15; round++)
        {
            Assert.False(ensemble.Train(CleanExamples(10)),
                "Learned training must fail with < 20 examples");

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
        using var ensemble = BuildActivatedEnsemble(neural);
        var betaStart = ensemble.CurrentNeuralBeta;
        Assert.True(betaStart > 0, $"Neural beta must have activated, was {betaStart:F4}");

        // Learned succeeds but neural fails. Each such round halves beta.
        for (var round = 0; round < 12; round++)
        {
            Assert.True(ensemble.Train(CleanExamples(29)),
                "Learned training must succeed with >= 12 examples");

            var beta = ensemble.CurrentNeuralBeta;
            Assert.True(beta == 0.0 || beta >= EnsembleScoringStrategy.NeuralBetaMinFloor,
                $"Beta must never linger between zero and the floor, was {beta:F6}");
        }

        Assert.Equal(0.0, ensemble.CurrentNeuralBeta);
    }

    [Fact]
    public void Dispose_OwnsNeuralFalse_DoesNotDisposeSharedNeural()
    {
        // Train the shared neural so it scores away from the disposed-degradation baseline of 0.5.
        var shared = new NeuralScoringStrategy();
        Assert.True(((ITrainableStrategy)shared).Train(CleanExamples(160)));

        var probe = new CandidateFeatures
        {
            GenreSimilarity = 0.9,
            CombinedCriticScore = 0.85,
            RecencyScore = 0.7,
            GenreCount = 4
        };
        var beforeScore = shared.Score(probe);

        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);

        // ownsNeural: false - a per-user ensemble that borrows the shared neural must not tear it down.
        var borrowing = new EnsembleScoringStrategy(learned, heuristic, shared, ownsNeural: false);
        borrowing.Dispose();

        // Disposing a NeuralScoringStrategy tears down its ReaderWriterLockSlim; a disposed instance
        // silently degrades to the 0.5 baseline. The shared neural must instead keep its trained score.
        var afterScore = shared.Score(probe);
        Assert.Equal(beforeScore, afterScore, 12);
        Assert.NotEqual(0.5, afterScore, 6);

        shared.Dispose();

        // Contrast: the default ownsNeural: true DOES dispose the composed neural, degrading it to 0.5.
        var ownedNeural = new NeuralScoringStrategy();
        Assert.True(((ITrainableStrategy)ownedNeural).Train(CleanExamples(160)));
        Assert.NotEqual(0.5, ownedNeural.Score(probe), 6);

        var owning = new EnsembleScoringStrategy(
            new LearnedScoringStrategy(),
            new HeuristicScoringStrategy(genrePenaltyFloor: 1.0),
            ownedNeural);
        owning.Dispose();

        Assert.Equal(0.5, ownedNeural.Score(probe), 12);
    }

    [Fact]
    public void Train_TrainNeuralFalse_DoesNotTrainNeural()
    {
        var neural = new NeuralScoringStrategy();
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        using var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        // Baselines before any training: the neural has never trained (generation 0, initial weights).
        var neuralGenBefore = neural.TrainingGeneration;
        var neuralWeightsBefore = neural.GetCurrentWeightsHidden();
        var learnedWeightsBefore = ensemble.LearnedStrategy.GetCurrentWeights();
        Assert.Equal(0, neuralGenBefore);

        // 3-arg overload with trainNeural: false - learned trains, neural is left untouched.
        Assert.True(ensemble.Train(CleanExamples(160), heldOutForMetrics: null, trainNeural: false));

        // Learned model DID train: its weights moved.
        Assert.NotEqual(learnedWeightsBefore, ensemble.LearnedStrategy.GetCurrentWeights());

        // Neural model did NOT train: generation unchanged and weights byte-identical.
        Assert.Equal(neuralGenBefore, neural.TrainingGeneration);
        Assert.Equal(neuralWeightsBefore, neural.GetCurrentWeightsHidden());
    }

    [Fact]
    public void Train_TrainNeuralTrue_TrainsNeural()
    {
        var neural = new NeuralScoringStrategy();
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        using var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        var neuralGenBefore = neural.TrainingGeneration;
        var neuralWeightsBefore = neural.GetCurrentWeightsHidden();

        // 3-arg overload with trainNeural: true (the default path) - both models train.
        Assert.True(ensemble.Train(CleanExamples(160), heldOutForMetrics: null, trainNeural: true));

        // Neural model DID train: generation advanced and weights moved.
        Assert.True(neural.TrainingGeneration > neuralGenBefore);
        Assert.NotEqual(neuralWeightsBefore, neural.GetCurrentWeightsHidden());
    }
}
