using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
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

    // ── SeerrCleanupAgeDays ─────────────────────────────────────────────────

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
        // Explicit zero means "immediate cleanup" — must be accepted as valid.
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

    // ── CreatedAt timezone warning ──────────────────────────────────────────

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
}
