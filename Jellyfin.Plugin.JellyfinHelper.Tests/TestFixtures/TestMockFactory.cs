using System.IO;
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
        var effectiveConfigPath = configPath ?? Path.Combine(effectiveDataPath, "config");
        var mock = new Mock<IApplicationPaths>();
        mock.Setup(ap => ap.DataPath).Returns(effectiveDataPath);
        mock.Setup(ap => ap.PluginConfigurationsPath).Returns(effectiveConfigPath);
        mock.Setup(ap => ap.PluginsPath).Returns(Path.Combine(effectiveDataPath, "plugins"));
        mock.Setup(ap => ap.LogDirectoryPath).Returns(Path.Combine(effectiveDataPath, "logs"));
        mock.Setup(ap => ap.ConfigurationDirectoryPath).Returns(effectiveConfigPath);
        return mock;
    }

    // ===== Logger Mocks =====

    /// <summary>Creates a new <see cref="Mock{ILogger}"/> (non-generic).</summary>
    public static Mock<ILogger> CreateLogger() => new();

    /// <summary>Creates a new <see cref="Mock{T}"/> for a typed logger.</summary>
    public static Mock<ILogger<T>> CreateLogger<T>() => new();

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

                return Path.IsPathRooted(trashPath) ? trashPath : Path.Combine(path, trashPath);
            });
        mock.Setup(c => c.GetTrickplayTaskMode()).Returns(cfg.TrickplayTaskMode);
        mock.Setup(c => c.GetEmptyMediaFolderTaskMode()).Returns(cfg.EmptyMediaFolderTaskMode);
        mock.Setup(c => c.GetOrphanedSubtitleTaskMode()).Returns(cfg.OrphanedSubtitleTaskMode);
        mock.Setup(c => c.GetStrmRepairTaskMode()).Returns(cfg.StrmRepairTaskMode);
        mock.Setup(c => c.IsDryRunTrickplay()).Returns(() => CleanupConfigHelper.IsDryRun(cfg.TrickplayTaskMode));
        mock.Setup(c => c.IsDryRunEmptyMediaFolders()).Returns(() => CleanupConfigHelper.IsDryRun(cfg.EmptyMediaFolderTaskMode));
        mock.Setup(c => c.IsDryRunOrphanedSubtitles()).Returns(() => CleanupConfigHelper.IsDryRun(cfg.OrphanedSubtitleTaskMode));
        mock.Setup(c => c.IsDryRunStrmRepair()).Returns(() => CleanupConfigHelper.IsDryRun(cfg.StrmRepairTaskMode));
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

    /// <summary>
    /// Creates a new <see cref="Mock{IPluginConfigurationService}"/> with sensible defaults.
    /// Returns a fresh <see cref="PluginConfiguration"/> so tests don't depend on Plugin.Instance.
    /// </summary>
    public static Mock<IPluginConfigurationService> CreateConfigurationService(PluginConfiguration? config = null)
    {
        var cfg = config ?? new PluginConfiguration();
        var mock = new Mock<IPluginConfigurationService>();
        mock.Setup(s => s.GetConfiguration()).Returns(cfg);
        mock.Setup(s => s.IsInitialized).Returns(true);
        mock.Setup(s => s.PluginVersion).Returns("1.0.0-test");
        return mock;
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
