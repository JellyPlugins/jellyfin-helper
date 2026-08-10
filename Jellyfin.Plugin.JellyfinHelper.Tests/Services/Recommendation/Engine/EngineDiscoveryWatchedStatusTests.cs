using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the discovery "Requested + Watched" reconciliation the recommendation
///     <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine"/>
///     runs at the top of <c>TrainStrategy</c> (via <c>UpdateDiscoveryWatchedStatus</c>).
///     <para>
///         This pass cross-references discovery feedback (items the user requested via Seerr)
///         against the live library (to resolve TMDb ids) and the user's watch history. When a
///         previously-requested discovery item now exists in the library AND the user has watched
///         it, the feedback entry is upgraded from "Requested" to "RequestedAndWatched" via
///         <see cref="IDiscoveryFeedbackStore.MarkWatched"/> - a stronger training label.
///     </para>
///     <para>
///         BUG SURFACE: this is best-effort enrichment that must never crash training. A
///         regression that mis-resolves the media type ('movie' vs 'tv'), fails to cross-reference
///         the watch profile, or lets a feedback-store failure propagate would either poison the
///         training label or abort every training run. Each contract is pinned via Moq verification.
///     </para>
/// </summary>
public sealed class EngineDiscoveryWatchedStatusTests
{
    private static readonly IReadOnlyList<RecommendationResult> NoPreviousResults =
        new List<RecommendationResult>();

    private static Movie MakeMovieWithTmdb(Guid id, int tmdbId)
    {
        return new Movie
        {
            Id = id,
            Name = "Discovered Film",
            Path = $"/media/movies/{id:N}.mkv",
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
    }

    private static Series MakeSeriesWithTmdb(Guid id, int tmdbId)
    {
        return new Series
        {
            Id = id,
            Name = "Discovered Show",
            Path = $"/media/series/{id:N}",
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
    }

    private static void WireLibraryItems(EngineTestFactory.EngineHarness harness, List<BaseItem> movieAndSeriesItems)
    {
        // UpdateDiscoveryWatchedStatus queries movies+series together (IncludeItemTypes length 2).
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 2
                && Array.IndexOf(q.IncludeItemTypes, BaseItemKind.Movie) >= 0
                && Array.IndexOf(q.IncludeItemTypes, BaseItemKind.Series) >= 0)))
            .Returns(movieAndSeriesItems);
    }

    private static DiscoveryFeedbackResult MakeRequestedNotWatched(Guid userId, int tmdbId, string mediaType)
    {
        return new DiscoveryFeedbackResult
        {
            UserId = userId,
            Entries = new List<DiscoveryFeedbackEntry>
            {
                new()
                {
                    TmdbId = tmdbId,
                    MediaType = mediaType,
                    RequestedAtUtc = DateTime.UtcNow,
                    WasWatched = false
                }
            }
        };
    }

    [Fact]
    public void TrainStrategy_RequestedDiscoveryItemNowWatched_MarksWatchedWithTmdbAndMediaType()
    {
        // A discovery item the user requested is now in the library and has been watched: the
        // store must be told to upgrade it, keyed by the resolved (TmdbId, 'movie') composite.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        const int tmdbId = 550;
        var itemId = Guid.NewGuid();

        harness.FeedbackStore.Setup(f => f.LoadAll())
            .Returns(new List<DiscoveryFeedbackResult> { MakeRequestedNotWatched(userId, tmdbId, "movie") });

        WireLibraryItems(harness, [MakeMovieWithTmdb(itemId, tmdbId)]);

        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "requester",
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new() { ItemId = itemId, Name = "Discovered Film", ItemType = "Movie", Played = true, PlayCount = 1 }
            }
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        harness.Engine.TrainStrategy(NoPreviousResults, incremental: false, CancellationToken.None);

        var expected = (tmdbId, "movie");
        harness.FeedbackStore.Verify(
            f => f.MarkWatched(
                userId,
                It.Is<IReadOnlySet<(int TmdbId, string MediaType)>>(s => s.Contains(expected))),
            Times.Once);
    }

    [Fact]
    public void TrainStrategy_FavoriteSeriesWatched_MarksSeriesWatchedAsTv()
    {
        // A favorited series whose id resolves to a library TMDb id must be marked watched with the
        // 'tv' media type - the series-favorite branch of the watched-set resolution.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        const int seriesTmdbId = 1399;
        var seriesId = Guid.NewGuid();

        harness.FeedbackStore.Setup(f => f.LoadAll())
            .Returns(new List<DiscoveryFeedbackResult> { MakeRequestedNotWatched(userId, seriesTmdbId, "tv") });

        WireLibraryItems(harness, [MakeSeriesWithTmdb(seriesId, seriesTmdbId)]);

        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "series-fan",
            FavoriteSeriesIds = { seriesId }
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        harness.Engine.TrainStrategy(NoPreviousResults, incremental: false, CancellationToken.None);

        var expected = (seriesTmdbId, "tv");
        harness.FeedbackStore.Verify(
            f => f.MarkWatched(
                userId,
                It.Is<IReadOnlySet<(int TmdbId, string MediaType)>>(s => s.Contains(expected))),
            Times.Once);
    }

    [Fact]
    public void TrainStrategy_NoLibraryTmdbIds_SkipsWithoutMarkingWatched()
    {
        // When no library item carries a parseable Tmdb id the TMDb map is empty and the pass must
        // return early WITHOUT marking anything - there is no way to cross-reference a watch.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        harness.FeedbackStore.Setup(f => f.LoadAll())
            .Returns(new List<DiscoveryFeedbackResult> { MakeRequestedNotWatched(userId, 42, "movie") });

        // A library movie WITHOUT any provider id - tmdbIdByItemId stays empty.
        var untaggedMovie = new Movie { Id = itemId, Name = "Untagged", Path = "/media/movies/x.mkv" };
        WireLibraryItems(harness, [untaggedMovie]);

        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "requester",
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new() { ItemId = itemId, Name = "Untagged", ItemType = "Movie", Played = true, PlayCount = 1 }
            }
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        harness.Engine.TrainStrategy(NoPreviousResults, incremental: false, CancellationToken.None);

        harness.FeedbackStore.Verify(
            f => f.MarkWatched(It.IsAny<Guid>(), It.IsAny<IReadOnlySet<(int, string)>>()),
            Times.Never);
    }

    [Fact]
    public void TrainStrategy_FeedbackStoreThrows_IsSwallowedAsNonCritical()
    {
        // The discovery reconciliation is best-effort: a feedback-store failure must be caught and
        // logged at debug level, never propagated. TrainStrategy still returns (false here, since
        // the default heuristic strategy has nothing to train on).
        var harness = EngineTestFactory.Create();
        harness.FeedbackStore.Setup(f => f.LoadAll())
            .Throws(new InvalidOperationException("simulated feedback store failure"));

        var trained = harness.Engine.TrainStrategy(NoPreviousResults, incremental: false, CancellationToken.None);

        Assert.False(trained);
        harness.PluginLog.Verify(
            p => p.LogDebug(
                It.IsAny<string>(),
                It.Is<string>(msg => msg.Contains("non-critical")),
                It.IsAny<Microsoft.Extensions.Logging.ILogger>()),
            Times.AtLeastOnce);
    }
}
