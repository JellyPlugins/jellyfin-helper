using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Independent revealed-choice evaluation never invents binary dislikes or retunes on acceptance outcomes.</summary>
public sealed class ChoiceRankingAdmissionEvaluatorTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public void RealHeldOutChoicesPermitRankingLearningWithoutExplicitNegativeRatings()
    {
        var user = Guid.NewGuid();
        var calibration = Rows(user, 24);
        var acceptance = Rows(user, 24);
        var heuristic = Predictor(_ => .4);
        var learned = Predictor(features => features.GenreSimilarity > .5 ? .9 : .1);
        var reserved = new HashSet<Guid>();
        var decision = EnsembleAdmissionEvaluator.Evaluate(calibration, acceptance, new(heuristic, learned), Now, .75, 0,
            ids => { reserved.UnionWith(ids); return null; });
        Assert.True(decision.Accepted);
        Assert.Equal("AcceptedChoiceRanking", decision.Reason);
        Assert.True(decision.Utility.IsChoiceRanking);
        Assert.Equal(1, decision.Utility.ChoiceRecallAtK);
        Assert.True(decision.Utility.LowerHeuristicImprovement > 0);
        Assert.Null(decision.Utility.CandidatePrecisionAtK);
        Assert.All(acceptance, example => Assert.True(example.IsObserved));
        Assert.Equal(24, reserved.Count);
        Assert.All(acceptance, example => Assert.Contains(example.ObservationId, reserved));
        Assert.All(acceptance.SelectMany(example => example.Choice!.Alternatives), alternative => Assert.DoesNotContain(alternative.ObservationId, reserved));
    }

    [Fact]
    public void UninformativeOrReversedPredictorsCannotBypassTheQualityGate()
    {
        var user = Guid.NewGuid();
        var decision = EnsembleAdmissionEvaluator.Evaluate(Rows(user, 24), Rows(user, 24),
            new(Predictor(_ => .4), Predictor(features => features.GenreSimilarity > .5 ? .1 : .9)), Now, .75, 0);
        Assert.False(decision.Accepted);
        Assert.Equal("RejectedChoiceRanking", decision.Reason);
    }

    [Fact]
    public void AcceptanceCannotReselectACalibrationWinnerWhenItsRankingFails()
    {
        var user = Guid.NewGuid();
        var learned = Predictor(ContextualRanking);
        var decision = EnsembleAdmissionEvaluator.Evaluate(Rows(user, 24), Rows(user, 24, good: false),
            new(Predictor(_ => .4), learned), Now, .75, 0);
        Assert.False(decision.Accepted);
        Assert.True(decision.Candidate.Learned > 0);
        Assert.True(decision.Utility.LowerHeuristicImprovement < 0);
    }

    [Fact]
    public void ABetterIncumbentIsRetainedEvenWhenTheCandidateBeatsTheHeuristic()
    {
        var user = Guid.NewGuid();
        var decision = EnsembleAdmissionEvaluator.Evaluate(Rows(user, 24), Rows(user, 24),
            new(Predictor(_ => .4), Predictor(features => features.GenreSimilarity > .5 ? .9 : .1),
                Incumbent: Predictor(features => features.GenreSimilarity > .5 ? .95 : .05)), Now, .75, 0);
        Assert.False(decision.Accepted);
        Assert.True(decision.Utility.LowerHeuristicImprovement > 0);
        Assert.True(decision.Utility.LowerIncumbentImprovement < 0);
    }

    [Fact]
    public void AMinorityCapturedTasteCannotBeSacrificedForAverageChoiceGain()
    {
        var user = Guid.NewGuid();
        var primary = Guid.NewGuid();
        var secondary = Guid.NewGuid();
        var calibration = Rows(user, 20, lane: primary).Concat(Rows(user, 4, lane: secondary)).ToArray();
        var acceptance = Rows(user, 20, lane: primary).Concat(Rows(user, 4, good: false, lane: secondary)).ToArray();
        var learned = Predictor(ContextualRanking);
        var decision = EnsembleAdmissionEvaluator.Evaluate(calibration, acceptance, new(Predictor(_ => .4), learned), Now, .75, 0);
        Assert.False(decision.Accepted);
        Assert.Equal("RejectedChoiceRanking", decision.Reason);
    }

    [Fact]
    public void AConstantNeuralPredictionCannotReceiveInfluenceWithoutRankingIncrement()
    {
        var user = Guid.NewGuid();
        var decision = EnsembleAdmissionEvaluator.Evaluate(Rows(user, 24), Rows(user, 24),
            new(Predictor(_ => .1), Predictor(features => features.GenreSimilarity > .5 ? .6 : .3), Predictor(_ => 1)), Now, .75, .3);
        Assert.True(decision.Accepted);
        Assert.Equal(0, decision.Candidate.Neural);
        Assert.Null(decision.Utility.LowerNeuralImprovement);
    }

    [Fact]
    public void InsufficientAndRepeatedTitleChoicesStayUnknownAndUnspent()
    {
        var user = Guid.NewGuid();
        var models = new AdmissionModels(Predictor(_ => .4), Predictor(_ => .8));
        var calls = 0;
        string? Reserve(IReadOnlyCollection<Guid> _) { calls++; return null; }
        var first = EnsembleAdmissionEvaluator.Evaluate(Rows(user, 19), Rows(user, 24), models, Now, .75, 0, Reserve);
        var single = Rows(user, 1)[0];
        var repeated = Enumerable.Repeat(single, 100).ToArray();
        var second = EnsembleAdmissionEvaluator.Evaluate(repeated, Rows(user, 24), models, Now, .75, 0, Reserve);
        Assert.False(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(19, 1, false)]
    [InlineData(10, 10, true)]
    public void EffectiveChoiceSupportUsesTheSameMacroUserWeightsAsItsUtility(int firstCount, int secondCount, bool supported)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        TrainingExample[] Slice() => Rows(first, firstCount).Concat(Rows(second, secondCount)).ToArray();
        var calibration = Slice();
        var acceptance = Slice();
        Assert.Equal(supported, ChoiceRankingAdmissionEvaluator.HasSupport(calibration, Now));
        var reservations = 0;
        var decision = ChoiceRankingAdmissionEvaluator.Evaluate(calibration, acceptance,
            new(Predictor(_ => .4), Predictor(features => features.GenreSimilarity > .5 ? .9 : .1)), Now, (.75, 0),
            _ => { reservations++; return null; });
        Assert.Equal(supported, decision?.Accepted == true);
        Assert.Equal(supported ? 1 : 0, reservations);
        if (supported) Assert.Equal(20, decision!.Evidence.AcceptanceEffectiveTitles, 10);
    }

    [Fact]
    public void NewMaterialCannotBecomeRelativeAcceptanceEvidence()
    {
        var user = Guid.NewGuid();
        var calibration = Rows(user, 24);
        foreach (var row in calibration) row.Choice = row.Choice! with { AvailableAtUtc = Now.AddDays(-1) };
        var decision = EnsembleAdmissionEvaluator.Evaluate(calibration, Rows(user, 24), new(Predictor(_ => .4), Predictor(_ => .8)), Now, .75, 0);
        Assert.False(decision.Accepted);
        Assert.Equal("HomogeneousObservedOutcomes", decision.Reason);
    }

    [Fact]
    public void ChoiceReservationFailureCannotPublishAModel()
    {
        var user = Guid.NewGuid();
        var decision = EnsembleAdmissionEvaluator.Evaluate(Rows(user, 24), Rows(user, 24),
            new(Predictor(_ => .4), Predictor(features => features.GenreSimilarity > .5 ? .9 : .1)), Now, .75, 0, _ => "AcceptanceLedgerUnavailable");
        Assert.False(decision.Accepted);
        Assert.Equal("AcceptanceLedgerUnavailable", decision.Reason);
    }

    private static TrainingExample[] Rows(Guid user, int count, bool good = true, Guid? lane = null)
        => Enumerable.Range(0, count).Select(_ =>
        {
            var id = Guid.NewGuid();
            var features = new CandidateFeatures { GenreSimilarity = 1, CombinedCriticScore = good ? 1 : 0, ContentTasteKey = lane };
            return new TrainingExample
            {
                UserId = user, ObservationId = id, HasStableObservationId = true, IsObserved = true,
                Label = .85, GeneratedAtUtc = Now.AddDays(-3), Features = features,
                Choice = new RecommendationChoiceEvidence
                {
                    ChosenObservationId = id, AvailableAtUtc = Now.AddDays(-10), RecommendedAtUtc = Now.AddDays(-3), ChosenAtUtc = Now.AddDays(-2),
                    ChosenFeatures = features.Copy(), Alternatives = [new(Guid.NewGuid(), new CandidateFeatures { GenreSimilarity = 0, CombinedCriticScore = good ? 1 : 0 })]
                }
            };
        }).ToArray();

    private static IScoringStrategy Predictor(Func<CandidateFeatures, double> score)
    {
        var mock = new Mock<IScoringStrategy>();
        mock.Setup(strategy => strategy.Score(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) => score(features));
        return mock.Object;
    }

    private static double ContextualRanking(CandidateFeatures features)
        => (features.GenreSimilarity > .5) == (features.CombinedCriticScore > .5) ? .9 : .1;
}
