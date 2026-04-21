using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

public class WatchHistoryServiceTests
{
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IUserManager> _mockUserManager;
    private readonly Mock<IUserDataManager> _mockUserDataManager;
    private readonly Mock<IPluginLogService> _mockPluginLog;
    private readonly Mock<ILogger<WatchHistoryService>> _mockLogger;
    private readonly WatchHistoryService _service;

    public WatchHistoryServiceTests()
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

    // --- GetUserWatchProfile ---

    [Fact]
    public void GetUserWatchProfile_UserNotFound_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        _mockUserManager
            .Setup(m => m.GetUserById(userId))
            .Returns((Jellyfin.Database.Implementations.Entities.User?)null);

        var result = _service.GetUserWatchProfile(userId);

        Assert.Null(result);
    }

    // --- GetAllUserWatchProfiles ---

    [Fact]
    public void GetAllUserWatchProfiles_NoUsers_ReturnsEmptyCollection()
    {
        _mockUserManager
            .Setup(m => m.Users)
            .Returns(Enumerable.Empty<Jellyfin.Database.Implementations.Entities.User>().AsQueryable());

        // Library returns empty item list
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var result = _service.GetAllUserWatchProfiles();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetAllUserWatchProfiles_ExceptionInBuildProfile_SkipsUserAndContinues()
    {
        // Use a testable subclass that overrides BuildProfile to throw for a specific user
        var throwingService = new ThrowingWatchHistoryService(
            _mockLibraryManager.Object,
            _mockUserManager.Object,
            _mockUserDataManager.Object,
            _mockPluginLog.Object,
            _mockLogger.Object);

        var user1 = CreateMockUser("alice");
        var user2 = CreateMockUser("bob-throws");
        var user3 = CreateMockUser("charlie");

        _mockUserManager
            .Setup(m => m.Users)
            .Returns(new[] { user1, user2, user3 }.AsQueryable());

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var result = throwingService.GetAllUserWatchProfiles();

        // user2 threw, so only user1 and user3 should be in results
        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.UserName == "alice");
        Assert.Contains(result, p => p.UserName == "charlie");
        Assert.DoesNotContain(result, p => p.UserName == "bob-throws");
    }

    [Fact]
    public void GetAllUserWatchProfiles_ReturnsProfilesForAllValidUsers()
    {
        var user1 = CreateMockUser("alice");
        var user2 = CreateMockUser("bob");

        _mockUserManager
            .Setup(m => m.Users)
            .Returns(new[] { user1, user2 }.AsQueryable());

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        // UserData returns null for all items (no interaction)
        _mockUserDataManager
            .Setup(m => m.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        var result = _service.GetAllUserWatchProfiles();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.UserName == "alice");
        Assert.Contains(result, p => p.UserName == "bob");
    }

    // --- LoadAllVideoItems ---

    [Fact]
    public void LoadAllVideoItems_DelegatesWithVideoMediaType()
    {
        InternalItemsQuery? capturedQuery = null;
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        _service.LoadAllVideoItems();

        Assert.NotNull(capturedQuery);
        Assert.Contains(MediaType.Video, capturedQuery!.MediaTypes);
        Assert.False(capturedQuery.IsFolder);
    }

    // --- Helpers ---

    private static Jellyfin.Database.Implementations.Entities.User CreateMockUser(string username)
    {
        // Create user entity directly — it's a simple POCO in Jellyfin
        return new Jellyfin.Database.Implementations.Entities.User(username, "default", "default")
        {
            Id = Guid.NewGuid()
        };
    }

    /// <summary>
    ///     Testable subclass that overrides BuildProfile to throw for users
    ///     whose username contains "throws", allowing resilience testing.
    /// </summary>
    private sealed class ThrowingWatchHistoryService : WatchHistoryService
    {
        public ThrowingWatchHistoryService(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            IPluginLogService pluginLog,
            ILogger<WatchHistoryService> logger)
            : base(libraryManager, userManager, userDataManager, pluginLog, logger)
        {
        }

        internal override UserWatchProfile BuildProfile(
            Jellyfin.Database.Implementations.Entities.User user,
            IReadOnlyList<BaseItem>? allItems = null)
        {
            if (user.Username.Contains("throws", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Simulated failure for {user.Username}");
            }

            return new UserWatchProfile
            {
                UserId = user.Id,
                UserName = user.Username
            };
        }
    }
}