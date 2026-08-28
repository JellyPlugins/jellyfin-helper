using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using System.Collections.ObjectModel;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
///     Tests for HelperCleanupTask failure handling: how the orchestrator reacts when an individual stage (a sub-task, trash purge, statistics scan, or growth timeline) throws.
/// </summary>
public sealed class HelperCleanupTaskErrorHandlingTests
{
    private readonly Mock<ILogger<HelperCleanupTask>> _loggerMock;
    private readonly Mock<IMediaStatisticsService> _statisticsServiceMock;
    private readonly Mock<IGrowthTimelineService> _growthServiceMock;
    private readonly Mock<ITrashService> _trashServiceMock;
    private readonly Mock<ICleanupConfigHelper> _configHelperMock;
    private readonly Mock<ISeerrDiscoveryService> _seerrDiscoveryServiceMock;
    private readonly HelperCleanupTask _task;
    private PluginConfiguration _config;

    public HelperCleanupTaskErrorHandlingTests()
    {
        var libraryManagerMock = TestMockFactory.CreateLibraryManager();
        var fileSystemMock = TestMockFactory.CreateFileSystem();
        var loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerMock = TestMockFactory.CreateLogger<HelperCleanupTask>();

        loggerFactoryMock
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns((string categoryName) =>
            {
                if (categoryName.Contains("HelperCleanupTask")) return _loggerMock.Object;

                return TestMockFactory.CreateLogger().Object;
            });

        libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        _statisticsServiceMock = TestMockFactory.CreateMediaStatisticsService();
        var cacheServiceMock = TestMockFactory.CreateStatisticsCacheService();
        _growthServiceMock = TestMockFactory.CreateGrowthTimelineService();

        _config = new PluginConfiguration();

        _configHelperMock = new Mock<ICleanupConfigHelper>();
        _configHelperMock.Setup(c => c.GetConfig()).Returns(() => _config);
        _configHelperMock.Setup(c => c.GetTrickplayTaskMode()).Returns(() => _config.TrickplayTaskMode);
        _configHelperMock.Setup(c => c.GetEmptyMediaFolderTaskMode()).Returns(() => _config.EmptyMediaFolderTaskMode);
        _configHelperMock.Setup(c => c.GetOrphanedSubtitleTaskMode()).Returns(() => _config.OrphanedSubtitleTaskMode);
        _configHelperMock.Setup(c => c.GetLinkRepairTaskMode()).Returns(() => _config.LinkRepairTaskMode);
        _configHelperMock.Setup(c => c.IsOldEnoughForDeletion(It.IsAny<string>())).Returns(true);
        _configHelperMock.Setup(c => c.IsFileOldEnoughForDeletion(It.IsAny<string>())).Returns(true);
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns<string>(lib => Path.Join(lib, ".trash"));
        _configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns<ILibraryManager>(_ => new List<string>());

        var trackingServiceMock = new Mock<ICleanupTrackingService>();
        _trashServiceMock = new Mock<ITrashService>();
        var linkRepairServiceMock = new Mock<ILinkRepairService>();
        var seerrServiceMock = new Mock<ISeerrIntegrationService>();
        seerrServiceMock
            .Setup(s => s.CleanupExpiredRequestsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeerrCleanupResult());

        var userActivityInsightsMock = new Mock<IUserActivityInsightsService>();
        userActivityInsightsMock
            .Setup(s => s.BuildActivityReport())
            .Returns(new UserActivityResult());
        var userActivityCacheMock = new Mock<IUserActivityCacheService>();
        var recsEngineMock = new Mock<IRecommendationEngine>();
        recsEngineMock
            .Setup(e => e.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new Collection<RecommendationResult>());
        var recsCacheMock = new Mock<IRecommendationCacheService>();
        var playlistServiceMock = new Mock<IRecommendationPlaylistService>();

        _seerrDiscoveryServiceMock = new Mock<ISeerrDiscoveryService>();

        _task = new HelperCleanupTask(
            libraryManagerMock.Object,
            fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(),
            loggerFactoryMock.Object,
            _statisticsServiceMock.Object,
            cacheServiceMock.Object,
            _growthServiceMock.Object,
            _configHelperMock.Object,
            trackingServiceMock.Object,
            _trashServiceMock.Object,
            linkRepairServiceMock.Object,
            seerrServiceMock.Object,
            userActivityInsightsMock.Object,
            userActivityCacheMock.Object,
            recsEngineMock.Object,
            recsCacheMock.Object,
            playlistServiceMock.Object,
            _seerrDiscoveryServiceMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_SubTaskThrowsNonFatal_LogsErrorAndContinues()
    {
        // Only Seerr Discovery active; its service throws a non-fatal error. The orchestrator
        // must record the failure but keep going through the post-cleanup stages.
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate,
            RecommendationsTaskMode = TaskMode.Activate
        };

        _seerrDiscoveryServiceMock
            .Setup(s => s.GenerateDiscoveryRecommendationsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Error executing Seerr Discovery", LogLevel.Error);
        VerifyLogContains("Finished Seerr Discovery (with errors)", LogLevel.Information);
        VerifyLogContains("Helper Cleanup finished", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_SubTaskThrowsOperationCanceled_LogsCancelWarningAndRethrows()
    {
        // Cancellation surfaced from inside a sub-task must propagate, unlike ordinary failures.
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate,
            RecommendationsTaskMode = TaskMode.Activate
        };

        _seerrDiscoveryServiceMock
            .Setup(s => s.GenerateDiscoveryRecommendationsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        VerifyLogContains("cancelled during Seerr Discovery", LogLevel.Warning);
        VerifyLogNeverContains("Helper Cleanup finished", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_TrashPurge_PurgesEachDistinctTrashPathOnce()
    {
        // Two library roots whose trash paths collide to one shared folder must be purged once.
        _config = DeactivatedTrashConfig();

        var libA = Path.Combine(Path.GetTempPath(), "hct-lib-a");
        var libB = Path.Combine(Path.GetTempPath(), "hct-lib-b");
        var sharedTrash = Path.Combine(Path.GetTempPath(), "hct-shared-trash");
        var sharedTrashFull = Path.GetFullPath(sharedTrash);

        _configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string> { libA, libB });
        _configHelperMock.Setup(c => c.GetTrashPath(libA)).Returns(sharedTrash);
        _configHelperMock.Setup(c => c.GetTrashPath(libB)).Returns(sharedTrash);

        _trashServiceMock
            .Setup(t => t.PurgeExpiredTrash(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()))
            .Returns((123L, 4));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _trashServiceMock.Verify(
            t => t.PurgeExpiredTrash(sharedTrashFull, 30, It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
            Times.Once);
        VerifyLogContains("Trash purge completed", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_TrashPurge_TrashPathEqualsLibraryRoot_SkipsWithoutPurging()
    {
        // A trash path resolving back to the library root must never be purged (defense in depth).
        _config = DeactivatedTrashConfig();

        var libRoot = Path.Combine(Path.GetTempPath(), "hct-root-lib");

        _configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string> { libRoot });
        _configHelperMock.Setup(c => c.GetTrashPath(libRoot)).Returns(libRoot);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("is a library root", LogLevel.Warning);
        _trashServiceMock.Verify(
            t => t.PurgeExpiredTrash(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_TrashPurgeCancelled_LogsWarningAndRethrows()
    {
        _config = DeactivatedTrashConfig();

        var lib = Path.Combine(Path.GetTempPath(), "hct-cancel-lib");
        _configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string> { lib });
        _configHelperMock.Setup(c => c.GetTrashPath(lib))
            .Returns(Path.Combine(Path.GetTempPath(), "hct-cancel-trash"));

        _trashServiceMock
            .Setup(t => t.PurgeExpiredTrash(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()))
            .Throws(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        VerifyLogContains("cancelled during trash purge", LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_TrashPurgeThrowsNonFatal_LogsErrorAndContinues()
    {
        _config = DeactivatedTrashConfig();

        var lib = Path.Combine(Path.GetTempPath(), "hct-nonfatal-lib");
        _configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string> { lib });
        _configHelperMock.Setup(c => c.GetTrashPath(lib))
            .Returns(Path.Combine(Path.GetTempPath(), "hct-nonfatal-trash"));

        _trashServiceMock
            .Setup(t => t.PurgeExpiredTrash(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()))
            .Throws(new InvalidOperationException("boom"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Error during trash purge", LogLevel.Error);
        VerifyLogContains("Helper Cleanup finished", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_StatisticsScanCancelled_LogsWarningAndRethrows()
    {
        _config = AllDeactivatedConfig();

        _statisticsServiceMock
            .Setup(s => s.CalculateStatistics())
            .Throws(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        VerifyLogContains("cancelled during post-cleanup statistics scan", LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_StatisticsScanThrowsNonFatal_LogsWarningAndContinues()
    {
        _config = AllDeactivatedConfig();

        _statisticsServiceMock
            .Setup(s => s.CalculateStatistics())
            .Throws(new InvalidOperationException("boom"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Failed to run post-cleanup statistics scan", LogLevel.Warning);
        VerifyLogContains("Helper Cleanup finished", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_GrowthTimelineCancelled_LogsWarningAndRethrows()
    {
        _config = AllDeactivatedConfig();

        _growthServiceMock
            .Setup(g => g.ComputeTimelineAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        VerifyLogContains("cancelled during growth timeline computation", LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_GrowthTimelineThrowsNonFatal_LogsWarningAndFinishes()
    {
        _config = AllDeactivatedConfig();

        _growthServiceMock
            .Setup(g => g.ComputeTimelineAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Failed to recompute growth timeline", LogLevel.Warning);
        VerifyLogContains("Helper Cleanup finished", LogLevel.Information);
    }

    private static PluginConfiguration AllDeactivatedConfig() => new()
    {
        TrickplayTaskMode = TaskMode.Deactivate,
        EmptyMediaFolderTaskMode = TaskMode.Deactivate,
        OrphanedSubtitleTaskMode = TaskMode.Deactivate,
        LinkRepairTaskMode = TaskMode.Deactivate,
        SeerrCleanupTaskMode = TaskMode.Deactivate,
        RecommendationsTaskMode = TaskMode.Deactivate
    };

    private static PluginConfiguration DeactivatedTrashConfig()
    {
        var config = AllDeactivatedConfig();
        config.UseTrash = true;
        config.TrashRetentionDays = 30;
        return config;
    }

    private void VerifyLogContains(string messagePart, LogLevel level)
    {
        _loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    private void VerifyLogNeverContains(string messagePart, LogLevel level)
    {
        _loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
}
