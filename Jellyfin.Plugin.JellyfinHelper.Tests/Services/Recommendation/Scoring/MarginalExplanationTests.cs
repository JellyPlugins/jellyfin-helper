using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Named explanation effects equal actual score changes, including nonlinear saturation, interactions and genre policy.</summary>
public sealed class MarginalExplanationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NamedEffects_AreActualRemovalDeltasAndPreserveTheForwardScore(int strategyKind)
    {
        using var neural = new NeuralScoringStrategy();
        using var ensemble = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1.0), neural);
        IScoringStrategy strategy = strategyKind switch
        {
            0 => new HeuristicScoringStrategy(),
            1 => new LearnedScoringStrategy(),
            2 => neural,
            _ => ensemble
        };
        var features = new CandidateFeatures { GenreSimilarity = 0.7, CollaborativeScore = 0.4, CombinedCriticScore = 0.85, RecencyScore = 0.8, PeopleSimilarity = 0.6, GenreCount = 3, StudioMatch = true, FranchiseAffinity = 0.5, CollectionProgressionBoost = 0.4, PopularityScore = 0.3 };
        var explanation = strategy.ScoreWithExplanation(features);
        Assert.True(explanation.ContributionsAreMarginalEffects);
        Assert.Equal(strategy.Score(features), explanation.FinalScore);
        Assert.Equal(strategy.Score(features) - strategy.Score(features.WithoutSignal(FeatureIndex.GenreSimilarity)), explanation.GenreContribution, precision: 12);
        Assert.Equal(strategy.Score(features) - strategy.Score(features.WithoutSignal(FeatureIndex.PeopleSimilarity)), explanation.PeopleContribution, precision: 12);
        Assert.Equal(strategy.Score(features) - strategy.Score(features.WithoutSignal(FeatureIndex.FranchiseAffinity)), explanation.FranchiseContribution, precision: 12);
        Assert.Equal(strategy.Score(features) - strategy.Score(features.WithoutSignal(FeatureIndex.GenreCountNormalized)), explanation.InteractionContribution, precision: 12);
    }

    [Fact]
    public void Counterfactual_RecomputesInteractionTermsWithoutMutatingTheObservation()
    {
        var features = new CandidateFeatures { GenreSimilarity = 0.8, CombinedCriticScore = 0.7, CollaborativeScore = 0.6, PeopleSimilarity = 0.5 };
        var withoutGenre = features.WithoutSignal(FeatureIndex.GenreSimilarity).ToVector();
        Assert.Equal(0.0, withoutGenre[(int)FeatureIndex.GenreCriticInteraction]);
        Assert.Equal(0.0, withoutGenre[(int)FeatureIndex.GenreCollabInteraction]);
        Assert.Equal(0.0, withoutGenre[(int)FeatureIndex.PeopleGenreInteraction]);
        Assert.Equal(0.8, features.GenreSimilarity);
    }

    [Fact]
    public void AbsentNamedSignals_DoNotAcquirePositiveNeuralReasons()
    {
        using var strategy = new NeuralScoringStrategy();
        var explanation = strategy.ScoreWithExplanation(new CandidateFeatures { GenreSimilarity = 0.9, PeopleSimilarity = 0.6 });
        Assert.Equal(0.0, explanation.FranchiseContribution);
        Assert.Equal(0.0, explanation.CollectionProgressionContribution);
        Assert.Equal(0.0, explanation.PopularityContribution);
    }

    [Fact]
    public void ExternalMissingSignal_DoesNotProduceAnOcclusionEffect()
    {
        using var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures { IsExternalCandidate = true, GenreSimilarity = 0.8, CollaborativeScore = 0.9, FranchiseAffinity = 0.9 };
        var explanation = strategy.ScoreWithExplanation(features);
        Assert.Equal(0.0, explanation.CollaborativeContribution);
        Assert.Equal(0.0, explanation.FranchiseContribution);
    }
}
