using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Playlist;

public class RecommendationPlaylistServiceTests
{
    private readonly Mock<IPlaylistManager> _playlistManagerMock = new();
    private readonly Mock<IUserManager> _userManagerMock = new();
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger<RecommendationPlaylistService>> _loggerMock = new();

    private RecommendationPlaylistService CreateSut() =>
        new(
            _playlistManagerMock.Object,
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            _pluginLogMock.Object,
            _loggerMock.Object);

    private static RecommendationResult CreateResult(Guid userId, string userName, int itemCount)
    {
        var items = new Collection<RecommendedItem>();
        for (var i = 0; i < itemCount; i++)
        {
            items.Add(new RecommendedItem
            {
                ItemId = Guid.NewGuid(),
                Name = $"Item {i}",
                ItemType = "Movie",
                Score = 1.0 - (i * 0.05)
            });
        }

        return new RecommendationResult
        {
            UserId = userId,
            UserName = userName,
            Recommendations = items
        };
    }

    /// <summary>
    ///     Convenience wrapper: sets up the playlist-lookup query to return an empty list.
    ///     Equivalent to <c>SetupPlaylistLookup(Array.Empty&lt;BaseItem&gt;())</c>. Kept as a
    ///     named helper because "no pre-existing managed playlists" is the far more common
    ///     setup across tests, and reading <c>SetupPlaylistQuery()</c> at the call site is
    ///     clearer than the empty-array variant.
    /// </summary>
    private void SetupPlaylistQuery() => SetupPlaylistLookup(Array.Empty<BaseItem>());

    private void SetupEpisodeResolution(Dictionary<Guid, Guid>? seriesEpisodeMap = null)
    {
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns<InternalItemsQuery>(query =>
            {
                if (query.AncestorIds is { Length: > 0 })
                {
                    var seriesId = query.AncestorIds[0];
                    if (seriesEpisodeMap != null && seriesEpisodeMap.TryGetValue(seriesId, out var episodeId))
                    {
                        return new List<BaseItem> { new MediaBrowser.Controller.Entities.TV.Episode { Id = episodeId, Path = "/media/ep.mkv" } };
                    }

                    return new List<BaseItem> { new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Path = "/media/ep.mkv" } };
                }

                return new List<BaseItem>();
            });
    }

    [Fact]
    public async Task UpdatePlaylists_CreatesPlaylistForEachUser()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var results = new List<RecommendationResult>
        {
            CreateResult(user1, "Alice", 5),
            CreateResult(user2, "Bob", 3)
        };

        SetupPlaylistQuery();
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.Equal(2, syncResult.PlaylistsCreated);
        Assert.Equal(8, syncResult.TotalItemsAdded);
        Assert.Equal(0, syncResult.PlaylistsFailed);
    }

    [Fact]
    public async Task UpdatePlaylists_SkipsUsersWithNoRecommendations()
    {
        var results = new List<RecommendationResult>
        {
            CreateResult(Guid.NewGuid(), "Alice", 0)
        };

        SetupPlaylistQuery();
        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.TotalItemsAdded);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylists_HandlesCreationFailureGracefully()
    {
        var results = new List<RecommendationResult>
        {
            CreateResult(Guid.NewGuid(), "Alice", 5)
        };

        SetupPlaylistQuery();
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ThrowsAsync(new InvalidOperationException("Playlist creation failed"));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.PlaylistsFailed);
    }

    [Fact]
    public async Task UpdatePlaylists_CancellationRespected()
    {
        var results = new List<RecommendationResult>
        {
            CreateResult(Guid.NewGuid(), "Alice", 5)
        };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.UpdatePlaylistsForAllUsersAsync(results, cts.Token));
    }

    [Fact]
    public async Task UpdatePlaylists_EmptyResultsList_Succeeds()
    {
        var results = new List<RecommendationResult>();
        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.TotalItemsAdded);
        Assert.Equal(0, syncResult.PlaylistsFailed);
    }

    [Fact]
    public void BuildPlaylistName_ContainsPrefixAndUserName()
    {
        var name = RecommendationPlaylistService.BuildPlaylistName("Alice");

        Assert.StartsWith(RecommendationPlaylistService.PlaylistNamePrefix, name);
        Assert.Contains("for Alice", name);
    }

    [Fact]
    public void BuildPlaylistName_FallsBackToYou_WhenNameEmpty()
    {
        var name = RecommendationPlaylistService.BuildPlaylistName("");

        Assert.StartsWith(RecommendationPlaylistService.PlaylistNamePrefix, name);
        Assert.Contains("for you", name);
    }

    [Fact]
    public async Task UpdatePlaylists_NullResults_ThrowsArgumentNull()
    {
        var sut = CreateSut();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.UpdatePlaylistsForAllUsersAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePlaylists_PreservesEngineOrder()
    {
        var userId = Guid.NewGuid();
        // Verify that the service preserves the engine's diversity-reranked order (no re-sort by score)
        var result = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = new Collection<RecommendedItem>
            {
                new() { ItemId = Guid.NewGuid(), Name = "Low",  ItemType = "Movie", Score = 0.30 },
                new() { ItemId = Guid.NewGuid(), Name = "High", ItemType = "Movie", Score = 0.95 },
                new() { ItemId = Guid.NewGuid(), Name = "Mid",  ItemType = "Movie", Score = 0.60 }
            }
        };
        var results = new List<RecommendationResult> { result };

        SetupPlaylistQuery();

        IReadOnlyList<Guid>? capturedItemIds = null;
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(req => capturedItemIds = req.ItemIdList)
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.NotNull(capturedItemIds);
        var expectedIds = result.Recommendations
            .Select(r => r.ItemId)
            .ToArray();
        Assert.Equal(expectedIds, capturedItemIds);
    }

    [Fact]
    public void ResolvePlaylistItemIds_MoviesPassedThrough()
    {
        var sut = CreateSut();
        var movieId = Guid.NewGuid();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = movieId, Name = "Test Movie", ItemType = "Movie", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Single(result);
        Assert.Equal(movieId, result[0]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesResolvedToFirstEpisode()
    {
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        SetupEpisodeResolution(new Dictionary<Guid, Guid> { { seriesId, episodeId } });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Breaking Bad", ItemType = "Series", Score = 0.95 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Single(result);
        Assert.Equal(episodeId, result[0]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesWithNoEpisodes_Skipped()
    {
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem>());

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = Guid.NewGuid(), Name = "Empty Series", ItemType = "Series", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolvePlaylistItemIds_MixedContent_CorrectlyResolved()
    {
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        SetupEpisodeResolution(new Dictionary<Guid, Guid> { { seriesId, episodeId } });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = movieId, Name = "Inception", ItemType = "Movie", Score = 0.95 },
            new() { ItemId = seriesId, Name = "Breaking Bad", ItemType = "Series", Score = 0.90 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Equal(2, result.Length);
        Assert.Equal(movieId, result[0]);
        Assert.Equal(episodeId, result[1]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_MultipleSeriesEachResolvedOnce()
    {
        var series1 = Guid.NewGuid();
        var series2 = Guid.NewGuid();
        var ep1 = Guid.NewGuid();
        var ep2 = Guid.NewGuid();
        SetupEpisodeResolution(new Dictionary<Guid, Guid>
        {
            { series1, ep1 },
            { series2, ep2 }
        });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = series1, Name = "Series A", ItemType = "Series", Score = 0.9 },
            new() { ItemId = series2, Name = "Series B", ItemType = "Series", Score = 0.8 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        // Each series should produce exactly one episode entry
        Assert.Equal(2, result.Length);
        Assert.Equal(ep1, result[0]);
        Assert.Equal(ep2, result[1]);
    }

    [Fact]
    public async Task UpdatePlaylists_WithSeriesRecommendations_ResolvesToEpisodes()
    {
        // Arrange - mixed recommendations with movies and series
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        var result = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = new Collection<RecommendedItem>
            {
                new() { ItemId = movieId, Name = "Movie", ItemType = "Movie", Score = 0.95 },
                new() { ItemId = seriesId, Name = "Series", ItemType = "Series", Score = 0.85 }
            }
        };

        SetupPlaylistQuery();
        SetupEpisodeResolution(new Dictionary<Guid, Guid> { { seriesId, episodeId } });

        IReadOnlyList<Guid>? capturedItemIds = null;
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(req => capturedItemIds = req.ItemIdList)
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();

        // Act
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { result }, CancellationToken.None);

        // Assert - playlist should contain movie ID + resolved episode ID (not series ID)
        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(2, syncResult.TotalItemsAdded);
        Assert.NotNull(capturedItemIds);
        Assert.Equal(2, capturedItemIds.Count);
        Assert.Contains(movieId, capturedItemIds);
        Assert.Contains(episodeId, capturedItemIds);
        Assert.DoesNotContain(seriesId, capturedItemIds);
    }

    private static BaseItem BuildFakePlaylist(string name)
    {
        // Playlist is a Folder subclass in Jellyfin; using the concrete Playlist type
        // keeps the property surface (Name, Id) intact for the SUT.
        return new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            Name = name
        };
    }

    private void SetupUserManagerSingleUser(Guid userId, string username)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User(username, "default", "default")
        {
            Id = userId
        };
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { user });
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(user);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_ExactNameMatch_IsRemoved()
    {
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var managed = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { managed });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(managed, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_NumericSuffix_IsRemoved()
    {
        // Jellyfin auto-appends numeric suffixes to disambiguate identical playlist names.
        // We must recognize e.g. "Recommended for Alice1", "Alice2", "Alice42" as managed.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var expected = RecommendationPlaylistService.BuildPlaylistName("Alice");
        var suffix1 = BuildFakePlaylist(expected + "1");
        var suffix42 = BuildFakePlaylist(expected + "42");
        SetupPlaylistLookup(new[] { suffix1, suffix42 });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        _libraryManagerMock.Verify(m => m.DeleteItem(suffix1, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(m => m.DeleteItem(suffix42, It.IsAny<DeleteOptions>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_NonNumericSuffix_IsPreserved()
    {
        // A user who manually copied our playlist and appended a custom suffix
        // (e.g. "Recommended for Alice_Backup") must NOT be silently deleted.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var expected = RecommendationPlaylistService.BuildPlaylistName("Alice");
        var manual = BuildFakePlaylist(expected + "_ManualCopy");
        var alsoManual = BuildFakePlaylist(expected + " Extra");
        SetupPlaylistLookup(new[] { manual, alsoManual });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_NameThatIsPrefixOfExpected_IsPreserved()
    {
        // Critical defense: a playlist named for another user whose username is a prefix
        // of the current user's must NOT be deleted (this is the case the "AllDigit" check
        // was explicitly introduced to prevent — see the guard comment in the SUT).
        // Setup: current user is "Alice"; playlist name uses "Al" prefix instead of "Alice".
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var otherUsersPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Al"));
        SetupPlaylistLookup(new[] { otherUsersPlaylist });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(otherUsersPlaylist, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_UnrelatedPlaylist_IsPreserved()
    {
        // A user-created playlist with no relation to our name pattern must be untouched.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var unrelated = BuildFakePlaylist("My Favorite 80s Movies");
        SetupPlaylistLookup(new[] { unrelated });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_UserNotFound_ReturnsZeroWithoutError()
    {
        // If GetUserById returns null, the SUT must silently skip that user (not throw).
        var userId = Guid.NewGuid();
        var user = new Jellyfin.Database.Implementations.Entities.User("Ghost", "default", "default") { Id = userId };
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { user });
        _userManagerMock
            .Setup(m => m.GetUserById(userId))
            .Returns((Jellyfin.Database.Implementations.Entities.User?)null);

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_MixedManagedAndUnrelated_OnlyManagedRemoved()
    {
        // End-to-end sanity check: ensure only the exact managed names are removed
        // while all other playlists remain in place.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var expected = RecommendationPlaylistService.BuildPlaylistName("Alice");

        var managedExact = BuildFakePlaylist(expected);
        var managedNumeric = BuildFakePlaylist(expected + "3");
        var otherUser = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Bob"));
        var manualCopy = BuildFakePlaylist(expected + "_Copy");
        var unrelated = BuildFakePlaylist("Weekend Watchlist");

        SetupPlaylistLookup(new BaseItem[] { managedExact, managedNumeric, otherUser, manualCopy, unrelated });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        _libraryManagerMock.Verify(m => m.DeleteItem(managedExact, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(m => m.DeleteItem(managedNumeric, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(m => m.DeleteItem(otherUser, It.IsAny<DeleteOptions>()), Times.Never);
        _libraryManagerMock.Verify(m => m.DeleteItem(manualCopy, It.IsAny<DeleteOptions>()), Times.Never);
        _libraryManagerMock.Verify(m => m.DeleteItem(unrelated, It.IsAny<DeleteOptions>()), Times.Never);
    }

    /// <summary>
    ///     Sets up the playlist-lookup query to return the given items for both the
    ///     initial existence probe and the subsequent cleanup lookup.
    /// </summary>
    private void SetupPlaylistLookup(IEnumerable<BaseItem> playlists)
    {
        var list = playlists.ToList();
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Playlist)))
            .Returns(list);
    }

    [Fact]
    public async Task UpdatePlaylists_CreatesNewPlaylistBeforeDeletingOldOnes()
    {
        // The order of side effects must be: Create -> Delete. If deletion ran first
        // and creation later failed, the user would be left with nothing.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var oldPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { oldPlaylist });

        var operations = new List<string>();
        _playlistManagerMock
            .Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(_ => operations.Add("Create"))
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        _libraryManagerMock
            .Setup(m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback<BaseItem, DeleteOptions>((_, _) => operations.Add("Delete"));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 3) },
            CancellationToken.None);

        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.OldPlaylistsRemoved);
        Assert.Equal(new[] { "Create", "Delete" }, operations);
    }

    [Fact]
    public async Task UpdatePlaylists_DoesNotDeleteFreshlyCreatedPlaylist()
    {
        // The cleanup pass runs against ALL managed-named playlists including the one
        // we just created. The SUT must exclude the freshly created ID; otherwise it
        // would delete its own new playlist.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var newPlaylistId = Guid.NewGuid();
        var freshlyCreated = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = newPlaylistId,
            Name = RecommendationPlaylistService.BuildPlaylistName("Alice")
        };
        SetupPlaylistLookup(new BaseItem[] { freshlyCreated });

        _playlistManagerMock
            .Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(newPlaylistId.ToString()));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 3) },
            CancellationToken.None);

        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.OldPlaylistsRemoved);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(freshlyCreated, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylists_NoRecommendations_StillRemovesStalePlaylists()
    {
        // When a user has zero recommendations we skip creation but MUST still clean
        // up any stale managed playlists so the user doesn't keep seeing outdated data.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var stale = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { stale });

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 0) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.OldPlaylistsRemoved);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()),
            Times.Never);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(stale, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_NoResolvableItems_StillRemovesStalePlaylists()
    {
        // A recommendation list of only unresolvable series (no episodes on disk)
        // yields zero playable items. The SUT must skip creation but still remove stale
        // playlists so the user isn't stuck with outdated recommendations.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var stale = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { stale });

        // Series exists in the recommendations, but the library returns no episodes.
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem>());

        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = Guid.NewGuid(), Name = "Ghost Series", ItemType = "Series", Score = 0.9 }
        };
        var result = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = recs
        };

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { result },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.OldPlaylistsRemoved);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()),
            Times.Never);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(stale, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_CreationReturnsEmptyId_CountsAsFailureAndPreservesOldPlaylists()
    {
        // Jellyfin's CreatePlaylist can return an empty ID under edge conditions
        // (e.g. permission failure). This must count as a failure — and critically,
        // old playlists must be PRESERVED so we don't leave the user without anything.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var oldPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { oldPlaylist });

        _playlistManagerMock
            .Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(string.Empty));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 3) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.PlaylistsFailed);
        Assert.Equal(0, syncResult.OldPlaylistsRemoved);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylists_CreationThrows_PreservesOldPlaylists()
    {
        // When creation throws, we count it as a failure and MUST NOT delete old
        // playlists — otherwise a transient error would wipe the user's playlist.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var oldPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { oldPlaylist });

        _playlistManagerMock
            .Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ThrowsAsync(new InvalidOperationException("transient"));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 3) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.PlaylistsFailed);
        Assert.Equal(0, syncResult.OldPlaylistsRemoved);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    /// <summary>
    ///     Builds an <see cref="Episode"/> with explicit season/episode indexes and path.
    /// </summary>
    private static Episode BuildEpisode(int? season, int? episode, string path = "/media/ep.mkv")
    {
        return new Episode
        {
            Id = Guid.NewGuid(),
            ParentIndexNumber = season,
            IndexNumber = episode,
            Path = path
        };
    }

    [Fact]
    public void ResolvePlaylistItemIds_RespectsMaxItems()
    {
        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = Guid.NewGuid(), Name = "M1", ItemType = "Movie", Score = 0.9 },
            new() { ItemId = Guid.NewGuid(), Name = "M2", ItemType = "Movie", Score = 0.8 },
            new() { ItemId = Guid.NewGuid(), Name = "M3", ItemType = "Movie", Score = 0.7 },
            new() { ItemId = Guid.NewGuid(), Name = "M4", ItemType = "Movie", Score = 0.6 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, maxItems: 2);

        Assert.Equal(2, result.Length);
        Assert.Equal(recs[0].ItemId, result[0]);
        Assert.Equal(recs[1].ItemId, result[1]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_UnresolvableSeries_BackfillsFromLaterCandidates()
    {
        // A series without episodes is skipped, and the resolver continues iterating so
        // the final list still reaches maxItems whenever enough valid candidates remain.
        var brokenSeries = Guid.NewGuid();
        var validSeries = Guid.NewGuid();
        var validEpisode = Guid.NewGuid();
        var trailingMovie = Guid.NewGuid();

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns<InternalItemsQuery>(query =>
            {
                if (query.AncestorIds is { Length: > 0 } && query.AncestorIds[0] == validSeries)
                {
                    return new List<BaseItem> { new Episode { Id = validEpisode, Path = "/media/ep.mkv" } };
                }

                return new List<BaseItem>();
            });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = brokenSeries, Name = "Broken", ItemType = "Series", Score = 0.95 },
            new() { ItemId = validSeries, Name = "Valid", ItemType = "Series", Score = 0.90 },
            new() { ItemId = trailingMovie, Name = "Movie", ItemType = "Movie", Score = 0.80 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, maxItems: 2);

        Assert.Equal(2, result.Length);
        Assert.Equal(validEpisode, result[0]);
        Assert.Equal(trailingMovie, result[1]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesResolution_DeprioritizesSpecials()
    {
        // When a series has both a specials episode (season 0) and a regular pilot,
        // the resolver must pick the pilot (S01E01), not the special (S00E01).
        var seriesId = Guid.NewGuid();
        var specialEpisode = BuildEpisode(season: 0, episode: 1);
        var pilotEpisode = BuildEpisode(season: 1, episode: 1);

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { specialEpisode, pilotEpisode });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Show", ItemType = "Series", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Single(result);
        Assert.Equal(pilotEpisode.Id, result[0]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesResolution_SortsBySeasonThenEpisode()
    {
        // Given an arbitrarily ordered mix of episodes across multiple seasons,
        // the resolver must pick S01E01 as the "first" episode.
        var seriesId = Guid.NewGuid();
        var s2e5 = BuildEpisode(2, 5);
        var s1e3 = BuildEpisode(1, 3);
        var s1e1 = BuildEpisode(1, 1);
        var s3e1 = BuildEpisode(3, 1);

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { s2e5, s1e3, s1e1, s3e1 });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Show", ItemType = "Series", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Single(result);
        Assert.Equal(s1e1.Id, result[0]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesResolution_SkipsEpisodesWithoutPath()
    {
        // Episodes without a file path represent unavailable media (metadata-only stubs)
        // and must never end up as the resolved first episode.
        var seriesId = Guid.NewGuid();
        var missingPathEpisode = BuildEpisode(1, 1, path: string.Empty);
        var playableEpisode = BuildEpisode(1, 2);

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { missingPathEpisode, playableEpisode });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Show", ItemType = "Series", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Single(result);
        Assert.Equal(playableEpisode.Id, result[0]);
    }

    [Theory]
    [InlineData("Series")]
    [InlineData("series")]
    [InlineData("SERIES")]
    [InlineData("SeRiEs")]
    public void ResolvePlaylistItemIds_SeriesTypeMatch_IsCaseInsensitive(string itemType)
    {
        // The ItemType string arrives from serialized recommendation payloads and may
        // vary in casing across engine versions. The resolver contract compares it with
        // OrdinalIgnoreCase, so every casing must trigger series resolution.
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        SetupEpisodeResolution(new Dictionary<Guid, Guid> { { seriesId, episodeId } });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Show", ItemType = itemType, Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Single(result);
        Assert.Equal(episodeId, result[0]);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_MultipleUsers_AggregatesRemovedCount()
    {
        // With two users each owning a managed playlist, the returned total must equal
        // the sum of removals across all users.
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var alice = new Jellyfin.Database.Implementations.Entities.User("Alice", "default", "default") { Id = aliceId };
        var bob = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = bobId };
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { alice, bob });
        _userManagerMock.Setup(m => m.GetUserById(aliceId)).Returns(alice);
        _userManagerMock.Setup(m => m.GetUserById(bobId)).Returns(bob);

        var alicePlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        var bobPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Bob"));

        // The library query is called once per user; return the full set both times and
        // let the SUT filter by each user's expected name.
        SetupPlaylistLookup(new BaseItem[] { alicePlaylist, bobPlaylist });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        _libraryManagerMock.Verify(m => m.DeleteItem(alicePlaylist, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(m => m.DeleteItem(bobPlaylist, It.IsAny<DeleteOptions>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_OneUserThrows_ContinuesWithRemainingUsers()
    {
        // A failure while processing one user must not abort the whole cleanup; other
        // users' playlists must still be removed.
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var alice = new Jellyfin.Database.Implementations.Entities.User("Alice", "default", "default") { Id = aliceId };
        var bob = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = bobId };
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { alice, bob });
        _userManagerMock.Setup(m => m.GetUserById(aliceId)).Throws(new InvalidOperationException("db hiccup"));
        _userManagerMock.Setup(m => m.GetUserById(bobId)).Returns(bob);

        var bobPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Bob"));
        SetupPlaylistLookup(new BaseItem[] { bobPlaylist });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        _libraryManagerMock.Verify(m => m.DeleteItem(bobPlaylist, It.IsAny<DeleteOptions>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_CancellationRespected()
    {
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.RemoveAllRecommendationPlaylistsAsync(cts.Token));

        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }
}
