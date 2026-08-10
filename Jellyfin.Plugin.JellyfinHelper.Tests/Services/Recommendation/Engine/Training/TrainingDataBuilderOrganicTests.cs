using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>
///     Tests for the Phase-2 organic-watch path of <see cref="TrainingDataBuilder.BuildExamples"/>.
///     Organic examples are items the user found and watched on their own (never recommended),
///     which supply positive signal the recommendation-feedback path misses. These tests pin the
///     per-series aggregation, the standalone-item feature computation from cross-user cached
///     metadata, and the favorite/abandoned label split.
/// </summary>
public sealed class TrainingDataBuilderOrganicTests
{
    private static readonly DateTime Anchor = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildExamples_OrganicUserWithoutPriorResults_EmitsOrganicExample()
    {
        // User B has organic watches but no prior RecommendationResult, so the per-user
        // recommended-set lookup misses and falls back to empty - letting B's watched item
        // through as an organic discovery. User A carries the only prior result.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = userA,
                UserName = "A",
                WatchedItems =
                {
                    new WatchedItemInfo { ItemId = itemA, ItemType = "Movie", Played = true, LastPlayedDate = Anchor }
                }
            },
            new()
            {
                UserId = userB,
                UserName = "B",
                WatchedItems =
                {
                    new WatchedItemInfo { ItemId = itemB, ItemType = "Movie", Played = true, Genres = ["Drama"], LastPlayedDate = Anchor }
                }
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = userA,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem { ItemId = itemA, ItemType = "Movie", Genres = ["Action"] }
                }
            }
        };

        var (examples, organicCount, _, _) = TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        Assert.True(organicCount > 0);
        Assert.Contains(examples, e => Math.Abs(e.SampleWeight - 0.7) < 1e-9);
    }

    [Fact]
    public void BuildExamples_OrganicSeriesEpisodes_EmitOneAggregatedExample()
    {
        // Several organically watched episodes of a never-recommended series must collapse into a
        // single aggregated example (not one per episode), so OrganicCount is 1 and exactly one
        // organic example (weight 0.7) is emitted for the series.
        var user = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        WatchedItemInfo Episode()
            => new()
            {
                ItemId = Guid.NewGuid(),
                SeriesId = seriesId,
                ItemType = "Episode",
                Played = true,
                Genres = ["Drama"],
                RuntimeTicks = 1000,
                PlaybackPositionTicks = 1000,
                LastPlayedDate = Anchor
            };

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = user,
                UserName = "u",
                WatchedItems = { Episode(), Episode(), Episode() }
            }
        };

        var (examples, organicCount, _, _) =
            TrainingDataBuilder.BuildExamples([], profiles, CancellationToken.None);

        Assert.Equal(1, organicCount);
        var example = Assert.Single(examples);
        Assert.Equal(0.7, example.SampleWeight, 9);
    }

    [Fact]
    public void BuildExamples_OrganicStandaloneMovie_ComputesStudioTagAndBoxSetFeatures()
    {
        // User B organically watches a standalone movie whose metadata (studios/tags/BoxSet) is
        // only known because the SAME item was recommended to user A. Those cross-user cached
        // lookups must resolve real feature values for B's organic example.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var boxSet = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = userA,
                UserName = "A",
                WatchedItems =
                {
                    // A watched exactly the item A was recommended, so A contributes no organic
                    // example - keeping the only weight-0.7 movie example the one built for B.
                    new WatchedItemInfo { ItemId = movieId, ItemType = "Movie", Played = true, LastPlayedDate = Anchor }
                }
            },
            new()
            {
                UserId = userB,
                UserName = "B",
                WatchedItems =
                {
                    new WatchedItemInfo
                    {
                        ItemId = movieId,
                        ItemType = "Movie",
                        Played = true,
                        Genres = ["Action"],
                        LastPlayedDate = Anchor
                    },
                    // A standalone Series row (no SeriesId, no episode rows) exercises the
                    // standalone-series aggregation-marking branch.
                    new WatchedItemInfo
                    {
                        ItemId = seriesId,
                        ItemType = "Series",
                        Played = true,
                        Genres = ["Drama"],
                        LastPlayedDate = Anchor
                    }
                }
            }
        };

        // movieId is recommended to A (populating the cross-user studio/tag/boxset lookups) but
        // never to B, so it stays an organic discovery for B.
        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = userA,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem
                    {
                        ItemId = movieId,
                        ItemType = "Movie",
                        Studios = ["Lionsgate"],
                        Tags = ["heist"],
                        BoxSetIds = [boxSet],
                        Genres = ["Action"]
                    }
                }
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        // The organic movie example for B (weight 0.7, not a series).
        var movieExample = Assert.Single(examples, e => Math.Abs(e.SampleWeight - 0.7) < 1e-9 && !e.Features.IsSeries);
        Assert.True(movieExample.Features.StudioMatch);
        Assert.True(movieExample.Features.TagSimilarity > 0.0);
        Assert.True(movieExample.Features.CollectionProgressionBoost > 0.0);
    }

    [Fact]
    public void BuildExamples_OrganicFavoriteAndAbandoned_LabelledDistinctly()
    {
        // Two standalone organic items exercise both non-default arms of the organic label switch:
        // a favorite-only item (not played, no playback progress) is explicit interest (0.65),
        // while a started-but-abandoned item (playback below the abandoned threshold) is active
        // rejection (AbandonedLabel).
        var user = Guid.NewGuid();
        var favoriteId = Guid.NewGuid();
        var abandonedId = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = user,
                UserName = "u",
                WatchedItems =
                {
                    new WatchedItemInfo
                    {
                        ItemId = favoriteId,
                        ItemType = "Movie",
                        Played = false,
                        IsFavorite = true,
                        PlaybackPositionTicks = 0,
                        Genres = ["Drama"],
                        LastPlayedDate = Anchor
                    },
                    new WatchedItemInfo
                    {
                        ItemId = abandonedId,
                        ItemType = "Movie",
                        Played = false,
                        IsFavorite = false,
                        RuntimeTicks = 1000,
                        PlaybackPositionTicks = 100, // 10% - below AbandonedCompletionThreshold
                        Genres = ["Action"],
                        LastPlayedDate = Anchor
                    }
                }
            }
        };

        var (examples, _, _, _) =
            TrainingDataBuilder.BuildExamples([], profiles, CancellationToken.None);

        Assert.Equal(2, examples.Count);
        Assert.Contains(examples, e => Math.Abs(e.Label - 0.65) < 1e-9);
        Assert.Contains(examples, e => Math.Abs(e.Label - EngineConstants.AbandonedLabel) < 1e-9);
    }
}
