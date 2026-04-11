using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
/// Tests for <see cref="HelperCleanupTask"/>, the master orchestration task.
/// </summary>
[Collection("ConfigOverride")]
public class HelperCleanupTaskTests : IDisposable
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IApplicationPaths> _applicationPathsMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly HelperCleanupTask _task;
    private readonly Mock<ILogger<HelperCleanupTask>> _loggerMock;

    public HelperCleanupTaskTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _applicationPathsMock = new Mock<IApplicationPaths>();
        _applicationPathsMock.Setup(p => p.DataPath).Returns(Path.GetTempPath());
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerMock = new Mock<ILogger<HelperCleanupTask>>();

        // Setup logger factory to return loggers for all required types
        _loggerFactoryMock
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns((string categoryName) =>
            {
                if (categoryName.Contains("HelperCleanupTask"))
                {
                    return _loggerMock.Object;
                }

                return new Mock<ILogger>().Object;
            });

        // Default: empty libraries so sub-tasks finish quickly
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        _task = new HelperCleanupTask(
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            _applicationPathsMock.Object,
            _loggerFactoryMock.Object);

        // Default: All tasks activated
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Activate,
            EmptyMediaFolderTaskMode = TaskMode.Activate,
            OrphanedSubtitleTaskMode = TaskMode.Activate,
            StrmRepairTaskMode = TaskMode.Activate
        };
    }

    public void Dispose()
    {
        CleanupConfigHelper.ConfigOverride = null;
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
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            StrmRepairTaskMode = TaskMode.Deactivate
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Skipping Trickplay Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Empty Media Folder Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping Orphaned Subtitle Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Skipping STRM File Repair (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Helper Cleanup finished", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_AllActivated_RunsAllTasks()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Activate,
            EmptyMediaFolderTaskMode = TaskMode.Activate,
            OrphanedSubtitleTaskMode = TaskMode.Activate,
            StrmRepairTaskMode = TaskMode.Activate
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Trickplay Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting Empty Media Folder Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting Orphaned Subtitle Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Starting STRM File Repair (Active)", LogLevel.Information);
        VerifyLogContains("Helper Cleanup finished", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_DryRunMode_LogsDryRunLabel()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.DryRun,
            EmptyMediaFolderTaskMode = TaskMode.DryRun,
            OrphanedSubtitleTaskMode = TaskMode.DryRun,
            StrmRepairTaskMode = TaskMode.DryRun
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Trickplay Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting Empty Media Folder Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting Orphaned Subtitle Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Starting STRM File Repair (Dry Run)", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_MixedModes_HandlesEachCorrectly()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Activate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.DryRun,
            StrmRepairTaskMode = TaskMode.Deactivate
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Starting Trickplay Cleanup (Active)", LogLevel.Information);
        VerifyLogContains("Skipping Empty Media Folder Cleanup (deactivated in settings)", LogLevel.Information);
        VerifyLogContains("Starting Orphaned Subtitle Cleanup (Dry Run)", LogLevel.Information);
        VerifyLogContains("Skipping STRM File Repair (deactivated in settings)", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_StopsProcessing()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _task.ExecuteAsync(new Progress<double>(), cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_ProgressReaches100()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            StrmRepairTaskMode = TaskMode.Deactivate
        };

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(v => reportedValues.Add(v));

        await _task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Contains(100.0, reportedValues);
    }

    [Fact]
    public async Task ExecuteAsync_ProgressIsReportedForEachSubTask()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            StrmRepairTaskMode = TaskMode.Deactivate
        };

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(v => reportedValues.Add(v));

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
        VerifyLogContains("Finished STRM File Repair", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_TrashEnabled_RunsTrashPurge()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            StrmRepairTaskMode = TaskMode.Deactivate,
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
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            StrmRepairTaskMode = TaskMode.Deactivate,
            UseTrash = false,
            TrashRetentionDays = 30
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Running trash purge", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_TrashEnabledRetentionZero_RunsTrashPurge()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Deactivate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.Deactivate,
            StrmRepairTaskMode = TaskMode.Deactivate,
            UseTrash = true,
            TrashRetentionDays = 0,
            TrashFolderPath = ".jellyfin-trash"
        };

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Running trash purge (retention: 0 days)", LogLevel.Information);
    }

    /// <summary>
    /// A synchronous implementation of IProgress that invokes the callback immediately.
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
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