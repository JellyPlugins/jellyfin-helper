using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.WatchHistory;

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
        var user1 = CreateTestUser("alice");
        var user2 = CreateTestUser("bob-throws");
        var user3 = CreateTestUser("charlie");

        _mockUserManager
            .Setup(m => m.Users)
            .Returns(new[] { user1, user2, user3 }.AsQueryable());

        var movie = new Movie { Id = Guid.NewGuid(), Name = "Test Movie" };
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        _mockUserDataManager
            .Setup(m => m.GetUserData(
                It.Is<Jellyfin.Database.Implementations.Entities.User>(u => u.Username == "bob-throws"),
                It.IsAny<BaseItem>()))
            .Throws(new InvalidOperationException("Simulated failure for bob-throws"));

        _mockUserDataManager
            .Setup(m => m.GetUserData(
                It.Is<Jellyfin.Database.Implementations.Entities.User>(u => u.Username != "bob-throws"),
                It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        var result = _service.GetAllUserWatchProfiles();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.UserName == "alice");
        Assert.Contains(result, p => p.UserName == "charlie");
        Assert.DoesNotContain(result, p => p.UserName == "bob-throws");
    }

    [Fact]
    public void GetAllUserWatchProfiles_ReturnsProfilesForAllValidUsers()
    {
        var user1 = CreateTestUser("alice");
        var user2 = CreateTestUser("bob");

        _mockUserManager
            .Setup(m => m.Users)
            .Returns(new[] { user1, user2 }.AsQueryable());

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

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
        Assert.Equal(false, capturedQuery.IsFolder);
    }

    // --- BuildProfile Tests ---

    [Fact]
    public void BuildProfile_MoviePlayed_IncrementsMovieCount()
    {
        var user = CreateTestUser("alice");

        _mockUserManager
            .Setup(m => m.GetUserById(user.Id))
            .Returns(user);

        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Test Movie",
            Genres = new[] { "Action", "Thriller" },
            CommunityRating = 7.5f,
            ProductionYear = 2023,
            RunTimeTicks = TimeSpan.FromMinutes(120).Ticks
        };

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        _mockUserDataManager
            .Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData
            {
                Key = "movie-key",
                Played = true,
                PlayCount = 1,
                LastPlayedDate = DateTime.UtcNow
            });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Equal(1, profile!.WatchedMovieCount);
        Assert.Equal(0, profile.WatchedEpisodeCount);
        Assert.Equal(0, profile.WatchedSeriesCount);
        Assert.Single(profile.WatchedItems);
        Assert.Equal("Test Movie", profile.WatchedItems[0].Name);
        Assert.True(profile.TotalWatchTimeTicks > 0);
    }

    [Fact]
    public void BuildProfile_EpisodesFromSameSeries_CountsSeriesOnce()
    {
        var user = CreateTestUser("bob");

        _mockUserManager
            .Setup(m => m.GetUserById(user.Id))
            .Returns(user);

        var seriesId = Guid.NewGuid();
        var ep1 = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Episode 1",
            SeriesId = seriesId,
            Genres = new[] { "Drama" },
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        var ep2 = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Episode 2",
            SeriesId = seriesId,
            Genres = new[] { "Drama" },
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { ep1, ep2 });

        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData
            {
                Key = "episode-key",
                Played = true,
                PlayCount = 1,
                LastPlayedDate = DateTime.UtcNow
            });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Equal(2, profile!.WatchedEpisodeCount);
        Assert.Equal(1, profile.WatchedSeriesCount);
        Assert.Equal(0, profile.WatchedMovieCount);
    }

    [Fact]
    public void BuildProfile_GenreDistribution_CountsCorrectly()
    {
        var user = CreateTestUser("charlie");

        _mockUserManager
            .Setup(m => m.GetUserById(user.Id))
            .Returns(user);

        var movie1 = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Action Movie",
            Genres = new[] { "Action", "Thriller" }
        };
        var movie2 = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Action Comedy",
            Genres = new[] { "Action", "Comedy" }
        };

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie1, movie2 });

        _mockUserDataManager
            .Setup(m => m.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData
            {
                Key = "genre-key",
                Played = true,
                PlayCount = 1,
                LastPlayedDate = DateTime.UtcNow
            });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Equal(2, profile!.GenreDistribution["Action"]);
        Assert.Equal(1, profile.GenreDistribution["Thriller"]);
        Assert.Equal(1, profile.GenreDistribution["Comedy"]);
    }

    [Fact]
    public void BuildProfile_UnplayedItems_AreExcluded()
    {
        var user = CreateTestUser("dave");

        _mockUserManager
            .Setup(m => m.GetUserById(user.Id))
            .Returns(user);

        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Unwatched Movie",
            Genres = new[] { "Horror" }
        };

        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        _mockUserDataManager
            .Setup(m => m.GetUserData(user, movie))
            .Returns(new UserItemData
            {
                Key = "unplayed-key",
                Played = false,
                PlayCount = 0,
                PlaybackPositionTicks = 0,
                IsFavorite = false
            });

        var profile = _service.GetUserWatchProfile(user.Id);

        Assert.NotNull(profile);
        Assert.Empty(profile!.WatchedItems);
        Assert.Equal(0, profile.WatchedMovieCount);
    }

    // --- Helpers ---

    private static Jellyfin.Database.Implementations.Entities.User CreateTestUser(string username)
    {
        return new Jellyfin.Database.Implementations.Entities.User(username, "default", "default")
        {
            Id = Guid.NewGuid()
        };
    }
}
