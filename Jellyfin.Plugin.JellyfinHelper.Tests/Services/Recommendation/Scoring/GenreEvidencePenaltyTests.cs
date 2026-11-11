using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Missing genre metadata is neutral, while actual mismatches and legacy score snapshots retain their penalty.</summary>
public sealed class GenreEvidencePenaltyTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    public void Ensemble_AllScoringPathsDistinguishUnknownGenresFromMismatch(double alpha)
    {
        using var strategy = new EnsembleScoringStrategy(alphaMin: alpha, alphaMax: 1);
        using var unpenalized = new EnsembleScoringStrategy(alphaMin: alpha, alphaMax: 1, genrePenaltyFloor: 1);
        var unknown = new CandidateFeatures { GenreCount = 1, HasGenreEvidence = false, GenreSimilarity = 0, CombinedCriticScore = 0.9, StudioMatch = true, FranchiseAffinity = 1 };
        Assert.Equal(unpenalized.Score(unknown), strategy.Score(unknown), 12);
        var explanation = strategy.ScoreWithExplanation(unknown);
        Assert.Equal(strategy.Score(unknown), explanation.FinalScore, 12);
        Assert.Equal(1, explanation.GenrePenaltyMultiplier);
        Assert.Equal(0, explanation.GenreContribution, 12);
        foreach (var offset in new[] { -0.1, 0.1 })
        {
            var score = strategy.ScoreWithOffset(unknown, offset);
            Assert.Equal(unpenalized.ScoreWithOffset(unknown, offset), score, 12);
            Assert.Equal(score, strategy.ScoreWithExplanationAndOffset(unknown, offset).FinalScore, 12);
        }

        var mismatch = unknown.Copy();
        mismatch.HasGenreEvidence = true;
        Assert.Equal(unpenalized.Score(mismatch) * 0.1, strategy.Score(mismatch), 12);
    }

    [Fact]
    public void Heuristic_UsesTheSameEvidencePolicyAndSnapshotsStayV5Compatible()
    {
        var strategy = new HeuristicScoringStrategy();
        var unpenalized = new HeuristicScoringStrategy(genrePenaltyFloor: 1);
        var unknown = new CandidateFeatures { HasGenreEvidence = false, GenreCount = 1, GenreSimilarity = 0, CombinedCriticScore = 0.9, StudioMatch = true };
        Assert.Equal(unpenalized.Score(unknown), strategy.Score(unknown), 12);
        Assert.Equal(1, strategy.ScoreWithExplanation(unknown).GenrePenaltyMultiplier);
        var loaded = JsonSerializer.Deserialize<CandidateFeatures>(JsonSerializer.Serialize(unknown))!;
        Assert.False(loaded.HasGenreEvidence);
        Assert.Equal(strategy.Score(unknown), strategy.Score(loaded));
        Assert.True(JsonSerializer.Deserialize<CandidateFeatures>("{}")!.HasGenreEvidence);
        Assert.Equal(38, loaded.ToVector().Length);
        Assert.Equal(5, LearnedScoringStrategy.CurrentWeightsVersion);
        Assert.Equal(5, NeuralScoringStrategy.CurrentWeightsVersion);
    }
}
