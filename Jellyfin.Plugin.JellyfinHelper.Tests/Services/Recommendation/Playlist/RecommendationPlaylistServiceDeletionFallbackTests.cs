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

/// <summary>
///     Exercises the resilient deletion path in RecommendationPlaylistService: when DeleteItem(DeleteFileLocation=true) throws (a drifted/orphaned on-disk folder), the service falls back to a DB-item delete and best-effort folder cleanup.
/// </summary>
public sealed class RecommendationPlaylistServiceDeletionFallbackTests : IDisposable
{
    private readonly Mock<IPlaylistManager> _playlistManagerMock = new();
    private readonly Mock<IUserManager> _userManagerMock = new();
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger<RecommendationPlaylistService>> _loggerMock = new();

    // Real playlists root the SUT treats as the safe base directory.
    private readonly string _playlistsRoot =
        Path.Combine(Path.GetTempPath(), "rec-playlist-fallback-" + Guid.NewGuid().ToString("N"));

    // A separate root used to model an out-of-root drifted path.
    private readonly string _outsideRoot =
        Path.Combine(Path.GetTempPath(), "rec-playlist-outside-" + Guid.NewGuid().ToString("N"));

    public RecommendationPlaylistServiceDeletionFallbackTests()
    {
        Directory.CreateDirectory(_playlistsRoot);
        Directory.CreateDirectory(_outsideRoot);
    }

    private RecommendationPlaylistService CreateSut() =>
        new(
            _playlistManagerMock.Object,
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            _pluginLogMock.Object,
            _loggerMock.Object);

    private void SetupUserManagerSingleUser(Guid userId, string username)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User(username, "default", "default")
        {
            Id = userId
        };
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { user });
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(user);
    }

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

    private static MediaBrowser.Controller.Playlists.Playlist BuildManagedPlaylist(string userName, string path) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = RecommendationPlaylistService.BuildPlaylistName(userName),
            Path = path
        };

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_FirstDeleteThrows_FallsBackToDbDeleteAndRemovesOrphanFolder()
    {
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        // Real folder strictly inside the playlists root - the recursive delete must run.
        var folder = Path.Combine(_playlistsRoot, "Recommended for Alice");
        Directory.CreateDirectory(folder);
        var managed = BuildManagedPlaylist("Alice", folder);
        SetupPlaylistLookup(new BaseItem[] { managed });

        _playlistManagerMock
            .Setup(m => m.GetPlaylistsFolder())
            .Returns(new Folder { Path = _playlistsRoot });

        // First DeleteItem (file location) throws; the DB-item fallback (false) succeeds.
        _libraryManagerMock
            .Setup(m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => o.DeleteFileLocation)))
            .Throws(new InvalidOperationException("on-disk folder path drifted"));

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        // The DB fallback succeeded, so the playlist still counts as removed.
        Assert.Equal(1, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => o.DeleteFileLocation)), Times.Once);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => !o.DeleteFileLocation)), Times.Once);
        // Orphan folder inside the safe root was recursively deleted by our fallback.
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_FallbackFolderOutsideRoot_SkipsRecursiveDeleteButStillCounts()
    {
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        // A real folder OUTSIDE the playlists root: fallback must not touch it.
        var folder = Path.Combine(_outsideRoot, "Recommended for Alice");
        Directory.CreateDirectory(folder);
        var managed = BuildManagedPlaylist("Alice", folder);
        SetupPlaylistLookup(new BaseItem[] { managed });

        _playlistManagerMock
            .Setup(m => m.GetPlaylistsFolder())
            .Returns(new Folder { Path = _playlistsRoot });

        _libraryManagerMock
            .Setup(m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => o.DeleteFileLocation)))
            .Throws(new InvalidOperationException("drifted path"));

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        // DB item removed so it can't be re-imported - counts as removed.
        Assert.Equal(1, removed);
        // The out-of-root folder is rejected by IsSafePath and must survive on disk.
        Assert.True(Directory.Exists(folder));
        _pluginLogMock.Verify(
            m => m.LogWarning(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("Skipped recursive delete", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_BothDeletesThrow_NotCountedAndWarns()
    {
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var folder = Path.Combine(_playlistsRoot, "Recommended for Alice");
        Directory.CreateDirectory(folder);
        var managed = BuildManagedPlaylist("Alice", folder);
        SetupPlaylistLookup(new BaseItem[] { managed });

        _playlistManagerMock
            .Setup(m => m.GetPlaylistsFolder())
            .Returns(new Folder { Path = _playlistsRoot });

        // Both the file-location delete and the DB-item fallback throw non-fatal.
        _libraryManagerMock
            .Setup(m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => o.DeleteFileLocation)))
            .Throws(new InvalidOperationException("file delete failed"));
        _libraryManagerMock
            .Setup(m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => !o.DeleteFileLocation)))
            .Throws(new InvalidOperationException("db delete failed"));

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        // Neither delete succeeded, so the playlist must NOT count as removed.
        Assert.Equal(0, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => o.DeleteFileLocation)), Times.Once);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => !o.DeleteFileLocation)), Times.Once);
        _pluginLogMock.Verify(
            m => m.LogWarning(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("Failed to remove playlist", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_InnerOperationCanceled_PropagatesWithoutCountingFailure()
    {
        // Non-cancelled token passes the top-of-loop guard; the OCE originates inside the
        // try body (CreatePlaylist) and must be re-thrown, not swallowed as a failure.
        var userId = Guid.NewGuid();
        var result = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = new System.Collections.ObjectModel.Collection<RecommendedItem>
            {
                new() { ItemId = Guid.NewGuid(), Name = "Movie", ItemType = "Movie", Score = 0.9 }
            }
        };

        SetupPlaylistLookup(Array.Empty<BaseItem>());
        _playlistManagerMock
            .Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.UpdatePlaylistsForAllUsersAsync(new List<RecommendationResult> { result }, CancellationToken.None));

        // The OCE catch rethrows before the generic non-fatal handler runs.
        _pluginLogMock.Verify(
            m => m.LogWarning(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("Failed to sync playlist", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_InnerOperationCanceled_Propagates()
    {
        // Non-cancelled token; the per-user playlist lookup raises OCE from inside the try,
        // which must reach the method's OCE catch and rethrow rather than be swallowed.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Playlist)))
            .Throws(new OperationCanceledException());

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None));

        _pluginLogMock.Verify(
            m => m.LogWarning(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("Failed to remove playlists for user", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_PerPlaylistDeleteCanceled_PropagatesAndSkipsFallback()
    {
        // Cancellation raised by the file-location DeleteItem must surface as OCE, not be
        // absorbed by the non-fatal fallback: cancelling a delete is not "the folder drifted".
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var folder = Path.Combine(_playlistsRoot, "Recommended for Alice");
        Directory.CreateDirectory(folder);
        var managed = BuildManagedPlaylist("Alice", folder);
        SetupPlaylistLookup(new BaseItem[] { managed });

        _playlistManagerMock
            .Setup(m => m.GetPlaylistsFolder())
            .Returns(new Folder { Path = _playlistsRoot });

        _libraryManagerMock
            .Setup(m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => o.DeleteFileLocation)))
            .Throws(new OperationCanceledException());

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None));

        // Fallback DB-item delete must never run once cancellation is in flight.
        _libraryManagerMock.Verify(
            m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => !o.DeleteFileLocation)), Times.Never);
        // No fallback log lines: neither the DB-fallback success nor the per-playlist failure warning.
        _pluginLogMock.Verify(
            m => m.LogInfo(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("via DB-item fallback", StringComparison.Ordinal)),
                It.IsAny<ILogger?>()),
            Times.Never);
        _pluginLogMock.Verify(
            m => m.LogWarning(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("Failed to remove playlist", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Never);
    }

    public void Dispose()
    {
        if (Directory.Exists(_playlistsRoot))
        {
            Directory.Delete(_playlistsRoot, recursive: true);
        }

        if (Directory.Exists(_outsideRoot))
        {
            Directory.Delete(_outsideRoot, recursive: true);
        }
    }
}
