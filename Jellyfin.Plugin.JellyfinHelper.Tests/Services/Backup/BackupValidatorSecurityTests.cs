using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Backup;

/// <summary>
///     Security tests for BackupValidator. Verifies that an operator-enabled trash path carrying injection payloads (null bytes, newline log/header injection) is rejected by the dedicated path-safety guard rather than silently accepted.
/// </summary>
public sealed class BackupValidatorSecurityTests
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
        TrashRetentionDays = 30,
        UseTrash = true
    };

    [Fact]
    [Trait("Category", "Security")]
    public void Validate_TrashPath_WithNullByte_FlaggedByPathSafety()
    {
        // The path-safety guard emits its own null-byte error, distinct from the
        // string-field binary-injection message, so a NUL in the trash path is caught here.
        var backup = CreateValidBackup();
        backup.TrashFolderPath = "trash\0evil";

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e == "TrashFolderPath contains null bytes.");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("trash\nevil")]
    [InlineData("trash\revil")]
    public void Validate_TrashPath_WithNewline_FlaggedAsInjection(string trashPath)
    {
        // Newlines in a path enable log/header injection and must be rejected.
        var backup = CreateValidBackup();
        backup.TrashFolderPath = trashPath;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e == "TrashFolderPath contains newline characters.");
    }
}
