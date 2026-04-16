using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
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
            linkRepairServiceMock.Object);
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
            LinkRepairTaskMode = TaskMode.Deactivate
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Skipping Trickplay Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Empty Media Folder Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Orphaned Subtitle Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Link Repair (deactivated in settings)", LogLevel.Information);
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
            LinkRepairTaskMode = TaskMode.Activate
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Trickplay Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting Empty Media Folder Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting Orphaned Subtitle Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting Link Repair (Active)", LogLevel.Information);
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
            LinkRepairTaskMode = TaskMode.DryRun
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Trickplay Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting Empty Media Folder Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting Orphaned Subtitle Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting Link Repair (Dry Run)", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_MixedModes_HandlesEachCorrectly()
    {
        _config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Activate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.DryRun,
            LinkRepairTaskMode = TaskMode.Deactivate
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
            LinkRepairTaskMode = TaskMode.Deactivate
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
            LinkRepairTaskMode = TaskMode.Deactivate
        };

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(reportedValues.Add);

        await _task.ExecuteAsync(progress, CancellationToken.None);

        // 4 sub-tasks → progress at 25, 50, 75, 100
        Assert.Equal(4, reportedValues.Count);
        Assert.Equal(25.0, reportedValues[0]);
        Assert.Equal(50.0, reportedValues[1]);
        Assert.Equal(75.0, reportedValues[2]);
        Assert.Equal(100.0, reportedValues[3]);
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
            UseTrash = false,
            TrashRetentionDays = 30
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Running trash purge", LogLevel.Information);
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
            UseTrash = true,
            TrashRetentionDays = 0,
            TrashFolderPath = ".jellyfin-trash"
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Running trash purge (retention: 0 days)", LogLevel.Information);
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