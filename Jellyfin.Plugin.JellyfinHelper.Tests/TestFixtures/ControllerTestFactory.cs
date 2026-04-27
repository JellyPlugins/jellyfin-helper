using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    /// Creates a <see cref="BackupController"/> with all dependencies mocked.
    /// </summary>
    /// <param name="dataPath">The data path returned by IApplicationPaths.DataPath.</param>
    /// <param name="pluginLog">The plugin log service; a new one is created if null.</param>
    /// <returns>The controller.</returns>
    public static BackupController CreateBackupController(string? dataPath = null, IPluginLogService? pluginLog = null)
    {
        var appPathsMock = TestMockFactory.CreateAppPaths(dataPath: dataPath ?? Path.GetTempPath());
        var log = pluginLog ?? TestMockFactory.CreatePluginLogService();
        var configServiceMock = new Mock<IPluginConfigurationService>();
        configServiceMock.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration());
        configServiceMock.Setup(c => c.PluginVersion).Returns("1.0.0-test");
        var backupService = new BackupService(appPathsMock.Object, configServiceMock.Object, log, new Mock<ILogger<BackupService>>().Object);

        var controller = new BackupController(
            backupService,
            log,
            new Mock<ILogger<BackupController>>().Object);
        return controller;
    }

    /// <summary>
    /// Creates a <see cref="TrashController"/> with all dependencies mocked.
    /// </summary>
    /// <returns>A tuple of the controller and its library manager.</returns>
    public static (TrashController controller, Mock<ILibraryManager> libraryManagerMock, Mock<ICleanupConfigHelper> configHelperMock, Mock<ITrashService> trashServiceMock) CreateTrashController()
    {
        var libraryManagerMock = TestMockFactory.CreateLibraryManager();

        var configHelperMock = new Mock<ICleanupConfigHelper>();
        var trashServiceMock = new Mock<ITrashService>();

        var controller = new TrashController(
            libraryManagerMock.Object,
            TestMockFactory.CreatePluginLogService(),
            new Mock<ILogger<TrashController>>().Object,
            configHelperMock.Object,
            trashServiceMock.Object);
        return (controller, libraryManagerMock, configHelperMock, trashServiceMock);
    }

    /// <summary>
    /// Creates a <see cref="ArrIntegrationController"/> with all dependencies mocked.
    /// </summary>
    /// <returns>A tuple of the controller and its mocks.</returns>
    public static (ArrIntegrationController Controller, Mock<ILibraryManager> LibraryManagerMock, Mock<IFileSystem> FileSystemMock, Mock<IHttpClientFactory> HttpClientFactoryMock, Mock<ICleanupConfigHelper> ConfigHelperMock) CreateArrIntegrationController()
    {
        var libraryManagerMock = TestMockFactory.CreateLibraryManager();
        var fileSystemMock = TestMockFactory.CreateFileSystem();
        var httpClientFactoryMock = TestMockFactory.CreateHttpClientFactory();
        var pluginLog = TestMockFactory.CreatePluginLogService();
        var arrService = new ArrIntegrationService(httpClientFactoryMock.Object, pluginLog, new Mock<ILogger<ArrIntegrationService>>().Object);

        var configHelperMock = new Mock<ICleanupConfigHelper>();

        var controller = new ArrIntegrationController(
            libraryManagerMock.Object,
            fileSystemMock.Object,
            arrService,
            pluginLog,
            new Mock<ILogger<ArrIntegrationController>>().Object,
            configHelperMock.Object);

        return (controller, libraryManagerMock, fileSystemMock, httpClientFactoryMock, configHelperMock);
    }

    /// <summary>
    /// Initializes the <see cref="Plugin.Instance"/> with mocked dependencies for testing.
    /// This is necessary because many controllers and services rely on the static instance.
    /// </summary>
    public static void InitializePluginInstance()
    {
        if (Plugin.Instance != null)
        {
            return;
        }

        var tempDir = Path.Join(Path.GetTempPath(), "JellyfinHelperPlugin_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var appPathsMock = TestMockFactory.CreateAppPaths(dataPath: tempDir, configPath: tempDir);
        var xmlSerializerMock = new Mock<IXmlSerializer>();

        // Constructor of Plugin sets Plugin.Instance
        var plugin = new Plugin(appPathsMock.Object, xmlSerializerMock.Object);

        // BasePlugin<T> holds configuration in a protected field or similar. 
        // We can set the Configuration property directly if it has a setter 
        // or use reflection to set the backing field.
        var configProperty = typeof(MediaBrowser.Common.Plugins.BasePlugin<PluginConfiguration>).GetProperty("Configuration")
            ?? throw new InvalidOperationException("Failed to find Configuration property via reflection on BasePlugin<PluginConfiguration>.");
        configProperty.SetValue(plugin, new PluginConfiguration());
    }

    /// <summary>
    /// Resets the <see cref="Plugin.Instance"/> configuration to a fresh default.
    /// Use this in test classes that depend on default configuration values and might
    /// be affected by state pollution from other test classes that modify the config.
    /// </summary>
    public static void ResetPluginConfiguration()
    {
        if (Plugin.Instance == null)
        {
            return;
        }

        var configProperty = typeof(MediaBrowser.Common.Plugins.BasePlugin<PluginConfiguration>).GetProperty("Configuration")
            ?? throw new InvalidOperationException("Failed to find Configuration property via reflection on BasePlugin<PluginConfiguration>.");
        configProperty.SetValue(Plugin.Instance, new PluginConfiguration());
    }

    /// <summary>
    /// Adds a JSON body to a controller's request.
    /// </summary>
    /// <param name="controller">The controller to which the JSON body will be added.</param>
    /// <param name="jsonBody">The JSON body content to be added.</param>
    /// <param name="contentLength">The content length of the JSON body. If null, it will be calculated based on the JSON body content.</param>
    /// <returns>The controller with the JSON body added to its request.</returns>
    public static ControllerBase AddJsonBodyToController(
        ControllerBase controller,
        string jsonBody,
        long? contentLength = null)
    {
        var httpContext = new DefaultHttpContext();
        var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        var requestBodyStream = new MemoryStream(bodyBytes);
        httpContext.Request.Body = requestBodyStream;
        httpContext.Response.RegisterForDispose(requestBodyStream);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = contentLength ?? bodyBytes.Length;

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        return controller;
    }
}
