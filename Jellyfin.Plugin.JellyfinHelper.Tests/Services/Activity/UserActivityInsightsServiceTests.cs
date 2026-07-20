using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Activity;

public class UserActivityInsightsServiceTests
{
    // === CalculateCompletion (internal static helper) ===

    [Fact]
    public void CalculateCompletion_Played_Returns100()
        => Assert.Equal(100.0, UserActivityInsightsService.CalculateCompletion(0, 1000, played: true));

    [Fact]
    public void CalculateCompletion_Played_IgnoresPosition()
        => Assert.Equal(100.0, UserActivityInsightsService.CalculateCompletion(500, 1000, played: true));

    [Fact]
    public void CalculateCompletion_ZeroRuntime_ReturnsZero()
        => Assert.Equal(0.0, UserActivityInsightsService.CalculateCompletion(500, 0, played: false));

    [Fact]
    public void CalculateCompletion_NegativeRuntime_ReturnsZero()
        => Assert.Equal(0.0, UserActivityInsightsService.CalculateCompletion(500, -100, played: false));

    [Fact]
    public void CalculateCompletion_ZeroPosition_ReturnsZero()
        => Assert.Equal(0.0, UserActivityInsightsService.CalculateCompletion(0, 1000, played: false));

    [Fact]
    public void CalculateCompletion_NegativePosition_ReturnsZero()
        => Assert.Equal(0.0, UserActivityInsightsService.CalculateCompletion(-100, 1000, played: false));

    [Fact]
    public void CalculateCompletion_HalfWatched_Returns50()
        => Assert.Equal(50.0, UserActivityInsightsService.CalculateCompletion(500, 1000, played: false));

    [Fact]
    public void CalculateCompletion_PartialWatch_RoundsToOneDecimal()
        => Assert.Equal(33.3, UserActivityInsightsService.CalculateCompletion(333, 1000, played: false), 1);

    [Fact]
    public void CalculateCompletion_ExceedsRuntime_CapsAt100()
        => Assert.Equal(100.0, UserActivityInsightsService.CalculateCompletion(1500, 1000, played: false));

    [Fact]
    public void CalculateCompletion_AlmostComplete_RoundsCorrectly()
        => Assert.Equal(99.9, UserActivityInsightsService.CalculateCompletion(999, 1000, played: false), 1);

    [Fact]
    public void CalculateCompletion_BothZero_ReturnsZero()
        => Assert.Equal(0.0, UserActivityInsightsService.CalculateCompletion(0, 0, played: false));

    [Fact]
    public void CalculateCompletion_Played_ZeroRuntime_StillReturns100()
        => Assert.Equal(100.0, UserActivityInsightsService.CalculateCompletion(0, 0, played: true));

    // === BuildActivityReport (end-to-end behavioral tests) ===
    // These lock in observable behavior so internal fetch-path swaps
    // (e.g. batch user-data on Jellyfin 12+) can be verified against the same suite.

    private static (
        UserActivityInsightsService Service,
        Mock<ILibraryManager> Library,
        Mock<IUserManager> UserManager,
        Mock<IUserDataManager> UserData) CreateSut()
    {
        var library = new Mock<ILibraryManager>();
        var userManager = new Mock<IUserManager>();
        var userData = new Mock<IUserDataManager>();
        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<UserActivityInsightsService>>();
        var service = new UserActivityInsightsService(
            library.Object, userManager.Object, userData.Object, pluginLog.Object, logger.Object);
        return (service, library, userManager, userData);
    }

    private static Jellyfin.Database.Implementations.Entities.User User(string username)
        => new(username, "default", "default") { Id = Guid.NewGuid() };

    private static UserItemData Played(int count = 1, DateTime? at = null)
        => new()
        {
            Key = Guid.NewGuid().ToString("N"),
            Played = true,
            PlayCount = count,
            PlaybackPositionTicks = 0,
            LastPlayedDate = at ?? DateTime.UtcNow,
            IsFavorite = false
        };

    private static UserItemData Partial(long positionTicks, DateTime? at = null)
        => new()
        {
            Key = Guid.NewGuid().ToString("N"),
            Played = false,
            PlayCount = 0,
            PlaybackPositionTicks = positionTicks,
            LastPlayedDate = at,
            IsFavorite = false
        };

    private static UserItemData Fav() => new()
    {
        Key = Guid.NewGuid().ToString("N"),
        Played = false,
        PlayCount = 0,
        PlaybackPositionTicks = 0,
        LastPlayedDate = null,
        IsFavorite = true
    };

    private static UserItemData Idle() => new()
    {
        Key = Guid.NewGuid().ToString("N"),
        Played = false,
        PlayCount = 0,
        PlaybackPositionTicks = 0,
        LastPlayedDate = null,
        IsFavorite = false
    };

    private static Movie NewMovie(string name, long runtimeMinutes = 100) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        RunTimeTicks = TimeSpan.FromMinutes(runtimeMinutes).Ticks,
        Genres = ["Drama"]
    };

    private static Episode NewEpisode(string series, int season, int number, string name = "Episode") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SeriesName = series,
        ParentIndexNumber = season,
        IndexNumber = number,
        RunTimeTicks = TimeSpan.FromMinutes(45).Ticks,
        Genres = []
    };

    [Fact]
    public void BuildActivityReport_NoUsers_ReturnsEmptyResult()
    {
        var (svc, lib, um, _) = CreateSut();
        um.Setup(m => m.GetUsers()).Returns(Enumerable.Empty<Jellyfin.Database.Implementations.Entities.User>());
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);

        var r = svc.BuildActivityReport();

        Assert.NotNull(r);
        Assert.Equal(0, r.TotalUsersAnalyzed);
        Assert.Equal(0, r.TotalItemsWithActivity);
        Assert.Equal(0L, r.TotalPlayCount);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void BuildActivityReport_NoItems_ReturnsEmptyItemsWithUserCount()
    {
        var (svc, lib, um, _) = CreateSut();
        var alice = User("alice");
        um.Setup(m => m.GetUsers()).Returns(new[] { alice });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);

        var r = svc.BuildActivityReport();

        Assert.Equal(1, r.TotalUsersAnalyzed);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void BuildActivityReport_UserDataNull_ItemIsExcluded()
    {
        var (svc, lib, um, ud) = CreateSut();
        var alice = User("alice");
        var movie = NewMovie("Ghost");
        um.Setup(m => m.GetUsers()).Returns(new[] { alice });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie]);
        ud.Setup(m => m.GetUserData(alice, movie)).Returns((UserItemData?)null);

        var r = svc.BuildActivityReport();

        Assert.Empty(r.Items);
    }

    [Fact]
    public void BuildActivityReport_IdleUserData_ItemIsExcluded()
    {
        var (svc, lib, um, ud) = CreateSut();
        var alice = User("alice");
        var movie = NewMovie("Untouched");
        um.Setup(m => m.GetUsers()).Returns(new[] { alice });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie]);
        ud.Setup(m => m.GetUserData(alice, movie)).Returns(Idle());

        var r = svc.BuildActivityReport();

        Assert.Empty(r.Items);
    }

    [Fact]
    public void BuildActivityReport_PlayedMovie_IncludedWithCorrectMetrics()
    {
        var (svc, lib, um, ud) = CreateSut();
        var alice = User("alice");
        var movie = NewMovie("Played Movie", 120);
        um.Setup(m => m.GetUsers()).Returns(new[] { alice });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie]);
        var lastPlayed = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        ud.Setup(m => m.GetUserData(alice, movie)).Returns(Played(count: 3, at: lastPlayed));

        var r = svc.BuildActivityReport();

        Assert.Single(r.Items);
        var s = r.Items.Single();
        Assert.Equal(movie.Id, s.ItemId);
        Assert.Equal("Played Movie", s.ItemName);
        Assert.Equal("Movie", s.ItemType);
        Assert.Equal(3, s.TotalPlayCount);
        Assert.Equal(1, s.UniqueViewers);
        Assert.Equal(100.0, s.AverageCompletionPercent);
        Assert.Equal(0, s.FavoriteCount);
        Assert.Equal(lastPlayed, s.MostRecentWatch);
        Assert.Single(s.UserActivities);
        Assert.Equal(alice.Id, s.UserActivities[0].UserId);
        Assert.True(s.UserActivities[0].Played);
        Assert.Equal(3L, r.TotalPlayCount);
    }

    [Fact]
    public void BuildActivityReport_FavoriteWithoutPlayback_IsFavoriteButNoViewer()
    {
        var (svc, lib, um, ud) = CreateSut();
        var alice = User("alice");
        var movie = NewMovie("Favorite Only");
        um.Setup(m => m.GetUsers()).Returns(new[] { alice });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie]);
        ud.Setup(m => m.GetUserData(alice, movie)).Returns(Fav());

        var r = svc.BuildActivityReport();

        Assert.Single(r.Items);
        var s = r.Items.Single();
        Assert.Equal(1, s.FavoriteCount);
        Assert.Equal(0, s.UniqueViewers);
        Assert.Equal(0, s.TotalPlayCount);
        Assert.Equal(0.0, s.AverageCompletionPercent);
        Assert.Null(s.MostRecentWatch);
        Assert.Single(s.UserActivities);
        Assert.True(s.UserActivities[0].IsFavorite);
    }

    [Fact]
    public void BuildActivityReport_PartialPlayback_ReportsCompletionPercent()
    {
        var (svc, lib, um, ud) = CreateSut();
        var alice = User("alice");
        var movie = NewMovie("Half Watched", 100);
        um.Setup(m => m.GetUsers()).Returns(new[] { alice });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie]);
        var half = movie.RunTimeTicks!.Value / 2;
        ud.Setup(m => m.GetUserData(alice, movie))
            .Returns(Partial(half, at: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

        var r = svc.BuildActivityReport();

        Assert.Single(r.Items);
        var s = r.Items.Single();
        Assert.Equal(1, s.UniqueViewers);
        Assert.Equal(50.0, s.AverageCompletionPercent);
    }

    [Fact]
    public void BuildActivityReport_MultipleUsers_AggregatesTotalsAndViewers()
    {
        var (svc, lib, um, ud) = CreateSut();
        var alice = User("alice");
        var bob = User("bob");
        var carol = User("carol");
        var movie = NewMovie("Popular", 100);
        um.Setup(m => m.GetUsers()).Returns(new[] { alice, bob, carol });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie]);
        ud.Setup(m => m.GetUserData(alice, movie))
            .Returns(Played(count: 2, at: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        ud.Setup(m => m.GetUserData(bob, movie))
            .Returns(Played(count: 1, at: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
        ud.Setup(m => m.GetUserData(carol, movie)).Returns(Idle());

        var r = svc.BuildActivityReport();

        Assert.Single(r.Items);
        var s = r.Items.Single();
        Assert.Equal(3, s.TotalPlayCount);
        Assert.Equal(2, s.UniqueViewers);          // carol excluded (idle)
        Assert.Equal(2, s.UserActivities.Count);   // one entry per active user
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), s.MostRecentWatch);
        Assert.Equal(3, r.TotalUsersAnalyzed);      // count reflects total users, not just active
    }

    [Fact]
    public void BuildActivityReport_SortsByTotalPlayCountDescending()
    {
        var (svc, lib, um, ud) = CreateSut();
        var alice = User("alice");
        var low = NewMovie("Low");
        var high = NewMovie("High");
        var mid = NewMovie("Mid");
        um.Setup(m => m.GetUsers()).Returns(new[] { alice });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([low, high, mid]);
        ud.Setup(m => m.GetUserData(alice, low)).Returns(Played(count: 1));
        ud.Setup(m => m.GetUserData(alice, high)).Returns(Played(count: 10));
        ud.Setup(m => m.GetUserData(alice, mid)).Returns(Played(count: 5));

        var r = svc.BuildActivityReport();

        Assert.Equal(3, r.Items.Count);
        Assert.Equal("High", r.Items[0].ItemName);
        Assert.Equal("Mid", r.Items[1].ItemName);
        Assert.Equal("Low", r.Items[2].ItemName);
    }

    [Fact]
    public void BuildActivityReport_EpisodeItem_PopulatesSeriesNameAndEpisodeLabel()
    {
        var (svc, lib, um, ud) = CreateSut();
        var alice = User("alice");
        var episode = NewEpisode("Breaking Bad", season: 3, number: 7, name: "One Minute");
        um.Setup(m => m.GetUsers()).Returns(new[] { alice });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([episode]);
        ud.Setup(m => m.GetUserData(alice, episode)).Returns(Played());

        var r = svc.BuildActivityReport();

        Assert.Single(r.Items);
        var s = r.Items.Single();
        Assert.Equal("Episode", s.ItemType);
        Assert.Equal("Breaking Bad", s.SeriesName);
        Assert.Equal("S03E07", s.EpisodeLabel);
    }

    [Fact]
    public void BuildActivityReport_InvalidOperationException_IsCaughtAndUserSkipped()
    {
        // Contract: a per-(user,item) InvalidOperationException must not abort the whole report.
        // The affected user is skipped for that item; other users remain in the summary.
        var (svc, lib, um, ud) = CreateSut();
        var alice = User("alice");
        var bob = User("bob");
        var movie = NewMovie("Shared");
        um.Setup(m => m.GetUsers()).Returns(new[] { alice, bob });
        lib.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie]);
        ud.Setup(m => m.GetUserData(alice, movie)).Returns(Played(count: 1));
        ud.Setup(m => m.GetUserData(bob, movie)).Throws(new InvalidOperationException("db oops"));

        var r = svc.BuildActivityReport();

        Assert.Single(r.Items);
        var s = r.Items.Single();
        Assert.Single(s.UserActivities);
        Assert.Equal(alice.Id, s.UserActivities[0].UserId);
    }

    // === Batch user-data fallback contract (Jellyfin 12+ GetUserDataBatch) ===
    // Omitted in 2.2.0.0 / 10.11.x branch — GetUserDataBatch does not exist in 10.11.x;
    // the code always uses per-item GetUserData directly.
}

