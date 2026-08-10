using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Backup;

/// <summary>
///     Tests for the failure and edge-case paths of <see cref="BackupService"/>: partial-apply
///     warnings when a config mutation throws after a file write, directory creation on save,
///     save-failure handling, and the size/DoS + corrupt-JSON guards in the source-file loader.
///     These paths are deliberately not exercised by the happy-path restore/create tests.
/// </summary>
public sealed class BackupServiceErrorHandlingTests : IDisposable
{
    private const string TimelineFileName = "jellyfin-helper-growth-timeline.json";
    private const string BaselineFileName = "jellyfin-helper-growth-baseline.json";

    private readonly string _tempDir;

    public BackupServiceErrorHandlingTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-backup-err-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private static BackupData MakeMinimalValidBackup()
    {
        return new BackupData
        {
            BackupVersion = 1,
            CreatedAt = DateTime.UtcNow,
            PluginVersion = "1.0.0",
            Language = "en",
            PluginLogLevel = "INFO",
            TrickplayTaskMode = "DryRun",
            EmptyMediaFolderTaskMode = "DryRun",
            OrphanedSubtitleTaskMode = "DryRun",
            LinkRepairTaskMode = "DryRun",
            SeerrCleanupTaskMode = "DryRun",
            RecommendationsTaskMode = "DryRun",
            UseTrash = true,
            TrashFolderPath = ".trash",
            TrashRetentionDays = 14
        };
    }

    private static GrowthTimelineResult MakeTimeline()
    {
        var timeline = new GrowthTimelineResult { Granularity = "monthly" };
        timeline.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 1000,
            CumulativeFileCount = 2
        });
        return timeline;
    }

    [Fact]
    public void RestoreBackup_ConfigMutationThrowsAfterFileWrite_RethrowsAndWarnsPartialApply()
    {
        // The timeline file is written first, so once RestoreConfiguration throws the restore
        // is in a partially-applied state. The service must surface that with a warning naming
        // both data files and must re-throw the original exception rather than swallow it.
        var pluginLogMock = new Mock<IPluginLogService>();
        var liveConfig = new PluginConfiguration();
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
        configMock.Setup(c => c.IsInitialized).Returns(true);
        configMock.Setup(c => c.PluginVersion).Returns("1.0.0");
        configMock.Setup(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()))
            .Throws(new InvalidOperationException("mutation boom"));

        var service = new BackupService(
            _tempDir,
            configMock.Object,
            pluginLogMock.Object,
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = MakeMinimalValidBackup();
        backup.GrowthTimeline = MakeTimeline();

        Assert.Throws<InvalidOperationException>(() => service.RestoreBackup(backup));

        pluginLogMock.Verify(
            p => p.LogWarning(
                "Backup",
                It.Is<string>(msg =>
                    msg.Contains("partially applied", StringComparison.OrdinalIgnoreCase)
                    && msg.Contains(TimelineFileName, StringComparison.Ordinal)
                    && msg.Contains(BaselineFileName, StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public void RestoreBackup_DataPathDirectoryMissing_CreatesItAndWritesFiles()
    {
        // The data path may not exist yet on a fresh install; SaveJsonFile must create the
        // missing directory before the atomic write instead of failing the restore.
        var nestedDataPath = Path.Join(_tempDir, "does", "not", "exist");
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.IsInitialized).Returns(false);

        var service = new BackupService(
            nestedDataPath,
            configMock.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = MakeMinimalValidBackup();
        backup.GrowthTimeline = MakeTimeline();
        backup.GrowthBaseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var summary = service.RestoreBackup(backup);

        Assert.True(summary.TimelineRestored);
        Assert.True(summary.BaselineRestored);
        Assert.True(File.Exists(Path.Join(nestedDataPath, TimelineFileName)));
        Assert.True(File.Exists(Path.Join(nestedDataPath, BaselineFileName)));
    }

    [Fact]
    public void RestoreBackup_SaveFails_ReturnsFalseAndDoesNotMarkRestored()
    {
        // Place a regular file where the data-path directory should be so Directory.CreateDirectory
        // throws an IOException that SaveJsonFile catches: the write must report failure, the
        // restored flag must stay false, and the error must be logged - not silently dropped.
        var filePosingAsDataPath = Path.Join(_tempDir, "iam-a-file");
        File.WriteAllText(filePosingAsDataPath, "not a directory");

        var pluginLogMock = new Mock<IPluginLogService>();
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.IsInitialized).Returns(false);

        var service = new BackupService(
            filePosingAsDataPath,
            configMock.Object,
            pluginLogMock.Object,
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = MakeMinimalValidBackup();
        backup.GrowthTimeline = MakeTimeline();

        var summary = service.RestoreBackup(backup);

        Assert.False(summary.TimelineRestored);
        pluginLogMock.Verify(
            p => p.LogError(
                "Backup",
                It.Is<string>(msg =>
                    msg.Contains("Could not save", StringComparison.Ordinal)
                    && msg.Contains("during restore", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CreateBackup_SourceFileExceedsSizeLimit_SkipsFileAndWarns()
    {
        // Size/DoS guard: a source file larger than MaxBackupSizeBytes must be skipped (not
        // deserialized into memory) and a warning naming the byte limit emitted.
        var pluginLogMock = new Mock<IPluginLogService>();
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration());
        configMock.Setup(c => c.IsInitialized).Returns(true);
        configMock.Setup(c => c.PluginVersion).Returns("1.0.0");

        var oversizedPath = Path.Join(_tempDir, TimelineFileName);
        File.WriteAllText(oversizedPath, new string('x', (int)BackupService.MaxBackupSizeBytes + 1024));

        var service = new BackupService(
            _tempDir,
            configMock.Object,
            pluginLogMock.Object,
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = service.CreateBackup();

        Assert.Null(backup.GrowthTimeline);
        pluginLogMock.Verify(
            p => p.LogWarning(
                "Backup",
                It.Is<string>(msg =>
                    msg.Contains("exceeds", StringComparison.Ordinal)
                    && msg.Contains(BackupService.MaxBackupSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void CreateBackup_CorruptSourceJson_SkipsFileAndWarns()
    {
        // A malformed source file must not abort the backup: the deserialize failure is caught
        // and the file simply skipped (null) with a diagnostic warning.
        var pluginLogMock = new Mock<IPluginLogService>();
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration());
        configMock.Setup(c => c.IsInitialized).Returns(true);
        configMock.Setup(c => c.PluginVersion).Returns("1.0.0");

        File.WriteAllText(Path.Join(_tempDir, BaselineFileName), "{ not valid");

        var service = new BackupService(
            _tempDir,
            configMock.Object,
            pluginLogMock.Object,
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = service.CreateBackup();

        Assert.Null(backup.GrowthBaseline);
        pluginLogMock.Verify(
            p => p.LogWarning(
                "Backup",
                It.Is<string>(msg => msg.Contains("Could not load", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.AtLeastOnce);
    }
}
