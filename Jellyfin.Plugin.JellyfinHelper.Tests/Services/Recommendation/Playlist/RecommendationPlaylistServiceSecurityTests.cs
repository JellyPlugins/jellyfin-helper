using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
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
///     Security guards on the DB-item fallback delete: a drifted/hostile <c>playlist.Path</c>
///     must never trigger a recursive <c>Directory.Delete</c> outside Jellyfin's playlists
///     root - including the root itself, which would wipe every user's playlists.
/// </summary>
public sealed class RecommendationPlaylistServiceSecurityTests : IDisposable
{
    private readonly Mock<IPlaylistManager> _playlistManagerMock = new();
    private readonly Mock<IUserManager> _userManagerMock = new();
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger<RecommendationPlaylistService>> _loggerMock = new();

    private readonly string _playlistsRoot =
        Path.Combine(Path.GetTempPath(), "rec-playlist-sec-" + Guid.NewGuid().ToString("N"));

    public RecommendationPlaylistServiceSecurityTests()
    {
        Directory.CreateDirectory(_playlistsRoot);
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

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(true)] // Path == the playlists root itself (equals-root rejection).
    [InlineData(false)] // Path is a sibling location outside the root (general outside-root skip).
    public async Task RemoveAllRecommendationPlaylists_FallbackPathEscapesPlaylistsRoot_NeverRecursivelyDeleted(
        bool pathEqualsRoot)
    {
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        string hostilePath;
        if (pathEqualsRoot)
        {
            // The most dangerous drift: the item Path resolves to the playlists root itself.
            // A recursive delete here would destroy every user's playlists.
            hostilePath = _playlistsRoot;
        }
        else
        {
            // A sibling temp dir outside the playlists root that genuinely exists on disk.
            hostilePath = Path.Combine(Path.GetTempPath(), "rec-playlist-sec-escape-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(hostilePath);
        }

        var managed = BuildManagedPlaylist("Alice", hostilePath);
        SetupPlaylistLookup(new BaseItem[] { managed });

        _playlistManagerMock
            .Setup(m => m.GetPlaylistsFolder())
            .Returns(new Folder { Path = _playlistsRoot });

        // Force the fallback: the file-location delete throws, DB-item delete succeeds.
        _libraryManagerMock
            .Setup(m => m.DeleteItem(managed, It.Is<DeleteOptions>(o => o.DeleteFileLocation)))
            .Throws(new InvalidOperationException("drifted path"));

        try
        {
            var sut = CreateSut();
            var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

            // DB item removed so it can't resurrect, but the folder must be left intact.
            Assert.Equal(1, removed);
            Assert.True(Directory.Exists(hostilePath));
            _pluginLogMock.Verify(
                m => m.LogWarning(
                    "PlaylistSync",
                    It.Is<string>(s => s.Contains("Skipped recursive delete", StringComparison.Ordinal)),
                    It.IsAny<Exception?>(),
                    It.IsAny<ILogger?>()),
                Times.Once);
        }
        finally
        {
            if (!pathEqualsRoot && Directory.Exists(hostilePath))
            {
                Directory.Delete(hostilePath, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_playlistsRoot))
        {
            Directory.Delete(_playlistsRoot, recursive: true);
        }
    }
}
