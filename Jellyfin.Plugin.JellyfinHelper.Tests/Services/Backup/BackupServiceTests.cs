using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Backup;

/// <summary>
///     Comprehensive tests for the BackupService, covering validation, sanitization,
///     serialization, and security checks against malicious input.
/// </summary>
public class BackupServiceTests
{
    private static BackupData CreateValidBackup()
    {
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = DateTime.UtcNow,
            PluginVersion = "1.0.0",
            Language = "en",
            ExcludedLibraries = "",
            OrphanMinAgeDays = 7,
            PluginLogLevel = "INFO",
            TrickplayTaskMode = "DryRun",
            EmptyMediaFolderTaskMode = "Activate",
            OrphanedSubtitleTaskMode = "Deactivate",
            LinkRepairTaskMode = "DryRun",
            SeerrCleanupTaskMode = "DryRun",
            RecommendationsTaskMode = "DryRun",
            UseTrash = true,
            TrashFolderPath = ".jellyfin-trash",
            TrashRetentionDays = 30
        };
        backup.RadarrInstances.Add(new BackupArrInstance
        { Name = "Radarr", Url = "http://localhost:7878", ApiKey = "abc123" });
        backup.SonarrInstances.Add(new BackupArrInstance
        { Name = "Sonarr", Url = "http://localhost:8989", ApiKey = "def456" });
        return backup;
    }

    // ===== Validation: Valid backup =====

    [Fact]
    public void Validate_ValidBackup_ReturnsNoErrors()
    {
        var backup = CreateValidBackup();
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_NullBackup_ReturnsError()
    {
        var result = BackupValidator.Validate(null);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("null", result.Errors[0]);
    }

    // ===== Validation: Version =====

    [Fact]
    public void Validate_UnsupportedVersion_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.BackupVersion = 99;
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("version"));
    }

    [Fact]
    public void Validate_VersionZero_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.BackupVersion = 0;
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
    }

    // ===== Validation: Timestamp =====

    [Fact]
    public void Validate_OldTimestamp_ReturnsWarning()
    {
        var backup = CreateValidBackup();
        backup.CreatedAt = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid); // Warning, not error
        Assert.Contains(result.Warnings, w => w.Contains("old"));
    }

    [Fact]
    public void Validate_FutureTimestamp_ReturnsWarning()
    {
        var backup = CreateValidBackup();
        backup.CreatedAt = DateTime.UtcNow.AddDays(5);
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("future"));
    }

    // ===== Validation: Language =====

    [Fact]
    public void Validate_UnknownLanguage_ReturnsWarning()
    {
        var backup = CreateValidBackup();
        backup.Language = "xx";
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("language") || w.Contains("Language"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("zh")]
    [InlineData("tr")]
    [InlineData("sv")]
    public void Validate_ValidLanguages_NoWarnings(string lang)
    {
        var backup = CreateValidBackup();
        backup.Language = lang;
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("language") || w.Contains("Language"));
    }

    // ===== Validation: Task Modes =====

    [Theory]
    [InlineData("InvalidMode")]
    [InlineData("Execute")]
    [InlineData("delete")]
    public void Validate_InvalidTaskMode_ReturnsWarning(string mode)
    {
        var backup = CreateValidBackup();
        backup.TrickplayTaskMode = mode;
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid); // Warning, not error
        Assert.Contains(result.Warnings, w => w.Contains("task mode") || w.Contains("TrickplayTaskMode"));
    }

    [Theory]
    [InlineData("Activate")]
    [InlineData("DryRun")]
    [InlineData("Deactivate")]
    public void Validate_ValidTaskModes_NoWarnings(string mode)
    {
        var backup = CreateValidBackup();
        backup.TrickplayTaskMode = mode;
        backup.EmptyMediaFolderTaskMode = mode;
        backup.OrphanedSubtitleTaskMode = mode;
        backup.LinkRepairTaskMode = mode;
        backup.SeerrCleanupTaskMode = mode;
        backup.RecommendationsTaskMode = mode;
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
    }

    // ===== Validation: Numeric ranges =====

    [Fact]
    public void Validate_NegativeOrphanMinAge_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.OrphanMinAgeDays = -1;
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("OrphanMinAgeDays"));
    }

    [Fact]
    public void Validate_ExcessiveOrphanMinAge_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.OrphanMinAgeDays = 9999;
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_NegativeTrashRetention_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.TrashRetentionDays = -5;
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("TrashRetentionDays"));
    }

    // ===== Security: Script injection =====

    [Theory]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("<iframe src='evil.com'>")]
    [InlineData("<svg onload='alert(1)'>")]
    [InlineData("\" onmouseover=\"alert(1)")]
    public void Validate_ScriptInjectionInLanguage_ReturnsError(string malicious)
    {
        var backup = CreateValidBackup();
        backup.Language = malicious;
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("script injection") || e.Contains("Language"));
    }

    [Theory]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("javascript:void(0)")]
    public void Validate_ScriptInjectionInLibraryNames_ReturnsError(string malicious)
    {
        var backup = CreateValidBackup();
        backup.ExcludedLibraries = malicious;
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("<script>")]
    [InlineData("<embed src='evil'>")]
    [InlineData("<object data='evil'>")]
    [InlineData("<form action='evil'>")]
    public void Validate_ScriptInjectionInTrashPath_ReturnsError(string malicious)
    {
        var backup = CreateValidBackup();
        backup.TrashFolderPath = malicious;
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
    }

    // ===== Security: Null bytes =====

    [Fact]
    public void Validate_NullBytesInString_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.ExcludedLibraries = "Movies\0EvilPayload";
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("null bytes"));
    }

    // ===== Security: Path traversal =====

    [Fact]
    public void Validate_PathTraversalInTrashPath_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.TrashFolderPath = "../../../etc/passwd";
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("traversal"));
    }

    [Theory]
    [InlineData("path|command")]
    [InlineData("path`command`")]
    [InlineData("$(HOME)/trash")]
    [InlineData("path;rm -rf /")]
    public void Validate_CommandInjectionInTrashPath_ReturnsError(string path)
    {
        var backup = CreateValidBackup();
        backup.TrashFolderPath = path;
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("dangerous characters") || e.Contains("TrashFolderPath"));
    }

    [Fact]
    public void Validate_PathTraversalInTrashPath_UseTrashFalse_ReturnsError()
    {
        // BUG GUARD: injection checks must run even when UseTrash=false so a malicious path
        // cannot be stored in config and later activated without re-validation.
        var backup = new BackupData
        {
            BackupVersion = 1, CreatedAt = DateTime.UtcNow, PluginVersion = "1.0.0",
            Language = "en", OrphanMinAgeDays = 7, PluginLogLevel = "INFO",
            TrickplayTaskMode = "DryRun", EmptyMediaFolderTaskMode = "Activate",
            OrphanedSubtitleTaskMode = "Deactivate", LinkRepairTaskMode = "DryRun",
            SeerrCleanupTaskMode = "DryRun", RecommendationsTaskMode = "DryRun",
            UseTrash = false,
            TrashFolderPath = "../../../etc/cron.d/evil",
            TrashRetentionDays = 30
        };
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("traversal", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("path|cmd")]
    [InlineData("$(evil)")]
    [InlineData("path;rm -rf /")]
    public void Validate_CommandInjectionInTrashPath_UseTrashFalse_ReturnsError(string injectedPath)
    {
        var backup = new BackupData
        {
            BackupVersion = 1, CreatedAt = DateTime.UtcNow, PluginVersion = "1.0.0",
            Language = "en", OrphanMinAgeDays = 7, PluginLogLevel = "INFO",
            TrickplayTaskMode = "DryRun", EmptyMediaFolderTaskMode = "Activate",
            OrphanedSubtitleTaskMode = "Deactivate", LinkRepairTaskMode = "DryRun",
            SeerrCleanupTaskMode = "DryRun", RecommendationsTaskMode = "DryRun",
            UseTrash = false,
            TrashFolderPath = injectedPath,
            TrashRetentionDays = 30
        };
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("dangerous characters") || e.Contains("TrashFolderPath"));
    }

    // ===== Security: String length overflow =====

    [Fact]
    public void Validate_ExcessiveStringLength_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.ExcludedLibraries = new string('A', BackupValidator.MaxStringLength + 1);
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("maximum length"));
    }

    // ===== Arr Instances Validation =====

    [Fact]
    public void Validate_TooManyArrInstances_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.RadarrInstances.Clear();
        backup.RadarrInstances.Add(
            new BackupArrInstance { Name = "R1", Url = "http://localhost:7878", ApiKey = "key1" });
        backup.RadarrInstances.Add(
            new BackupArrInstance { Name = "R2", Url = "http://localhost:7879", ApiKey = "key2" });
        backup.RadarrInstances.Add(
            new BackupArrInstance { Name = "R3", Url = "http://localhost:7880", ApiKey = "key3" });
        backup.RadarrInstances.Add(
            new BackupArrInstance { Name = "R4", Url = "http://localhost:7881", ApiKey = "key4" });
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("too many"));
    }

    [Fact]
    public void Validate_InvalidArrUrl_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.RadarrInstances.Clear();
        backup.RadarrInstances.Add(
            new BackupArrInstance { Name = "Radarr", Url = "ftp://not-http.com", ApiKey = "key" });
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("URL"));
    }

    [Fact]
    public void Validate_InvalidArrUrlFormat_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.RadarrInstances.Clear();
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "Radarr", Url = "not-a-url-at-all", ApiKey = "key" });
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ScriptInjectionInArrName_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.RadarrInstances.Clear();
        backup.RadarrInstances.Add(new BackupArrInstance
        { Name = "<script>alert(1)</script>", Url = "http://localhost:7878", ApiKey = "key" });
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_EmptyArrInstances_IsValid()
    {
        var backup = CreateValidBackup();
        backup.RadarrInstances.Clear();
        backup.SonarrInstances.Clear();
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
    }

    // ===== Timeline Validation =====

    [Fact]
    public void Validate_TimelineWithTooManyPoints_ReturnsWarning()
    {
        var backup = CreateValidBackup();
        backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "monthly" };
        for (var i = 0; i < BackupValidator.MaxTimelineDataPoints + 100; i++)
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = DateTime.UtcNow.AddDays(-i),
                CumulativeSize = i * 1000,
                CumulativeFileCount = i
            });

        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("trimmed") || w.Contains("data points"));
    }

    [Fact]
    public void Validate_TimelineWithNegativeSize_ReturnsWarning()
    {
        var backup = CreateValidBackup();
        backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "monthly" };
        backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = DateTime.UtcNow,
            CumulativeSize = -1000
        });

        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("negative"));
    }

    [Fact]
    public void Validate_NullTimeline_IsValid()
    {
        var backup = CreateValidBackup();
        backup.GrowthTimeline = null;
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
    }

    // ===== Baseline Validation =====

    [Fact]
    public void Validate_BaselineWithScriptInPath_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.GrowthBaseline = new GrowthTimelineBaseline
        {
            Directories =
            {
                ["<script>alert(1)</script>"] = new BaselineDirectoryEntry
                {
                    CreatedUtc = DateTime.UtcNow,
                    Size = 1000
                }
            }
        };

        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("script injection"));
    }

    [Fact]
    public void Validate_BaselineWithLongPath_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.GrowthBaseline = new GrowthTimelineBaseline
        {
            Directories =
            {
                [new string('A', 1001)] = new BaselineDirectoryEntry
                {
                    CreatedUtc = DateTime.UtcNow,
                    Size = 1000
                }
            }
        };

        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("1000 characters"));
    }

    // ===== Sanitize =====

    [Fact]
    public void Sanitize_InvalidLanguage_DefaultsToEnglish()
    {
        var backup = CreateValidBackup();
        backup.Language = "invalid";
        BackupSanitizer.Sanitize(backup);

        Assert.Equal("en", backup.Language);
    }

    [Fact]
    public void Sanitize_InvalidTaskMode_DefaultsToDryRun()
    {
        var backup = CreateValidBackup();
        backup.TrickplayTaskMode = "InvalidMode";
        backup.EmptyMediaFolderTaskMode = "";
        backup.OrphanedSubtitleTaskMode = null!;
        BackupSanitizer.Sanitize(backup);

        Assert.Equal("DryRun", backup.TrickplayTaskMode);
        Assert.Equal("DryRun", backup.EmptyMediaFolderTaskMode);
        Assert.Equal("DryRun", backup.OrphanedSubtitleTaskMode);
    }

    [Fact]
    public void Sanitize_OutOfRangeNumbers_AreClamped()
    {
        var backup = CreateValidBackup();
        backup.OrphanMinAgeDays = -10;
        backup.TrashRetentionDays = 99999;
        BackupSanitizer.Sanitize(backup);

        Assert.Equal(0, backup.OrphanMinAgeDays);
        Assert.Equal(3650, backup.TrashRetentionDays);
    }

    [Fact]
    public void Sanitize_LongStrings_AreTruncated()
    {
        var backup = CreateValidBackup();
        backup.ExcludedLibraries = new string('A', 2000);
        BackupSanitizer.Sanitize(backup);

        Assert.Equal(BackupValidator.MaxStringLength, backup.ExcludedLibraries.Length);
    }

    [Fact]
    public void Sanitize_TooManyArrInstances_AreTrimmed()
    {
        var backup = CreateValidBackup();
        backup.RadarrInstances.Clear();
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R1", Url = "http://localhost:1", ApiKey = "k1" });
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R2", Url = "http://localhost:2", ApiKey = "k2" });
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R3", Url = "http://localhost:3", ApiKey = "k3" });
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R4", Url = "http://localhost:4", ApiKey = "k4" });
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R5", Url = "http://localhost:5", ApiKey = "k5" });
        BackupSanitizer.Sanitize(backup);

        Assert.Equal(BackupValidator.MaxArrInstances, backup.RadarrInstances.Count);
    }

    [Fact]
    public void Sanitize_InvalidLogLevel_DefaultsToInfo()
    {
        var backup = CreateValidBackup();
        backup.PluginLogLevel = "TRACE";
        BackupSanitizer.Sanitize(backup);

        Assert.Equal("INFO", backup.PluginLogLevel);
    }

    // ===== Serialization round-trip =====

    [Fact]
    public void SerializeDeserialize_RoundTrip_PreservesData()
    {
        var backup = CreateValidBackup();
        backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "monthly" };
        backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 123456789,
            CumulativeFileCount = 42
        });
        backup.GrowthBaseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            Directories =
            {
                [@"C:\Media\Movie 1"] = new BaselineDirectoryEntry
                {
                    CreatedUtc = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    Size = 55555
                }
            }
        };

        var json = BackupService.SerializeBackup(backup);
        var restored = BackupService.DeserializeBackup(json);

        Assert.NotNull(restored);
        Assert.Equal(backup.BackupVersion, restored.BackupVersion);
        Assert.Equal(backup.Language, restored.Language);
        Assert.Equal(backup.TrickplayTaskMode, restored.TrickplayTaskMode);
        Assert.Equal(backup.UseTrash, restored.UseTrash);
        Assert.Equal(backup.TrashFolderPath, restored.TrashFolderPath);
        Assert.Equal(backup.TrashRetentionDays, restored.TrashRetentionDays);
        Assert.Single(restored.RadarrInstances);
        Assert.Equal("Radarr", restored.RadarrInstances[0].Name);
        Assert.NotNull(restored.GrowthTimeline);
        Assert.Single(restored.GrowthTimeline!.DataPoints);
        Assert.Equal(123456789, restored.GrowthTimeline.DataPoints[0].CumulativeSize);
        Assert.NotNull(restored.GrowthBaseline);
        Assert.Equal(backup.GrowthBaseline.FirstScanTimestamp, restored.GrowthBaseline!.FirstScanTimestamp);
        Assert.Single(restored.GrowthBaseline.Directories);
        Assert.Equal(55555, restored.GrowthBaseline.Directories[@"C:\Media\Movie 1"].Size);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsNull()
    {
        var result = BackupService.DeserializeBackup("not valid json {{{");
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_EmptyString_ReturnsNull()
    {
        Assert.Null(BackupService.DeserializeBackup(""));
        Assert.Null(BackupService.DeserializeBackup(null!));
        Assert.Null(BackupService.DeserializeBackup("   "));
    }

    [Fact]
    public void Deserialize_EmptyObject_ReturnsDefaults()
    {
        var result = BackupService.DeserializeBackup("{}");
        Assert.NotNull(result);
        Assert.Equal(1, result.BackupVersion); // default from class initializer
        Assert.Equal("en", result.Language); // default from class
    }

    // ===== Security: ContainsScriptInjection helper =====

    [Theory]
    [InlineData("<script>alert(1)</script>", true)]
    [InlineData("javascript:void(0)", true)]
    [InlineData("<SCRIPT>", true)]
    [InlineData("<iframe src='x'>", true)]
    [InlineData("<embed>", true)]
    [InlineData("<object>", true)]
    [InlineData("<form action='x'>", true)]
    [InlineData("<svg onload='x'>", true)]
    [InlineData("onclick=alert(1)", true)]
    [InlineData("normal text", false)]
    [InlineData("/path/to/file.json", false)]
    [InlineData("Movies, TV Shows", false)]
    [InlineData("http://localhost:7878", false)]
    [InlineData("", false)]
    public void ContainsScriptInjection_DetectsCorrectly(string input, bool expected)
    {
        Assert.Equal(expected, BackupValidator.ContainsScriptInjection(input));
    }

    // ===== Security: ContainsNullBytes helper =====

    [Fact]
    public void ContainsNullBytes_DetectsNullByte()
    {
        Assert.True(BackupValidator.ContainsNullBytes("hello\0world"));
        Assert.False(BackupValidator.ContainsNullBytes("hello world"));
        Assert.False(BackupValidator.ContainsNullBytes(""));
    }

    // ===== Full validation pipeline: malicious backup =====

    [Fact]
    public void Validate_CompletelyMaliciousBackup_RejectsAll()
    {
        var backup = new BackupData
        {
            BackupVersion = 999,
            Language = "<script>alert(1)</script>",
            ExcludedLibraries = new string('A', 5000),
            TrashFolderPath = "../../../etc/shadow",
            OrphanMinAgeDays = -100,
            TrashRetentionDays = -50
        };
        backup.RadarrInstances.Add(new BackupArrInstance
        { Name = "<script>", Url = "ftp://evil.com", ApiKey = "key\0evil" });
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R2", Url = "http://ok.com", ApiKey = "ok" });
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R3", Url = "http://ok.com", ApiKey = "ok" });
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "R4", Url = "http://ok.com", ApiKey = "ok" });

        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        // Should have multiple errors
        Assert.True(result.Errors.Count >= 5,
            $"Expected >= 5 errors, got {result.Errors.Count}: {string.Join("; ", result.Errors)}");
    }

    // ===== Edge cases =====

    [Fact]
    public void Validate_EmptyBackup_WithVersion1_IsValid()
    {
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = DateTime.UtcNow
        };
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_BoundaryOrphanMinAge_IsValid()
    {
        var backup = CreateValidBackup();
        backup.OrphanMinAgeDays = 0;
        Assert.True(BackupValidator.Validate(backup).IsValid);

        backup.OrphanMinAgeDays = 3650;
        Assert.True(BackupValidator.Validate(backup).IsValid);
    }

    [Fact]
    public void Validate_BoundaryTrashRetention_IsValid()
    {
        var backup = CreateValidBackup();
        backup.TrashRetentionDays = 0;
        Assert.True(BackupValidator.Validate(backup).IsValid);

        backup.TrashRetentionDays = 3650;
        Assert.True(BackupValidator.Validate(backup).IsValid);
    }

    // ===== File I/O: RestoreBackup with data path =====

    [Fact]
    public void CreateBackup_ReadsHistoricalDataFiles()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "jh-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var timeline = new GrowthTimelineResult { Granularity = "monthly" };
            timeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                CumulativeSize = 1000,
                CumulativeFileCount = 2
            });

            var baseline = new GrowthTimelineBaseline
            {
                FirstScanTimestamp = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                Directories =
                {
                    [@"C:\Media\Movie 1"] = new BaselineDirectoryEntry
                    {
                        CreatedUtc = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                        Size = 2000
                    }
                }
            };

            File.WriteAllText(Path.Join(tempDir, "jellyfin-helper-growth-timeline.json"),
                JsonSerializer.Serialize(timeline));
            File.WriteAllText(Path.Join(tempDir, "jellyfin-helper-growth-baseline.json"),
                JsonSerializer.Serialize(baseline));

            var logger = TestMockFactory.CreateLogger<BackupService>();
            var configService = new Mock<IPluginConfigurationService>();
            configService.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration());
            configService.Setup(c => c.PluginVersion).Returns("1.0.0-test");
            var service = new BackupService(tempDir, configService.Object, TestMockFactory.CreatePluginLogService(),
                logger.Object);

            var backup = service.CreateBackup();

            Assert.NotNull(backup.GrowthTimeline);
            Assert.Single(backup.GrowthTimeline!.DataPoints);
            Assert.Equal(1000, backup.GrowthTimeline.DataPoints[0].CumulativeSize);
            Assert.NotNull(backup.GrowthBaseline);
            Assert.Equal(baseline.FirstScanTimestamp, backup.GrowthBaseline!.FirstScanTimestamp);
            Assert.Single(backup.GrowthBaseline.Directories);
            Assert.Equal(2000, backup.GrowthBaseline.Directories[@"C:\Media\Movie 1"].Size);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RestoreBackup_WritesTimelineFile()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "jh-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var logger = TestMockFactory.CreateLogger<BackupService>();
            var configService = new Mock<IPluginConfigurationService>();
            var service = new BackupService(tempDir, configService.Object, TestMockFactory.CreatePluginLogService(),
                logger.Object);

            var backup = CreateValidBackup();
            backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "monthly" };
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = DateTime.UtcNow,
                CumulativeSize = 1000
            });
            backup.GrowthBaseline = new GrowthTimelineBaseline
            {
                FirstScanTimestamp = DateTime.UtcNow
            };
            // RestoreBackup won't restore config (no Plugin.Instance), but should write files
            var summary = service.RestoreBackup(backup);

            Assert.True(summary.TimelineRestored);
            Assert.True(summary.BaselineRestored);

            // Verify files were written
            Assert.True(File.Exists(Path.Join(tempDir, "jellyfin-helper-growth-timeline.json")));
            Assert.True(File.Exists(Path.Join(tempDir, "jellyfin-helper-growth-baseline.json")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RestoreBackup_NoHistoricalData_SkipsFiles()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "jh-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var logger = TestMockFactory.CreateLogger<BackupService>();
            var configService = new Mock<IPluginConfigurationService>();
            var service = new BackupService(tempDir, configService.Object, TestMockFactory.CreatePluginLogService(),
                logger.Object);

            var backup = CreateValidBackup();
            backup.GrowthTimeline = null;
            backup.GrowthBaseline = null;
            var summary = service.RestoreBackup(backup);

            Assert.False(summary.TimelineRestored);
            Assert.False(summary.BaselineRestored);

            Assert.False(File.Exists(Path.Join(tempDir, "jellyfin-helper-growth-timeline.json")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BackupData_RecommendationsTaskMode_SurvivesJsonRoundTrip()
    {
        var backup = CreateValidBackup();
        backup.RecommendationsTaskMode = "Activate";
        backup.SyncRecommendationsToPlaylist = true;

        var json = JsonSerializer.Serialize(backup);
        var deserialized = JsonSerializer.Deserialize<BackupData>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Activate", deserialized!.RecommendationsTaskMode);
        Assert.True(deserialized.SyncRecommendationsToPlaylist);
    }

    [Fact]
    public void Sanitize_InvalidSeerrCleanupTaskMode_DefaultsToDeactivate()
    {
        var backup = CreateValidBackup();
        backup.SeerrCleanupTaskMode = "InvalidMode";
        BackupSanitizer.Sanitize(backup);

        Assert.Equal("Deactivate", backup.SeerrCleanupTaskMode);
    }

    [Fact]
    public void Sanitize_InvalidRecommendationsTaskMode_DefaultsToDryRun()
    {
        var backup = CreateValidBackup();
        backup.RecommendationsTaskMode = "InvalidMode";
        BackupSanitizer.Sanitize(backup);

        Assert.Equal("DryRun", backup.RecommendationsTaskMode);
    }

    // ===== BackupValidator: previously-uncovered growth-baseline branches =====

    [Fact]
    public void Validate_GrowthBaseline_NegativeDirectorySize_EmitsWarning()
    {
        // BUG GUARD: Line 419-423 in BackupValidator emits a warning when a baseline directory
        // reports a negative size. Without this warning the operator would silently import
        // corrupted data with impossible sizes.
        var backup = CreateValidBackup();
        backup.GrowthBaseline = new GrowthTimelineBaseline();
        backup.GrowthBaseline.Directories["/media/movies"] = new BaselineDirectoryEntry
        {
            Size = -1L,
            Count = 5,
            CreatedUtc = DateTime.UtcNow
        };

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("negative size", StringComparison.OrdinalIgnoreCase));
        // Errors must NOT include this — negative size is a warning, not a hard fail.
        Assert.DoesNotContain(result.Errors, e => e.Contains("negative size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_GrowthBaseline_NegativeDirectoryCount_EmitsWarning()
    {
        // BUG GUARD: Line 425-429. Parallel guard for negative file counts.
        var backup = CreateValidBackup();
        backup.GrowthBaseline = new GrowthTimelineBaseline();
        backup.GrowthBaseline.Directories["/media/movies"] = new BaselineDirectoryEntry
        {
            Size = 1024,
            Count = -3,
            CreatedUtc = DateTime.UtcNow
        };

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("negative count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_GrowthBaseline_NegativeSizeAndCount_EachWarnedOnlyOnce()
    {
        // BUG GUARD: Lines 375-383 & 425-429 use per-flag `warnedNegative*` sentinels to
        // avoid spamming the operator with N warnings for N corrupt entries. This test
        // proves the sentinel logic — only ONE warning per issue type across many bad rows.
        var backup = CreateValidBackup();
        backup.GrowthBaseline = new GrowthTimelineBaseline();
        for (int i = 0; i < 10; i++)
        {
            backup.GrowthBaseline.Directories[$"/media/dir{i}"] = new BaselineDirectoryEntry
            {
                Size = -100L * (i + 1),
                Count = -1 - i,
                CreatedUtc = DateTime.UtcNow
            };
        }

        var result = BackupValidator.Validate(backup);

        var negSizeWarnings = result.Warnings.Count(w => w.Contains("negative size", StringComparison.OrdinalIgnoreCase));
        var negCountWarnings = result.Warnings.Count(w => w.Contains("negative count", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, negSizeWarnings);
        Assert.Equal(1, negCountWarnings);
    }

    [Fact]
    public void Validate_GrowthBaseline_OversizedDirectoryKey_EmitsError()
    {
        // BUG GUARD: Line 432-436 — an attacker could embed a huge string as a directory
        // key. The validator must reject any path > 1000 chars with a hard error, breaking
        // out of the loop to avoid ballooning the error list.
        var backup = CreateValidBackup();
        backup.GrowthBaseline = new GrowthTimelineBaseline();
        var longKey = new string('a', 1500);
        backup.GrowthBaseline.Directories[longKey] = new BaselineDirectoryEntry
        {
            Size = 100,
            Count = 1,
            CreatedUtc = DateTime.UtcNow
        };

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("exceeds 1000 characters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_GrowthBaseline_ScriptInjectionInDirectoryKey_EmitsError()
    {
        // BUG GUARD: Line 438-444 — a baseline export from a compromised system might
        // carry <script>...</script> as a directory key. The validator must reject it
        // to prevent DOM injection when the key is later shown in the admin UI.
        var backup = CreateValidBackup();
        backup.GrowthBaseline = new GrowthTimelineBaseline();
        backup.GrowthBaseline.Directories["/media/<script>alert(1)</script>"] = new BaselineDirectoryEntry
        {
            Size = 100,
            Count = 1,
            CreatedUtc = DateTime.UtcNow
        };

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("script injection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_GrowthBaseline_TooManyDirectories_EmitsWarningOnly()
    {
        // BUG GUARD: Line 407-411 — baselines above MaxBaselineDirectories (50_000) must
        // trigger a WARNING (not an error) so the import proceeds with trimming rather
        // than aborting for a merely oversized dataset. We seed a fixture that is otherwise
        // fully valid so the only failure surface is the oversized directory set, and then
        // assert the warn-only contract directly (IsValid must remain true).
        var backup = CreateValidBackup();
        backup.GrowthBaseline = new GrowthTimelineBaseline();
        for (int i = 0; i < BackupValidator.MaxBaselineDirectories + 5; i++)
        {
            backup.GrowthBaseline.Directories[$"/media/dir{i}"] = new BaselineDirectoryEntry
            {
                Size = 100,
                Count = 1,
                CreatedUtc = DateTime.UtcNow
            };
        }

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("directories", StringComparison.OrdinalIgnoreCase));
        // Warn-only contract: the import must remain valid. A weaker "no directory-related
        // error" check would let unrelated errors mask a regression here.
        Assert.True(
            result.IsValid,
            $"oversized baseline must NOT invalidate the backup; errors: {string.Join("; ", result.Errors)}");
    }

    [Fact]
    public void Validate_GrowthTimeline_NegativeCumulativeSize_EmitsWarningOnce()
    {
        // BUG GUARD: Lines 379-384 mirror the baseline warn-once logic. Even with 100
        // consecutive negative sizes we must produce exactly ONE warning.
        var backup = CreateValidBackup();
        backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "monthly" };
        for (int i = 0; i < 100; i++)
        {
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = DateTime.UtcNow.AddDays(-i),
                CumulativeSize = -(i + 1) * 1000,
                CumulativeFileCount = 10
            });
        }

        var result = BackupValidator.Validate(backup);

        var negSizeWarnings = result.Warnings.Count(w => w.Contains("negative cumulative size", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, negSizeWarnings);
    }

    [Fact]
    public void Validate_GrowthTimeline_NegativeCumulativeFileCount_EmitsWarningOnce()
    {
        // BUG GUARD: Lines 386-391 parallel guard for file count.
        var backup = CreateValidBackup();
        backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "monthly" };
        for (int i = 0; i < 5; i++)
        {
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = DateTime.UtcNow.AddDays(-i),
                CumulativeSize = 1000,
                CumulativeFileCount = -1 - i
            });
        }

        var result = BackupValidator.Validate(backup);

        var negCountWarnings = result.Warnings.Count(w => w.Contains("negative cumulative file count", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, negCountWarnings);
    }

    [Fact]
    public void Validate_GrowthTimeline_UnknownGranularity_EmitsWarning()
    {
        // BUG GUARD: Line 369-372 — the ValidGranularities set is authoritative. An unknown
        // granularity must degrade to a warning so the timeline still imports, but with a
        // hint that the value is unexpected.
        var backup = CreateValidBackup();
        backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "hyperfast" };

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("granularity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_SeerrUrl_InvalidScheme_EmitsError()
    {
        // BUG GUARD: Lines 150-155 — SeerrUrl must reject ftp/file/gopher schemes; only
        // http/https are safe for the outbound API calls.
        var backup = CreateValidBackup();
        backup.SeerrUrl = "ftp://malicious.example.com";

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("SeerrUrl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_SeerrUrl_MalformedString_EmitsError()
    {
        var backup = CreateValidBackup();
        backup.SeerrUrl = "not-a-url-at-all";

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("SeerrUrl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_UseTrash_EmptyPath_EmitsError()
    {
        // BUG GUARD: Lines 203-216 — when UseTrash is true, an empty TrashFolderPath must
        // produce an error. Otherwise the trash system would be enabled with no destination
        // and every "move to trash" call would silently no-op or move files to the plugin's
        // working directory.
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = DateTime.UtcNow,
            Language = "en",
            UseTrash = true,
            TrashFolderPath = string.Empty
        };

        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("TrashFolderPath", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_TrashPath_WithNewline_EmitsError()
    {
        // BUG GUARD: Line 306-309 — trash paths containing \n or \r could be exploited for
        // log-injection when the path is echoed into structured log lines.
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = DateTime.UtcNow,
            Language = "en",
            UseTrash = true,
            TrashFolderPath = "/tmp/trash\nInjected: line"
        };

        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("newline", StringComparison.OrdinalIgnoreCase));
    }

    // ===== Fix #2: ContainsSecrets flag on CreateBackup =====

    [Fact]
    public void CreateBackup_ContainsSecrets_TrueWhenSeerrKeyPresent()
    {
        // BUG GUARD: the backup file is downloadable and may contain credentials.
        // ContainsSecrets must be true whenever any API key is non-empty so the
        // UI/caller can display a security warning to the operator.
        var tempDir = Path.Join(Path.GetTempPath(), "jh-cs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var liveConfig = new PluginConfiguration { SeerrApiKey = "my-seerr-key" };
            var configMock = new Mock<IPluginConfigurationService>();
            configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
            configMock.Setup(c => c.PluginVersion).Returns("1.0.0");

            var service = new BackupService(tempDir, configMock.Object,
                TestMockFactory.CreatePluginLogService(),
                TestMockFactory.CreateLogger<BackupService>().Object);

            var backup = service.CreateBackup();

            Assert.True(backup.ContainsSecrets);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateBackup_ContainsSecrets_TrueWhenArrKeyPresent()
    {
        // ContainsSecrets must be true when Radarr or Sonarr instances carry keys
        // even if SeerrApiKey is empty.
        var tempDir = Path.Join(Path.GetTempPath(), "jh-cs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var liveConfig = new PluginConfiguration();
            liveConfig.RadarrInstances.Add(new ArrInstanceConfig
                { Name = "R1", Url = "http://r:7878", ApiKey = "arr-key" });

            var configMock = new Mock<IPluginConfigurationService>();
            configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
            configMock.Setup(c => c.PluginVersion).Returns("1.0.0");

            var service = new BackupService(tempDir, configMock.Object,
                TestMockFactory.CreatePluginLogService(),
                TestMockFactory.CreateLogger<BackupService>().Object);

            var backup = service.CreateBackup();

            Assert.True(backup.ContainsSecrets);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateBackup_ContainsSecrets_FalseWhenAllKeysEmpty()
    {
        // When there are no API keys at all ContainsSecrets must be false — the
        // operator does not need a security warning for a credential-free export.
        var tempDir = Path.Join(Path.GetTempPath(), "jh-cs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var liveConfig = new PluginConfiguration(); // SeerrApiKey = "" by default, no Arr instances
            var configMock = new Mock<IPluginConfigurationService>();
            configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
            configMock.Setup(c => c.PluginVersion).Returns("1.0.0");

            var service = new BackupService(tempDir, configMock.Object,
                TestMockFactory.CreatePluginLogService(),
                TestMockFactory.CreateLogger<BackupService>().Object);

            var backup = service.CreateBackup();

            Assert.False(backup.ContainsSecrets);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ===== Fix #5: CredentialsChanged on BackupRestoreSummary =====

    [Fact]
    public void RestoreBackup_CredentialsChanged_TrueWhenSeerrKeyDiffers()
    {
        // BUG GUARD: CredentialsChanged must be set when the incoming Seerr key is
        // different from the one currently stored so callers can surface an audit
        // notification without having to re-inspect the config themselves.
        var tempDir = Path.Join(Path.GetTempPath(), "jh-cc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var liveConfig = new PluginConfiguration { SeerrApiKey = "old-key" };
            var configMock = new Mock<IPluginConfigurationService>();
            configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
            configMock.Setup(c => c.IsInitialized).Returns(true);
            configMock.Setup(c => c.PluginVersion).Returns("1.0.0");
            TestMockFactory.SetupReadAndMutate(configMock, liveConfig);

            var service = new BackupService(tempDir, configMock.Object,
                TestMockFactory.CreatePluginLogService(),
                TestMockFactory.CreateLogger<BackupService>().Object);

            var backup = CreateValidBackup();
            backup.SeerrApiKey = "new-different-key";

            var summary = service.RestoreBackup(backup);

            Assert.True(summary.CredentialsChanged);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RestoreBackup_CredentialsChanged_FalseWhenAllKeysIdentical()
    {
        // When the backup contains the same keys as the live config no credential
        // change is occurring — CredentialsChanged must stay false to avoid false
        // positives that would confuse the operator.
        var tempDir = Path.Join(Path.GetTempPath(), "jh-cc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var liveConfig = new PluginConfiguration { SeerrApiKey = "same-key" };
            // Add Arr instances with the exact same keys that the backup will carry.
            liveConfig.RadarrInstances.Add(new ArrInstanceConfig
                { Name = "Radarr", Url = "http://localhost:7878", ApiKey = "abc123" });
            liveConfig.SonarrInstances.Add(new ArrInstanceConfig
                { Name = "Sonarr", Url = "http://localhost:8989", ApiKey = "def456" });

            var configMock = new Mock<IPluginConfigurationService>();
            configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
            configMock.Setup(c => c.IsInitialized).Returns(true);
            configMock.Setup(c => c.PluginVersion).Returns("1.0.0");
            TestMockFactory.SetupReadAndMutate(configMock, liveConfig);

            var service = new BackupService(tempDir, configMock.Object,
                TestMockFactory.CreatePluginLogService(),
                TestMockFactory.CreateLogger<BackupService>().Object);

            // CreateValidBackup() has Radarr "abc123", Sonarr "def456" — identical to live.
            var backup = CreateValidBackup();
            backup.SeerrApiKey = "same-key"; // identical to live value

            var summary = service.RestoreBackup(backup);

            Assert.False(summary.CredentialsChanged);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RestoreBackup_CredentialsChanged_WarningLogFiredWhenKeyChanges()
    {
        // BUG GUARD: the audit Warning log must fire whenever a credential actually
        // changes so ops tooling that tails Warning entries gets a reliable signal.
        var tempDir = Path.Join(Path.GetTempPath(), "jh-cc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var pluginLogMock = new Mock<Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService>();
            var liveConfig = new PluginConfiguration { SeerrApiKey = "old-key" };
            var configMock = new Mock<IPluginConfigurationService>();
            configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
            configMock.Setup(c => c.IsInitialized).Returns(true);
            configMock.Setup(c => c.PluginVersion).Returns("1.0.0");
            TestMockFactory.SetupReadAndMutate(configMock, liveConfig);

            var service = new BackupService(tempDir, configMock.Object,
                pluginLogMock.Object,
                TestMockFactory.CreateLogger<BackupService>().Object);

            var backup = CreateValidBackup();
            backup.SeerrApiKey = "brand-new-key";

            service.RestoreBackup(backup);

            pluginLogMock.Verify(
                p => p.LogWarning(
                    "Backup",
                    It.Is<string>(msg =>
                        msg.Contains("replacing credentials", StringComparison.OrdinalIgnoreCase)
                        && msg.Contains("Seerr", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<Exception?>(),
                    It.IsAny<Microsoft.Extensions.Logging.ILogger?>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
