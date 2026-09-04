using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Verifies the Engine honors excluded libraries and skips duplicate library entries of watched titles.
/// </summary>
public sealed class EngineExclusionTests
{
    private static Movie MakeMovie(string name, string path, int tmdbId = 0)
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = path,
            ProductionYear = 2020,
            Genres = ["Action"],
            CommunityRating = 7.5f,
            PremiereDate = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };
        if (tmdbId > 0)
        {
            movie.ProviderIds["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return movie;
    }

    private static void WireMovies(EngineTestFactory.EngineHarness harness, List<BaseItem> movies)
    {
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(movies);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns([]);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns([]);
    }

    [Fact]
    public void GetRecommendations_ExcludedLibrary_DropsItemsUnderThatLibrary()
    {
        var harness = EngineTestFactory.Create();

        // Exclude the "home videos" library by name; its location is /media/home.
        harness.ConfigService
            .Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { ExcludedLibraries = "Home Videos" });
        harness.LibraryManager
            .Setup(lm => lm.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies, Locations = ["/media/movies"] },
                new VirtualFolderInfo { Name = "Home Videos", CollectionType = CollectionTypeOptions.homevideos, Locations = ["/media/home"] }
            ]);

        var keep = MakeMovie("Real Film", "/media/movies/real.mkv");
        var drop = MakeMovie("Home Clip", "/media/home/clip.mkv");
        WireMovies(harness, [keep, drop]);

        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "u", WatchedItems = [] });

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.ItemId == keep.Id);
        Assert.DoesNotContain(result.Recommendations, r => r.ItemId == drop.Id);
    }

    [Fact]
    public void GetRecommendations_ExcludedLibraryNestedUnderAllowed_DropsNestedItems()
    {
        // Regression: the excluded library "/media/anime" is nested under the allowed "/media" root.
        // Dropping the excluded folder by name alone leaves its items under the retained allowed root;
        // the scope now denies them because the deeper excluded root is the more specific match.
        var harness = EngineTestFactory.Create();

        harness.ConfigService
            .Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { ExcludedLibraries = "Anime" });
        harness.LibraryManager
            .Setup(lm => lm.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Media", CollectionType = CollectionTypeOptions.movies, Locations = ["/media"] },
                new VirtualFolderInfo { Name = "Anime", CollectionType = CollectionTypeOptions.tvshows, Locations = ["/media/anime"] }
            ]);

        var keep = MakeMovie("Real Film", "/media/movies/real.mkv");
        var drop = MakeMovie("Anime Film", "/media/anime/clip.mkv");
        WireMovies(harness, [keep, drop]);

        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "u", WatchedItems = [] });

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.ItemId == keep.Id);
        Assert.DoesNotContain(result.Recommendations, r => r.ItemId == drop.Id);
    }

    [Fact]
    public void GetRecommendations_NoExcludedLibraries_KeepsAllItems()
    {
        var harness = EngineTestFactory.Create();

        var a = MakeMovie("A", "/media/movies/a.mkv");
        var b = MakeMovie("B", "/media/home/b.mkv");
        WireMovies(harness, [a, b]);

        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "u", WatchedItems = [] });

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.ItemId == a.Id);
        Assert.Contains(result.Recommendations, r => r.ItemId == b.Id);
    }

    [Fact]
    public void GetAllRecommendations_ExcludedLibrary_RestrictsIdfFacetQueryToAllowedRoots()
    {
        // The genre/studio IDF prior is aggregated by the repository from facet counts, which carry
        // no per-item path, so the excluded library must be kept out of the query itself. With one
        // library excluded, the facet query must be restricted to the allowed root's item id.
        var harness = EngineTestFactory.Create();

        var allowedRootId = Guid.NewGuid();
        var excludedRootId = Guid.NewGuid();

        harness.ConfigService
            .Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { ExcludedLibraries = "Home Videos" });
        harness.LibraryManager
            .Setup(lm => lm.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Movies", ItemId = allowedRootId.ToString("N"), CollectionType = CollectionTypeOptions.movies, Locations = ["/media/movies"] },
                new VirtualFolderInfo { Name = "Home Videos", ItemId = excludedRootId.ToString("N"), CollectionType = CollectionTypeOptions.homevideos, Locations = ["/media/home"] }
            ]);

        Guid[]? observedGenreAncestors = null;
        harness.ItemRepository
            .Setup(r => r.GetGenres(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => observedGenreAncestors = q.AncestorIds)
            .Returns(new QueryResult<(BaseItem, ItemCounts)>([]));
        harness.ItemRepository
            .Setup(r => r.GetStudios(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<(BaseItem, ItemCounts)>([]));

        WireMovies(harness, [MakeMovie("Real Film", "/media/movies/real.mkv")]);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>
            {
                new()
                {
                    UserId = Guid.NewGuid(),
                    UserName = "warm",
                    WatchedItems = new Collection<WatchedItemInfo>
                    {
                        new() { ItemId = Guid.NewGuid(), Name = "W", ItemType = "Movie", Played = true, PlayCount = 1, Genres = new List<string> { "Action" } }
                    }
                }
            });

        harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        Assert.NotNull(observedGenreAncestors);
        Assert.Contains(allowedRootId, observedGenreAncestors!);
        Assert.DoesNotContain(excludedRootId, observedGenreAncestors!);
    }

    [Fact]
    public void GetAllRecommendations_NoExcludedLibraries_LeavesIdfFacetQueryUnrestricted()
    {
        // Without an exclusion the facet query must stay unrestricted so the prior is built over the
        // whole library, matching the behavior before scoping was introduced.
        var harness = EngineTestFactory.Create();

        bool queryHadAncestors = true;
        harness.ItemRepository
            .Setup(r => r.GetGenres(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => queryHadAncestors = q.AncestorIds is { Length: > 0 })
            .Returns(new QueryResult<(BaseItem, ItemCounts)>([]));
        harness.ItemRepository
            .Setup(r => r.GetStudios(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<(BaseItem, ItemCounts)>([]));

        WireMovies(harness, [MakeMovie("Real Film", "/media/movies/real.mkv")]);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>
            {
                new()
                {
                    UserId = Guid.NewGuid(),
                    UserName = "warm",
                    WatchedItems = new Collection<WatchedItemInfo>
                    {
                        new() { ItemId = Guid.NewGuid(), Name = "W", ItemType = "Movie", Played = true, PlayCount = 1, Genres = new List<string> { "Action" } }
                    }
                }
            });

        harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        Assert.False(queryHadAncestors);
    }

    [Fact]
    public void GetRecommendations_DuplicateTmdbId_WatchedElsewhere_ExcludesCandidate()
    {
        // A watched movie and a candidate share a TMDb id but have different Jellyfin ids (e.g. the
        // same film re-added or present in two libraries). The Guid check misses this; the TMDb key must catch it.
        var harness = EngineTestFactory.Create();

        var candidate = MakeMovie("Duplicate Film", "/media/movies/dup.mkv", tmdbId: 550);
        WireMovies(harness, [candidate]);

        var userId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "u",
            WatchedMovieCount = 1,
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    Name = "Duplicate Film (other copy)",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 1,
                    TmdbId = 550,
                    Genres = new List<string> { "Action" }
                }
            }
        };
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Recommendations, r => r.ItemId == candidate.Id);
    }

    [Fact]
    public void GetRecommendations_NoSharedTmdbId_CandidateStillRecommended()
    {
        // Regression guard: a distinct TMDb id must not be excluded by the provider-id fallback.
        var harness = EngineTestFactory.Create();

        var candidate = MakeMovie("Fresh Film", "/media/movies/fresh.mkv", tmdbId: 700);
        WireMovies(harness, [candidate]);

        var userId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "u",
            WatchedMovieCount = 1,
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    Name = "Something Else",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 1,
                    TmdbId = 550,
                    Genres = new List<string> { "Action" }
                }
            }
        };
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.ItemId == candidate.Id);
    }

    [Fact]
    public void GetRecommendations_WatchedEpisodeOfSeries_ExcludesDuplicateSeriesCandidate()
    {
        // A user watched episodes of a show; a duplicate Series entry (same series TMDb id, different
        // Jellyfin id) must be excluded via the watched episode's resolved parent-series TMDb key.
        var harness = EngineTestFactory.Create();

        var duplicateSeries = MakeSeries("Duplicate Show", "/media/series/dup", tmdbId: 1399);
        WireSeries(harness, [duplicateSeries]);

        var userId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "u",
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    Name = "S01E01 (other copy)",
                    ItemType = "Episode",
                    Played = true,
                    PlayCount = 1,
                    SeriesTmdbId = 1399,
                    Genres = new List<string> { "Action" }
                }
            }
        };
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Recommendations, r => r.ItemId == duplicateSeries.Id);
    }

    [Fact]
    public void GetRecommendations_FavoritedSeries_ExcludesDuplicateSeriesCandidate()
    {
        // A user favorited a series without watching it; a duplicate Series entry (same series TMDb
        // id, different Jellyfin id) must be excluded via the favorite's own TMDb key. Before the
        // favorite carried its TMDb id, the Guid check missed the duplicate and it leaked through.
        var harness = EngineTestFactory.Create();

        var duplicateSeries = MakeSeries("Duplicate Show", "/media/series/dup", tmdbId: 1399);
        WireSeries(harness, [duplicateSeries]);

        var userId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "u",
            FavoriteCount = 1,
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    Name = "Duplicate Show (other copy)",
                    ItemType = "Series",
                    Played = false,
                    IsFavorite = true,
                    TmdbId = 1399,
                    SeriesTmdbId = 1399,
                    Genres = new List<string> { "Action" }
                }
            }
        };
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Recommendations, r => r.ItemId == duplicateSeries.Id);
    }

    [Fact]
    public void GetRecommendations_WatchedEpisodeWithoutResolvedSeriesTmdb_DoesNotExcludeUnrelatedSeries()
    {
        // Regression guard: an episode whose parent-series TMDb id could not be resolved (0) must not
        // contribute a (0, "tv") key that would accidentally suppress unrelated series candidates.
        var harness = EngineTestFactory.Create();

        var series = MakeSeries("Unrelated Show", "/media/series/unrelated", tmdbId: 2000);
        WireSeries(harness, [series]);

        var userId = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "u",
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    Name = "Orphan Episode",
                    ItemType = "Episode",
                    Played = true,
                    PlayCount = 1,
                    SeriesTmdbId = 0,
                    Genres = new List<string> { "Action" }
                }
            }
        };
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.ItemId == series.Id);
    }

    private static Series MakeSeries(string name, string path, int tmdbId = 0)
    {
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = path,
            ProductionYear = 2020,
            Genres = ["Action"],
            CommunityRating = 8.0f,
            PremiereDate = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };
        if (tmdbId > 0)
        {
            series.ProviderIds["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return series;
    }

    private static void WireSeries(EngineTestFactory.EngineHarness harness, List<BaseItem> series)
    {
        // A series only enters the candidate pool if it has at least one playable episode indexed,
        // so give every wired series one episode (with a Path) under its own id.
        var episodes = series
            .OfType<Series>()
            .Select(s => (BaseItem)new Episode
            {
                Id = Guid.NewGuid(),
                Name = s.Name + " S01E01",
                SeriesId = s.Id,
                Path = s.Path + "/s01e01.mkv"
            })
            .ToList();

        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns([]);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(series);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(episodes);
    }
}
