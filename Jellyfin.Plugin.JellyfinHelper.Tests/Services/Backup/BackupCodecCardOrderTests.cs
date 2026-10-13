using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Backup;

/// <summary>
///     Tests for the Codecs-tab card order through the backup pipeline: lenient sanitization, validation
///     (unknown ids warn, dangerous/oversized ids error), and the CreateBackup -> RestoreConfiguration
///     round-trip. The backup path is lenient where the settings endpoint is strict, so an import from a
///     different plugin version stays usable.
/// </summary>
public sealed class BackupCodecCardOrderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Jellyfin.Plugin.JellyfinHelper.Services.Security.ISecretProtector _secretProtector
        = TestMockFactory.CreateSecretProtector();

    public BackupCodecCardOrderTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-backup-codec-" + Guid.NewGuid().ToString("N")[..8]);
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

    private BackupService CreateService(IPluginConfigurationService configService) => new(
        _tempDir,
        configService,
        TestMockFactory.CreatePluginLogService(),
        TestMockFactory.CreateLogger<BackupService>().Object,
        growthTimeline: null,
        secretProtector: _secretProtector);

    private (BackupService Service, PluginConfiguration LiveConfig) CreateServiceWithInitializedConfig()
    {
        var liveConfig = new PluginConfiguration();
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
        configMock.Setup(c => c.IsInitialized).Returns(true);
        configMock.Setup(c => c.PluginVersion).Returns("1.0.0-test");
        TestMockFactory.SetupReadAndMutate(configMock, liveConfig);
        return (CreateService(configMock.Object), liveConfig);
    }

    private static BackupData MakeMinimalValidBackup() => new()
    {
        BackupVersion = 1,
        CreatedAt = DateTime.UtcNow,
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

    [Fact]
    public void Sanitize_DropsUnknownOversizedAndDuplicateIds()
    {
        var backup = new BackupData
        {
            CodecCardOrder =
            {
                "containers",
                "notACard",            // unknown -> dropped
                new string('x', 65),   // oversized -> dropped
                "resolutions",
                "containers"           // duplicate -> dropped
            }
        };

        BackupSanitizer.Sanitize(backup);

        Assert.Equal(["containers", "resolutions"], backup.CodecCardOrder);
    }

    [Fact]
    public void Sanitize_NeverExceedsCap()
    {
        // Only known ids survive and there are 11 of them, so the result can never breach the 32 cap.
        // This locks the post-sanitize count invariant even against an oversized, noisy input.
        var backup = new BackupData();
        for (var i = 0; i < 40; i++)
        {
            backup.CodecCardOrder.Add($"card{i}");
        }

        backup.CodecCardOrder.AddRange(CodecCardCatalog.Canonical);

        BackupSanitizer.Sanitize(backup);

        Assert.True(backup.CodecCardOrder.Count <= BackupValidator.MaxCodecCardOrderEntries);
        Assert.Equal(CodecCardCatalog.Canonical, backup.CodecCardOrder);
    }

    [Fact]
    public void Validate_UnknownId_ProducesWarningNotError()
    {
        var backup = MakeMinimalValidBackup();
        backup.CodecCardOrder.Add("containers");
        backup.CodecCardOrder.Add("notACard");

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Warnings, w => w.Contains("notACard", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, e => e.Contains("notACard", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_OverlongEntry_ProducesError()
    {
        var backup = MakeMinimalValidBackup();
        backup.CodecCardOrder.Add(new string('x', 65));

        var result = BackupValidator.Validate(backup);

        Assert.Contains(result.Errors, e => e.Contains("CodecCardOrder", StringComparison.Ordinal));
    }

    [Fact]
    public void SanitizeAndValidate_BackupWithoutField_IsClean()
    {
        // A backup from before the field existed has the default empty list - it must sanitize to
        // empty and validate without any CodecCardOrder error or warning.
        var backup = MakeMinimalValidBackup();
        Assert.Empty(backup.CodecCardOrder);

        BackupSanitizer.Sanitize(backup);
        Assert.Empty(backup.CodecCardOrder);

        var result = BackupValidator.Validate(backup);
        Assert.DoesNotContain(result.Errors, e => e.Contains("CodecCardOrder", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("CodecCardOrder", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateBackupThenRestore_PreservesPermutation()
    {
        var permutation = CodecCardCatalog.Canonical.Reverse().ToList();

        var sourceConfig = new PluginConfiguration { CodecCardOrder = permutation.ToList() };
        var sourceMock = new Mock<IPluginConfigurationService>();
        sourceMock.Setup(c => c.GetConfiguration()).Returns(sourceConfig);
        sourceMock.Setup(c => c.IsInitialized).Returns(true);
        sourceMock.Setup(c => c.PluginVersion).Returns("1.0.0");
        var exporter = CreateService(sourceMock.Object);

        var backup = exporter.CreateBackup(includeSecrets: true);
        Assert.Equal(permutation, backup.CodecCardOrder);

        var (service, liveConfig) = CreateServiceWithInitializedConfig();
        service.RestoreBackup(backup);

        Assert.Equal(permutation, liveConfig.CodecCardOrder);
    }

    [Fact]
    public void RestoreBackup_DoesNotFlagCredentialsChangedForCardOrder()
    {
        // Restoring a card order is not a credential change; CredentialsChanged must stay false when
        // only the card order (and no secrets) differs.
        var (service, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.CodecCardOrder.AddRange(CodecCardCatalog.Canonical.Reverse());

        var summary = service.RestoreBackup(backup);

        Assert.False(summary.CredentialsChanged);
    }
}
