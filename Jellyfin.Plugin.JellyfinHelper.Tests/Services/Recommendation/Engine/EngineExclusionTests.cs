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
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
}
