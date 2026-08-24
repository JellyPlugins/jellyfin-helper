using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Backup;

public class BackupValidatorTests
{
    private static BackupData CreateValidBackup() => new BackupData
    {
        BackupVersion = 1,
        CreatedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
        PluginVersion = "1.0.0",
        Language = "en",
        TrickplayTaskMode = "DryRun",
        EmptyMediaFolderTaskMode = "DryRun",
        OrphanedSubtitleTaskMode = "DryRun",
        LinkRepairTaskMode = "DryRun",
        SeerrCleanupTaskMode = "Deactivate",
        RecommendationsTaskMode = "DryRun",
        OrphanMinAgeDays = 7,
        TrashRetentionDays = 30
    };

    // SeerrCleanupAgeDays

    [Fact]
    public void Validate_SeerrCleanupAgeDays_Null_NoError()
    {
        var backup = CreateValidBackup();
        backup.SeerrCleanupAgeDays = null;

        var result = BackupValidator.Validate(backup);

        Assert.DoesNotContain(result.Errors, e => e.Contains("SeerrCleanupAgeDays"));
    }

    [Fact]
    public void Validate_SeerrCleanupAgeDays_Zero_NoError()
    {
        // Explicit zero means "immediate cleanup" - must be accepted as valid.
        var backup = CreateValidBackup();
        backup.SeerrCleanupAgeDays = 0;

        var result = BackupValidator.Validate(backup);

        Assert.DoesNotContain(result.Errors, e => e.Contains("SeerrCleanupAgeDays"));
    }

    [Fact]
    public void Validate_SeerrCleanupAgeDays_Negative_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.SeerrCleanupAgeDays = -1;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("SeerrCleanupAgeDays"));
    }

    [Fact]
    public void Validate_SeerrCleanupAgeDays_MaxRetentionDays_NoError()
    {
        var backup = CreateValidBackup();
        backup.SeerrCleanupAgeDays = BackupValidator.MaxRetentionDays;

        var result = BackupValidator.Validate(backup);

        Assert.DoesNotContain(result.Errors, e => e.Contains("SeerrCleanupAgeDays"));
    }

    [Fact]
    public void Validate_SeerrCleanupAgeDays_ExceedsMaxRetentionDays_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.SeerrCleanupAgeDays = BackupValidator.MaxRetentionDays + 1;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("SeerrCleanupAgeDays"));
    }

    // CreatedAt timezone warning

    [Fact]
    public void Validate_CreatedAt_UtcKind_NoTimezoneWarning()
    {
        var backup = CreateValidBackup();
        backup.CreatedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        var result = BackupValidator.Validate(backup);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("no timezone indicator"));
    }

    [Fact]
    public void Validate_CreatedAt_LocalKind_NoTimezoneWarning()
    {
        // Local kind is unambiguous (it IS local); only Unspecified should warn.
        var backup = CreateValidBackup();
        backup.CreatedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Local);

        var result = BackupValidator.Validate(backup);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("no timezone indicator"));
    }

    [Fact]
    public void Validate_CreatedAt_UnspecifiedKind_AddsTimezoneWarning()
    {
        var backup = CreateValidBackup();
        backup.CreatedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Unspecified);

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("no timezone indicator"));
    }

    [Fact]
    public void Validate_CreatedAt_UnspecifiedKind_StillValidatesTimestamp()
    {
        // Even with Unspecified kind the timestamp comparison must still run (treated as UTC).
        // A clearly-future Unspecified timestamp should produce the future warning in addition
        // to the timezone warning.
        var backup = CreateValidBackup();
        backup.CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(10), DateTimeKind.Unspecified);

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("no timezone indicator"));
        Assert.Contains(result.Warnings, w => w.Contains("in the future"));
    }

    // SeerrUrl scheme

    [Theory]
    [InlineData("ftp://server/x")]
    [InlineData("not a url")]
    public void Validate_SeerrUrl_NotHttpOrHttps_ReturnsError(string seerrUrl)
    {
        // Only HTTP/HTTPS are usable by the Seerr client; anything else must be rejected.
        var backup = CreateValidBackup();
        backup.SeerrUrl = seerrUrl;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("SeerrUrl is not a valid HTTP/HTTPS URL"));
    }

    // PluginLogLevel

    [Fact]
    public void Validate_PluginLogLevel_Unknown_AddsWarning()
    {
        var backup = CreateValidBackup();
        backup.PluginLogLevel = "TRACE";

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("Unknown log level") && w.Contains("INFO"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("PluginLogLevel"));
    }

    // Null-tolerant string fields

    [Fact]
    public void Validate_NullStringField_IsSkippedWithoutError()
    {
        // An absent string field (null) is legitimate and must not be flagged.
        var backup = CreateValidBackup();
        backup.ExcludedLibraries = null!;

        var result = BackupValidator.Validate(backup);

        Assert.DoesNotContain(result.Errors, e => e.Contains("ExcludedLibraries"));
    }

    // Arr instances

    [Fact]
    public void Validate_NullArrInstanceList_IsSkippedWithoutError()
    {
        // A backup that omits the Radarr list entirely should not raise a validation error.
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
            PluginVersion = "1.0.0",
            Language = "en",
            TrickplayTaskMode = "DryRun",
            EmptyMediaFolderTaskMode = "DryRun",
            OrphanedSubtitleTaskMode = "DryRun",
            LinkRepairTaskMode = "DryRun",
            SeerrCleanupTaskMode = "Deactivate",
            RecommendationsTaskMode = "DryRun",
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            RadarrInstances = null!
        };

        var result = BackupValidator.Validate(backup);

        Assert.DoesNotContain(result.Errors, e => e.Contains("RadarrInstances"));
    }

    [Fact]
    public void Validate_ArrInstances_NullElement_ReturnsErrorAndContinues()
    {
        // A null element must be reported, and validation of following elements must still run.
        var backup = CreateValidBackup();
        backup.SonarrInstances.Add(null!);
        backup.SonarrInstances.Add(new BackupArrInstance
        {
            Name = "valid",
            Url = "http://sonarr.local"
        });

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("SonarrInstances[0] is null"));
        // The trailing valid instance has a well-formed URL, so no URL error for index 1.
        Assert.DoesNotContain(result.Errors, e => e.Contains("SonarrInstances[1]"));
    }

    // Growth timeline

    [Fact]
    public void Validate_GrowthTimeline_UnknownGranularity_AddsWarning()
    {
        var backup = CreateValidBackup();
        var timeline = new GrowthTimelineResult { Granularity = "hourly" };
        timeline.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 1,
            CumulativeFileCount = 1
        });
        backup.GrowthTimeline = timeline;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("Unknown timeline granularity 'hourly'"));
    }

    // Growth baseline

    [Fact]
    public void Validate_GrowthBaseline_TooManyDirectories_AddsTrimWarning()
    {
        var backup = CreateValidBackup();
        var baseline = new GrowthTimelineBaseline();
        for (var i = 0; i <= BackupValidator.MaxBaselineDirectories; i++)
        {
            baseline.Directories[$"/media/dir{i}"] = new BaselineDirectoryEntry { Size = 1, Count = 1 };
        }

        backup.GrowthBaseline = baseline;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("directories") && w.Contains("Will be trimmed"));
    }

    [Fact]
    public void Validate_GrowthBaseline_NegativeSize_ShortKey_AddsWarning()
    {
        // A short key is logged verbatim, so the warning carries the full path inline.
        var backup = CreateValidBackup();
        var baseline = new GrowthTimelineBaseline();
        baseline.Directories["/media/movies"] = new BaselineDirectoryEntry { Size = -1, Count = 0 };
        backup.GrowthBaseline = baseline;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(
            result.Warnings,
            w => w.Contains("negative size") && w.Contains("/media/movies"));
    }

    [Fact]
    public void Validate_GrowthBaseline_NegativeCount_LongKey_AddsTruncatedWarning()
    {
        // A key longer than 80 chars is truncated to 80 + "..." before being logged.
        var backup = CreateValidBackup();
        var longKey = new string('a', 120);
        var baseline = new GrowthTimelineBaseline();
        // Size non-negative so only the negative-count branch fires.
        baseline.Directories[longKey] = new BaselineDirectoryEntry { Size = 0, Count = -1 };
        backup.GrowthBaseline = baseline;

        var result = BackupValidator.Validate(backup);

        var truncated = new string('a', 80) + "...";
        Assert.Contains(
            result.Warnings,
            w => w.Contains("negative count") && w.Contains(truncated));
        Assert.DoesNotContain(result.Warnings, w => w.Contains(longKey));
    }
}
