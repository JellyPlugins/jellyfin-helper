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

    private void SetupPlaylistQuery()
    {
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Playlist)))
            .Returns(new List<BaseItem>());
    }

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
                        return new List<BaseItem> { new MediaBrowser.Controller.Entities.TV.Episode { Id = episodeId } };
                    }

                    return new List<BaseItem> { new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid() } };
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
    public async Task UpdatePlaylists_ItemsOrderedByScoreDescending()
    {
        var userId = Guid.NewGuid();
        var result = CreateResult(userId, "Alice", 3);
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
            .OrderByDescending(r => r.Score)
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
        // Arrange — mixed recommendations with movies and series
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

        // Assert — playlist should contain movie ID + resolved episode ID (not series ID)
        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(2, syncResult.TotalItemsAdded);
        Assert.NotNull(capturedItemIds);
        Assert.Equal(2, capturedItemIds.Count);
        Assert.Contains(movieId, capturedItemIds);
        Assert.Contains(episodeId, capturedItemIds);
        Assert.DoesNotContain(seriesId, capturedItemIds);
    }
}
