using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using System.Collections.ObjectModel;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
///     Tests for <see cref="HelperCleanupTask" />, the master orchestration task.
/// </summary>
public class HelperCleanupTaskTests : IDisposable
{
    private readonly Mock<ILogger<HelperCleanupTask>> _loggerMock;
    private readonly Mock<ISeerrIntegrationService> _seerrServiceMock;
    private readonly HelperCleanupTask _task;
    private readonly string _testDataPath;
    private PluginConfiguration _config;

    public HelperCleanupTaskTests()
    {
        var libraryManagerMock = TestMockFactory.CreateLibraryManager();
        var fileSystemMock = TestMockFactory.CreateFileSystem();
        _testDataPath = Path.Join(Path.GetTempPath(), "JellyfinHelperTests_Data_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataPath);
        var loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerMock = new Mock<ILogger<HelperCleanupTask>>();

        // Setup logger factory to return loggers for all required types
        loggerFactoryMock
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns((string categoryName) =>
            {
                if (categoryName.Contains("HelperCleanupTask")) return _loggerMock.Object;

                return new Mock<ILogger>().Object;
            });

        // Default: empty libraries so sub-tasks finish quickly
        libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        var statisticsServiceMock = TestMockFactory.CreateMediaStatisticsService();
        var cacheServiceMock = TestMockFactory.CreateStatisticsCacheService();
        var growthServiceMock = TestMockFactory.CreateGrowthTimelineService();

        // Setup mock-based config helper
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Activate,
            EmptyMediaFolderTaskMode = TaskMode.Activate,
            OrphanedSubtitleTaskMode = TaskMode.Activate,
            LinkRepairTaskMode = TaskMode.Activate
        };

        var configHelperMock = new Mock<ICleanupConfigHelper>();
        configHelperMock.Setup(c => c.GetConfig()).Returns(() => _config);
        configHelperMock.Setup(c => c.GetTrickplayTaskMode()).Returns(() => _config.TrickplayTaskMode);
        configHelperMock.Setup(c => c.GetEmptyMediaFolderTaskMode()).Returns(() => _config.EmptyMediaFolderTaskMode);
        configHelperMock.Setup(c => c.GetOrphanedSubtitleTaskMode()).Returns(() => _config.OrphanedSubtitleTaskMode);
        configHelperMock.Setup(c => c.GetLinkRepairTaskMode()).Returns(() => _config.LinkRepairTaskMode);
        configHelperMock.Setup(c => c.IsDryRunTrickplay()).Returns(() => _config.TrickplayTaskMode == TaskMode.DryRun);
        configHelperMock.Setup(c => c.IsDryRunEmptyMediaFolders())
            .Returns(() => _config.EmptyMediaFolderTaskMode == TaskMode.DryRun);
        configHelperMock.Setup(c => c.IsDryRunOrphanedSubtitles())
            .Returns(() => _config.OrphanedSubtitleTaskMode == TaskMode.DryRun);
        configHelperMock.Setup(c => c.IsDryRunLinkRepair())
            .Returns(() => _config.LinkRepairTaskMode == TaskMode.DryRun);
        configHelperMock.Setup(c => c.IsOldEnoughForDeletion(It.IsAny<string>())).Returns(true);
        configHelperMock.Setup(c => c.IsFileOldEnoughForDeletion(It.IsAny<string>())).Returns(true);
        configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns<string>(lib => Path.Join(lib, ".trash"));
        configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns<ILibraryManager>(_ => new List<string>());

        var trackingServiceMock = new Mock<ICleanupTrackingService>();
        var trashServiceMock = new Mock<ITrashService>();
        var linkRepairServiceMock = new Mock<ILinkRepairService>();
        _seerrServiceMock = new Mock<ISeerrIntegrationService>();
        _seerrServiceMock
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

        _task = new HelperCleanupTask(
            libraryManagerMock.Object,
            fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(),
            loggerFactoryMock.Object,
            statisticsServiceMock.Object,
            cacheServiceMock.Object,
            growthServiceMock.Object,
            configHelperMock.Object,
            trackingServiceMock.Object,
            trashServiceMock.Object,
            linkRepairServiceMock.Object,
            _seerrServiceMock.Object,
            userActivityInsightsMock.Object,
            userActivityCacheMock.Object,
            recsEngineMock.Object,
            recsCacheMock.Object,
            playlistServiceMock.Object);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_testDataPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_testDataPath, true);
        }
        catch (IOException)
        {
            /* best effort cleanup */
        }
        catch (UnauthorizedAccessException)
        {
            /* best effort cleanup */
        }
    }

    [Fact]
    public void Name_ReturnsHelperCleanup()
    {
        Assert.Equal("Helper Cleanup", _task.Name);
    }

    [Fact]
    public void Key_ReturnsHelperCleanup()
    {
        Assert.Equal("HelperCleanup", _task.Key);
    }

    [Fact]
    public void Category_ReturnsJellyfinHelper()
    {
        Assert.Equal("Jellyfin Helper", _task.Category);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(_task.Description));
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsSundayWeeklyTrigger()
    {
        var triggers = _task.GetDefaultTriggers().ToList();
        Assert.Single(triggers);
        Assert.Equal(TaskTriggerInfoType.WeeklyTrigger, triggers[0].Type);
        Assert.Equal(DayOfWeek.Sunday, triggers[0].DayOfWeek);
    }

    [Fact]
    public async Task ExecuteAsync_AllDeactivated_SkipsAllTasks()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate,
            RecommendationsTaskMode = TaskMode.Deactivate
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Skipping Trickplay Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Empty Media Folder Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Orphaned Subtitle Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Link Repair (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Seerr Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping User Watch Activity (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Smart Recommendations (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Helper Cleanup finished", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_AllActivated_RunsAllTasks()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Activate,
            EmptyMediaFolderTaskMode = TaskMode.Activate,
            OrphanedSubtitleTaskMode = TaskMode.Activate,
            LinkRepairTaskMode = TaskMode.Activate,
            SeerrCleanupTaskMode = TaskMode.Activate,
            SeerrUrl = "http://localhost:5055",
            SeerrApiKey = "test-key",
            RecommendationsTaskMode = TaskMode.Activate
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Trickplay Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting Empty Media Folder Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting Orphaned Subtitle Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting Link Repair (Active)", LogLevel.Information);
        VerifyLogContains("Starting Seerr Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting User Watch Activity (Active)", LogLevel.Information);
        VerifyLogContains("Starting Smart Recommendations (Active)", LogLevel.Information);
        VerifyLogContains("Helper Cleanup finished", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_DryRunMode_LogsDryRunLabel()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.DryRun,
            EmptyMediaFolderTaskMode = TaskMode.DryRun,
            OrphanedSubtitleTaskMode = TaskMode.DryRun,
            LinkRepairTaskMode = TaskMode.DryRun,
            SeerrCleanupTaskMode = TaskMode.DryRun,
            SeerrUrl = "http://localhost:5055",
            SeerrApiKey = "test-key",
            RecommendationsTaskMode = TaskMode.DryRun
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Trickplay Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting Empty Media Folder Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting Orphaned Subtitle Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting Link Repair (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting Seerr Cleanup (Dry Run)", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_MixedModes_HandlesEachCorrectly()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Activate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.DryRun,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Trickplay Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Skipping Empty Media Folder Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Starting Orphaned Subtitle Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Skipping Link Repair (deactivated in settings)", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_StopsProcessing()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _task.ExecuteAsync(new Progress<double>(), cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_ProgressReaches100()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate
        };

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(reportedValues.Add);

        await _task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Contains(100.0, reportedValues);
    }

    [Fact]
    public async Task ExecuteAsync_ProgressIsReportedForEachSubTask()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate,
            RecommendationsTaskMode = TaskMode.Deactivate
        };

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(reportedValues.Add);

        await _task.ExecuteAsync(progress, CancellationToken.None);

        // At least 7 reports (one per sub-task boundary, plus internal sub-progress)
        Assert.True(reportedValues.Count >= 7,
            $"Expected at least 7 progress reports, got {reportedValues.Count}");
        // All values should be non-decreasing and end at ~100
        for (var i = 1; i < reportedValues.Count; i++)
        {
            Assert.True(reportedValues[i] >= reportedValues[i - 1],
                $"Progress should be non-decreasing: [{i - 1}]={reportedValues[i - 1]}, [{i}]={reportedValues[i]}");
        }

        Assert.InRange(reportedValues[^1], 100.0 - 0.01, 100.0 + 0.01);
    }

    [Fact]
    public async Task ExecuteAsync_AllActivated_LogsFinishedForEachTask()
    {
        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Finished Trickplay Cleanup", LogLevel.Information);
        VerifyLogContains("Finished Empty Media Folder Cleanup", LogLevel.Information);
        VerifyLogContains("Finished Orphaned Subtitle Cleanup", LogLevel.Information);
        VerifyLogContains("Finished Link Repair", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_TrashEnabled_RunsTrashPurge()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate,
            UseTrash = true,
            TrashRetentionDays = 30,
            TrashFolderPath = ".jellyfin-trash"
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Running trash purge (retention: 30 days)", LogLevel.Information);
        VerifyLogContains("Trash purge completed", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_TrashDisabled_SkipsTrashPurge()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate,
            UseTrash = false,
            TrashRetentionDays = 30
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Running trash purge", LogLevel.Information);
    }

    // ===== Seerr-specific tests =====

    [Fact]
    public async Task ExecuteAsync_SeerrActivated_NotConfigured_LogsSkipped()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Activate,
            SeerrUrl = "",
            SeerrApiKey = ""
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Seerr not configured", LogLevel.Information);
        VerifySeerrNeverCalled();
    }

    [Fact]
    public async Task ExecuteAsync_SeerrActivated_OnlyUrlSet_LogsSkipped()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Activate,
            SeerrUrl = "http://localhost:5055",
            SeerrApiKey = ""
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Seerr not configured", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_SeerrDryRun_Configured_LogsDryRunMode()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.DryRun,
            SeerrUrl = "http://localhost:5055",
            SeerrApiKey = "test-key",
            SeerrCleanupAgeDays = 180
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Seerr Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Max age: 180 days", LogLevel.Information);
        VerifySeerrCalledWith("http://localhost:5055", "test-key", 180, true);
    }

    [Fact]
    public async Task ExecuteAsync_SeerrDeactivated_SkipsEvenIfConfigured()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate,
            SeerrUrl = "http://localhost:5055",
            SeerrApiKey = "test-key"
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Skipping Seerr Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogNeverContains("Starting Seerr Cleanup", LogLevel.Information);
        VerifySeerrNeverCalled();
    }

    [Fact]
    public async Task ExecuteAsync_SeerrActivated_LogsFinishedSummary()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Activate,
            SeerrUrl = "http://localhost:5055",
            SeerrApiKey = "test-key",
            SeerrCleanupAgeDays = 365
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Seerr Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Task finished.", LogLevel.Information);
        VerifySeerrCalledWith("http://localhost:5055", "test-key", 365, false);
    }

    [Fact]
    public async Task ExecuteAsync_TrashEnabledRetentionZero_RunsTrashPurge()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            LinkRepairTaskMode = TaskMode.Deactivate,
            SeerrCleanupTaskMode = TaskMode.Deactivate,
            UseTrash = true,
            TrashRetentionDays = 0,
            TrashFolderPath = ".jellyfin-trash"
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Running trash purge (retention: 0 days)", LogLevel.Information);
    }

    private void VerifySeerrCalledWith(string url, string apiKey, int ageDays, bool dryRun)
    {
        _seerrServiceMock.Verify(
            s => s.CleanupExpiredRequestsAsync(url, apiKey, ageDays, dryRun, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void VerifySeerrNeverCalled()
    {
        _seerrServiceMock.Verify(
            s => s.CleanupExpiredRequestsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
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

    /// <summary>
    ///     A synchronous implementation of IProgress that invokes the callback immediately.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            handler(value);
        }
    }
}