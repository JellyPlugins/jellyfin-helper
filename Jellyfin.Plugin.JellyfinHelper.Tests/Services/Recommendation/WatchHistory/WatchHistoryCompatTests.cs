using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.WatchHistory;

/// <summary>
///     Tests for <see cref="WatchHistoryService.GetAllUserWatchProfiles"/> compatibility handling.
///     Verifies that the service gracefully handles binary incompatibilities between the
///     NuGet compile-time API and the actual runtime Jellyfin assemblies (LXC, native packages).
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
    public void GetAllUserWatchProfiles_UsersPropertyWorks_ReturnsProfiles()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("testuser", "default", "default")
        {
            Id = Guid.NewGuid()
        };

        _mockUserManager
            .Setup(m => m.Users)
            .Returns(new[] { user }.AsQueryable());

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var result = _service.GetAllUserWatchProfiles();

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("testuser", result[0].UserName);
    }

    [Fact]
    public void GetAllUserWatchProfiles_MissingMethodException_ReturnsEmpty()
    {
        _mockUserManager
            .Setup(m => m.Users)
            .Throws(new MissingMethodException("get_Users not found"));

        var result = _service.GetAllUserWatchProfiles();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetAllUserWatchProfiles_MissingMethodException_LogsWarningWithMessage()
    {
        _mockUserManager
            .Setup(m => m.Users)
            .Throws(new MissingMethodException("get_Users not found"));

        _service.GetAllUserWatchProfiles();

        _mockPluginLog.Verify(
            l => l.LogWarning(
                "WatchHistory",
                It.Is<string>(msg =>
                    msg.Contains("IUserManager API incompatible")
                    && msg.Contains("get_Users not found")
                    && msg.Contains("Discovery skipped")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public void GetAllUserWatchProfiles_MissingMemberException_ReturnsEmpty()
    {
        _mockUserManager
            .Setup(m => m.Users)
            .Throws(new MissingMemberException("Users member not found"));

        var result = _service.GetAllUserWatchProfiles();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetAllUserWatchProfiles_TypeLoadException_ReturnsEmpty()
    {
        _mockUserManager
            .Setup(m => m.Users)
            .Throws(new TypeLoadException("Could not load type"));

        var result = _service.GetAllUserWatchProfiles();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetAllUserWatchProfiles_UnexpectedException_Throws()
    {
        _mockUserManager
            .Setup(m => m.Users)
            .Throws(new InvalidOperationException("Unexpected"));

        Assert.Throws<InvalidOperationException>(() => _service.GetAllUserWatchProfiles());
    }
}