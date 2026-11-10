using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Explanations preserve the forward score and fitted state in both raw and standardized coordinates.</summary>
public sealed class NeuralExplanationStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explanation_PreservesExactForwardScoreAndModelState(bool standardized)
    {
        using var strategy = new NeuralScoringStrategy();
        if (standardized)
        {
            SetArray(strategy, "_featureMeans", Enumerable.Repeat(0.2, CandidateFeatures.FeatureCount).ToArray());
            SetArray(strategy, "_featureStdDevs", Enumerable.Repeat(0.4, CandidateFeatures.FeatureCount).ToArray());
        }

        var initialWeights = strategy.GetCurrentWeightsHidden();
        var rng = new Random(18);
        for (var i = 0; i < 40; i++)
        {
            var features = new CandidateFeatures { GenreSimilarity = rng.NextDouble(), CollaborativeScore = rng.NextDouble(), CombinedCriticScore = rng.NextDouble(), PeopleSimilarity = rng.NextDouble(), LibraryAddedRecency = rng.NextDouble() };
            var explanation = strategy.ScoreWithExplanation(features);
            Assert.Equal(strategy.Score(features), explanation.FinalScore);
            Assert.True(double.IsFinite(explanation.GenreContribution));
            Assert.Equal(explanation.GenreContribution, strategy.ScoreWithExplanation(features).GenreContribution);
            var withoutGenre = features.Copy();
            withoutGenre.GenreSimilarity = 0;
            Assert.Equal(strategy.Score(features) - strategy.Score(withoutGenre), explanation.GenreContribution, precision: 12);
        }

        Assert.Equal(initialWeights, strategy.GetCurrentWeightsHidden());
    }

    private static void SetArray(NeuralScoringStrategy strategy, string name, double[] values)
        => typeof(NeuralScoringStrategy).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(strategy, values);
}
