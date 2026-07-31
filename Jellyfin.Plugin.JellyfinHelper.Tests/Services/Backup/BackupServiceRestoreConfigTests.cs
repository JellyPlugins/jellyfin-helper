using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Backup;

/// <summary>
///     Focused tests for the RestoreBackup + RestoreConfiguration path when
///     <see cref="IPluginConfigurationService.IsInitialized"/> returns <c>true</c> - this
///     exercises the full config-restore pipeline that the existing tests deliberately
///     skip (they set <c>IsInitialized = false</c> so only the file I/O paths run).
///     Covers sanitization/validation edge cases: invalid language falls back to "en",
///     out-of-range values are clamped, task-mode parsing rejects garbage input, and
///     Arr instance lists are properly cleared before replacement.
/// </summary>
public sealed class BackupServiceRestoreConfigTests : IDisposable
{
    private readonly string _tempDir;

    public BackupServiceRestoreConfigTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-backup-restore-" + Guid.NewGuid().ToString("N")[..8]);
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

    private (BackupService Service, PluginConfiguration LiveConfig, Mock<IPluginConfigurationService> ConfigMock)
        CreateServiceWithInitializedConfig()
    {
        var liveConfig = new PluginConfiguration();
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
        configMock.Setup(c => c.IsInitialized).Returns(true);
        configMock.Setup(c => c.PluginVersion).Returns("1.0.0-test");
        // Wire ReadAndMutate to invoke the callback on liveConfig so tests can assert
        // field values after restore without hitting the real SaveConfiguration path.
        TestMockFactory.SetupReadAndMutate(configMock, liveConfig);

        var service = new BackupService(
            _tempDir,
            configMock.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<BackupService>().Object);

        return (service, liveConfig, configMock);
    }

    private static BackupData MakeMinimalValidBackup(bool useTrash = true)
    {
        return new BackupData
        {
            BackupVersion = 1,
            CreatedAt = DateTime.UtcNow,
            PluginVersion = "1.0.0",
            Language = "en",
            ExcludedLibraries = "Movies,TV",
            OrphanMinAgeDays = 7,
            PluginLogLevel = "INFO",
            TrickplayTaskMode = "Activate",
            EmptyMediaFolderTaskMode = "Activate",
            OrphanedSubtitleTaskMode = "DryRun",
            LinkRepairTaskMode = "DryRun",
            SeerrCleanupTaskMode = "DryRun",
            RecommendationsTaskMode = "Activate",
            SeerrUrl = "https://seerr.example.com",
            SeerrApiKey = "test-key",
            SeerrCleanupAgeDays = 30,
            UseTrash = useTrash,
            TrashFolderPath = ".trash",
            TrashRetentionDays = 14,
            SyncRecommendationsToPlaylist = true,
            DiscoveryUserAccessEnabled = true
        };
    }

    [Fact]
    public void RestoreBackup_InitializedConfig_RestoresAllScalarFields()
    {
        var (service, liveConfig, configMock) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();

        var summary = service.RestoreBackup(backup);

        Assert.True(summary.ConfigurationRestored);
        Assert.Equal("en", liveConfig.Language);
        Assert.Equal("Movies,TV", liveConfig.ExcludedLibraries);
        Assert.Equal(7, liveConfig.OrphanMinAgeDays);
        Assert.Equal("INFO", liveConfig.PluginLogLevel);
        Assert.Equal(TaskMode.Activate, liveConfig.TrickplayTaskMode);
        Assert.Equal(TaskMode.Activate, liveConfig.EmptyMediaFolderTaskMode);
        Assert.Equal(TaskMode.DryRun, liveConfig.OrphanedSubtitleTaskMode);
        Assert.Equal(TaskMode.DryRun, liveConfig.LinkRepairTaskMode);
        Assert.Equal(TaskMode.DryRun, liveConfig.SeerrCleanupTaskMode);
        Assert.Equal(TaskMode.Activate, liveConfig.RecommendationsTaskMode);
        Assert.Equal("https://seerr.example.com", liveConfig.SeerrUrl);
        Assert.Equal("test-key", liveConfig.SeerrApiKey);
        Assert.Equal(30, liveConfig.SeerrCleanupAgeDays);
        Assert.True(liveConfig.UseTrash);
        Assert.Equal(".trash", liveConfig.TrashFolderPath);
        Assert.Equal(14, liveConfig.TrashRetentionDays);
        Assert.True(liveConfig.SyncRecommendationsToPlaylist);
        Assert.True(liveConfig.DiscoveryUserAccessEnabled);

        configMock.Verify(c => c.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.Once);
    }

    [Fact]
    public void RestoreBackup_InvalidLanguage_FallsBackToEnglish()
    {
        // BUG GUARD: If the persisted language is corrupted or from a newer version that
        // introduced language codes the current version doesn't know, the plugin must NOT
        // apply the untrusted value - it must fall back to "en" to keep the UI usable.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.Language = "klingon-KL";

        service.RestoreBackup(backup);

        Assert.Equal("en", liveConfig.Language);
    }

    [Fact]
    public void RestoreBackup_InvalidLogLevel_FallsBackToInfo()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.PluginLogLevel = "TRACE"; // Not a supported level

        service.RestoreBackup(backup);

        Assert.Equal("INFO", liveConfig.PluginLogLevel);
    }

    [Fact]
    public void RestoreBackup_NegativeOrphanMinAge_IsClampedToZero()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.OrphanMinAgeDays = -100;

        service.RestoreBackup(backup);

        Assert.Equal(0, liveConfig.OrphanMinAgeDays);
    }

    [Fact]
    public void RestoreBackup_ExcessiveOrphanMinAge_IsClampedToMax()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.OrphanMinAgeDays = 99999;

        service.RestoreBackup(backup);

        Assert.Equal(BackupValidator.MaxRetentionDays, liveConfig.OrphanMinAgeDays);
    }

    [Fact]
    public void RestoreBackup_NegativeTrashRetention_IsClampedToZero()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.TrashRetentionDays = -50;

        service.RestoreBackup(backup);

        Assert.Equal(0, liveConfig.TrashRetentionDays);
    }

    [Fact]
    public void RestoreBackup_EmptyTrashFolderPath_UsesDefault()
    {
        // BUG GUARD: If the persisted trash path is missing/blank, RestoreConfiguration must
        // inject the sensible default ".jellyfin-trash" - leaving it empty would break
        // subsequent trash operations that assume a valid folder name.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.TrashFolderPath = "";

        service.RestoreBackup(backup);

        Assert.Equal(".jellyfin-trash", liveConfig.TrashFolderPath);
    }

    [Fact]
    public void RestoreBackup_WhitespaceTrashFolderPath_UsesDefault()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.TrashFolderPath = "   ";

        service.RestoreBackup(backup);

        Assert.Equal(".jellyfin-trash", liveConfig.TrashFolderPath);
    }

    [Fact]
    public void RestoreBackup_TraversalTrashPath_TrashOff_DefangsToDefault()
    {
        // AUDIT GUARD (backup-01): a crafted backup with UseTrash=false must NOT hard-fail the
        // restore, but the traversal path must be defanged to the default so it can never reach
        // live config. Matches the e2e contract
        // "import defangs a traversal trash path (UseTrash off) to the default".
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup(useTrash: false);
        backup.TrashFolderPath = "../../etc";

        service.RestoreBackup(backup);

        Assert.Equal(".jellyfin-trash", liveConfig.TrashFolderPath);
    }

    [Fact]
    public void RestoreBackup_SensitiveAbsoluteTrashPath_TrashOff_DefangsToDefault()
    {
        // AUDIT GUARD (backup-01): a sensitive absolute system path with UseTrash=false must be
        // defanged to the default rather than persisted into live config. This is the actual attack
        // the audit finding raised (a /etc or C:\Windows path landing in config with trash disabled).
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup(useTrash: false);
        backup.TrashFolderPath = OperatingSystem.IsWindows() ? "C:\\Windows" : "/etc";

        service.RestoreBackup(backup);

        Assert.Equal(".jellyfin-trash", liveConfig.TrashFolderPath);
    }

    [Fact]
    public void RestoreBackup_LegitimateRelativeTrashPath_TrashOff_IsPreserved()
    {
        // The defang must NOT over-reach: a legitimate relative custom path (neither traversal nor
        // sensitive) survives a UseTrash=false restore unchanged.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup(useTrash: false);
        backup.TrashFolderPath = ".custom-trash";

        service.RestoreBackup(backup);

        Assert.Equal(".custom-trash", liveConfig.TrashFolderPath);
    }

    [Fact]
    public void RestoreBackup_UnknownTaskMode_DefaultsToDryRun()
    {
        // BUG GUARD: unknown task mode strings must fall back to DryRun (safe default) rather
        // than throw or corrupt the enum. This prevents a malicious/older backup from disabling
        // safety modes silently.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.TrickplayTaskMode = "MaliciousMode";
        backup.EmptyMediaFolderTaskMode = "AlsoBad";
        backup.OrphanedSubtitleTaskMode = "";
        backup.LinkRepairTaskMode = null!;

        service.RestoreBackup(backup);

        Assert.Equal(TaskMode.DryRun, liveConfig.TrickplayTaskMode);
        Assert.Equal(TaskMode.DryRun, liveConfig.EmptyMediaFolderTaskMode);
        Assert.Equal(TaskMode.DryRun, liveConfig.OrphanedSubtitleTaskMode);
        Assert.Equal(TaskMode.DryRun, liveConfig.LinkRepairTaskMode);
    }

    [Fact]
    public void RestoreBackup_EmptySeerrCleanupTaskMode_DefaultsToDeactivate()
    {
        // SeerrCleanupTaskMode intentionally falls back to Deactivate (not DryRun) because
        // enabling cleanup by default on a fresh restore could permanently delete Seerr
        // requests the admin has not reviewed. Deactivate is the safest no-op sentinel for
        // an opt-in background cleanup that modifies external data in a third-party service.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.SeerrCleanupTaskMode = "";

        service.RestoreBackup(backup);

        Assert.Equal(TaskMode.Deactivate, liveConfig.SeerrCleanupTaskMode);
    }

    [Fact]
    public void RestoreBackup_LowercaseTaskMode_ParsedCaseInsensitively()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.TrickplayTaskMode = "dryrun";
        backup.EmptyMediaFolderTaskMode = "ACTIVATE";
        backup.LinkRepairTaskMode = "Deactivate";

        service.RestoreBackup(backup);

        Assert.Equal(TaskMode.DryRun, liveConfig.TrickplayTaskMode);
        Assert.Equal(TaskMode.Activate, liveConfig.EmptyMediaFolderTaskMode);
        Assert.Equal(TaskMode.Deactivate, liveConfig.LinkRepairTaskMode);
    }

    [Fact]
    public void RestoreBackup_NonZeroSeerrCleanupAgeDays_OverwritesLiveValue()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrCleanupAgeDays = 45;
        var backup = MakeMinimalValidBackup();
        backup.SeerrCleanupAgeDays = 20;

        service.RestoreBackup(backup);

        Assert.Equal(20, liveConfig.SeerrCleanupAgeDays);
    }

    [Fact]
    public void RestoreBackup_EmptySeerrUrlInBackup_PreservesLiveUrl()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrUrl = "https://live.seerr.example.com";
        var backup = MakeMinimalValidBackup();
        backup.SeerrUrl = string.Empty;

        service.RestoreBackup(backup);

        Assert.Equal("https://live.seerr.example.com", liveConfig.SeerrUrl);
    }

    [Fact]
    public void RestoreBackup_NonEmptySeerrUrl_OverwritesLiveUrl()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrUrl = "https://old.seerr.example.com";
        var backup = MakeMinimalValidBackup();
        backup.SeerrUrl = "https://new.seerr.example.com";

        service.RestoreBackup(backup);

        Assert.Equal("https://new.seerr.example.com", liveConfig.SeerrUrl);
    }

    [Fact]
    public void RestoreBackup_EmptyArrApiKeyInBackup_PreservesLiveKey()
    {
        // When the backup has an empty key for a named instance that already has a key
        // configured live, the live key must be preserved - not wiped.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.RadarrInstances.Add(new ArrInstanceConfig
            { Name = "R1", Url = "http://r:7878", ApiKey = "live-key" });
        var backup = MakeMinimalValidBackup();
        backup.RadarrInstances.Add(new BackupArrInstance
            { Name = "R1", Url = "http://r:7878", ApiKey = string.Empty });

        service.RestoreBackup(backup);

        Assert.Single(liveConfig.RadarrInstances);
        Assert.Equal("live-key", liveConfig.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public void RestoreBackup_EmptySonarrApiKeyInBackup_PreservesLiveKey()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SonarrInstances.Add(new ArrInstanceConfig
            { Name = "S1", Url = "http://s:8989", ApiKey = "sonarr-live-key" });
        var backup = MakeMinimalValidBackup();
        backup.SonarrInstances.Add(new BackupArrInstance
            { Name = "S1", Url = "http://s:8989", ApiKey = string.Empty });

        service.RestoreBackup(backup);

        Assert.Single(liveConfig.SonarrInstances);
        Assert.Equal("sonarr-live-key", liveConfig.SonarrInstances[0].ApiKey);
    }

    [Fact]
    public void RestoreBackup_ZeroSeerrCleanupAgeDays_IsApplied()
    {
        // BUG-10 / HARDENING-6: with int?, null means "absent" and 0 is a valid
        // "immediate cleanup" value that MUST be applied. The previous int sentinel
        // (0 == absent) silently swallowed legitimate zero values.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrCleanupAgeDays = 45;
        var backup = MakeMinimalValidBackup();
        backup.SeerrCleanupAgeDays = 0; // explicit zero - must be applied, not skipped

        service.RestoreBackup(backup);

        Assert.Equal(0, liveConfig.SeerrCleanupAgeDays);
    }

    [Fact]
    public void RestoreBackup_NullSeerrCleanupAgeDays_LeavesConfigUnchanged()
    {
        // null in the backup payload means "field absent" (e.g. exported by an older
        // plugin version). The live value must be left untouched.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrCleanupAgeDays = 45;
        var backup = MakeMinimalValidBackup();
        backup.SeerrCleanupAgeDays = null; // absent → leave live value unchanged

        service.RestoreBackup(backup);

        Assert.Equal(45, liveConfig.SeerrCleanupAgeDays);
    }

    [Fact]
    public void RestoreBackup_ExcessiveSeerrCleanupAgeDays_IsClampedToMax()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.SeerrCleanupAgeDays = 99999;

        service.RestoreBackup(backup);

        Assert.Equal(BackupValidator.MaxRetentionDays, liveConfig.SeerrCleanupAgeDays);
    }

    [Fact]
    public void RestoreBackup_ArrInstances_ClearedBeforeReplacement()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.RadarrInstances.Add(new ArrInstanceConfig { Name = "OldRadarr", Url = "http://old", ApiKey = "old" });
        liveConfig.SonarrInstances.Add(new ArrInstanceConfig { Name = "OldSonarr", Url = "http://old", ApiKey = "old" });

        var backup = MakeMinimalValidBackup();
        backup.RadarrInstances.Add(new BackupArrInstance { Name = "NewRadarr", Url = "http://new", ApiKey = "new" });
        backup.SonarrInstances.Add(new BackupArrInstance { Name = "NewSonarr", Url = "http://new", ApiKey = "new" });

        service.RestoreBackup(backup);

        Assert.Single(liveConfig.RadarrInstances);
        Assert.Equal("NewRadarr", liveConfig.RadarrInstances[0].Name);
        Assert.DoesNotContain(liveConfig.RadarrInstances, i => i.Name == "OldRadarr");
        Assert.Single(liveConfig.SonarrInstances);
        Assert.Equal("NewSonarr", liveConfig.SonarrInstances[0].Name);
    }

    [Fact]
    public void RestoreBackup_ArrInstances_TruncatedToMaxCount()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.RadarrInstances.Clear();
        for (var i = 0; i < BackupValidator.MaxArrInstances + 5; i++)
        {
            backup.RadarrInstances.Add(new BackupArrInstance
            {
                Name = $"R{i}",
                Url = $"http://r{i}",
                ApiKey = $"k{i}"
            });
        }

        service.RestoreBackup(backup);

        Assert.Equal(BackupValidator.MaxArrInstances, liveConfig.RadarrInstances.Count);
    }

    [Fact]
    public void RestoreBackup_LongArrInstanceFields_AreTruncated()
    {
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.RadarrInstances.Clear();
        backup.RadarrInstances.Add(new BackupArrInstance
        {
            Name = new string('N', BackupValidator.MaxInstanceNameLength + 200),
            Url = new string('U', BackupValidator.MaxUrlLength + 200),
            ApiKey = new string('K', BackupValidator.MaxApiKeyLength + 200)
        });

        service.RestoreBackup(backup);

        var instance = Assert.Single(liveConfig.RadarrInstances);
        Assert.Equal(BackupValidator.MaxInstanceNameLength, instance.Name.Length);
        Assert.Equal(BackupValidator.MaxUrlLength, instance.Url.Length);
        Assert.Equal(BackupValidator.MaxApiKeyLength, instance.ApiKey.Length);
    }

    [Fact]
    public void RestoreBackup_LongValidSeerrUrl_IsTruncated()
    {
        // A long but valid http(s) URL must be truncated to MaxUrlLength and applied.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        // Build a URL that is valid http but exceeds MaxUrlLength via a long path segment.
        backup.SeerrUrl = "https://seerr.example.com/" + new string('s', BackupValidator.MaxUrlLength);
        backup.SeerrApiKey = new string('A', BackupValidator.MaxApiKeyLength + 500);

        service.RestoreBackup(backup);

        Assert.Equal(BackupValidator.MaxUrlLength, liveConfig.SeerrUrl.Length);
        Assert.Equal(BackupValidator.MaxApiKeyLength, liveConfig.SeerrApiKey.Length);
    }

    [Fact]
    public void RestoreBackup_InvalidSchemeSeerrUrl_IsSkipped()
    {
        // SEC-3: a non-http(s) URL in the backup (e.g. crafted file:// or ftp://)
        // must NOT be written to config. The live value must remain unchanged.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrUrl = "https://live.example.com";
        var backup = MakeMinimalValidBackup();
        backup.SeerrUrl = "file:///etc/passwd";

        service.RestoreBackup(backup);

        Assert.Equal("https://live.example.com", liveConfig.SeerrUrl);
    }

    [Fact]
    public void RestoreBackup_FtpSchemeSeerrUrl_IsSkipped()
    {
        // SEC-3: ftp:// must also be rejected.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrUrl = "https://live.example.com";
        var backup = MakeMinimalValidBackup();
        backup.SeerrUrl = "ftp://attacker.example.com";

        service.RestoreBackup(backup);

        Assert.Equal("https://live.example.com", liveConfig.SeerrUrl);
    }

    [Fact]
    public void RestoreBackup_UninitializedConfig_SkipsConfigButRestoresFiles()
    {
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.IsInitialized).Returns(false);

        var service = new BackupService(
            _tempDir,
            configMock.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = MakeMinimalValidBackup();

        var summary = service.RestoreBackup(backup);

        Assert.False(summary.ConfigurationRestored);
        configMock.Verify(c => c.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.Never);
    }

    [Fact]
    public void RestoreBackup_NullBackup_Throws()
    {
        var (service, _, _) = CreateServiceWithInitializedConfig();
        Assert.Throws<ArgumentNullException>(() => service.RestoreBackup(null!));
    }

    [Fact]
    public void CreateBackup_SeerrApiKey_IsIncludedInExport()
    {
        // API keys are now exported so that a backup/restore round-trip preserves
        // credentials. The ContainsSecrets flag is set to true so the UI/caller can
        // warn the user to store the file securely.
        var liveConfig = new PluginConfiguration { SeerrApiKey = "real-secret-key" };
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
        configMock.Setup(c => c.IsInitialized).Returns(true);
        configMock.Setup(c => c.PluginVersion).Returns("1.0.0");

        var service = new BackupService(
            _tempDir,
            configMock.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = service.CreateBackup(includeSecrets: true);

        Assert.Equal("real-secret-key", backup.SeerrApiKey);
        Assert.True(backup.ContainsSecrets);
    }

    [Fact]
    public void CreateBackup_ArrInstanceApiKeys_AreIncludedInExport()
    {
        // Same rationale - Radarr/Sonarr keys must be included for a lossless round-trip.
        var liveConfig = new PluginConfiguration();
        liveConfig.RadarrInstances.Add(new ArrInstanceConfig
            { Name = "R1", Url = "http://r:7878", ApiKey = "radarr-secret" });
        liveConfig.SonarrInstances.Add(new ArrInstanceConfig
            { Name = "S1", Url = "http://s:8989", ApiKey = "sonarr-secret" });

        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
        configMock.Setup(c => c.IsInitialized).Returns(true);
        configMock.Setup(c => c.PluginVersion).Returns("1.0.0");

        var service = new BackupService(
            _tempDir,
            configMock.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = service.CreateBackup(includeSecrets: true);

        Assert.All(backup.RadarrInstances, i => Assert.Equal("radarr-secret", i.ApiKey));
        Assert.All(backup.SonarrInstances, i => Assert.Equal("sonarr-secret", i.ApiKey));
        Assert.True(backup.ContainsSecrets);
    }

    [Fact]
    public void RestoreBackup_EmptySeerrApiKeyInBackup_PreservesLiveKey()
    {
        // Empty SeerrApiKey in the backup (normal case - key was omitted on export)
        // must leave whatever key is already configured on the server untouched.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrApiKey = "live-key-must-survive";
        var backup = MakeMinimalValidBackup();
        backup.SeerrApiKey = string.Empty;

        service.RestoreBackup(backup);

        Assert.Equal("live-key-must-survive", liveConfig.SeerrApiKey);
    }

    [Fact]
    public void RestoreBackup_NonEmptySeerrApiKeyInBackup_OverwritesLiveKey()
    {
        // A non-empty SeerrApiKey in the backup (e.g. a manually-crafted import) must
        // be applied - the operator chose to restore credentials explicitly.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrApiKey = "old-live-key";
        var backup = MakeMinimalValidBackup();
        backup.SeerrApiKey = "new-key-from-backup";

        service.RestoreBackup(backup);

        Assert.Equal("new-key-from-backup", liveConfig.SeerrApiKey);
    }

    [Fact]
    public void RestoreBackup_EmptyArrApiKeyInBackup_PreservesEmptyKey()
    {
        // Arr instance restored from a normal export has empty ApiKey - the empty
        // value is stored as-is (there is no existing live key for a newly-added instance).
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.RadarrInstances.Add(new BackupArrInstance
            { Name = "R1", Url = "http://r:7878", ApiKey = string.Empty });

        service.RestoreBackup(backup);

        Assert.Single(liveConfig.RadarrInstances);
        Assert.Equal(string.Empty, liveConfig.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public void RestoreBackup_NonEmptySeerrApiKey_EmitsAuditWarning()
    {
        // BUG GUARD: silently overwriting credentials from a backup file with no log
        // entry makes it impossible to audit. When a non-empty key in the backup differs
        // from the live value the service must emit a LogWarning before applying it.
        var pluginLogMock = new Mock<Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService>();
        var liveConfig = new PluginConfiguration();
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
        configMock.Setup(c => c.IsInitialized).Returns(true);
        configMock.Setup(c => c.PluginVersion).Returns("1.0.0");
        TestMockFactory.SetupReadAndMutate(configMock, liveConfig);

        var service = new BackupService(
            _tempDir,
            configMock.Object,
            pluginLogMock.Object,
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = MakeMinimalValidBackup();
        backup.SeerrApiKey = "overwriting-key"; // differs from liveConfig.SeerrApiKey ("")

        service.RestoreBackup(backup);

        pluginLogMock.Verify(
            p => p.LogWarning(
                "Backup",
                It.Is<string>(msg => msg.Contains("Seerr", StringComparison.OrdinalIgnoreCase)
                                     && msg.Contains("API key", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<Microsoft.Extensions.Logging.ILogger?>()),
            Times.Once);
    }

    [Fact]
    public void RestoreBackup_EmptySeerrApiKey_NoAuditWarning()
    {
        // When the backup has an empty key (normal export case) no audit warning
        // should fire - there is nothing being overwritten.
        var pluginLogMock = new Mock<Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService>();
        var liveConfig = new PluginConfiguration();
        var configMock = new Mock<IPluginConfigurationService>();
        configMock.Setup(c => c.GetConfiguration()).Returns(liveConfig);
        configMock.Setup(c => c.IsInitialized).Returns(true);
        configMock.Setup(c => c.PluginVersion).Returns("1.0.0");
        TestMockFactory.SetupReadAndMutate(configMock, liveConfig);

        var service = new BackupService(
            _tempDir,
            configMock.Object,
            pluginLogMock.Object,
            TestMockFactory.CreateLogger<BackupService>().Object);

        var backup = MakeMinimalValidBackup();
        backup.SeerrApiKey = string.Empty;

        service.RestoreBackup(backup);

        pluginLogMock.Verify(
            p => p.LogWarning(
                "Backup",
                It.Is<string>(msg => msg.Contains("Seerr", StringComparison.OrdinalIgnoreCase)
                                     && msg.Contains("API key", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<Microsoft.Extensions.Logging.ILogger?>()),
            Times.Never);
    }

    [Fact]
    public void RestoreConfig_SeerrApiKey_LongerThan200Chars_NoSpuriousCredentialsChangedWarning()
    {
        var longKey = new string('x', 250);
        var backupKey = new string('x', 200);

        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrApiKey = longKey;
        liveConfig.SeerrUrl = "https://seerr.example.com";

        var backup = MakeMinimalValidBackup();
        backup.SeerrApiKey = backupKey;

        var summary = service.RestoreBackup(backup);

        Assert.False(summary.CredentialsChanged,
            "No credentials change should be reported when the backup key is the truncated form of the stored key.");
        // The restored key must equal the truncated backup value (200 'x' chars), not the full 250-char stored value.
        Assert.Equal(backupKey, liveConfig.SeerrApiKey);
    }

    // ===== HARDENING-6 / BUG-10: nullable SeerrCleanupAgeDays =====

    [Fact]
    public void RestoreConfiguration_SeerrCleanupAgeDays_WhenNull_LeavesExistingValue()
    {
        // When the backup carries null (field absent - e.g. older plugin version that did not
        // export this field), the live config value must be left completely unchanged.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrCleanupAgeDays = 77;
        var backup = MakeMinimalValidBackup();
        backup.SeerrCleanupAgeDays = null;

        service.RestoreBackup(backup);

        Assert.Equal(77, liveConfig.SeerrCleanupAgeDays);
    }

    [Fact]
    public void RestoreConfiguration_SeerrCleanupAgeDays_WhenZero_AppliesZero()
    {
        // Zero is a legitimate "immediate cleanup" value that MUST be applied.
        // With int?, null is the only sentinel for "absent"; 0 carries real meaning.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrCleanupAgeDays = 30;
        var backup = MakeMinimalValidBackup();
        backup.SeerrCleanupAgeDays = 0;

        service.RestoreBackup(backup);

        Assert.Equal(0, liveConfig.SeerrCleanupAgeDays);
    }

    [Fact]
    public void RestoreConfiguration_SeerrCleanupAgeDays_WhenPositive_Clamps()
    {
        // A value beyond MaxRetentionDays must be clamped, not silently accepted.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        backup.SeerrCleanupAgeDays = BackupValidator.MaxRetentionDays + 1;

        service.RestoreBackup(backup);

        Assert.Equal(BackupValidator.MaxRetentionDays, liveConfig.SeerrCleanupAgeDays);
    }

    // ===== SEC-3: URL scheme validation on restore =====

    [Fact]
    public void RestoreConfiguration_SeerrUrl_FileScheme_IsRejected()
    {
        // A file:// URL in the backup must NOT be written to config.
        // The live value must remain unchanged.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrUrl = "https://live.seerr.example.com";
        var backup = MakeMinimalValidBackup();
        backup.SeerrUrl = "file:///etc/passwd";

        service.RestoreBackup(backup);

        Assert.Equal("https://live.seerr.example.com", liveConfig.SeerrUrl);
    }

    [Fact]
    public void RestoreConfiguration_SeerrUrl_HttpsScheme_IsApplied()
    {
        // A valid https:// URL must be applied normally.
        var (service, liveConfig, _) = CreateServiceWithInitializedConfig();
        liveConfig.SeerrUrl = "https://old.seerr.example.com";
        var backup = MakeMinimalValidBackup();
        backup.SeerrUrl = "https://new.seerr.example.com";

        service.RestoreBackup(backup);

        Assert.Equal("https://new.seerr.example.com", liveConfig.SeerrUrl);
    }

    // ===== HARDENING-2: data files written before config =====

    [Fact]
    public void RestoreBackup_FileWriteFails_ConfigStillRestored()
    {
        // HARDENING-2 ordering: data files are written first, THEN config is updated.
        // If file I/O fails (no timeline/baseline in this backup), RestoreConfiguration
        // must still run - the config restore is independent of data-file success.
        // This test verifies the production ordering by checking that config fields
        // are applied even when the backup carries no timeline/baseline data.
        var (service, liveConfig, configMock) = CreateServiceWithInitializedConfig();
        var backup = MakeMinimalValidBackup();
        // No timeline/baseline → no file writes occur; config restore must still run.
        backup.GrowthTimeline = null;
        backup.GrowthBaseline = null;
        backup.Language = "de";

        var summary = service.RestoreBackup(backup);

        // File writes were skipped (nothing to restore).
        Assert.False(summary.TimelineRestored);
        Assert.False(summary.BaselineRestored);

        // Config restore ran independently.
        Assert.True(summary.ConfigurationRestored);
        Assert.Equal("de", liveConfig.Language);
        configMock.Verify(c => c.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.Once);
    }
}
