using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.WatchHistory;

/// <summary>
///     Tests for <see cref="WatchHistoryService.GetAllUserWatchProfiles"/> verifying correct
///     usage of IUserManager.GetUsers() (Jellyfin 10.11.8+ API).
/// </summary>
public sealed class WatchHistoryCompatTests
{
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IUserManager> _mockUserManager;
    private readonly Mock<IUserDataManager> _mockUserDataManager;
    private readonly Mock<IPluginLogService> _mockPluginLog;
    private readonly Mock<ILogger<WatchHistoryService>> _mockLogger;
    private readonly WatchHistoryService _service;

    public WatchHistoryCompatTests()
    {
        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockUserManager = new Mock<IUserManager>();
        _mockUserDataManager = new Mock<IUserDataManager>();
        _mockPluginLog = new Mock<IPluginLogService>();
        _mockLogger = new Mock<ILogger<WatchHistoryService>>();

        _service = new WatchHistoryService(
            _mockLibraryManager.Object,
            _mockUserManager.Object,
            _mockUserDataManager.Object,
            _mockPluginLog.Object,
            _mockLogger.Object);
    }

    [Fact]
    public void GetAllUserWatchProfiles_CallsGetUsers_ReturnsProfiles()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("testuser", "default", "default")
        {
            Id = Guid.NewGuid()
        };

        _mockUserManager
            .Setup(m => m.GetUsers())
            .Returns(new[] { user });

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var result = _service.GetAllUserWatchProfiles();

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("testuser", result[0].UserName);
    }

    [Fact]
    public void GetAllUserWatchProfiles_NoUsers_ReturnsEmptyCollection()
    {
        _mockUserManager
            .Setup(m => m.GetUsers())
            .Returns(Enumerable.Empty<Jellyfin.Database.Implementations.Entities.User>());

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var result = _service.GetAllUserWatchProfiles();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetAllUserWatchProfiles_MultipleUsers_ReturnsAllProfiles()
    {
        var user1 = new Jellyfin.Database.Implementations.Entities.User("alice", "default", "default")
        {
            Id = Guid.NewGuid()
        };
        var user2 = new Jellyfin.Database.Implementations.Entities.User("bob", "default", "default")
        {
            Id = Guid.NewGuid()
        };

        _mockUserManager
            .Setup(m => m.GetUsers())
            .Returns(new[] { user1, user2 });

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var result = _service.GetAllUserWatchProfiles();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.UserName == "alice");
        Assert.Contains(result, p => p.UserName == "bob");
    }

    [Fact]
    public void GetAllUserWatchProfiles_GetUsersIsCalled_NotUsersProperty()
    {
        _mockUserManager
            .Setup(m => m.GetUsers())
            .Returns(Enumerable.Empty<Jellyfin.Database.Implementations.Entities.User>());

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        _service.GetAllUserWatchProfiles();

        // Verify GetUsers() was called (not the deprecated Users property)
        _mockUserManager.Verify(m => m.GetUsers(), Times.Once);
    }
}