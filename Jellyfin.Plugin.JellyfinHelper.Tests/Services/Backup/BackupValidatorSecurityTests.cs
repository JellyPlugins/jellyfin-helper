using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
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

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("key\r\nX-Injected: 1")]
    [InlineData("key\twith-tab")]
    [InlineData("key\0nul")]
    public void Validate_ArrApiKey_WithControlCharacters_Flagged(string apiKey)
    {
        var backup = CreateValidBackup();
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R1", Url = "http://r:7878", ApiKey = apiKey });

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("ApiKey contains invalid control characters.", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Validate_SeerrSkipCertificateValidation_True_WarnsTlsDowngrade()
    {
        var backup = CreateValidBackup();
        backup.SeerrSkipCertificateValidation = true;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("TLS certificate validation bypass", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Validate_ArrSkipCertificateValidation_True_WarnsTlsDowngrade()
    {
        var backup = CreateValidBackup();
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R1", Url = "http://r:7878", ApiKey = "k", SkipCertificateValidation = true });

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("TLS certificate validation bypass", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Validate_ArrUrl_Empty_SkipsUrlValidationWithoutError()
    {
        // An instance without a URL carries nothing to validate as a URL: no URL error may be reported,
        // while the other findings for that instance (here the TLS-bypass warning) are still recorded.
        var backup = CreateValidBackup();
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R1", Url = "", ApiKey = "k", SkipCertificateValidation = true });

        var result = BackupValidator.Validate(backup);

        Assert.DoesNotContain(result.Errors, e => e.Contains("is not a valid HTTP/HTTPS URL", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("TLS certificate validation bypass", StringComparison.Ordinal));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("ftp://host/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public void Validate_SeerrUrl_NonHttpScheme_Flagged(string url)
    {
        var backup = CreateValidBackup();
        backup.SeerrUrl = url;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("SeerrUrl is not a valid HTTP/HTTPS URL", StringComparison.Ordinal));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("ftp://evil/x")]
    [InlineData("file:///etc/passwd")]
    public void Validate_ArrUrl_NonHttpScheme_Flagged(string url)
    {
        var backup = CreateValidBackup();
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R1", Url = url, ApiKey = "k" });

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("is not a valid HTTP/HTTPS URL", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Validate_NullBackup_ReturnsError()
    {
        var result = BackupValidator.Validate(null);

        Assert.Contains(result.Errors, e => e.Contains("null or could not be deserialized", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Validate_UnsupportedVersion_Flagged()
    {
        var backup = CreateValidBackup();
        backup.BackupVersion = 999;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("Unsupported backup version", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Validate_TrashPath_WithControlChars_FlaggedByStrictGuard()
    {
        var backup = CreateValidBackup();
        backup.TrashFolderPath = "trash\twith-tab";

        var result = BackupValidator.Validate(backup);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Validate_BaselineDirectory_WithScriptInjection_Flagged()
    {
        var backup = CreateValidBackup();
        backup.GrowthBaseline = new GrowthTimelineBaseline();
        backup.GrowthBaseline.Directories["<script>alert(1)</script>"] = new BaselineDirectoryEntry
        {
            CreatedUtc = DateTime.UtcNow,
            Size = 1,
            Count = 1,
        };

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("script injection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Validate_OversizedTimeline_WarnsTrim()
    {
        var backup = CreateValidBackup();
        var timeline = new GrowthTimelineResult
        {
            ComputedAt = DateTime.UtcNow,
            Granularity = "daily",
            EarliestFileDate = DateTime.UtcNow.AddYears(-1),
        };
        for (var i = 0; i < BackupValidator.MaxTimelineDataPoints + 1; i++)
        {
            timeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = DateTime.UtcNow.AddDays(-i),
                CumulativeSize = i,
                CumulativeFileCount = i,
            });
        }

        backup.GrowthTimeline = timeline;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("Will be trimmed", StringComparison.Ordinal));
    }
}
