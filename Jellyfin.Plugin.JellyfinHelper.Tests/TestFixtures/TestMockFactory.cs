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
    /// <summary>Creates a new <see cref="Mock{ILibraryManager}"/> with GetVirtualFolders returning empty list.</summary>
    /// <returns></returns>
    public static Mock<ILibraryManager> CreateLibraryManager()
    {
        var mock = new Mock<ILibraryManager>();
        mock.Setup(lm => lm.GetVirtualFolders()).Returns([]);
        return mock;
    }

    /// <summary>Creates a new <see cref="Mock{IFileSystem}"/>.</summary>
    /// <returns></returns>
    public static Mock<IFileSystem> CreateFileSystem() => new();

    /// <summary>Creates a new <see cref="Mock{IApplicationPaths}"/> with common paths configured.</summary>
    /// <returns></returns>
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

    /// <summary>
    ///     Creates a new Mock{ILogger} (non-generic). IsEnabled(...) is set up to return true for all log levels so that production code guarded by logger.IsEnabled(...) checks (see CA1873 fixes) still executes the underlying Log(...) call under test.
    /// </summary>
    /// <returns></returns>
    public static Mock<ILogger> CreateLogger()
    {
        var mock = new Mock<ILogger>();
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return mock;
    }

    /// <summary>
    ///     Creates a new Mock{T} for a typed logger. IsEnabled(...) is set up to return true for all log levels so that production code guarded by logger.IsEnabled(...) checks (see CA1873 fixes) still executes the underlying Log(...) call under test.
    /// </summary>
    /// <returns></returns>
    public static Mock<ILogger<T>> CreateLogger<T>()
    {
        var mock = new Mock<ILogger<T>>();
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return mock;
    }

    /// <summary>
    ///     Creates a new Mock{ILogger} where IsEnabled(...) always returns false.
    /// </summary>
    /// <returns></returns>
    public static Mock<ILogger> CreateDisabledLogger()
    {
        var mock = new Mock<ILogger>();
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);
        return mock;
    }

    /// <summary>
    /// Typed variant of <see cref="CreateDisabledLogger()"/>.
    /// </summary>
    /// <returns></returns>
    public static Mock<ILogger<T>> CreateDisabledLogger<T>()
    {
        var mock = new Mock<ILogger<T>>();
        mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);
        return mock;
    }

    /// <summary>Creates a new <see cref="Mock{IHttpClientFactory}"/>.</summary>
    /// <returns></returns>
    public static Mock<IHttpClientFactory> CreateHttpClientFactory() => new();

    /// <summary>Creates a new <see cref="IMemoryCache"/> instance.</summary>
    /// <returns></returns>
    public static IMemoryCache CreateMemoryCache() => new MemoryCache(new MemoryCacheOptions());

    /// <summary>Creates a mock <see cref="HttpMessageHandler"/> that returns the given status code and content.</summary>
    /// <returns></returns>
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

    /// <summary>
    ///     Creates a new Mock{ICleanupConfigHelper} with sensible defaults. Returns a fixed PluginConfiguration instead of reading from the global singleton, avoiding order-dependent and flaky tests.
    /// </summary>
    /// <returns></returns>
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

    /// <summary>Creates a new <see cref="Mock{IMediaStatisticsService}"/>.</summary>
    /// <returns></returns>
    public static Mock<IMediaStatisticsService> CreateMediaStatisticsService() => new();

    /// <summary>Creates a new <see cref="Mock{IStatisticsCacheService}"/>.</summary>
    /// <returns></returns>
    public static Mock<IStatisticsCacheService> CreateStatisticsCacheService() => new();

    /// <summary>Creates a new <see cref="Mock{IGrowthTimelineService}"/>.</summary>
    /// <returns></returns>
    public static Mock<IGrowthTimelineService> CreateGrowthTimelineService() => new();

    /// <summary>Creates a new <see cref="Mock{ILibraryInsightsService}"/>.</summary>
    /// <returns></returns>
    public static Mock<ILibraryInsightsService> CreateLibraryInsightsService() => new();

    /// <summary>
    ///     Creates a new Mock{IPluginConfigurationService} with sensible defaults. Returns a fresh PluginConfiguration so tests don't depend on Plugin.Instance.
    /// </summary>
    /// <returns></returns>
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
    ///     Wires ReadAndMutate on mock so that the callback is immediately invoked on cfg. Call this whenever a test mock needs to support the atomic read-mutate-save path used by BackupService.RestoreConfiguration (and any future callers of ReadAndMutate).
    /// </summary>
    public static void SetupReadAndMutate(Mock<IPluginConfigurationService> mock, PluginConfiguration cfg)
    {
        mock.Setup(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()))
            .Callback<Action<PluginConfiguration>>(mutate => mutate(cfg));
    }

    /// <summary>
    ///     Creates a new PluginLogService backed by a mock IPluginConfigurationService. Convenience method so tests do not need to create the mock themselves.
    /// </summary>
    /// <returns></returns>
    public static PluginLogService CreatePluginLogService(PluginConfiguration? config = null)
    {
        return new PluginLogService(CreateConfigurationService(config).Object);
    }
}
