using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
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

    private WatchHistoryService CreateService(string excludedLibraries)
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
            _mockLogger.Object);
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

        Assert.Single(result);
        Assert.Equal(allowed.Id, result[0].Id);
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
    public void LoadAllSeriesItems_ExcludedLibraryNamed_DropsSeriesUnderExcludedRoot()
    {
        var allowed = new Series { Id = Guid.NewGuid(), Path = AllowedRoot + "/good-show" };
        var excluded = new Series { Id = Guid.NewGuid(), Path = ExcludedRoot + "/anime-show" };
        _mockLibraryManager
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { allowed, excluded });

        var service = CreateService("Anime");

        var result = service.LoadAllSeriesItems();

        Assert.Single(result);
        Assert.Equal(allowed.Id, result[0].Id);
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

        Assert.Single(result);
        Assert.Equal(allowed.Id, result[0].Id);
        _mockPluginLog.Verify(
            l => l.LogDebug(
                It.IsAny<string>(),
                It.Is<string>(m => m.Contains("no path", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<ILogger>()),
            Times.Once);
    }
}
