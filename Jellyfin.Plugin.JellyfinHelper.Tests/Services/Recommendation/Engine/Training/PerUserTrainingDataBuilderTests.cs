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
    public void BuildExamples_Phase1_SeriesEngagementExcludesOwnEpisodes()
    {
        // A recommended Series whose episodes the user has watched must not draw genre engagement from
        // those episodes: at inference a scored series is filtered out upstream and contributes nothing,
        // so training excludes the series' own episodes. The exclude set is keyed by episode ItemId, not
        // the series id, which is the bug this guards. With the episodes as the only matching Action
        // history, the three interaction features must be neutral.
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var episodes = new List<WatchedItemInfo>
        {
            new() { ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true, PlayCount = 1, PlaybackPositionTicks = 950, RuntimeTicks = 1000, Genres = ["Action"] },
            new() { ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true, PlayCount = 1, PlaybackPositionTicks = 900, RuntimeTicks = 1000, Genres = ["Action"] },
            new() { ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true, PlayCount = 1, PlaybackPositionTicks = 980, RuntimeTicks = 1000, Genres = ["Action"] }
        };

        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = userId, UserName = "U", WatchedItems = new Collection<WatchedItemInfo>(episodes) }
        };

        var previousResults = new List<RecommendationResult>
        {
            new()
            {
                UserId = userId,
                UserName = "U",
                GeneratedAt = DateTime.UtcNow.AddDays(-2),
                Recommendations = [new RecommendedItem { ItemId = seriesId, Name = "Series", ItemType = "Series", Genres = ["Action"], CommunityRating = 8 }],
                Cohort = "test"
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(previousResults, profiles, CancellationToken.None);

        var seriesExample = examples.First(e => e.UserId == userId && e.Features.IsSeries);

        // Episodes excluded => no other Action history => neutral engagement. Before the fix the three
        // fully-watched episodes leaked in as familiarity>0, completion≈0.95, abandon 0.
        Assert.False(seriesExample.Features.HasUserInteraction);
        Assert.Equal(0.5, seriesExample.Features.CompletionRatio, 6);
        Assert.Equal(0.0, seriesExample.Features.IsAbandoned, 6);
    }

    [Fact]
    public void BuildExamples_Phase2_StandaloneSeriesEngagementExcludesOwnEpisodes()
    {
        // Phase 2 organic standalone path: a Series watched organically must not draw genre engagement
        // from its own episodes, mirroring the Phase 1 guard. The exclude set on this path is built from
        // every episode id (plus the series id), not just the series id; before the fix the standalone
        // path excluded only w.ItemId (the series id) so the series' own fully-watched episodes leaked in
        // as familiarity>0 / completion~0.95, corrupting the label-adjacent features.
        //
        // Reaching BuildOrganicStandaloneExample for a Series requires threading three guards in
        // ProcessOrganicWatchedItem / PrescanOrganicWatchedItems:
        //   - The series record must be meaningful and its id must NOT be in recommendedItemIds (line 827),
        //     and it must have SeriesId = null so it is not routed to the aggregated-series path (line 838).
        //   - seriesWithOrgEpisodes must NOT contain the series id (line 887), or the standalone path is
        //     skipped in favour of the aggregated one. Prescan adds a series there only when an episode is
        //     meaningful AND neither the episode id nor the series id is recommended.
        // We satisfy both by recommending each EPISODE id (not the series): the episodes then fail the
        // seriesWithOrgEpisodes condition (their ids are recommended) yet still sit in the profile as
        // meaningful Action watches, and they still populate SeriesEpisodeLookupOrganic[seriesId] (which is
        // keyed off SeriesId regardless of meaningfulness), so the leak is possible and the fix's
        // episode-id exclude set is exercised.
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var episodes = new List<WatchedItemInfo>
        {
            new() { ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true, PlayCount = 1, PlaybackPositionTicks = 950, RuntimeTicks = 1000, Genres = ["Action"] },
            new() { ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true, PlayCount = 1, PlaybackPositionTicks = 950, RuntimeTicks = 1000, Genres = ["Action"] },
            new() { ItemId = Guid.NewGuid(), SeriesId = seriesId, Played = true, PlayCount = 1, PlaybackPositionTicks = 950, RuntimeTicks = 1000, Genres = ["Action"] }
        };

        // The Series itself as its own organic watched record (SeriesId = null so it is not routed to the
        // aggregated-series path, ItemId = seriesId so the fix's SeriesEpisodeLookupOrganic[w.ItemId] hits),
        // sharing the Action genre with its episodes.
        var seriesRecord = new WatchedItemInfo
        {
            ItemId = seriesId,
            ItemType = "Series",
            Genres = ["Action"],
            Played = true,
            PlayCount = 1
        };

        var watchedItems = new List<WatchedItemInfo>(episodes) { seriesRecord };

        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = userId, UserName = "U", WatchedItems = new Collection<WatchedItemInfo>(watchedItems) }
        };

        // Recommend the EPISODE ids (not the series): keeps the series id out of seriesWithOrgEpisodes so
        // the standalone routing is reached, while the series record itself is never recommended.
        var previousResults = new List<RecommendationResult>
        {
            new()
            {
                UserId = userId,
                UserName = "U",
                GeneratedAt = DateTime.UtcNow.AddDays(-2),
                Recommendations =
                [
                    new RecommendedItem { ItemId = episodes[0].ItemId, Name = "Ep1", Genres = ["Action"], CommunityRating = 8 },
                    new RecommendedItem { ItemId = episodes[1].ItemId, Name = "Ep2", Genres = ["Action"], CommunityRating = 8 },
                    new RecommendedItem { ItemId = episodes[2].ItemId, Name = "Ep3", Genres = ["Action"], CommunityRating = 8 }
                ],
                Cohort = "test"
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(previousResults, profiles, CancellationToken.None);

        var seriesExamples = examples.Where(e => e.Features.IsSeries).ToList();
        Assert.NotEmpty(seriesExamples);

        // Episodes excluded on the standalone path => no other Action history => neutral engagement. Before
        // the fix the standalone example would show familiarity>0 / completion~0.95, IsAbandoned 0.
        Assert.All(seriesExamples, e =>
        {
            Assert.False(e.Features.HasUserInteraction);
            Assert.Equal(0.5, e.Features.CompletionRatio, 6);
            Assert.Equal(0.0, e.Features.IsAbandoned, 6);
        });
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
