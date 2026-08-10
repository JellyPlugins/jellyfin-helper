using System;
using System.Net;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
/// Central factory for creating commonly used mock objects across all tests.
/// Reduces boilerplate and ensures consistent mock setup.
/// </summary>
public static class TestMockFactory
{
    // ===== Core Infrastructure Mocks =====

    /// <summary>Creates a new <see cref="Mock{ILibraryManager}"/> with GetVirtualFolders returning empty list.</summary>
    public static Mock<ILibraryManager> CreateLibraryManager()
    {
        var mock = new Mock<ILibraryManager>();
        mock.Setup(lm => lm.GetVirtualFolders()).Returns([]);
        return mock;
    }

    /// <summary>Creates a new <see cref="Mock{IFileSystem}"/>.</summary>
    public static Mock<IFileSystem> CreateFileSystem() => new();

    /// <summary>Creates a new <see cref="Mock{IApplicationPaths}"/> with common paths configured.</summary>
    public static Mock<IApplicationPaths> CreateAppPaths(string? dataPath = null, string? configPath = null)
    {
        var effectiveDataPath = dataPath ?? "/data";
        var effectiveConfigPath = configPath ?? Path.Join(effectiveDataPath, "config");
        var mock = new Mock<IApplicationPaths>();
        mock.Setup(ap => ap.DataPath).Returns(effectiveDataPath);
        mock.Setup(ap => ap.PluginConfigurationsPath).Returns(effectiveConfigPath);
        mock.Setup(ap => ap.PluginsPath).Returns(Path.Join(effectiveDataPath, "plugins"));
        mock.Setup(ap => ap.LogDirectoryPath).Returns(Path.Join(effectiveDataPath, "logs"));
        mock.Setup(ap => ap.ConfigurationDirectoryPath).Returns(effectiveConfigPath);
        return mock;
    }

    // ===== Logger Mocks =====

    /// <summary>
    /// Creates a new <see cref="Mock{ILogger}"/> (non-generic).
    /// <c>IsEnabled(...)</c> is set up to return <c>true</c> for all log levels so that
    /// production code guarded by <c>logger.IsEnabled(...)</c> checks (see CA1873 fixes)
    /// still executes the underlying <c>Log(...)</c> call under test.
    /// </summary>
    public static Mock<ILogger> CreateLogger()
    {
        var mock = new Mock<ILogger>();
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return mock;
    }

    /// <summary>
    /// Creates a new <see cref="Mock{T}"/> for a typed logger.
    /// <c>IsEnabled(...)</c> is set up to return <c>true</c> for all log levels so that
    /// production code guarded by <c>logger.IsEnabled(...)</c> checks (see CA1873 fixes)
    /// still executes the underlying <c>Log(...)</c> call under test.
    /// </summary>
    public static Mock<ILogger<T>> CreateLogger<T>()
    {
        var mock = new Mock<ILogger<T>>();
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return mock;
    }

    /// <summary>
    /// Creates a new <see cref="Mock{ILogger}"/> where <c>IsEnabled(...)</c> always returns
    /// <c>false</c>. Use this in tests that specifically want to exercise the
    /// <c>logger.IsEnabled(...) == false</c> branch - for example to prove that a guarded
    /// <c>Log(...)</c> call is skipped without side effects (an <c>ILoggerProvider</c> that
    /// throws when disabled would surface here). The main <see cref="CreateLogger()"/>
    /// helper deliberately returns <c>true</c> so the common test path exercises the
    /// happy-log flow; this disabled variant is the complementary regression guard.
    /// </summary>
    public static Mock<ILogger> CreateDisabledLogger()
    {
        var mock = new Mock<ILogger>();
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);
        return mock;
    }

    /// <summary>
    /// Typed variant of <see cref="CreateDisabledLogger()"/>.
    /// </summary>
    public static Mock<ILogger<T>> CreateDisabledLogger<T>()
    {
        var mock = new Mock<ILogger<T>>();
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);
        return mock;
    }

    // ===== Other Mocks =====

    /// <summary>Creates a new <see cref="Mock{IHttpClientFactory}"/>.</summary>
    public static Mock<IHttpClientFactory> CreateHttpClientFactory() => new();

    /// <summary>Creates a new <see cref="IMemoryCache"/> instance.</summary>
    public static IMemoryCache CreateMemoryCache() => new MemoryCache(new MemoryCacheOptions());

    // ===== HTTP Mocks =====

    /// <summary>Creates a mock <see cref="HttpMessageHandler"/> that returns the given status code and content.</summary>
    public static Mock<HttpMessageHandler> CreateHttpMessageHandler(HttpStatusCode statusCode, string content)
    {
        var mock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mock.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content),
            })
            .Verifiable();
        return mock;
    }

    // ===== Configuration Mocks =====

    /// <summary>
    /// Creates a new <see cref="Mock{ICleanupConfigHelper}"/> with sensible defaults.
    /// Returns a fixed <see cref="PluginConfiguration"/> instead of reading from the global singleton,
    /// avoiding order-dependent and flaky tests.
    /// </summary>
    public static Mock<ICleanupConfigHelper> CreateCleanupConfigHelper(PluginConfiguration? config = null)
    {
        var cfg = config ?? new PluginConfiguration();
        var mock = new Mock<ICleanupConfigHelper>();
        mock.Setup(c => c.GetConfig()).Returns(cfg);
        mock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns<string>(path =>
            {
                var trashPath = cfg.TrashFolderPath;
                if (string.IsNullOrWhiteSpace(trashPath))
                {
                    trashPath = ".jellyfin-helper-trash";
                }

                return Path.IsPathRooted(trashPath) ? trashPath : Path.Join(path, trashPath);
            });
        mock.Setup(c => c.GetTrickplayTaskMode()).Returns(() => cfg.TrickplayTaskMode);
        mock.Setup(c => c.GetEmptyMediaFolderTaskMode()).Returns(() => cfg.EmptyMediaFolderTaskMode);
        mock.Setup(c => c.GetOrphanedSubtitleTaskMode()).Returns(() => cfg.OrphanedSubtitleTaskMode);
        mock.Setup(c => c.GetLinkRepairTaskMode()).Returns(() => cfg.LinkRepairTaskMode);
        mock.Setup(c => c.IsDryRunTrickplay()).Returns(() => CleanupConfigHelper.IsDryRun(cfg.TrickplayTaskMode));
        mock.Setup(c => c.IsDryRunEmptyMediaFolders()).Returns(() => CleanupConfigHelper.IsDryRun(cfg.EmptyMediaFolderTaskMode));
        mock.Setup(c => c.IsDryRunOrphanedSubtitles()).Returns(() => CleanupConfigHelper.IsDryRun(cfg.OrphanedSubtitleTaskMode));
        mock.Setup(c => c.IsDryRunLinkRepair()).Returns(() => CleanupConfigHelper.IsDryRun(cfg.LinkRepairTaskMode));
        mock.Setup(c => c.IsOldEnoughForDeletion(It.IsAny<string>())).Returns(true);
        mock.Setup(c => c.IsFileOldEnoughForDeletion(It.IsAny<string>())).Returns(true);
        mock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(() => new List<string>());
        return mock;
    }

    // ===== Service Mocks =====

    /// <summary>Creates a new <see cref="Mock{IMediaStatisticsService}"/>.</summary>
    public static Mock<IMediaStatisticsService> CreateMediaStatisticsService() => new();

    /// <summary>Creates a new <see cref="Mock{IStatisticsCacheService}"/>.</summary>
    public static Mock<IStatisticsCacheService> CreateStatisticsCacheService() => new();

    /// <summary>Creates a new <see cref="Mock{IGrowthTimelineService}"/>.</summary>
    public static Mock<IGrowthTimelineService> CreateGrowthTimelineService() => new();

    /// <summary>Creates a new <see cref="Mock{ILibraryInsightsService}"/>.</summary>
    public static Mock<ILibraryInsightsService> CreateLibraryInsightsService() => new();

    /// <summary>
    /// Creates a new <see cref="Mock{IPluginConfigurationService}"/> with sensible defaults.
    /// Returns a fresh <see cref="PluginConfiguration"/> so tests don't depend on Plugin.Instance.
    /// <see cref="IPluginConfigurationService.ReadAndMutate"/> is stubbed to immediately invoke
    /// the delegate on the same config object returned by <see cref="IPluginConfigurationService.GetConfiguration"/>.
    /// </summary>
    public static Mock<IPluginConfigurationService> CreateConfigurationService(PluginConfiguration? config = null)
    {
        var cfg = config ?? new PluginConfiguration();
        var mock = new Mock<IPluginConfigurationService>();
        mock.Setup(s => s.GetConfiguration()).Returns(cfg);
        mock.Setup(s => s.IsInitialized).Returns(true);
        mock.Setup(s => s.PluginVersion).Returns("1.0.0-test");
        SetupReadAndMutate(mock, cfg);
        return mock;
    }

    /// <summary>
    /// Wires <see cref="IPluginConfigurationService.ReadAndMutate"/> on <paramref name="mock"/>
    /// so that the callback is immediately invoked on <paramref name="cfg"/>.
    /// Call this whenever a test mock needs to support the atomic read-mutate-save path used
    /// by <c>BackupService.RestoreConfiguration</c> (and any future callers of ReadAndMutate).
    /// </summary>
    public static void SetupReadAndMutate(Mock<IPluginConfigurationService> mock, PluginConfiguration cfg)
    {
        mock.Setup(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()))
            .Callback<Action<PluginConfiguration>>(mutate => mutate(cfg));
    }

    /// <summary>
    /// Creates a new <see cref="PluginLogService"/> backed by a mock <see cref="IPluginConfigurationService"/>.
    /// Convenience method so tests do not need to create the mock themselves.
    /// </summary>
    public static PluginLogService CreatePluginLogService(PluginConfiguration? config = null)
    {
        return new PluginLogService(CreateConfigurationService(config).Object);
    }
}
