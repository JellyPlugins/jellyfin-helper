using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
/// Factory for creating <see cref="MediaStatisticsController"/> instances in tests.
/// Encapsulates the complex constructor with many mock dependencies.
/// </summary>
public static class ControllerTestFactory
{
    /// <summary>
    /// Core factory that creates a <see cref="MediaStatisticsController"/> with all dependencies mocked,
    /// returning the controller, the library manager mock, and the memory cache.
    /// </summary>
    private static (MediaStatisticsController Controller, Mock<ILibraryManager> LibraryManagerMock, IMemoryCache Cache) CreateControllerCore(
        string? dataPath = null,
        IMemoryCache? cache = null)
    {
        var libraryManagerMock = TestMockFactory.CreateLibraryManager();
        var fileSystemMock = TestMockFactory.CreateFileSystem();
        var appPathsMock = TestMockFactory.CreateAppPaths(dataPath: dataPath ?? Path.GetTempPath());
        var httpClientFactoryMock = TestMockFactory.CreateHttpClientFactory();
        var memoryCache = cache ?? TestMockFactory.CreateMemoryCache();

        var controller = new MediaStatisticsController(
            libraryManagerMock.Object,
            fileSystemMock.Object,
            appPathsMock.Object,
            httpClientFactoryMock.Object,
            memoryCache,
            new Mock<ILogger<MediaStatisticsController>>().Object,
            new Mock<ILogger<MediaStatisticsService>>().Object,
            new Mock<ILogger<StatisticsHistoryService>>().Object,
            new Mock<ILogger<GrowthTimelineService>>().Object);

        return (controller, libraryManagerMock, memoryCache);
    }

    /// <summary>
    /// Creates a <see cref="MediaStatisticsController"/> with all dependencies mocked.
    /// </summary>
    /// <param name="dataPath">The data path returned by IApplicationPaths.DataPath.</param>
    /// <param name="cache">Optional memory cache; a new one is created if null.</param>
    /// <returns>A tuple of the controller and its memory cache (for pre-populating stats).</returns>
    public static (MediaStatisticsController Controller, IMemoryCache Cache) CreateController(
        string? dataPath = null,
        IMemoryCache? cache = null)
    {
        var (controller, _, memoryCache) = CreateControllerCore(dataPath, cache);
        return (controller, memoryCache);
    }

    /// <summary>
    /// Creates a <see cref="MediaStatisticsController"/> with all dependencies mocked,
    /// also returning the <see cref="ILibraryManager"/> mock for further configuration.
    /// </summary>
    public static (MediaStatisticsController Controller, Mock<ILibraryManager> LibraryManagerMock) CreateControllerWithLibraryManager(
        string? dataPath = null,
        IMemoryCache? cache = null)
    {
        var (controller, libraryManagerMock, _) = CreateControllerCore(dataPath, cache);
        return (controller, libraryManagerMock);
    }

    /// <summary>
    /// Creates a <see cref="MediaStatisticsController"/> with a JSON request body configured.
    /// Useful for testing import endpoints.
    /// </summary>
    public static MediaStatisticsController CreateControllerWithJsonBody(
        string dataPath,
        string jsonBody,
        long? contentLength = null)
    {
        var (controller, _) = CreateController(dataPath);

        var httpContext = new DefaultHttpContext();
        var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = contentLength ?? bodyBytes.Length;

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        return controller;
    }
}