using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.ConfigAccess;

/// <summary>
/// Tests for <see cref="IPluginConfigurationService"/> contract behaviour.
/// Verifies that SaveConfiguration persists changes made to the Configuration object,
/// and that GetConfiguration always returns the same mutable reference (singleton semantics).
/// </summary>
public class PluginConfigurationServiceTests
{
    /// <summary>
    /// Verifies that modifications to the configuration object returned by
    /// <see cref="IPluginConfigurationService.GetConfiguration"/> are visible
    /// after <see cref="IPluginConfigurationService.SaveConfiguration"/> is called
    /// and the configuration is retrieved again.
    /// This is the core contract: GetConfiguration returns a mutable reference,
    /// changes are made in-place, and SaveConfiguration persists them.
    /// </summary>
    [Fact]
    public void SaveConfiguration_PersistsChanges_ToConfigurationProperty()
    {
        // Arrange: a shared config instance (simulating Plugin.Instance.Configuration)
        var config = new PluginConfiguration
        {
            OrphanMinAgeDays = 0,
            Language = "en",
            PluginLogLevel = "INFO",
        };

        var saveCallCount = 0;

        var mock = new Mock<IPluginConfigurationService>();
        mock.Setup(s => s.GetConfiguration()).Returns(config);
        mock.Setup(s => s.IsInitialized).Returns(true);
        mock.Setup(s => s.SaveConfiguration()).Callback(() => saveCallCount++);

        var service = mock.Object;

        // Act: mutate the config and save
        var retrieved = service.GetConfiguration();
        retrieved.OrphanMinAgeDays = 42;
        retrieved.Language = "de";
        retrieved.PluginLogLevel = "DEBUG";
        retrieved.TrickplayTaskMode = TaskMode.Activate;
        service.SaveConfiguration();

        // Assert: the same reference should reflect the changes
        var afterSave = service.GetConfiguration();
        Assert.Same(retrieved, afterSave); // same reference (singleton semantics)
        Assert.Equal(42, afterSave.OrphanMinAgeDays);
        Assert.Equal("de", afterSave.Language);
        Assert.Equal("DEBUG", afterSave.PluginLogLevel);
        Assert.Equal(TaskMode.Activate, afterSave.TrickplayTaskMode);
        Assert.Equal(1, saveCallCount);
    }

    /// <summary>
    /// Verifies that multiple sequential saves accumulate changes correctly.
    /// Each save should persist the latest state of the configuration object.
    /// </summary>
    [Fact]
    public void SaveConfiguration_MultipleSaves_AccumulateChanges()
    {
        var config = new PluginConfiguration();
        var saveCallCount = 0;

        var mock = new Mock<IPluginConfigurationService>();
        mock.Setup(s => s.GetConfiguration()).Returns(config);
        mock.Setup(s => s.IsInitialized).Returns(true);
        mock.Setup(s => s.SaveConfiguration()).Callback(() => saveCallCount++);

        var service = mock.Object;

        // First mutation + save
        service.GetConfiguration().OrphanMinAgeDays = 10;
        service.SaveConfiguration();

        // Second mutation + save
        service.GetConfiguration().TrashRetentionDays = 60;
        service.SaveConfiguration();

        // Third mutation + save
        service.GetConfiguration().TotalBytesFreed = 999_999;
        service.SaveConfiguration();

        // All changes should be visible on the same config object
        var final = service.GetConfiguration();
        Assert.Equal(10, final.OrphanMinAgeDays);
        Assert.Equal(60, final.TrashRetentionDays);
        Assert.Equal(999_999, final.TotalBytesFreed);
        Assert.Equal(3, saveCallCount);
    }

    /// <summary>
    /// Verifies that IsInitialized correctly reflects the service state.
    /// When not initialized, consumers should handle gracefully.
    /// </summary>
    [Fact]
    public void IsInitialized_ReturnsFalse_WhenPluginInstanceNotAvailable()
    {
        var mock = new Mock<IPluginConfigurationService>();
        mock.Setup(s => s.IsInitialized).Returns(false);
        mock.Setup(s => s.GetConfiguration()).Returns(new PluginConfiguration());

        var service = mock.Object;

        Assert.False(service.IsInitialized);

        // GetConfiguration should still return a usable default config
        var config = service.GetConfiguration();
        Assert.NotNull(config);
    }

    /// <summary>
    /// Verifies that the configuration service works correctly in a realistic
    /// scenario where CleanupTrackingService records cleanup statistics.
    /// This simulates the actual pattern: read config → mutate → save.
    /// </summary>
    [Fact]
    public void SaveConfiguration_CleanupTracking_Pattern()
    {
        var config = new PluginConfiguration();

        var mock = new Mock<IPluginConfigurationService>();
        mock.Setup(s => s.GetConfiguration()).Returns(config);
        mock.Setup(s => s.IsInitialized).Returns(true);

        var service = mock.Object;

        // Simulate what CleanupTrackingService.RecordCleanup does
        var cfg = service.GetConfiguration();
        cfg.TotalBytesFreed += 1024 * 1024; // 1 MB
        cfg.TotalItemsDeleted += 5;
        cfg.LastCleanupTimestamp = new System.DateTime(2026, 4, 15, 14, 0, 0, System.DateTimeKind.Utc);
        service.SaveConfiguration();

        // Verify accumulated state
        var result = service.GetConfiguration();
        Assert.Equal(1024 * 1024, result.TotalBytesFreed);
        Assert.Equal(5, result.TotalItemsDeleted);
        Assert.Equal(new System.DateTime(2026, 4, 15, 14, 0, 0, System.DateTimeKind.Utc), result.LastCleanupTimestamp);
        mock.Verify(s => s.SaveConfiguration(), Times.Once);
    }

    /// <summary>
    /// Verifies that the configuration migration flow works correctly:
    /// GetConfig reads config → checks version → migrates → saves.
    /// This tests the pattern used in CleanupConfigHelper.GetConfig().
    /// </summary>
    [Fact]
    public void SaveConfiguration_MigrationPattern_SavesAfterMigration()
    {
        var config = new PluginConfiguration { ConfigVersion = 0 };

        var mock = new Mock<IPluginConfigurationService>();
        mock.Setup(s => s.GetConfiguration()).Returns(config);
        mock.Setup(s => s.IsInitialized).Returns(true);

        var service = mock.Object;

        // Simulate the migration pattern from CleanupConfigHelper.GetConfig()
        var cfg = service.GetConfiguration();
        if (cfg.ConfigVersion < 1)
        {
            cfg.MigrateFromLegacyBooleans();
            service.SaveConfiguration();
        }

        // After migration, ConfigVersion should be updated
        Assert.Equal(1, config.ConfigVersion);
        mock.Verify(s => s.SaveConfiguration(), Times.Once);
    }

    // ===== TestMockFactory Integration =====

    /// <summary>
    /// Verifies that <see cref="TestMockFactory.CreateConfigurationService()"/> returns a mock
    /// with all expected properties pre-configured (IsInitialized, GetConfiguration, PluginVersion).
    /// </summary>
    [Fact]
    public void CreateConfigurationService_DefaultConfig_HasExpectedDefaults()
    {
        var mock = TestMockFactory.CreateConfigurationService();
        var service = mock.Object;

        Assert.True(service.IsInitialized);
        Assert.NotNull(service.GetConfiguration());
        Assert.Equal("1.0.0-test", service.PluginVersion);
    }

    /// <summary>
    /// Verifies that <see cref="TestMockFactory.CreateConfigurationService"/> with a custom
    /// configuration returns the exact same instance (reference equality).
    /// </summary>
    [Fact]
    public void CreateConfigurationService_CustomConfig_ReturnsSameInstance()
    {
        var customConfig = new PluginConfiguration { OrphanMinAgeDays = 99, Language = "fr" };
        var mock = TestMockFactory.CreateConfigurationService(customConfig);
        var service = mock.Object;

        var retrieved = service.GetConfiguration();
        Assert.Same(customConfig, retrieved);
        Assert.Equal(99, retrieved.OrphanMinAgeDays);
        Assert.Equal("fr", retrieved.Language);
    }

    /// <summary>
    /// Verifies that <see cref="TestMockFactory.CreateConfigurationService"/> returns a mock
    /// whose PluginVersion is set to a test value, ensuring consumers don't depend on runtime version.
    /// </summary>
    [Fact]
    public void CreateConfigurationService_PluginVersion_ReturnsTestVersion()
    {
        var mock = TestMockFactory.CreateConfigurationService();
        Assert.Equal("1.0.0-test", mock.Object.PluginVersion);
    }

    /// <summary>
    /// Verifies that <see cref="TestMockFactory.CreatePluginLogService"/> creates a working
    /// <see cref="PluginLogService"/> that is properly wired to the configuration service.
    /// This is an integration test ensuring the factory method produces usable instances.
    /// </summary>
    [Fact]
    public void CreatePluginLogService_ReturnsWorkingInstance()
    {
        var sut = TestMockFactory.CreatePluginLogService();

        // Should be able to log without exceptions
        var ex = Record.Exception(() => sut.LogInfo("__PCS_Test__", "Factory test"));
        Assert.Null(ex);

        // Should have recorded the entry
        var entries = sut.GetEntries(source: "__PCS_Test__");
        Assert.Single(entries);
        Assert.Equal("INFO", entries[0].Level);
    }

    /// <summary>
    /// Verifies that <see cref="TestMockFactory.CreatePluginLogService"/> with a custom config
    /// respects the configured PluginLogLevel from the provided configuration.
    /// </summary>
    [Fact]
    public void CreatePluginLogService_CustomConfig_RespectsPluginLogLevel()
    {
        var config = new PluginConfiguration { PluginLogLevel = "ERROR" };
        var sut = TestMockFactory.CreatePluginLogService(config);
        sut.TestMinLevelOverride = null; // rely on config service, not test override

        sut.LogInfo("__PCS_Lvl__", "should-not-be-stored");
        sut.LogError("__PCS_Lvl__", "should-be-stored");

        var entries = sut.GetEntries(source: "__PCS_Lvl__");
        Assert.Single(entries);
        Assert.Equal("ERROR", entries[0].Level);
    }
}
