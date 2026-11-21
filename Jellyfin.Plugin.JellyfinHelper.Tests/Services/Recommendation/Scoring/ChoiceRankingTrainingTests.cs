using System.Security.Cryptography;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>The real linear trainer and atomic ensemble lifecycle must learn choices rather than only rerank them.</summary>
public sealed class ChoiceRankingTrainingTests
{
    [Fact]
    public void AnObservedChoiceCanReverseAnIncorrectHeuristicRankingThroughAnAcceptedLearnedRefresh()
    {
        var user = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using var ensemble = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), genrePenaltyFloor: 1);
        var chosen = Features(chosen: true);
        var alternative = Features(chosen: false);
        Assert.True(new HeuristicScoringStrategy(1).Score(chosen) < new HeuristicScoringStrategy(1).Score(alternative));
        var fitting = Enumerable.Range(0, 160).Select(index => new TrainingExample
        {
            UserId = user,
            ObservationId = Guid.NewGuid(),
            HasStableObservationId = true,
            IsObserved = index % 2 == 0,
            IsContrastSample = index % 2 != 0,
            Label = index % 2 == 0 ? .85 : .05,
            SampleWeight = index % 2 == 0 ? 1 : .25,
            GeneratedAtUtc = now.AddDays(-3),
            Features = Features(index % 2 == 0)
        }).ToArray();
        var holdout = Enumerable.Range(0, 48).Select(index => Choice(user, now, acceptance: index >= 24)).ToArray();
        Assert.True(ensemble.TrainValidated(fitting, holdout, trainNeural: false, TestContext.Current.CancellationToken));
        Assert.True(ensemble.LastAdmission!.Accepted);
        Assert.Equal("AcceptedChoiceRanking", ensemble.LastAdmission.Reason);
        Assert.Equal("ChoiceRanking", ensemble.GetDiagnosticsSnapshot().ValidationLevel);
        Assert.True(ensemble.Score(chosen) > ensemble.Score(alternative));
        Assert.Equal(41, chosen.ToVector().Length);
    }

    private static TrainingExample Choice(Guid user, DateTime now, bool acceptance)
    {
        Guid id;
        do { id = Guid.NewGuid(); } while (((SHA256.HashData(id.ToByteArray())[0] & 1) != 0) != acceptance);
        var features = Features(chosen: true);
        return new TrainingExample
        {
            UserId = user,
            ObservationId = id,
            HasStableObservationId = true,
            IsObserved = true,
            Label = .85,
            GeneratedAtUtc = now.AddDays(-3),
            Features = features,
            Choice = new RecommendationChoiceEvidence
            {
                ChosenObservationId = id,
                RecommendedAtUtc = now.AddDays(-3),
                AvailableAtUtc = now.AddDays(-10),
                ChosenAtUtc = now.AddDays(-2),
                ChosenFeatures = features.Copy(),
                Alternatives = [new(Guid.NewGuid(), Features(chosen: false))]
            }
        };
    }

    private static CandidateFeatures Features(bool chosen)
        => new() { HasGenreEvidence = true, GenreSimilarity = chosen ? .1 : .9, TagSimilarity = chosen ? 1 : 0, CombinedCriticScore = .8 };
}
