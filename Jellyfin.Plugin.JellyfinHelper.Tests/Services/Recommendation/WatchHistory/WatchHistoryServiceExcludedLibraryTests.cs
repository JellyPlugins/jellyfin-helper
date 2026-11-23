using Jellyfin.Data;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.WatchHistory;

/// <summary>
///     Verifies that <see cref="WatchHistoryService"/> honors the user's ExcludedLibraries setting:
///     library-load queries drop items whose path lives under an excluded library root while keeping
///     items under allowed roots, and pass everything through untouched when nothing is excluded.
/// </summary>
public sealed class WatchHistoryServiceExcludedLibraryTests
{
    // Lowercase paths so the ordinal comparison used on Linux CI and the case-insensitive
    // comparison used on Windows both match without OS branching.
    private const string AllowedRoot = "/media/movies";
    private const string ExcludedRoot = "/media/anime";

    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IPluginConfigurationService> _mockConfigService;
    private readonly Mock<IUserManager> _mockUserManager;
    private readonly Mock<IUserDataManager> _mockUserDataManager;
    private readonly Mock<IPluginLogService> _mockPluginLog;
    private readonly Mock<ILogger<WatchHistoryService>> _mockLogger;

    public WatchHistoryServiceExcludedLibraryTests()
    {
        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockConfigService = new Mock<IPluginConfigurationService>();
        _mockUserManager = new Mock<IUserManager>();
        _mockUserDataManager = new Mock<IUserDataManager>();
        _mockPluginLog = new Mock<IPluginLogService>();
        _mockLogger = new Mock<ILogger<WatchHistoryService>>();
    }

    private WatchHistoryService CreateService(string excludedLibraries, bool detectVirtual = false, bool userAccess = false)
    {
        _mockConfigService
            .Setup(s => s.GetConfiguration())
            .Returns(new PluginConfiguration { ExcludedLibraries = excludedLibraries });

        // Two libraries: "Movies" is allowed, "Anime" is what tests exclude by name.
        _mockLibraryManager
            .Setup(m => m.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Movies", Locations = [AllowedRoot] },
                new VirtualFolderInfo { Name = "Anime", Locations = [ExcludedRoot] }
            ]);

        return new WatchHistoryService(
            _mockLibraryManager.Object,
            _mockConfigService.Object,
            _mockUserManager.Object,
            _mockUserDataManager.Object,
            _mockPluginLog.Object,
            _mockLogger.Object)
        {
            VirtualItems = detectVirtual ? Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.TestMockFactory.CreateVirtualItemDetector() : null,
            UseUserAccessQueries = userAccess
        };
    }

    [Fact]
    public void UserHiddenLibrariesAndAdminExcludedLibrariesBothStayOutOfProfilesAndCandidateAccess()
    {
        var service = CreateService("Anime", detectVirtual: true, userAccess: true);
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "prov", "res") { Id = Guid.NewGuid() };
        _mockUserManager.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var allowed = new Movie { Id = Guid.NewGuid(), Path = "/media/visible/movie.mkv", Genres = ["Drama"], ProviderIds = new() { ["Tmdb"] = "100" } };
        var hidden = new Movie { Id = Guid.NewGuid(), Path = "/media/user-hidden/movie.mkv", Genres = ["Drama"], ProviderIds = new() { ["Tmdb"] = "100" } };
        var excluded = new Movie { Id = Guid.NewGuid(), Path = "/media/admin-excluded/movie.mkv", Genres = ["Animation"] };
        _mockLibraryManager.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Movies", Locations = ["/media"] },
            new VirtualFolderInfo { Name = "Anime", Locations = ["/media/admin-excluded"] }
        ]);
        _mockLibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IsFolder == true ? [] : [allowed, hidden, excluded]);
        _mockLibraryManager.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IsFolder == true ? [] : [allowed.Id, excluded.Id]);
        _mockUserDataManager.Setup(manager => manager.GetUserData(user, allowed)).Returns(new UserItemData { Key = "played", Played = true, PlayCount = 1 });
        _mockUserDataManager.Setup(manager => manager.GetUserData(user, hidden)).Throws(new InvalidOperationException("Hidden library must not be inspected for this user's taste."));
        _mockUserDataManager.Setup(manager => manager.GetUserData(user, excluded)).Throws(new InvalidOperationException("Admin-excluded library must not be inspected for taste."));
        var profile = service.GetUserWatchProfile(user.Id)!;
        Assert.Equal(allowed.Id, Assert.Single(profile.WatchedItems).ItemId);
        Assert.Equal(allowed.Id, Assert.Single(profile.AccessibleItemIds!));
        _mockLibraryManager.Verify(manager => manager.ConfigureUserAccess(It.Is<InternalItemsQuery>(query => query.User == user), user), Times.Exactly(2));
        _mockUserDataManager.Verify(manager => manager.GetUserData(user, hidden), Times.Never);
        _mockUserDataManager.Verify(manager => manager.GetUserData(user, excluded), Times.Never);
    }

    [Fact]
    public void PlaybackOnTheSecondPermittedPresentationCopySurvivesProfileAccessFiltering()
    {
        using var service = CreateService(string.Empty, userAccess: true);
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "prov", "res");
        _mockUserManager.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var first = new Movie { Id = Guid.NewGuid(), Path = AllowedRoot + "/first.mkv", PresentationUniqueKey = "same-title", ProviderIds = new() { ["Tmdb"] = "700" } };
        var second = new Movie { Id = Guid.NewGuid(), Path = AllowedRoot + "/second.mkv", PresentationUniqueKey = "same-title", ProviderIds = new() { ["Tmdb"] = "700" } };
        _mockLibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IsFolder == true || query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.BoxSet) ? [] : [first, second]);
        _mockLibraryManager.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            if (query.IsFolder == true) return [];
            return query.GroupByPresentationUniqueKey ? [first.Id] : [first.Id, second.Id];
        });
        _mockUserDataManager.Setup(manager => manager.GetUserData(user, first)).Returns(new UserItemData { Key = "never-played" });
        _mockUserDataManager.Setup(manager => manager.GetUserData(user, second)).Returns(new UserItemData { Key = "played", Played = true, PlayCount = 1 });
        var profile = service.GetUserWatchProfile(user.Id)!;
        Assert.Equal(second.Id, Assert.Single(profile.WatchedItems).ItemId);
        Assert.Equal(2, profile.AccessibleItemIds!.Count);
    }

    [Fact]
    public void BatchLibraryScopeUsesTheCapturedExclusionsEvenWhenConfigurationChangesAndReturns()
    {
        using var service = CreateService("Anime");
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "prov", "res");
        _mockUserManager.Setup(manager => manager.GetUsers()).Returns([user]);
        _mockUserManager.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var reads = 0;
        _mockConfigService.Setup(configuration => configuration.GetConfiguration()).Returns(() => new PluginConfiguration { ExcludedLibraries = ++reads == 2 ? "Movies" : "Anime" });
        var allowed = new Movie { Id = Guid.NewGuid(), Path = AllowedRoot + "/watched.mkv", Genres = ["Drama"] };
        var excluded = new Movie { Id = Guid.NewGuid(), Path = ExcludedRoot + "/watched.mkv", Genres = ["Animation"] };
        _mockLibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IsFolder == true || query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.BoxSet) ? [] : [allowed, excluded]);
        _mockUserDataManager.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns(new UserItemData { Key = "played", Played = true });
        var profile = Assert.Single(service.GetAllUserWatchProfiles());
        Assert.Equal(allowed.Id, Assert.Single(profile.WatchedItems).ItemId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Anime")]
    public void VirtualItemsCannotBecomeUserTasteOrCompletedSeriesProgressionEvenWithAPath(string excludedLibraries)
    {
        var service = CreateService(excludedLibraries, detectVirtual: true);
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "prov", "res") { Id = Guid.NewGuid() };
        _mockUserManager.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        _mockUserManager.Setup(manager => manager.GetUsers()).Returns([user]);
        var real = new Movie { Id = Guid.NewGuid(), Path = AllowedRoot + "/real.mkv", Genres = ["Crime"] };
        var flagged = new Movie { Id = Guid.NewGuid(), Path = AllowedRoot + "/flagged.mkv", IsVirtualItem = true, Genres = ["Animation"] };
        var stub = new Movie { Id = Guid.NewGuid(), Path = "/data/plugins/JellyNext/virtual/stub.mkv", Genres = ["Family"] };
        var parent = new Series { Id = Guid.NewGuid(), Path = AllowedRoot + "/series", IsVirtualItem = true, Genres = ["Fantasy"] };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = parent.Id, Path = AllowedRoot + "/series/episode.mkv", IsVirtualItem = true };
        _mockLibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IsFolder == true ? [parent] : [real, flagged, stub, episode]);
        _mockUserDataManager.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns(new UserItemData { Key = "watched", Played = true, PlayCount = 7, IsFavorite = true });
        var single = service.GetUserWatchProfile(user.Id)!;
        var batch = Assert.Single(service.GetAllUserWatchProfiles());
        Assert.Equal(real.Id, Assert.Single(single.WatchedItems).ItemId);
        Assert.Equal(real.Id, Assert.Single(batch.WatchedItems).ItemId);
        Assert.Empty(single.FavoriteSeriesIds);
        Assert.Empty(service.GetSeriesEpisodeCounts());
        Assert.DoesNotContain("Animation", single.GenreDistribution.Keys);
        Assert.DoesNotContain("Family", single.GenreDistribution.Keys);
        Assert.DoesNotContain("Fantasy", single.GenreDistribution.Keys);
        _mockUserDataManager.Verify(manager => manager.GetUserData(user, flagged), Times.Never);
        _mockUserDataManager.Verify(manager => manager.GetUserData(user, stub), Times.Never);
        _mockUserDataManager.Verify(manager => manager.GetUserData(user, episode), Times.Never);
    }

    [Fact]
    public void LoadAllVideoItems_ExcludedLibraryNamed_DropsItemsUnderExcludedRoot()
    {
        var allowed = new Movie { Id = Guid.NewGuid(), Path = AllowedRoot + "/film.mkv" };
        var excluded = new Movie { Id = Guid.NewGuid(), Path = ExcludedRoot + "/show.mkv" };
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { allowed, excluded });

        var service = CreateService("Anime");

        var result = service.LoadAllVideoItems();

        var resultItem = Assert.Single(result);
        Assert.Equal(allowed.Id, resultItem.Id);
        Assert.DoesNotContain(result, i => i.Id == excluded.Id);
    }

    [Fact]
    public void LoadAllVideoItems_NoExcludedLibraries_PassesAllItemsThrough()
    {
        var allowed = new Movie { Id = Guid.NewGuid(), Path = AllowedRoot + "/film.mkv" };
        var underOther = new Movie { Id = Guid.NewGuid(), Path = ExcludedRoot + "/show.mkv" };
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { allowed, underOther });

        var service = CreateService(string.Empty);

        var result = service.LoadAllVideoItems();

        // Empty exclusion set means no filtering: both items survive regardless of root.
        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.Id == allowed.Id);
        Assert.Contains(result, i => i.Id == underOther.Id);
    }

    [Fact]
    public void FreshProfilesAndContentFitDropExcludedAndRemovedMovieAndParentSeriesEvidence()
    {
        var service = CreateService(string.Empty);
        var configuration = new PluginConfiguration();
        _mockConfigService.Setup(config => config.GetConfiguration()).Returns(configuration);
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "prov", "res") { Id = Guid.NewGuid() };
        _mockUserManager.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var allowed = new Movie { Id = Guid.NewGuid(), Path = AllowedRoot + "/film.mkv", Genres = ["Crime"], Tags = ["detective"] };
        var excluded = new Movie { Id = Guid.NewGuid(), Path = ExcludedRoot + "/film.mkv", Genres = ["Adventure"], Tags = ["wizard"] };
        var parent = new Series { Id = Guid.NewGuid(), Path = ExcludedRoot + "/series", Genres = ["Animation"], Tags = ["shounen", "pirate"] };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = parent.Id, Path = parent.Path + "/episode.mkv" };
        IReadOnlyList<BaseItem> videos = [allowed, excluded, episode];
        IReadOnlyList<BaseItem> series = [parent];
        _mockLibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) => query.IsFolder == true ? series : videos);
        _mockUserDataManager.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "watched", Played = true, PlayCount = 1 });

        var first = service.GetUserWatchProfile(user.Id)!;
        Assert.Contains(first.WatchedItems, row => row.ItemId == excluded.Id);
        Assert.Contains(first.WatchedItems, row => row.ItemId == episode.Id);
        Assert.Equal(1, ContentFitProfile.Create(first).ScoreTags(["shounen", "pirate"]));

        configuration.ExcludedLibraries = "Anime";
        var filtered = service.GetUserWatchProfile(user.Id)!;
        Assert.Equal(allowed.Id, Assert.Single(filtered.WatchedItems).ItemId);
        Assert.DoesNotContain("Animation", filtered.GenreDistribution.Keys);
        Assert.Equal(0, ContentFitProfile.Create(filtered).ScoreTags(["shounen", "pirate", "wizard"]));
        Assert.Equal(1, ContentFitProfile.Create(filtered).ScoreTags(["detective"]));

        configuration.ExcludedLibraries = string.Empty;
        videos = [allowed];
        series = [];
        _mockLibraryManager.Setup(manager => manager.GetVirtualFolders()).Returns([new VirtualFolderInfo { Name = "Movies", Locations = [AllowedRoot] }]);
        var removed = service.GetUserWatchProfile(user.Id)!;
        Assert.Equal(allowed.Id, Assert.Single(removed.WatchedItems).ItemId);
        Assert.Equal(0, ContentFitProfile.Create(removed).ScoreTags(["shounen", "pirate", "wizard"]));
        Assert.Equal(1, ContentFitProfile.Create(removed).ScoreTags(["detective"]));
    }

    [Fact]
    public void LoadAllSeriesItems_ExcludedLibraryNamed_DropsSeriesUnderExcludedRoot()
    {
        var allowed = new Series { Id = Guid.NewGuid(), Path = AllowedRoot + "/good-show" };
        var excluded = new Series { Id = Guid.NewGuid(), Path = ExcludedRoot + "/anime-show" };
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { allowed, excluded });

        var service = CreateService("Anime");

        var result = service.LoadAllSeriesItems();

        var resultItem = Assert.Single(result);
        Assert.Equal(allowed.Id, resultItem.Id);
        Assert.DoesNotContain(result, i => i.Id == excluded.Id);
    }

    [Fact]
    public void LoadAllSeriesItems_NoExcludedLibraries_PassesAllSeriesThrough()
    {
        var allowed = new Series { Id = Guid.NewGuid(), Path = AllowedRoot + "/good-show" };
        var underOther = new Series { Id = Guid.NewGuid(), Path = ExcludedRoot + "/anime-show" };
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { allowed, underOther });

        var service = CreateService(string.Empty);

        var result = service.LoadAllSeriesItems();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetAllUserWatchProfiles_ResolvesAllowedRootsOncePerRun()
    {
        // Regression guard for the resolve-once optimization: a batch run must read virtual folders
        // a single time, not once per library load. Before the refactor each of the three loaders
        // re-resolved, hitting GetVirtualFolders repeatedly.
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
        var service = CreateService("Anime");
        var user = new Jellyfin.Database.Implementations.Entities.User("u", "prov", "res") { Id = Guid.NewGuid() };
        _mockUserManager.Setup(m => m.GetUsers()).Returns([user]);

        service.GetAllUserWatchProfiles();

        _mockLibraryManager.Verify(m => m.GetVirtualFolders(), Times.Once);
    }

    [Fact]
    public void LoadAllVideoItems_ItemWithEmptyPath_LogsDroppedCount()
    {
        // An item with no path cannot sit under any allowed root, so the filter drops it. That is
        // correct, but the drop must be observable so a sparse profile is not mistaken for missing data.
        var allowed = new Movie { Id = Guid.NewGuid(), Path = AllowedRoot + "/film.mkv" };
        var noPath = new Movie { Id = Guid.NewGuid(), Path = string.Empty };
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { allowed, noPath });
        var service = CreateService("Anime");

        var result = service.LoadAllVideoItems();

        var resultItem = Assert.Single(result);
        Assert.Equal(allowed.Id, resultItem.Id);
        _mockPluginLog.Verify(
            l => l.LogDebug(
                It.IsAny<string>(),
                It.Is<string>(m => m.Contains("no path", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public void LoadAllVideoItems_ExcludedLibraryNestedUnderAllowed_DropsItemsUnderExcludedRoot()
    {
        // Regression: an excluded library nested under an allowed one (allowed "/media", excluded
        // "/media/anime") must still drop the nested items. The name-based skip alone left them under
        // the retained allowed root; the scope now denies them via the most-specific matching root.
        const string parentRoot = "/media";
        const string nestedExcludedRoot = "/media/anime";
        _mockConfigService
            .Setup(s => s.GetConfiguration())
            .Returns(new PluginConfiguration { ExcludedLibraries = "Anime" });
        _mockLibraryManager
            .Setup(m => m.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Media", Locations = [parentRoot] },
                new VirtualFolderInfo { Name = "Anime", Locations = [nestedExcludedRoot] }
            ]);

        var kept = new Movie { Id = Guid.NewGuid(), Path = parentRoot + "/movies/film.mkv" };
        var dropped = new Movie { Id = Guid.NewGuid(), Path = nestedExcludedRoot + "/show/ep.mkv" };
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { kept, dropped });

        var service = new WatchHistoryService(
            _mockLibraryManager.Object,
            _mockConfigService.Object,
            _mockUserManager.Object,
            _mockUserDataManager.Object,
            _mockPluginLog.Object,
            _mockLogger.Object);

        var result = service.LoadAllVideoItems();

        var resultItem = Assert.Single(result);
        Assert.Equal(kept.Id, resultItem.Id);
        Assert.DoesNotContain(result, i => i.Id == dropped.Id);
    }
}
