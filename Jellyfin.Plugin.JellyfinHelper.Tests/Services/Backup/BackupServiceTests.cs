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
    private static readonly DateTime ReferenceTime = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static BackupData CreateValidBackup()
    {
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = ReferenceTime,
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
        backup.CreatedAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = BackupValidator.Validate(backup);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("future"));
    }

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

    [Fact]
    public void Validate_NullBytesInString_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.ExcludedLibraries = "Movies\0EvilPayload";
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("null bytes"));
    }

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
    public void Validate_ExcessiveStringLength_ReturnsError()
    {
        var backup = CreateValidBackup();
        backup.ExcludedLibraries = new string('A', BackupValidator.MaxStringLength + 1);
        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("maximum length"));
    }

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

    [Fact]
    public void Validate_TimelineWithTooManyPoints_ReturnsWarning()
    {
        var backup = CreateValidBackup();
        backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "monthly" };
        for (var i = 0; i < BackupValidator.MaxTimelineDataPoints + 100; i++)
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = ReferenceTime.AddDays(-i),
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
            Date = ReferenceTime,
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
                    CreatedUtc = ReferenceTime,
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
                    CreatedUtc = ReferenceTime,
                    Size = 1000
                }
            }
        };

        var result = BackupValidator.Validate(backup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("1000 characters"));
    }

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

    [Fact]
    public void ContainsNullBytes_DetectsNullByte()
    {
        Assert.True(BackupValidator.ContainsNullBytes("hello\0world"));
        Assert.False(BackupValidator.ContainsNullBytes("hello world"));
        Assert.False(BackupValidator.ContainsNullBytes(""));
    }

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

    [Fact]
    public void Validate_EmptyBackup_WithVersion1_IsValid()
    {
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = ReferenceTime
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
            backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "daily" };
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                CumulativeSize = 1000
            });
            backup.GrowthBaseline = new GrowthTimelineBaseline
            {
                FirstScanTimestamp = ReferenceTime
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
    public void SeerrCleanupAgeDays_BelowMin_ClampedToZero()
    {
        var backup = CreateValidBackup();
        backup.SeerrCleanupAgeDays = -5;
        BackupSanitizer.Sanitize(backup);

        Assert.Equal(0, backup.SeerrCleanupAgeDays);
    }

    [Fact]
    public void SeerrCleanupAgeDays_AboveMax_ClampedToMax()
    {
        var backup = CreateValidBackup();
        backup.SeerrCleanupAgeDays = 99999;
        BackupSanitizer.Sanitize(backup);

        Assert.Equal(BackupValidator.MaxRetentionDays, backup.SeerrCleanupAgeDays);
    }

    [Fact]
    public void SeerrCleanupAgeDays_Null_LeftNull()
    {
        var backup = CreateValidBackup();
        backup.SeerrCleanupAgeDays = null;
        BackupSanitizer.Sanitize(backup);

        Assert.Null(backup.SeerrCleanupAgeDays);
    }

    [Fact]
    public void TimelineTrimming_OverLimit_PreservesEarliestAndKeepsNewest()
    {
        var backup = CreateValidBackup();
        backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "daily" };
        var origin = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var totalPoints = BackupValidator.MaxTimelineDataPoints + 5;

        // Ascending daily points from a very old origin. Index 0 is the earliest (growth-curve origin).
        for (var i = 0; i < totalPoints; i++)
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = origin.AddDays(i),
                CumulativeSize = i * 1000,
                CumulativeFileCount = i
            });

        BackupSanitizer.Sanitize(backup);

        Assert.Equal(BackupValidator.MaxTimelineDataPoints, backup.GrowthTimeline.DataPoints.Count);

        // The earliest point must survive so the curve keeps its origin.
        Assert.Equal(origin, backup.GrowthTimeline.DataPoints[0].Date);

        // The newest point must survive so the curve keeps its latest value.
        Assert.Equal(origin.AddDays(totalPoints - 1), backup.GrowthTimeline.DataPoints[^1].Date);
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

    [Fact]
    public void Validate_TimelineWithBothNegativeSizeAndCount_EmitsBothWarningsOnce()
    {
        var backup = CreateValidBackup();
        var timeline = new GrowthTimelineResult { Granularity = "daily" };
        timeline.DataPoints.Add(new GrowthTimelinePoint { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = -1, CumulativeFileCount = -1 });
        timeline.DataPoints.Add(new GrowthTimelinePoint { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = -2, CumulativeFileCount = -2 });
        timeline.DataPoints.Add(new GrowthTimelinePoint { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = -3, CumulativeFileCount = -3 });
        backup.GrowthTimeline = timeline;

        var result = BackupValidator.Validate(backup);

        // Should warn exactly once per flag (break fires as soon as both are set)
        Assert.Equal(1, result.Warnings.Count(w => w.Contains("negative cumulative size")));
        Assert.Equal(1, result.Warnings.Count(w => w.Contains("negative cumulative file count")));
    }

    [Fact]
    public void Validate_BaselinePathWithScriptInjectionAndLongPath_ReportsInjectionError()
    {
        var backup = CreateValidBackup();
        // Path that both triggers script injection AND exceeds length limit.
        // Injection check must fire (not be skipped by a length-guard continue).
        var injectionPath = "<script>x</script>" + new string('A', 990);
        var baseline = new GrowthTimelineBaseline();
        baseline.Directories[injectionPath] = new BaselineDirectoryEntry { Size = 100, Count = 1 };
        backup.GrowthBaseline = baseline;

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("script injection"));
    }

    [Fact]
    public void RestoreBackup_DayBasedBackup_MergesIntoCurrentSeries()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "jh-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Current on-disk daily series: days 2 and 3.
            var current = new GrowthTimelineResult { Granularity = "daily" };
            current.DataPoints.Add(new GrowthTimelinePoint { Date = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 200, CumulativeFileCount = 2 });
            current.DataPoints.Add(new GrowthTimelinePoint { Date = new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 300, CumulativeFileCount = 3 });
            File.WriteAllText(Path.Join(tempDir, "jellyfin-helper-growth-timeline.json"), JsonSerializer.Serialize(current));

            var configService = new Mock<IPluginConfigurationService>();
            var service = new BackupService(tempDir, configService.Object, TestMockFactory.CreatePluginLogService(),
                TestMockFactory.CreateLogger<BackupService>().Object);

            // Backup (older server) daily series: day 1 (new history) and day 2 (overlap, higher value).
            var backup = CreateValidBackup();
            backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "daily" };
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint { Date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100, CumulativeFileCount = 1 });
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint { Date = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 250, CumulativeFileCount = 2 });

            var summary = service.RestoreBackup(backup);

            Assert.True(summary.TimelineRestored);

            var mergedJson = File.ReadAllText(Path.Join(tempDir, "jellyfin-helper-growth-timeline.json"));
            var merged = JsonSerializer.Deserialize<GrowthTimelineResult>(mergedJson)!;

            // Day 1 filled in retroactively; days 2 and 3 present; overlapping day 2 preserves the current on-disk point (200) rather than the backup's higher value to avoid a synthetic state that never existed.
            Assert.Equal(3, merged.DataPoints.Count);
            Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), merged.DataPoints[0].Date);
            Assert.Equal(200, merged.DataPoints[1].CumulativeSize);
            Assert.Equal(new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc), merged.DataPoints[2].Date);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RestoreBackup_NoCurrentTimeline_WritesBackupSeriesVerbatim()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "jh-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configService = new Mock<IPluginConfigurationService>();
            var service = new BackupService(tempDir, configService.Object, TestMockFactory.CreatePluginLogService(),
                TestMockFactory.CreateLogger<BackupService>().Object);

            var backup = CreateValidBackup();
            backup.GrowthTimeline = new GrowthTimelineResult { Granularity = "daily" };
            backup.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint { Date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100, CumulativeFileCount = 1 });

            var summary = service.RestoreBackup(backup);

            Assert.True(summary.TimelineRestored);
            var written = JsonSerializer.Deserialize<GrowthTimelineResult>(
                File.ReadAllText(Path.Join(tempDir, "jellyfin-helper-growth-timeline.json")))!;
            Assert.Single(written.DataPoints);
            Assert.Equal(100, written.DataPoints[0].CumulativeSize);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
