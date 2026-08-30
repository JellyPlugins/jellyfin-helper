using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
/// Verifies per-user handling in training and ranking.
/// </summary>
public sealed class PerUserRankingMetricsTests
{
    private sealed class GenreSimilarityStrategy : IScoringStrategy
    {
        public string Name => "Test";
        public string NameKey => "test";
        public double Score(CandidateFeatures features) => features.GenreSimilarity;
        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => new() { FinalScore = Score(features), StrategyName = Name };
    }

    [Fact]
    public void ComputeAll_PerUser_MacroAverageWeightsUsersEqually()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var strategy = new GenreSimilarityStrategy();

        // User A perfect ranking, user B worst ranking. Global pool would mix them and hide the poor user.
        var examples = new List<TrainingExample>
        {
            // User A: top prediction is relevant
            new() { Features = new CandidateFeatures { GenreSimilarity = 0.9 }, Label = 1.0, UserId = userA },
            new() { Features = new CandidateFeatures { GenreSimilarity = 0.1 }, Label = 0.0, UserId = userA },
            // User B: top prediction is irrelevant
            new() { Features = new CandidateFeatures { GenreSimilarity = 0.9 }, Label = 0.0, UserId = userB },
            new() { Features = new CandidateFeatures { GenreSimilarity = 0.1 }, Label = 1.0, UserId = userB },
        };

        var (pAtK, _, _) = RankingMetrics.ComputeAll(examples, strategy, k: 1);

        // Per user: A=1.0, B=0.0 => macro 0.5
        Assert.Equal(0.5, pAtK, 6);
    }

    [Fact]
    public void ComputeAll_SingleUser_EqualsGlobal()
    {
        var user = Guid.NewGuid();
        var strategy = new GenreSimilarityStrategy();
        var examples = new List<TrainingExample>
        {
            new() { Features = new CandidateFeatures { GenreSimilarity = 0.9 }, Label = 1.0, UserId = user },
            new() { Features = new CandidateFeatures { GenreSimilarity = 0.8 }, Label = 0.0, UserId = user },
            new() { Features = new CandidateFeatures { GenreSimilarity = 0.1 }, Label = 1.0, UserId = user },
        };

        var perUser = RankingMetrics.ComputeAll(examples, strategy, k: 2);
        var global = RankingMetrics.ComputeAllFromArrays(
            examples.Select(e => strategy.Score(e.Features)).ToArray(),
            examples.Select(e => e.Label).ToArray(),
            k: 2);

        Assert.Equal(global.PrecisionAtK, perUser.PrecisionAtK, 6);
    }

    [Fact]
    public void ComputeAll_WithoutUserId_FallsBackToGlobal()
    {
        var strategy = new GenreSimilarityStrategy();
        var examples = new List<TrainingExample>
        {
            new() { Features = new CandidateFeatures { GenreSimilarity = 0.9 }, Label = 1.0 },
            new() { Features = new CandidateFeatures { GenreSimilarity = 0.1 }, Label = 0.0 },
        };

        var result = RankingMetrics.ComputeAll(examples, strategy, k: 1);
        Assert.Equal(1.0, result.PrecisionAtK, 6);
    }
}
