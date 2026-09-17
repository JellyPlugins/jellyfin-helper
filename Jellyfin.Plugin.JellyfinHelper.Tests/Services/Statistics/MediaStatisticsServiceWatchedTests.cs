using System.IO;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class MediaStatisticsServiceWatchedTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;

    public MediaStatisticsServiceWatchedTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
    }

    private static string TestPath(params string[] segments) => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    private TestableMediaStatisticsService CreateService()
    {
        var loggerMock = TestMockFactory.CreateLogger<MediaStatisticsService>();
        var configHelperMock = TestMockFactory.CreateCleanupConfigHelper();
        return new TestableMediaStatisticsService(_libraryManagerMock.Object, _fileSystemMock.Object, TestMockFactory.CreatePluginLogService(), loggerMock.Object, configHelperMock.Object, _userDataManagerMock.Object, _userManagerMock.Object);
    }

    private void SetupUserManagerWithUsers(params (string username, int playCount)[] users)
        => SetupUserManagerWithUsers([], users);

    private void SetupUserManagerWithUsers(string[] disabledUsernames, params (string username, int playCount)[] users)
    {
        var userList = new List<User>();
        foreach (var (username, _) in users)
        {
            var user = new User(username, "default", "default") { Id = Guid.NewGuid() };
            if (disabledUsernames.Contains(username, StringComparer.Ordinal))
            {
                user.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
            }

            userList.Add(user);
        }

        _userManagerMock.Setup(m => m.GetUsers()).Returns(userList.AsQueryable());

        foreach (var (username, playCount) in users)
        {
            var user = userList.First(u => u.Username == username);
            _userDataManagerMock.Setup(m => m.GetUserData(user, It.IsAny<BaseItem>())).Returns(new UserItemData { Key = "key", PlayCount = playCount });
        }
    }

    private void SetupLibraryWithVideo(string filePath, long fileSize = 1_000_000_000)
    {
        var libraryPath = TestPath("media", "movies");
        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = Path.GetFileName(filePath),
            Length = fileSize,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([]);
    }

    [Fact]
    public void Watched_ZeroUsers_RecordsNeverWatched()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers();
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = path;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 }
        ]);
        var service = CreateService();
        service.SetItemLookup(path, mockItem.Object);

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.WatchedTiers["Never watched"]);
        Assert.Contains(path, stats.WatchedTierPaths["Never watched"]);
        Assert.Empty(stats.WatchedByUsers);
    }

    [Fact]
    public void Watched_OneUser_RecordsWatchedTierAndPerUserPaths()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers(("Alice", 1));
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = path;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 }
        ]);
        var service = CreateService();
        service.SetItemLookup(path, mockItem.Object);

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.WatchedTiers["Watched"]);
        Assert.Contains(path, stats.WatchedTierPaths["Watched"]);
        Assert.Single(stats.WatchedByUsers[path]);
        Assert.Contains("Alice", stats.WatchedByUsers[path]);
        Assert.Single(stats.WatchedByUserPaths["Alice"]);
        Assert.Contains(path, stats.WatchedByUserPaths["Alice"]);
        Assert.Single(stats.WatchedDetails[path]);
        Assert.Equal("Alice", stats.WatchedDetails[path].First().Username);
        Assert.Equal(1, stats.WatchedDetails[path].First().PlayCount);
    }

    [Fact]
    public void Watched_TwoUsers_RecordsWatchedTierAndBothUsers()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers(("Alice", 1), ("Bob", 2));
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = path;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 }
        ]);
        var service = CreateService();
        service.SetItemLookup(path, mockItem.Object);

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.WatchedTiers["Watched"]);
        Assert.Single(stats.WatchedByUserPaths["Alice"]);
        Assert.Single(stats.WatchedByUserPaths["Bob"]);
        Assert.Equal(2, stats.WatchedDetails[path].Count);
    }

    [Fact]
    public void Watched_FourUsers_RecordsWatchedTierWithAllPerUserEntries()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers(("A", 1), ("B", 1), ("C", 1), ("D", 1));
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = path;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 }
        ]);
        var service = CreateService();
        service.SetItemLookup(path, mockItem.Object);

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.WatchedTiers["Watched"]);
        Assert.Equal(4, stats.WatchedDetails[path].Count);
        Assert.Single(stats.WatchedByUserPaths["A"]);
        Assert.Single(stats.WatchedByUserPaths["D"]);
    }

    [Fact]
    public void Watched_UserPlayCountZero_NotCountedAsWatched()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers(("Alice", 0));
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = path;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 }
        ]);
        var service = CreateService();
        service.SetItemLookup(path, mockItem.Object);

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.WatchedTiers["Never watched"]);
        Assert.Empty(stats.WatchedByUsers);
    }

    [Fact]
    public void Watched_DisabledUserWithPlays_NotCountedAsWatched()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers(["Dave"], ("Dave", 5));
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = path;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 }
        ]);
        var service = CreateService();
        service.SetItemLookup(path, mockItem.Object);

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.WatchedTiers["Never watched"]);
        Assert.Empty(stats.WatchedByUsers);
        Assert.False(stats.WatchedByUserPaths.ContainsKey("Dave"));
    }

    [Fact]
    public void Watched_DisabledAndEnabledUsers_OnlyEnabledUserCounts()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers(["Dave"], ("Alice", 1), ("Dave", 5));
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = path;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 }
        ]);
        var service = CreateService();
        service.SetItemLookup(path, mockItem.Object);

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.WatchedTiers["Watched"]);
        Assert.Single(stats.WatchedByUsers[path]);
        Assert.Contains("Alice", stats.WatchedByUsers[path]);
        Assert.False(stats.WatchedByUserPaths.ContainsKey("Dave"));
        Assert.Single(stats.WatchedDetails[path]);
        Assert.Equal("Alice", stats.WatchedDetails[path].First().Username);
    }

    [Fact]
    public void Watched_UnresolvedItem_CountsAsNeverWatched()
    {
        // No item in the lookup and FindByPath returns null on the mock: the file is
        // unknown to Jellyfin, so it must land in Never watched instead of vanishing.
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers(("Alice", 1));
        var service = CreateService();

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.WatchedTiers["Never watched"]);
        Assert.Contains(path, stats.WatchedTierPaths["Never watched"]);
        Assert.Empty(stats.WatchedByUsers);
    }

    [Fact]
    public void Watched_UserDataThrows_SkipsUserAndCountsOthers()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers(("Alice", 2), ("Dave", 5));
        var dave = _userManagerMock.Object.GetUsers().First(u => u.Username == "Dave");
        _userDataManagerMock.Setup(m => m.GetUserData(dave, It.IsAny<BaseItem>())).Throws<InvalidOperationException>();
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = path;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 }
        ]);
        var service = CreateService();
        service.SetItemLookup(path, mockItem.Object);

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.WatchedTiers["Watched"]);
        Assert.Single(stats.WatchedByUsers[path]);
        Assert.Contains("Alice", stats.WatchedByUsers[path]);
        Assert.False(stats.WatchedByUserPaths.ContainsKey("Dave"));
    }

    [Fact]
    public void WatchedByUsers_MultipleUsers_StoresAllUsernames()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        SetupLibraryWithVideo(path);
        SetupUserManagerWithUsers(("Alice", 1), ("Bob", 3));
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = path;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 }
        ]);
        var service = CreateService();
        service.SetItemLookup(path, mockItem.Object);

        var result = service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(2, stats.WatchedByUsers[path].Count);
        Assert.Contains("Alice", stats.WatchedByUsers[path]);
        Assert.Contains("Bob", stats.WatchedByUsers[path]);
    }

    private sealed class TestableMediaStatisticsService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService pluginLog,
        ILogger<MediaStatisticsService> logger,
        ICleanupConfigHelper configHelper,
        IUserDataManager userDataManager,
        IUserManager userManager)
        : MediaStatisticsService(libraryManager, fileSystem, pluginLog, logger, configHelper, userDataManager, userManager)
    {
        private readonly Dictionary<string, BaseItem> _itemLookup = new(StringComparer.OrdinalIgnoreCase);
        public void SetItemLookup(string filePath, BaseItem item) => _itemLookup[filePath] = item;
        internal override Dictionary<string, BaseItem> BuildItemLookup() => new(_itemLookup, StringComparer.OrdinalIgnoreCase);
    }
}
