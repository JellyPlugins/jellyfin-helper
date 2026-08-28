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
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Regression tests for the watched people/studio set construction in Engine when the user's watch history contains episodes.
/// </summary>
public sealed class EngineEpisodicWatchHistoryTests
{
    /// <summary>
    ///     Constructs a Series that survives LoadCandidateItems. A Series is only included in candidates when the episode query returns at least one Episode whose SeriesId matches; an Episode item is wired in WireLibraryWithSeriesAndMovie for exactly this purpose.
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
    ///     Wires the library manager mock so that: The Movie query returns movie. The Series query returns series.
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

        // Throw from the batch API to force the per-item GetPeople fallback path. BatchFallbackHelper.TryRunBatch catches any non-cancellation exception and returns null, which causes SimilarityComputer to fall back to BuildPeopleLookupPerItem.
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
        // A user who has only watched TV episodes (not movies) must still receive recommendations.
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
        // A watched episode with no SeriesId (e.g. a standalone special) must not cause any exception.
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

    [Fact]
    public void GetRecommendations_WarmUser_SeriesCandidateWithEpisodeStreams_ResolvesLanguageAffinityViaFallback()
    {
        // A Series candidate has no direct media streams, so ResolveMediaLanguages must fall back to the first child episode (ParentId query) to compute LanguageAffinity.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();

        var seriesId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var watchedMovieId = Guid.NewGuid();

        // The Series candidate reports an EMPTY (not throwing) media-stream list so ResolveMediaLanguages takes the series-child fallback branch rather than the graceful catch.
        var seriesMock = new Mock<Series> { CallBase = true };
        seriesMock.Object.Id = seriesId;
        seriesMock.Object.Name = "Streamless Show";
        seriesMock.Object.Path = $"/media/series/{seriesId:N}";
        seriesMock.Object.Genres = ["Drama"];
        seriesMock.Object.ProductionYear = 2019;
        seriesMock.Object.CommunityRating = 8.2f;
        seriesMock.Setup(s => s.GetMediaStreams()).Returns([]);
        var series = seriesMock.Object;

        var movie = MakeMovie(movieId, "Some Movie", ["Drama"]);

        // The episode returned by the ParentId fallback exposes English audio + subtitle streams.
        var childEpisode = new Mock<Episode> { CallBase = true };
        childEpisode.Object.Id = Guid.NewGuid();
        childEpisode.Object.SeriesId = seriesId;
        childEpisode.Setup(e => e.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Audio, Language = "eng" },
            new MediaStream { Type = MediaStreamType.Subtitle, Language = "eng" }
        ]);

        // Episode that makes the series survive LoadCandidateItems (general Episode query).
        var indexEpisode = MakeEpisode(Guid.NewGuid(), seriesId);

        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(new List<BaseItem> { movie });
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(new List<BaseItem> { series });
        // General Episode query (no ParentId) used by the empty-series filter.
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode
                && q.ParentId == Guid.Empty)))
            .Returns(new List<BaseItem> { indexEpisode });
        // ParentId fallback query used by ResolveMediaLanguages for the streamless series.
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.ParentId == seriesId
                && q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { childEpisode.Object });

        // No people for the candidates (empty), forcing the neutral people-similarity path.
        harness.LibraryManager
            .Setup(lm => lm.GetPeopleNamesByItems(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<IReadOnlyList<string>>()))
            .Throws(new NotSupportedException("force per-item people fallback"));
        harness.LibraryManager
            .Setup(lm => lm.GetPeople(It.IsAny<BaseItem>()))
            .Returns(new List<PersonInfo>());

        // Warm user with a matching English language profile and a watched movie so the warm path runs.
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "lang-user",
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 10, ForcedCount = 0 } }
            },
            SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 10, ForcedCount = 0 } }
            },
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = watchedMovieId,
                    Name = "Watched",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 1,
                    Genres = new List<string> { "Drama" }
                }
            }
        };

        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);

        // The series-child stream fallback must have been consulted: the ParentId episode query
        // is issued ONLY from ResolveMediaLanguages for a streamless series candidate.
        harness.LibraryManager.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.ParentId == seriesId
                && q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)),
            Times.AtLeastOnce);
    }
}
