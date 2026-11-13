using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Falsifiable mixture admission, independent coefficient selection and per-user utility protections.</summary>
public sealed class EnsembleAdmissionEvaluatorTests
{
    private static readonly DateTime ReferenceTime = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UsefulNeuralIsAdmittedEvenWhenTheLearnedComponentIsOnlyAConstant()
    {
        var decision = Evaluate(Examples(80, 11), Examples(80, 12), new AdmissionModels(
            Constant(.5), Constant(.425), Scorer(features => features.CombinedCriticScore)));
        Assert.True(decision.Accepted);
        Assert.True(decision.Candidate.Neural > 0);
        Assert.InRange(decision.Candidate.Neural, 0, .3);
        Assert.True(decision.Utility.LowerNeuralImprovement > 0);
        Assert.Equal(1, decision.Utility.CandidateNdcg);
    }

    [Fact]
    public void PerfectLinearRankingDoesNotBuyUnnecessaryNeuralParticipation()
    {
        var decision = Evaluate(Examples(80, 21), Examples(80, 22), new AdmissionModels(
            Constant(.5), Scorer(features => features.CombinedCriticScore), Scorer(features => features.CombinedCriticScore)));
        Assert.True(decision.Accepted);
        Assert.True(decision.Candidate.Learned > 0);
        Assert.Equal(0, decision.Candidate.Neural);
        Assert.Equal(1, decision.Utility.CandidateNdcg);
    }

    [Fact]
    public void AcceptanceFailureCannotRetuneTheCandidateOrSelectANewFallback()
    {
        var cal = Examples(80, 31);
        var valid = Examples(80, 32);
        var bad = Examples(80, 33);
        foreach (var example in bad) example.Features.CombinedCriticScore = .85 - example.Label;
        var models = new AdmissionModels(Constant(.5), Constant(.425), Scorer(features => features.CombinedCriticScore));
        var accepted = Evaluate(cal, valid, models);
        var rejected = Evaluate(cal, bad, models);
        Assert.True(accepted.Accepted);
        Assert.False(rejected.Accepted);
        Assert.Equal(accepted.Candidate, rejected.Candidate);
        Assert.True(rejected.Candidate.Neural > 0);
        Assert.True(rejected.Utility.LowerNeuralImprovement < 0);
    }

    [Fact]
    public void ChangingOnlyAcceptanceUserOutcomesCannotChangeCalibrationCoefficients()
    {
        var cal = Examples(80, 131).Concat(Examples(80, 132, user: 2)).ToArray();
        var test = Examples(80, 133).Concat(Examples(80, 134, user: 2)).ToArray();
        foreach (var row in cal.Concat(test)) row.Features.IsSeries = row.UserId == Id(2);
        var models = new AdmissionModels(Constant(.5), Scorer(features => features.IsSeries ? .425 : features.CombinedCriticScore));
        var mixed = Evaluate(cal, test, models);
        foreach (var row in test.Where(row => row.UserId == Id(2))) row.Label = .85;
        var homogeneousUser = Evaluate(cal, test, models);
        Assert.Equal(mixed.Candidate, homogeneousUser.Candidate);
        Assert.Equal(mixed.Evidence.CalibrationTitles, homogeneousUser.Evidence.CalibrationTitles);
        foreach (var row in test) row.Label = .85;
        var unavailable = Evaluate(cal, test, models);
        Assert.False(unavailable.Accepted);
        Assert.Equal(mixed.Candidate, unavailable.Candidate);
        Assert.Null(unavailable.Utility.CandidateLoss);
    }

    [Fact]
    public void CandidateThatBeatsHeuristicCannotReplaceABetterIncumbent()
    {
        var oracle = Scorer(features => features.CombinedCriticScore);
        var decision = Evaluate(Examples(80, 41), Examples(80, 42), new AdmissionModels(Constant(.5), oracle, Incumbent: oracle));
        Assert.False(decision.Accepted);
        Assert.True(decision.Utility.LowerHeuristicImprovement > 0);
        Assert.True(decision.Utility.LowerIncumbentImprovement < 0);
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(40, true)]
    public void EffectiveTitleSupportHasAnExplicitBoundary(int count, bool expected)
    {
        var decision = Evaluate(Examples(count, 51), Examples(count, 52), new AdmissionModels(Constant(.5), Scorer(features => features.CombinedCriticScore)));
        Assert.Equal(expected, decision.Accepted);
    }

    [Fact]
    public void ReplayedTitlesAndLowWeightDoNotInflateSupport()
    {
        var cal = Examples(4, 61);
        var test = Examples(4, 62);
        var models = new AdmissionModels(Constant(.5), Scorer(features => features.CombinedCriticScore));
        var repeated = Evaluate(Enumerable.Repeat(cal, 20).SelectMany(rows => rows).ToArray(), Enumerable.Repeat(test, 20).SelectMany(rows => rows).ToArray(), models);
        Assert.False(repeated.Accepted);
        Assert.Equal(4, repeated.Evidence.CalibrationTitles);
        var calWeighted = Examples(80, 63);
        var testWeighted = Examples(80, 64);
        for (var i = 2; i < 80; i++)
        {
            calWeighted[i].SampleWeight = .01;
            testWeighted[i].SampleWeight = .01;
        }

        Assert.False(Evaluate(calWeighted, testWeighted, models).Accepted);
    }

    [Fact]
    public void WatchingPlusARequestIsNotNegativeEvidenceAndDoesNotRunTheModels()
    {
        var cal = Examples(80, 71);
        var test = Examples(80, 72);
        foreach (var example in cal.Concat(test)) example.Label = .85;
        test[0].Label = .75;
        var calls = 0;
        var model = Scorer(_ => { calls++; return .5; });
        var decision = Evaluate(cal, test, new AdmissionModels(model, model, model));
        Assert.False(decision.Accepted);
        Assert.Equal("HomogeneousObservedOutcomes", decision.Reason);
        Assert.Null(decision.Utility.CandidateLoss);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void UnseenContrastsNeverBecomeObservedAcceptanceEvidence()
    {
        var cal = Examples(80, 81).Select(row => Contrast(row)).ToArray();
        var test = Examples(80, 82).Select(row => Contrast(row)).ToArray();
        var decision = Evaluate(cal, test, new AdmissionModels(Constant(.5), Scorer(features => features.CombinedCriticScore)));
        Assert.False(decision.Accepted);
        Assert.Equal(0, decision.Evidence.CalibrationTitles);
        Assert.Equal(0, decision.Evidence.AcceptanceTitles);
    }

    [Fact]
    public void CalibrationAndAcceptanceMustNotContainTheSameTitle()
    {
        var rows = Examples(80, 91);
        var decision = Evaluate(rows, rows, new AdmissionModels(Constant(.5), Scorer(features => features.CombinedCriticScore)));
        Assert.False(decision.Accepted);
        Assert.Equal("OverlappingEvidence", decision.Reason);
    }

    [Fact]
    public void AnAverageGainDoesNotHideRegressionForAMinorityTaste()
    {
        var cal = Examples(320, 101).Concat(Examples(40, 102, user: 2)).ToArray();
        var test = Examples(320, 103).Concat(Examples(40, 104, user: 2)).ToArray();
        var model = Scorer(features => features.IsSeries ? .85 - features.CombinedCriticScore : features.CombinedCriticScore);
        foreach (var row in cal.Concat(test)) row.Features.IsSeries = row.UserId == Id(2);
        var decision = Evaluate(cal, test, new AdmissionModels(Constant(.5), model));
        Assert.False(decision.Accepted);
    }

    [Fact]
    public void NonfinitePredictionsFailClosedWithoutInventingZeroLoss()
    {
        var decision = Evaluate(Examples(80, 111), Examples(80, 112), new AdmissionModels(Constant(.5), Constant(double.NaN)));
        Assert.False(decision.Accepted);
        Assert.Equal("InvalidPrediction", decision.Reason);
        Assert.Null(decision.Utility.CandidateLoss);
    }

    [Fact]
    public void HistoricalRestorationUsesAFixedPredictorAndRequiresNewUtilityEvidence()
    {
        var rows = Examples(80, 121);
        var oracle = Scorer(features => features.CombinedCriticScore);
        var better = EnsembleAdmissionEvaluator.EvaluateFixedVersion(rows, Constant(.5), oracle, Constant(.7), ReferenceTime);
        Assert.NotNull(better);
        Assert.True(better.LowerHeuristicImprovement > 0);
        Assert.True(better.LowerIncumbentImprovement > 0);
        Assert.Equal(1, better.CandidateNdcg);
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateFixedVersion(rows, oracle, Constant(.5), oracle, ReferenceTime));
    }

    [Fact]
    public void EvaluationBlocksAreBoundedUserBalancedAndNeverSelectedByLabels()
    {
        var rows = Examples(5000, 141).Concat(Examples(5000, 151, user: 2)).ToArray();
        var first = EnsembleAdmissionEvaluator.SelectEvaluationBlock(rows);
        Assert.Equal(EnsembleAdmissionEvaluator.MaximumEvaluationTitles, first.Length);
        Assert.Equal(first.Length / 2, first.Count(row => row.UserId == Id(1)));
        foreach (var row in rows) row.Label = .85 - row.Label;
        var second = EnsembleAdmissionEvaluator.SelectEvaluationBlock(rows.Reverse());
        Assert.Equal(first.Select(row => row.ObservationId), second.Select(row => row.ObservationId));
        Assert.Empty(EnsembleAdmissionEvaluator.SelectEvaluationBlock([]));
        Assert.Single(EnsembleAdmissionEvaluator.SelectEvaluationBlock(Enumerable.Repeat(rows[0], 20)));
    }

    [Fact]
    public void HistoricalNeuralIncrementNeedsFreshLossAndRankingUtilityNotAnOldApproval()
    {
        var cal = Examples(80, 161);
        var test = Examples(80, 162);
        var h = Constant(.5);
        var oracle = Scorer(features => features.CombinedCriticScore);
        var reference = EnsembleAdmissionEvaluator.CalibrateNoNeuralReference(cal, h, [Constant(.425)], ReferenceTime, .75)!;
        Assert.True(EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(cal, h, oracle, reference, ReferenceTime) > 0);
        Assert.True(EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(test, h, oracle, reference, ReferenceTime) > 0);
        foreach (var row in test) row.Label = .85 - row.Label;
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(test, h, oracle, reference, ReferenceTime));
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(cal, h, oracle, oracle, ReferenceTime));
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(cal.Take(19).ToArray(), h, oracle, reference, ReferenceTime));
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(cal, h, Constant(double.NaN), reference, ReferenceTime));
    }

    [Fact]
    public void HistoricalNoNeuralReferenceChoosesTheBestPinnedVersionOnCalibrationOnly()
    {
        var cal = Examples(80, 171);
        var oracle = Scorer(features => features.CombinedCriticScore);
        var expected = EnsembleAdmissionEvaluator.CalibrateNoNeuralReference(cal, Constant(.5), [oracle], ReferenceTime, .75)!;
        var actual = EnsembleAdmissionEvaluator.CalibrateNoNeuralReference(cal, Constant(.5), [Constant(double.NaN), Constant(.425), oracle], ReferenceTime, .75)!;
        Assert.Equal(cal.Select(row => expected.Score(row.Features)), cal.Select(row => actual.Score(row.Features)));
        Assert.Null(EnsembleAdmissionEvaluator.CalibrateNoNeuralReference(cal, Constant(.5), [Constant(double.NaN)], ReferenceTime, .75));
        Assert.Null(EnsembleAdmissionEvaluator.CalibrateNoNeuralReference(cal.Take(19).ToArray(), Constant(.5), [oracle], ReferenceTime, .75));
        Assert.Equal("Calibrated no-Neural reference", actual.Name);
        Assert.Equal("strategyEnsemble", actual.NameKey);
        Assert.Equal(actual.Score(cal[0].Features), actual.ScoreWithExplanation(cal[0].Features).FinalScore);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.01)]
    [InlineData(1.01)]
    public void InvalidAlphaBudgetIsRejectedBeforeScoring(double alpha)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EnsembleAdmissionEvaluator.Evaluate(Examples(20, 181), Examples(20, 182), new AdmissionModels(Constant(.5), Constant(.4)), ReferenceTime, alpha, 0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.01)]
    [InlineData(.8)]
    public void InvalidAbsoluteNeuralBudgetIsRejectedBeforeScoring(double neural)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EnsembleAdmissionEvaluator.Evaluate(Examples(20, 183), Examples(20, 184), new AdmissionModels(Constant(.5), Constant(.4)), ReferenceTime, .75, neural));
    }

    [Fact]
    public void AcceptancePredictionFailureKeepsTheCalibrationSelectedCoefficients()
    {
        var cal = Examples(80, 191);
        var test = Examples(80, 192);
        var scorer = Scorer(features => features.IsSeries ? double.NaN : features.CombinedCriticScore);
        var valid = Evaluate(cal, test, new AdmissionModels(Constant(.5), scorer));
        foreach (var row in test) row.Features.IsSeries = true;
        var invalid = Evaluate(cal, test, new AdmissionModels(Constant(.5), scorer));
        Assert.Equal(valid.Candidate, invalid.Candidate);
        Assert.False(invalid.Accepted);
        Assert.Equal("InvalidPrediction", invalid.Reason);
        Assert.Null(invalid.Utility.CandidateLoss);
    }

    [Fact]
    public void CalibrationGainWithoutExtraRankingUsefulnessSelectsNoNeuralBeforeAcceptance()
    {
        var rows = Examples(80, 201);
        var test = Examples(80, 202);
        var learned = Scorer(features => .06 + .8 * features.CombinedCriticScore);
        var neural = Scorer(features => features.CombinedCriticScore);
        var decision = Evaluate(rows, test, new AdmissionModels(Constant(.5), learned, neural));
        Assert.True(decision.Accepted);
        Assert.True(decision.Candidate.Learned > 0);
        Assert.Equal(0, decision.Candidate.Neural);
    }

    [Fact]
    public void GenuineInteriorCoefficientsAreSolvedWithoutAWeightGridAndPreserveWithinMlNeuralFractions()
    {
        var cal = Examples(80, 211);
        var test = Examples(80, 212);
        foreach (var rows in new[] { cal, test })
        {
            for (var index = 0; index < rows.Count; index++)
            {
                rows[index].Features.RecencyScore = (index & 1) == 0 ? .1 : .9;
                rows[index].Features.CombinedCriticScore = (index & 2) == 0 ? .1 : .9;
                rows[index].Label = .5 + .25 * (rows[index].Features.RecencyScore - .5) + .2 * (rows[index].Features.CombinedCriticScore - .5);
            }
        }

        var models = new AdmissionModels(Constant(.5), Scorer(features => features.RecencyScore), Scorer(features => features.CombinedCriticScore));
        var decision = Evaluate(cal, test, models);
        Assert.True(decision.Accepted);
        Assert.Equal(.25, decision.Candidate.Learned, 12);
        Assert.Equal(.2, decision.Candidate.Neural, 12);
        Assert.True(decision.Candidate.Beta > .4);
        Assert.InRange(decision.Candidate.Neural, 0, .3);
        Assert.Equal(1 - decision.Candidate.Alpha, decision.Candidate.Heuristic);
    }

    [Fact]
    public void NonfiniteWithdrawalOrFixedVersionPredictionsCannotManufactureUtility()
    {
        var rows = Examples(80, 221);
        var bad = Constant(double.NaN);
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateWithdrawal(rows, Constant(.5), bad, ReferenceTime));
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateWithdrawal(rows, bad, Constant(.5), ReferenceTime));
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateFixedVersion(rows, Constant(.5), bad, Constant(.5), ReferenceTime));
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateFixedVersion(rows, bad, Constant(.5), Constant(.5), ReferenceTime));
    }

    private static AdmissionDecision Evaluate(IReadOnlyList<TrainingExample> cal, IReadOnlyList<TrainingExample> test, AdmissionModels models)
        => EnsembleAdmissionEvaluator.Evaluate(cal, test, models, ReferenceTime, .75, .3);

    private static List<TrainingExample> Examples(int count, int seed, int user = 1)
        => Enumerable.Range(0, count).Select(index => new TrainingExample
        {
            Features = new CandidateFeatures { CombinedCriticScore = index % 2 == 0 ? .85 : 0 },
            Label = index % 2 == 0 ? .85 : 0,
            ObservationId = Id(seed * 1000 + index),
            UserId = Id(user),
            HasStableObservationId = true,
            GeneratedAtUtc = ReferenceTime
        }).ToList();

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private static TrainingExample Contrast(TrainingExample source) => new()
    {
        Features = source.Features,
        Label = source.Label,
        ObservationId = source.ObservationId,
        UserId = source.UserId,
        HasStableObservationId = true,
        IsObserved = false,
        IsContrastSample = true,
        GeneratedAtUtc = ReferenceTime
    };

    private static IScoringStrategy Constant(double value) => Scorer(_ => value);

    private static FixedScorer Scorer(Func<CandidateFeatures, double> score) => new FixedScorer(score);

    private sealed class FixedScorer(Func<CandidateFeatures, double> score) : IScoringStrategy
    {
        public string Name => "Immutable fixture";

        public string NameKey => "fixture";

        public double Score(CandidateFeatures features) => score(features);

        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => new() { FinalScore = Score(features) };
    }
}
