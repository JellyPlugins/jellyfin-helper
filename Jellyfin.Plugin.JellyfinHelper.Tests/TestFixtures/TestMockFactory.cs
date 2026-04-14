using System.Net;
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
        var mock = new Mock<IApplicationPaths>();
        mock.Setup(ap => ap.DataPath).Returns(dataPath ?? "/data");
        mock.Setup(ap => ap.PluginConfigurationsPath).Returns(configPath ?? "/data/config");
        mock.Setup(ap => ap.PluginsPath).Returns("/data/plugins");
        mock.Setup(ap => ap.LogDirectoryPath).Returns("/data/logs");
        mock.Setup(ap => ap.ConfigurationDirectoryPath).Returns("/data/config");
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

    /// <summary>Creates a new <see cref="Mock{MediaStatisticsService}"/>.</summary>
    public static Mock<MediaStatisticsService> CreateMediaStatisticsService()
    {
        var mock = new Mock<MediaStatisticsService>(
            CreateLibraryManager().Object,
            CreateFileSystem().Object,
            new Mock<ILogger<MediaStatisticsService>>().Object);
        return mock;
    }

    /// <summary>Creates a new <see cref="Mock{StatisticsHistoryService}"/>.</summary>
    public static Mock<StatisticsHistoryService> CreateStatisticsHistoryService(IApplicationPaths appPaths)
    {
        var mock = new Mock<StatisticsHistoryService>(
            appPaths,
            new Mock<ILogger<StatisticsHistoryService>>().Object);
        return mock;
    }
    
    /// <summary>Creates a new <see cref="Mock{GrowthTimelineService}"/>.</summary>
    public static Mock<GrowthTimelineService> CreateGrowthTimelineService(IApplicationPaths appPaths)
    {
        var mock = new Mock<GrowthTimelineService>(
            CreateLibraryManager().Object,
            CreateFileSystem().Object,
            appPaths,
            new Mock<ILogger<GrowthTimelineService>>().Object);
        return mock;
    }
}