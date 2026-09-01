using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>
/// Ensures training examples are per-user isolated and free of interaction leakage.
/// </summary>
public sealed class PerUserTrainingDataBuilderTests
{
    [Fact]
    public void BuildExamples_SetsUserIdOnAllPhases()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = userA, UserName = "A", WatchedItems = [new WatchedItemInfo { ItemId = itemA, Played = true, PlayCount = 1, Genres = ["Action"] }] },
            new() { UserId = userB, UserName = "B", WatchedItems = [new WatchedItemInfo { ItemId = itemB, Played = true, PlayCount = 1, Genres = ["Horror"] }] },
        };

        var previousResults = new List<RecommendationResult>
        {
            new()
            {
                UserId = userA,
                UserName = "A",
                GeneratedAt = DateTime.UtcNow.AddDays(-1),
                Recommendations = [new RecommendedItem { ItemId = itemA, Name = "A", Genres = ["Action"], CommunityRating = 7 }],
                Cohort = "test"
            },
            new()
            {
                UserId = userB,
                UserName = "B",
                GeneratedAt = DateTime.UtcNow.AddDays(-1),
                Recommendations = [new RecommendedItem { ItemId = itemB, Name = "B", Genres = ["Horror"], CommunityRating = 7 }],
                Cohort = "test"
            },
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(previousResults, profiles, CancellationToken.None);

        Assert.NotEmpty(examples);
        Assert.All(examples, e => Assert.NotEqual(Guid.Empty, e.UserId));
        Assert.Contains(examples, e => e.UserId == userA);
        Assert.Contains(examples, e => e.UserId == userB);
    }

    [Fact]
    public void BuildExamples_Phase1_FeatureInteractionIsNeutralEvenWhenWatched()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var watched = new WatchedItemInfo
        {
            ItemId = itemId,
            Played = true,
            PlayCount = 1,
            PlaybackPositionTicks = 800,
            RuntimeTicks = 1000,
            Genres = ["Action"],
            UserRating = 9
        };

        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = userId, UserName = "U", WatchedItems = [watched] }
        };

        var previousResults = new List<RecommendationResult>
        {
            new()
            {
                UserId = userId,
                UserName = "U",
                GeneratedAt = DateTime.UtcNow.AddDays(-2),
                Recommendations = [new RecommendedItem { ItemId = itemId, Name = "Item", Genres = ["Action"], CommunityRating = 8 }],
                Cohort = "test"
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(previousResults, profiles, CancellationToken.None);

        var phase1 = examples.First(e => e.UserId == userId);

        // The target item is the user's only genre history and is excluded from the genre-engagement
        // aggregate to prevent label leakage (its own completion also drives the label below). With no
        // other Action history, the three interaction features are neutral by design.
        Assert.Equal(0.5, phase1.Features.CompletionRatio, 6);
        Assert.False(phase1.Features.HasUserInteraction);
        Assert.Equal(0.5, phase1.Features.UserRatingScore);
        Assert.Equal(0.0, phase1.Features.IsAbandoned, 6);
        Assert.True(phase1.Label > 0.5, "Label must still reflect actual engagement even though feature is neutral");
    }

    [Fact]
    public void BuildExamples_TwoUsersWithOppositeTaste_GetIndependentExamples()
    {
        var userAction = Guid.NewGuid();
        var userHorror = Guid.NewGuid();
        var actionItem = Guid.NewGuid();
        var horrorItem = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = userAction, UserName = "ActionFan", WatchedItems = [new WatchedItemInfo { ItemId = actionItem, Played = true, PlayCount = 5, Genres = ["Action", "SciFi"] }] },
            new() { UserId = userHorror, UserName = "HorrorFan", WatchedItems = [new WatchedItemInfo { ItemId = horrorItem, Played = true, PlayCount = 5, Genres = ["Horror"] }] },
        };

        var previousResults = new List<RecommendationResult>
        {
            new() { UserId = userAction, UserName = "ActionFan", GeneratedAt = DateTime.UtcNow.AddDays(-1), Recommendations = [new RecommendedItem { ItemId = Guid.NewGuid(), Name = "Rec", Genres = ["Action"] }], Cohort = "test" },
            new() { UserId = userHorror, UserName = "HorrorFan", GeneratedAt = DateTime.UtcNow.AddDays(-1), Recommendations = [new RecommendedItem { ItemId = Guid.NewGuid(), Name = "Rec", Genres = ["Horror"] }], Cohort = "test" },
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(previousResults, profiles, CancellationToken.None);

        var actionExamples = examples.Where(e => e.UserId == userAction).ToList();
        var horrorExamples = examples.Where(e => e.UserId == userHorror).ToList();

        Assert.NotEmpty(actionExamples);
        Assert.NotEmpty(horrorExamples);
        Assert.All(actionExamples, e => Assert.Equal(userAction, e.UserId));
        Assert.All(horrorExamples, e => Assert.Equal(userHorror, e.UserId));
        // User specific features must differ; would be equal if profiles leaked
        Assert.NotEqual(actionExamples.First().Features.GenreSimilarity, horrorExamples.First().Features.GenreSimilarity);
    }
}
