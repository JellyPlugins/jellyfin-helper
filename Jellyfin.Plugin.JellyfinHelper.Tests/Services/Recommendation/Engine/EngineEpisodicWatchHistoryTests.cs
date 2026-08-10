using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Regression tests for the watched people/studio set construction in
///     <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine"/>
///     when the user's watch history contains episodes.
///     <para>
///         The engine's <c>allCandidates</c> list contains Movies and Series only - never
///         Episodes. When watch-history rows represent episodes, their <c>ItemId</c> has no
///         entry in <c>peopleLookup</c> or <c>candidateLookup</c>. The SeriesId fallback
///         resolves people and studios from the parent Series, ensuring that episode watch
///         history contributes the same people/studio signals as movie watch history.
///     </para>
/// </summary>
public sealed class EngineEpisodicWatchHistoryTests
{
    /// <summary>
    ///     Constructs a <see cref="Series"/> that survives <c>LoadCandidateItems</c>.
    ///     A Series is only included in candidates when the episode query returns at
    ///     least one Episode whose <c>SeriesId</c> matches; an Episode item is wired
    ///     in <see cref="WireLibraryWithSeriesAndMovie"/> for exactly this purpose.
    /// </summary>
    private static Series MakeSeries(Guid id, string name, string[] genres)
    {
        return new Series
        {
            Id = id,
            Name = name,
            Path = $"/media/series/{id:N}",
            Genres = genres,
            ProductionYear = 2019,
            CommunityRating = 8.2f,
            PremiereDate = new DateTime(2019, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DateCreated = DateTime.UtcNow.AddDays(-60)
        };
    }

    private static Movie MakeMovie(Guid id, string name, string[] genres, float rating = 7.8f)
    {
        return new Movie
        {
            Id = id,
            Name = name,
            Path = $"/media/movies/{id:N}.mkv",
            Genres = genres,
            ProductionYear = 2021,
            CommunityRating = rating,
            PremiereDate = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };
    }

    private static Episode MakeEpisode(Guid id, Guid seriesId)
    {
        return new Episode
        {
            Id = id,
            SeriesId = seriesId,
            Name = "Episode 1",
            Path = $"/media/series/{seriesId:N}/S01E01.mkv"
        };
    }

    /// <summary>
    ///     Wires the library manager mock so that:
    ///     <list type="bullet">
    ///         <item>The Movie query returns <paramref name="movie"/>.</item>
    ///         <item>The Series query returns <paramref name="series"/>.</item>
    ///         <item>The Episode query returns a synthetic episode whose <c>SeriesId</c>
    ///               equals <paramref name="series"/>.Id, letting the series survive
    ///               the <c>LoadCandidateItems</c> episode-presence filter.</item>
    ///         <item><c>GetPeopleNamesByItems</c> returns null (simulating a pre-12 host),
    ///               so the per-item <c>GetPeople</c> fallback is exercised.</item>
    ///         <item><c>GetPeople</c> for <paramref name="series"/> returns the shared actor.</item>
    ///         <item><c>GetPeople</c> for <paramref name="movie"/> returns the same actor,
    ///               creating a people-overlap between the watched series and the candidate movie.</item>
    ///     </list>
    /// </summary>
    private static void WireLibraryWithSeriesAndMovie(
        EngineTestFactory.EngineHarness harness,
        Series series,
        Movie movie,
        string sharedActorName)
    {
        var syntheticEpisode = MakeEpisode(Guid.NewGuid(), series.Id);

        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(new List<BaseItem> { movie });

        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(new List<BaseItem> { series });

        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { syntheticEpisode });

        // Throw from the batch API to force the per-item GetPeople fallback path.
        // BatchFallbackHelper.TryRunBatch catches any non-cancellation exception and
        // returns null, which causes SimilarityComputer to fall back to BuildPeopleLookupPerItem.
        harness.LibraryManager
            .Setup(lm => lm.GetPeopleNamesByItems(
                It.IsAny<IReadOnlyList<Guid>>(),
                It.IsAny<IReadOnlyList<string>>()))
            .Throws(new NotSupportedException("Simulated pre-Jellyfin-12 host"));

        var sharedPerson = new PersonInfo { Name = sharedActorName, Type = PersonKind.Actor };

        harness.LibraryManager
            .Setup(lm => lm.GetPeople(It.Is<BaseItem>(b => b.Id == series.Id)))
            .Returns(new List<PersonInfo> { sharedPerson });

        harness.LibraryManager
            .Setup(lm => lm.GetPeople(It.Is<BaseItem>(b => b.Id == movie.Id)))
            .Returns(new List<PersonInfo> { sharedPerson });
    }

    [Fact]
    public void GetRecommendations_WatchedEpisode_SeriesIdFallback_ProducesRecommendations()
    {
        // A user who has only watched TV episodes (not movies) must still receive
        // recommendations. The watched-people and watched-studio sets are built from
        // SeriesId when the episode's own ItemId is absent from the candidate lookup,
        // so the people-overlap signal between the watched series and candidate movies
        // is preserved and the scoring pipeline produces a non-empty result.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();

        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid(); // never in allCandidates
        var movieId = Guid.NewGuid();

        var series = MakeSeries(seriesId, "Watched Show", ["Drama", "Thriller"]);
        var movie = MakeMovie(movieId, "Candidate Film", ["Drama", "Thriller"]);

        WireLibraryWithSeriesAndMovie(harness, series, movie, "Jane Doe");

        // The watched item is an Episode: ItemId is the episode GUID (absent from
        // allCandidates), SeriesId points to the Series that IS in allCandidates.
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "episodic-user",
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = episodeId,
                    SeriesId = seriesId,
                    Name = "Watched Show S01E01",
                    ItemType = "Episode",
                    Played = true,
                    PlayCount = 1,
                    Genres = new List<string> { "Drama", "Thriller" }
                }
            }
        };

        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.NotEmpty(result.Recommendations);
    }

    [Fact]
    public void GetRecommendations_WatchedEpisode_WithNoSeriesId_DoesNotThrow()
    {
        // A watched episode with no SeriesId (e.g. a standalone special) must not
        // cause any exception. The fallback is simply skipped and an empty set is
        // used, which is identical to the pre-fix behavior for non-episode items
        // that have no people entry in the lookup.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();

        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var movieId = Guid.NewGuid();

        var series = MakeSeries(seriesId, "Background Show", ["Action"]);
        var movie = MakeMovie(movieId, "Candidate Film", ["Action"], 7.5f);

        WireLibraryWithSeriesAndMovie(harness, series, movie, "John Smith");

        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "no-series-id-user",
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = episodeId,
                    SeriesId = null, // no parent series - fallback must be skipped cleanly
                    Name = "Standalone Special",
                    ItemType = "Episode",
                    Played = true,
                    PlayCount = 1,
                    Genres = new List<string> { "Action" }
                }
            }
        };

        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        // Must complete without throwing regardless of whether a recommendation
        // is produced - the empty watched-people set is a valid degraded state.
        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
    }
}
