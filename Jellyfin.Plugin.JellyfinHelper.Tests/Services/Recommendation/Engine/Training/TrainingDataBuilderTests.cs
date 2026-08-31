using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>
///     Tests for BuildExamples. Cross-user random negatives must be reproducible across runs with identical input, otherwise training weights drift between runs and Regressions-Tests against the ensemble output become flaky.
/// </summary>
public sealed class TrainingDataBuilderTests
{
    [Fact]
    public void BuildExamples_Phase3RandomNegatives_AreDeterministicAcrossRuns()
    {
        // Two users, each with one recommendation of a unique item. Neither user has watched the OTHER's recommended item -> both are eligible cross-user negatives in Phase 3.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var generatedAt = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        (Collection<UserWatchProfile> Profiles, List<RecommendationResult> Results) BuildInput()
        {
            var profiles = new Collection<UserWatchProfile>
            {
                new()
                {
                    UserId = userA,
                    UserName = "A",
                    WatchedItems = [new WatchedItemInfo { ItemId = itemA, Played = true, LastPlayedDate = generatedAt }]
                },
                new()
                {
                    UserId = userB,
                    UserName = "B",
                    WatchedItems = [new WatchedItemInfo { ItemId = itemB, Played = true, LastPlayedDate = generatedAt }]
                }
            };

            var results = new List<RecommendationResult>
            {
                new()
                {
                    UserId = userA,
                    UserName = "A",
                    GeneratedAt = generatedAt,
                    Recommendations =
                    {
                        new RecommendedItem { ItemId = itemA, Name = "A-rec", ItemType = "Movie", Genres = ["Action"] }
                    }
                },
                new()
                {
                    UserId = userB,
                    UserName = "B",
                    GeneratedAt = generatedAt,
                    Recommendations =
                    {
                        new RecommendedItem { ItemId = itemB, Name = "B-rec", ItemType = "Movie", Genres = ["Drama"] }
                    }
                }
            };

            return (profiles, results);
        }

        var (profiles1, results1) = BuildInput();
        var (profiles2, results2) = BuildInput();

        var run1 = TrainingDataBuilder.BuildExamples(results1, profiles1, CancellationToken.None);
        var run2 = TrainingDataBuilder.BuildExamples(results2, profiles2, CancellationToken.None);

        // Sanity: Phase 3 actually fired (would be 0 if the setup didn't produce eligible negatives).
        Assert.True(run1.RandomNegativeCount > 0,
            "Phase 3 must produce at least one negative for this test to guard determinism.");

        // Same phase counts across runs.
        Assert.Equal(run1.OrganicCount, run2.OrganicCount);
        Assert.Equal(run1.RandomNegativeCount, run2.RandomNegativeCount);
        Assert.Equal(run1.DiscoveryCount, run2.DiscoveryCount);
        Assert.Equal(run1.Examples.Count, run2.Examples.Count);

        // Deterministic contract: for every example emitted, label + feature vector must match position-by-position.
        for (var i = 0; i < run1.Examples.Count; i++)
        {
            var e1 = run1.Examples[i];
            var e2 = run2.Examples[i];
            Assert.Equal(e1.Label, e2.Label, 9);
            Assert.Equal(e1.SampleWeight, e2.SampleWeight, 9);
            Assert.Equal(e1.Features.GenreCount, e2.Features.GenreCount);
            Assert.Equal(e1.Features.IsSeries, e2.Features.IsSeries);
            Assert.Equal(e1.Features.CollaborativeScore, e2.Features.CollaborativeScore, 9);
            Assert.Equal(e1.Features.GenreSimilarity, e2.Features.GenreSimilarity, 9);
        }
    }

    private static readonly DateTime Anchor = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildExamples_SeriesLevelFavorite_CountsRecommendedSeriesAsWatched()
    {
        // A series marked favorite at the series level (FavoriteSeriesIds), with no watched episode rows for it.
        var user = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = user,
                UserName = "u",
                FavoriteSeriesIds = { seriesId }
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem { ItemId = seriesId, ItemType = "Series", Genres = ["Drama"] }
                }
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        var seriesExample = Assert.Single(examples);
        Assert.Equal(0.65, seriesExample.Label, 9);
    }

    [Fact]
    public void BuildExamples_RecommendationWithRichMetadata_PopulatesCachedLookups()
    {
        // A recommended item that the user also organically watched (same ItemId in a prior rec AND in WatchedItems).
        var user = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var boxSet = Guid.NewGuid();

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
                        ItemId = itemId,
                        ItemType = "Movie",
                        Played = true,
                        LastPlayedDate = Anchor
                    }
                }
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem
                    {
                        ItemId = itemId,
                        ItemType = "Movie",
                        Genres = ["Action"],
                        PeopleNames = ["Keanu Reeves"],
                        PeopleWeights = [1.0],
                        Studios = ["Lionsgate"],
                        Tags = ["heist"],
                        BoxSetIds = [boxSet]
                    }
                }
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        var example = Assert.Single(examples);
        Assert.True(example.Features.StudioMatch);
        Assert.True(example.Features.PeopleSimilarity > 0.0);
        Assert.True(example.Features.TagSimilarity > 0.0);
        Assert.True(example.Features.CollectionProgressionBoost > 0.0);
    }

    [Fact]
    public void BuildExamples_SharedWatchHistory_ProducesPositiveCollaborativeScore()
    {
        // Two users share three watched items (>= MinCollaborativeOverlap) plus each has one unique item. This produces a finite positive co-occurrence for the neighbour's unique item, raising collaborativeMax above zero.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var shared1 = Guid.NewGuid();
        var shared2 = Guid.NewGuid();
        var shared3 = Guid.NewGuid();
        var uniqueA = Guid.NewGuid();
        var uniqueB = Guid.NewGuid();

        static WatchedItemInfo Watched(Guid id, DateTime when)
            => new() { ItemId = id, ItemType = "Movie", Played = true, LastPlayedDate = when };

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = userA,
                UserName = "A",
                WatchedItems =
                {
                    Watched(shared1, Anchor), Watched(shared2, Anchor), Watched(shared3, Anchor),
                    Watched(uniqueA, Anchor)
                }
            },
            new()
            {
                UserId = userB,
                UserName = "B",
                WatchedItems =
                {
                    Watched(shared1, Anchor), Watched(shared2, Anchor), Watched(shared3, Anchor),
                    Watched(uniqueB, Anchor)
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
                    new RecommendedItem { ItemId = uniqueB, ItemType = "Movie", Genres = ["Action"] }
                }
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        Assert.Contains(examples, e => e.Features.CollaborativeScore > 0.0);
    }

    [Fact]
    public void BuildExamples_RecommendedSeriesWithWatchedEpisodes_NeutralisesUserInteraction()
    {
        // A user meaningfully watched several episodes of a series, and that SERIES id is later recommended.
        var user = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        static WatchedItemInfo Episode(Guid seriesId, DateTime when)
            => new()
            {
                ItemId = Guid.NewGuid(),
                SeriesId = seriesId,
                ItemType = "Episode",
                Played = true,
                RuntimeTicks = 1000,
                PlaybackPositionTicks = 1000,
                LastPlayedDate = when
            };

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = user,
                UserName = "u",
                WatchedItems =
                {
                    Episode(seriesId, Anchor.AddDays(-2)),
                    Episode(seriesId, Anchor.AddDays(-1))
                }
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem { ItemId = seriesId, ItemType = "Series", Genres = ["Drama"] }
                }
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        var seriesExample = Assert.Single(examples, e => e.Features.IsSeries);
        Assert.False(seriesExample.Features.HasUserInteraction);
        Assert.Equal(0.5, seriesExample.Features.UserRatingScore, 9);
        Assert.Equal(0.5, seriesExample.Features.CompletionRatio, 9);
        Assert.True(seriesExample.Label > 0.0);
    }

    [Fact]
    public void BuildExamples_RecommendedSeriesFavoriteWithoutEpisodes_NeutralisesInteraction()
    {
        // A recommended series is a series-level favorite but has no watched episode rows, so the resolved watched item is null.
        var user = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = user,
                UserName = "u",
                FavoriteSeriesIds = { seriesId }
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem { ItemId = seriesId, ItemType = "Series", Genres = ["Sci-Fi"] }
                }
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        var seriesExample = Assert.Single(examples);
        Assert.False(seriesExample.Features.HasUserInteraction);
        Assert.Equal(0.5, seriesExample.Features.UserRatingScore, 9);
        Assert.Equal(0.5, seriesExample.Features.CompletionRatio, 9);
        Assert.Equal(0.65, seriesExample.Label, 9);
    }

    [Fact]
    public void BuildExamples_StartedThenAbandonedRecommendation_GetsAbandonedLabel()
    {
        // A recommended item the user started but abandoned early (playback progress well below the abandoned-completion threshold, never marked played, not a favorite) is active rejection - it must be labelled AbandonedLabel (0.0), not treated as a normal watch.
        var user = Guid.NewGuid();
        var itemId = Guid.NewGuid();

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
                        ItemId = itemId,
                        ItemType = "Movie",
                        Played = false,
                        IsFavorite = false,
                        RuntimeTicks = 1000,
                        PlaybackPositionTicks = 100, // 10% completion - below AbandonedCompletionThreshold
                        LastPlayedDate = Anchor
                    }
                }
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem { ItemId = itemId, ItemType = "Movie", Genres = ["Action"] }
                }
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        var example = Assert.Single(examples);
        Assert.Equal(EngineConstants.AbandonedLabel, example.Label, 9);
    }

    [Fact]
    public void BuildExamples_Phase3NegativesForUserWithoutOwnRecs_UseSeriesStudioAndBoxSetCounts()
    {
        // Sampled user (B) has NO prior recommendations of their own, watches episodes whose studios resolve only via the series-level studio lookup, and watches an item belonging to a BoxSet.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var boxItemId = Guid.NewGuid();
        var boxSet = Guid.NewGuid();
        var negItem = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = userA,
                UserName = "A",
                WatchedItems =
                {
                    new WatchedItemInfo { ItemId = negItem, ItemType = "Movie", Played = true, LastPlayedDate = Anchor }
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
                        ItemId = episodeId,
                        SeriesId = seriesId,
                        ItemType = "Episode",
                        Played = true,
                        Genres = ["Action"],
                        LastPlayedDate = Anchor
                    },
                    new WatchedItemInfo
                    {
                        ItemId = boxItemId,
                        ItemType = "Movie",
                        Played = true,
                        Genres = ["Action"],
                        LastPlayedDate = Anchor
                    }
                }
            }
        };

        // Recommendations to A only: the series carries studios (resolved for B via SeriesId), boxItemId carries the shared BoxSet (so B's watched item contributes a BoxSet count), and negItem shares genres/studios with B's watched content.
        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = userA,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem { ItemId = seriesId, ItemType = "Series", Studios = ["Marvel"], Genres = ["Action"] },
                    new RecommendedItem { ItemId = boxItemId, ItemType = "Movie", BoxSetIds = [boxSet], Genres = ["Action"] },
                    new RecommendedItem
                    {
                        ItemId = negItem,
                        ItemType = "Movie",
                        Studios = ["Marvel"],
                        Genres = ["Action"],
                        BoxSetIds = [boxSet]
                    }
                }
            }
        };

        var (examples, _, randomNegativeCount, _) =
            TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        Assert.True(randomNegativeCount > 0);

        var negatives = examples.Where(e => e.Label == 0.0 && Math.Abs(e.SampleWeight - 0.5) < 1e-9).ToList();
        Assert.NotEmpty(negatives);
        Assert.Contains(
            negatives,
            e => e.Features.ContentNearestNeighborScore > 0.0 || e.Features.CollectionProgressionBoost > 0.0);
    }

    [Fact]
    public void BuildExamples_WithDiscoveryFeedback_DelegatesAndCountsDiscovery()
    {
        // The 4-argument discovery overload must forward the feedback through the core so Phase 4 produces discovery examples.
        var user = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = user,
                UserName = "u",
                WatchedItems =
                {
                    new WatchedItemInfo { ItemId = itemId, ItemType = "Movie", Played = true, LastPlayedDate = Anchor }
                }
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem { ItemId = itemId, ItemType = "Movie", Genres = ["Action"] }
                }
            }
        };

        var discovery = new List<DiscoveryFeedbackResult>
        {
            new()
            {
                UserId = user,
                Entries =
                {
                    new DiscoveryFeedbackEntry
                    {
                        TmdbId = 42,
                        MediaType = "movie",
                        Genres = ["Action"],
                        ShownAtUtc = Anchor,
                        RequestedAtUtc = Anchor.AddDays(1),
                        WasWatched = true,
                        WatchedAtUtc = Anchor.AddDays(2)
                    }
                }
            }
        };

        var (examples, _, _, discoveryCount) =
            TrainingDataBuilder.BuildExamples(results, profiles, discovery, CancellationToken.None);

        Assert.True(discoveryCount > 0);
        Assert.Contains(examples, e => e.Label == EngineConstants.DiscoveryRequestedAndWatchedLabel);
    }

    [Fact]
    public void BuildExamples_Phase1WatchedEpisodeStudiosResolveViaSeries_FeedContentNearestNeighbor()
    {
        // A watched episode carries no studios under its own ItemId, but its SeriesId was recommended with studios.
        var user = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

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
                        ItemId = episodeId,
                        SeriesId = seriesId,
                        ItemType = "Episode",
                        Played = true,
                        Genres = ["Action"],
                        LastPlayedDate = Anchor
                    }
                }
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    // The SERIES carries the studio; the episode's own ItemId is absent from the
                    // studio lookup, so only the SeriesId fallback can supply "Marvel".
                    new RecommendedItem { ItemId = seriesId, ItemType = "Series", Studios = ["Marvel"], Genres = ["Action"] },
                    // Exposure candidate: genres disjoint from watched ("Horror" vs "Action"), no
                    // cast, studio shared. Its only path to a positive score is studio overlap.
                    new RecommendedItem
                    {
                        ItemId = candidateId,
                        ItemType = "Movie",
                        Genres = ["Horror"],
                        Studios = ["Marvel"]
                    }
                }
            }
        };

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(results, profiles, CancellationToken.None);

        // Only two examples emerge: the recommended series and this standalone Movie candidate.
        var candidate = Assert.Single(examples, e => e.Features.IsSeries is false);

        // 0.50*0 genre + 0.30*0 people + 0.20*1 studio = 0.20, but only because the series
        // studios were folded into the watched-studio set. Without that fallback it would be 0.0.
        Assert.Equal(0.20, candidate.Features.ContentNearestNeighborScore, 9);
    }
}