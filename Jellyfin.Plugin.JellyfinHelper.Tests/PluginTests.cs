using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
///     Tests for Plugin - the plugin bootstrap that handles legacy index.html cleanup, on-disk data-file cleanup during uninstall, and recommendation playlist directory purge.
/// </summary>
[Collection("ConfigOverride")]
public sealed class PluginTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _webPath;
    private readonly string _dataPath;

    public PluginTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "JellyfinHelperPluginTests_" + Guid.NewGuid().ToString("N"));
        _webPath = Path.Combine(_tempRoot, "web");
        _dataPath = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(_webPath);
        Directory.CreateDirectory(_dataPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private Plugin CreatePlugin()
    {
        // TestMockFactory sets DataPath, PluginConfigurationsPath, PluginsPath, LogDirectoryPath and ConfigurationDirectoryPath.
        var appPathsMock = TestMockFactory.CreateAppPaths(dataPath: _dataPath, configPath: _dataPath);
        appPathsMock.Setup(p => p.WebPath).Returns(_webPath);

        var xmlSerializerMock = new Mock<IXmlSerializer>();
        var loggerMock = new Mock<ILogger<Plugin>>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        return new Plugin(appPathsMock.Object, xmlSerializerMock.Object, loggerMock.Object, Mock.Of<IPluginLogService>());
    }

    [Fact]
    public void Ctor_SetsInstanceStatic()
    {
        var plugin = CreatePlugin();
        Assert.Same(plugin, Plugin.Instance);
    }

    [Fact]
    public void Ctor_ExposesNameAndDescription()
    {
        var plugin = CreatePlugin();
        Assert.Equal("Jellyfin Helper", plugin.Name);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Description));
        Assert.Equal(Guid.Parse("0c737645-5cbb-4bd8-80c7-d377b560aaa4"), plugin.Id);
    }

    [Fact]
    public void GetPages_ReturnsSingleMainMenuEntry()
    {
        // A single PluginPageInfo must be registered. Duplicates would surface as
        // a doubled menu entry in Jellyfin's sidebar; a wrong name would break the URL routing.
        var plugin = CreatePlugin();
        var pages = plugin.GetPages().ToList();
        var page = Assert.Single(pages);

        Assert.Equal("Jellyfin Helper", page.Name);
        Assert.Equal("Jellyfin Helper", page.DisplayName);
        Assert.True(page.EnableInMainMenu);
        Assert.Equal("handyman", page.MenuIcon);
        Assert.EndsWith(".PluginPages.configPage.html", page.EmbeddedResourcePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_StripsLegacyOnDiskScriptTagAndTempFiles()
    {
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(
            indexPath,
            "<html><body><script plugin=\"Jellyfin Helper\" version=\"1\" src=\"../JellyfinHelper/Discovery/My/script?v=1\"></script>\n</body></html>");
        var orphan = Path.Combine(_webPath, "index.html." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(orphan, "leftover");

        _ = CreatePlugin();

        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", File.ReadAllText(indexPath), StringComparison.Ordinal);
        Assert.False(File.Exists(orphan));
    }

    // ReportClampedConfigValues - the "clamped hand-edited XML" warning path

    [Fact]
    public void Ctor_WithClampedConfigValues_LogsWarningPerReport()
    {
        // An operator who hand-edits the XML config to an out-of-range value (e.g. MaxRecommendationsPerUser=999) gets that value silently narrowed by the property setter.
        var config = new global::Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration
        {
            MaxRecommendationsPerUser = 999, // clamped down to 100
            EnsembleAlphaMin = 2.5,          // clamped down to 1.0
        };

        var appPathsMock = TestMockFactory.CreateAppPaths(dataPath: _dataPath, configPath: _dataPath);
        appPathsMock.Setup(p => p.WebPath).Returns(_webPath);
        var xmlSerializerMock = new Mock<IXmlSerializer>();
        var loggerMock = new Mock<ILogger<Plugin>>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        // Track LogWarning calls
        var warningCount = 0;
        loggerMock
            .Setup(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(() => warningCount++);

        // Trick: build the plugin, then set Configuration via reflection, then re-run ReportClampedConfigValues indirectly by pulling the private method.
        var plugin = new Plugin(appPathsMock.Object, xmlSerializerMock.Object, loggerMock.Object, Mock.Of<IPluginLogService>());
        var configProperty = typeof(MediaBrowser.Common.Plugins.BasePlugin<global::Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration>)
            .GetProperty("Configuration");
        Assert.NotNull(configProperty);
        configProperty!.SetValue(plugin, config);

        // Now invoke the private ReportClampedConfigValues to exercise the count>0 branch.
        var method = typeof(Plugin).GetMethod(
            "ReportClampedConfigValues",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        warningCount = 0;
        method!.Invoke(plugin, []);

        // 2 clamped values + 1 NormalizeAlphaRange swap (clamped alphaMin=1.0 > default alphaMax=0.75).
        Assert.Equal(3, warningCount);
    }

    [Fact]
    public void Ctor_WithNoClampedValues_DoesNotLogClampWarning()
    {
        // The fast-return path when DrainClampReports returns an empty list must not log ANY clamp warnings. Regressions to "always log" would spam the log on every startup and hide real problems.
        var config = new global::Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration
        {
            MaxRecommendationsPerUser = 20,   // within range, no clamp
            EnsembleAlphaMin = 0.3,           // default, no clamp
        };

        var appPathsMock = TestMockFactory.CreateAppPaths(dataPath: _dataPath, configPath: _dataPath);
        appPathsMock.Setup(p => p.WebPath).Returns(_webPath);
        var xmlSerializerMock = new Mock<IXmlSerializer>();
        var loggerMock = new Mock<ILogger<Plugin>>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var clampWarnings = 0;
        loggerMock
            .Setup(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString()!.Contains("was outside its accepted range", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(() => clampWarnings++);

        var plugin = new Plugin(appPathsMock.Object, xmlSerializerMock.Object, loggerMock.Object, Mock.Of<IPluginLogService>());
        var configProperty = typeof(MediaBrowser.Common.Plugins.BasePlugin<global::Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration>)
            .GetProperty("Configuration");
        configProperty!.SetValue(plugin, config);

        var method = typeof(Plugin).GetMethod(
            "ReportClampedConfigValues",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        clampWarnings = 0;
        method!.Invoke(plugin, []);

        Assert.Equal(0, clampWarnings);
    }

    [Fact]
    public void OnUninstalling_DeletesJellyfinHelperJsonFiles()
    {
        var dataFile1 = Path.Combine(_dataPath, "jellyfin-helper-statistics-latest.json");
        var dataFile2 = Path.Combine(_dataPath, "jellyfin-helper-recommendations-latest.json");
        var tmpLeftover = Path.Combine(_dataPath, "jellyfin-helper-growth-timeline.tmp");
        File.WriteAllText(dataFile1, "{}");
        File.WriteAllText(dataFile2, "{}");
        File.WriteAllText(tmpLeftover, string.Empty);

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.False(File.Exists(dataFile1));
        Assert.False(File.Exists(dataFile2));
        Assert.False(File.Exists(tmpLeftover));
    }

    [Fact]
    public void OnUninstalling_DeletesUnprefixedMlAndStateArtifacts()
    {
        // Regression: CleanupDataFiles' "jellyfin-helper-*" + .json/.tmp glob does NOT match the recommendation ML/state artifacts, which are written to DataPath either without the prefix (ml_weights.json, neural_weights.json, ensemble_state.json - see PluginServiceRegistrator) or with.
        var mlWeights = Path.Combine(_dataPath, "ml_weights.json");
        var neuralWeights = Path.Combine(_dataPath, "neural_weights.json");
        var ensembleState = Path.Combine(_dataPath, "ensemble_state.json");
        var batchGeneration = Path.Combine(_dataPath, "jellyfin-helper-batch-generation.txt");
        var prefixedCache = Path.Combine(_dataPath, "jellyfin-helper-recommendations-latest.json");
        File.WriteAllText(mlWeights, "{}");
        File.WriteAllText(neuralWeights, "{}");
        File.WriteAllText(ensembleState, "{}");
        File.WriteAllText(batchGeneration, "3");
        File.WriteAllText(prefixedCache, "{}");

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.False(File.Exists(mlWeights), "ml_weights.json must be removed on uninstall");
        Assert.False(File.Exists(neuralWeights), "neural_weights.json must be removed on uninstall");
        Assert.False(File.Exists(ensembleState), "ensemble_state.json must be removed on uninstall");
        Assert.False(File.Exists(batchGeneration), "batch-generation counter must be removed on uninstall");
        Assert.False(File.Exists(prefixedCache));
    }

    [Fact]
    public void OnUninstalling_DeletesPerUserRecommendationModelFiles()
    {
        // One pair of id-suffixed files per user with a trained per-user model. Uninstall must sweep all of
        // them, matched on the exact 32-hex id shape this plugin writes.
        var userA = Guid.NewGuid().ToString("N");
        var userB = Guid.NewGuid().ToString("N");
        var weightsA = Path.Combine(_dataPath, $"ml_weights_{userA}.json");
        var stateA = Path.Combine(_dataPath, $"ensemble_state_{userA}.json");
        var weightsB = Path.Combine(_dataPath, $"ml_weights_{userB}.json");
        var stateB = Path.Combine(_dataPath, $"ensemble_state_{userB}.json");
        foreach (var f in new[] { weightsA, stateA, weightsB, stateB })
        {
            File.WriteAllText(f, "{}");
        }

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.False(File.Exists(weightsA), "per-user ml_weights must be removed on uninstall");
        Assert.False(File.Exists(stateA), "per-user ensemble_state must be removed on uninstall");
        Assert.False(File.Exists(weightsB));
        Assert.False(File.Exists(stateB));
    }

    [Fact]
    public void OnUninstalling_PreservesFilesThatOnlyShareThePerUserStem()
    {
        // A user-made file that happens to share the ml_weights_ / ensemble_state_ stem but does not carry the
        // 32-hex id suffix is not something this plugin wrote, so uninstall must leave it in place.
        var backup = Path.Combine(_dataPath, "ml_weights_backup.json");
        var notes = Path.Combine(_dataPath, "ensemble_state_notes.json");
        // A genuine per-user file alongside them, to prove the sweep still runs.
        var realPerUser = Path.Combine(_dataPath, $"ml_weights_{Guid.NewGuid():N}.json");
        File.WriteAllText(backup, "{}");
        File.WriteAllText(notes, "{}");
        File.WriteAllText(realPerUser, "{}");

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.True(File.Exists(backup), "a file that only shares the stem must be preserved");
        Assert.True(File.Exists(notes), "a file that only shares the stem must be preserved");
        Assert.False(File.Exists(realPerUser), "the id-suffixed per-user file must still be removed");
    }

    [Fact]
    public void OnUninstalling_PreservesUnrelatedFilesInDataPath()
    {
        // A wildcard change to "jellyfin-*" or "*.json" would nuke unrelated files.
        var pluginFile = Path.Combine(_dataPath, "jellyfin-helper-statistics-latest.json");
        var unrelated1 = Path.Combine(_dataPath, "jellyfin.db");
        var unrelated2 = Path.Combine(_dataPath, "some-other-plugin-data.json");
        var unrelated3 = Path.Combine(_dataPath, "jellyfin-helper-something.log"); // wrong extension
        File.WriteAllText(pluginFile, "{}");
        File.WriteAllText(unrelated1, "db");
        File.WriteAllText(unrelated2, "{}");
        File.WriteAllText(unrelated3, "log");

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.False(File.Exists(pluginFile));
        Assert.True(File.Exists(unrelated1));
        Assert.True(File.Exists(unrelated2));
        // The .log file matches "jellyfin-helper-*" but must be preserved by the extension guard.
        Assert.True(File.Exists(unrelated3));
    }

    [Fact]
    public void OnUninstalling_MissingDataDirectory_DoesNotThrow()
    {
        Directory.Delete(_dataPath, recursive: true);

        var plugin = CreatePlugin();
        var ex = Record.Exception(() => plugin.OnUninstalling());
        Assert.Null(ex);
    }

    [Fact]
    public void OnUninstalling_DeletesRecommendationPlaylistFolders()
    {
        var playlistsRoot = Path.Combine(_dataPath, "playlists");
        Directory.CreateDirectory(playlistsRoot);
        var managed = Path.Combine(playlistsRoot, RecommendationPlaylistService.PlaylistNamePrefix + " for Alice");
        var userOwned = Path.Combine(playlistsRoot, "My Awesome Playlist");
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(userOwned);
        File.WriteAllText(Path.Combine(managed, "playlist.xml"), "<x/>");
        File.WriteAllText(Path.Combine(userOwned, "playlist.xml"), "<x/>");

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.False(Directory.Exists(managed));
        Assert.True(Directory.Exists(userOwned));
    }

    [Fact]
    public void OnUninstalling_PlaylistNamePrefixMismatch_DoesNotDelete()
    {
        // Only exact "🎬 Recommended for " prefix must delete.
        var playlistsRoot = Path.Combine(_dataPath, "playlists");
        Directory.CreateDirectory(playlistsRoot);
        var closelyNamed = Path.Combine(playlistsRoot, "🎬 Recommended Movies of 2024");
        Directory.CreateDirectory(closelyNamed);

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.True(Directory.Exists(closelyNamed));
    }

    [Fact]
    public void OnUninstalling_MissingPlaylistsDirectory_DoesNotThrow()
    {
        // No playlists dir exists - must short-circuit without throwing.
        var plugin = CreatePlugin();
        var ex = Record.Exception(() => plugin.OnUninstalling());
        Assert.Null(ex);
    }

    [Fact]
    public void Logger_ExposesTheInjectedLoggerInstance()
    {
        // Internal helpers (InjectScript, cleanup, etc.) log through Plugin.Logger and must share the plugin's ILogger<Plugin> category.
        var appPathsMock = TestMockFactory.CreateAppPaths(dataPath: _dataPath, configPath: _dataPath);
        appPathsMock.Setup(p => p.WebPath).Returns(_webPath);
        var xmlSerializerMock = new Mock<IXmlSerializer>();
        var loggerMock = new Mock<ILogger<Plugin>>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var plugin = new Plugin(appPathsMock.Object, xmlSerializerMock.Object, loggerMock.Object, Mock.Of<IPluginLogService>());

        Assert.Same(loggerMock.Object, plugin.Logger);
    }

    [Fact]
    public void UpdateConfiguration_PluginConfiguration_NormalizesAlphaRangeBeforePersisting()
    {
        // UpdateConfiguration is the override Jellyfin invokes when the operator saves the config page. A PluginConfiguration whose Min > Max must be normalized (swapped) before base persistence so downstream ensemble scoring never sees an inverted range.
        var config = new global::Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration();
        var configType = typeof(global::Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration);
        var minField = configType.GetField("_ensembleAlphaMin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var maxField = configType.GetField("_ensembleAlphaMax", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(minField);
        Assert.NotNull(maxField);
        minField!.SetValue(config, 0.9); // Min above Max - the inverted state UpdateConfiguration must fix
        maxField!.SetValue(config, 0.2);

        var plugin = CreatePlugin();
        var ex = Record.Exception(() => plugin.UpdateConfiguration(config));

        Assert.Null(ex);
        Assert.True(
            config.EnsembleAlphaMin <= config.EnsembleAlphaMax,
            "UpdateConfiguration must normalize the alpha range so Min <= Max before persisting");
    }

    [Fact]
    public void OnUninstalling_LockedRecommendationPlaylistFolder_SkipsItWithoutThrowing()
    {
        // Uninstall purges every managed recommendation playlist folder.
        var playlistsRoot = Path.Combine(_dataPath, "playlists");
        Directory.CreateDirectory(playlistsRoot);
        var lockedManaged = Path.Combine(playlistsRoot, RecommendationPlaylistService.PlaylistNamePrefix + " for Bob");
        var freeManaged = Path.Combine(playlistsRoot, RecommendationPlaylistService.PlaylistNamePrefix + " for Carol");
        Directory.CreateDirectory(lockedManaged);
        Directory.CreateDirectory(freeManaged);
        File.WriteAllText(Path.Combine(freeManaged, "playlist.xml"), "<x/>");
        var lockedFile = Path.Combine(lockedManaged, "playlist.xml");
        File.WriteAllText(lockedFile, "<x/>");

        var plugin = CreatePlugin();

        // A FileShare.None handle only makes Directory.Delete(recursive) throw on Windows. On POSIX (Linux CI, Docker) an open exclusive handle does NOT block unlink, so the folder is deleted regardless and the per-folder catch is never reached.
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var ex = Record.Exception(() => plugin.OnUninstalling());

                Assert.Null(ex);
                Assert.True(Directory.Exists(lockedManaged), "the locked managed folder must survive - the per-folder catch skips it");
                Assert.False(Directory.Exists(freeManaged), "the unlocked managed folder must still be deleted after the locked one is skipped");
            }
        }
        else
        {
            var ex = Record.Exception(() => plugin.OnUninstalling());

            Assert.Null(ex);
            Assert.False(Directory.Exists(lockedManaged), "every managed playlist folder must be purged on uninstall");
            Assert.False(Directory.Exists(freeManaged), "every managed playlist folder must be purged on uninstall");
        }
    }

    [Fact]
    public void OnUninstalling_LockedDataFile_SkipsItAndDeletesOthers()
    {
        // Uninstall sweeps every jellyfin-helper-*.json data file.
        var lockedFile = Path.Combine(_dataPath, "jellyfin-helper-statistics-latest.json");
        var freeFile = Path.Combine(_dataPath, "jellyfin-helper-recommendations-latest.json");
        File.WriteAllText(lockedFile, "{}");
        File.WriteAllText(freeFile, "{}");

        var plugin = CreatePlugin();

        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var ex = Record.Exception(() => plugin.OnUninstalling());

                Assert.Null(ex);
                Assert.True(File.Exists(lockedFile), "the locked data file must survive - the per-file catch skips it");
                Assert.False(File.Exists(freeFile), "the unlocked data file must still be deleted after the locked one is skipped");
                Assert.True(Directory.Exists(_dataPath), "the data directory itself must remain intact");
            }
        }
        else
        {
            var ex = Record.Exception(() => plugin.OnUninstalling());

            Assert.Null(ex);
            Assert.False(File.Exists(lockedFile), "matching data files must be swept on uninstall");
            Assert.False(File.Exists(freeFile), "matching data files must be swept on uninstall");
            Assert.True(Directory.Exists(_dataPath), "the data directory itself must remain intact");
        }
    }
}
