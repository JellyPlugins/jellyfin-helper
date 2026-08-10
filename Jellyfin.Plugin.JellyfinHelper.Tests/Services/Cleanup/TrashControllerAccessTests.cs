using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Unit tests for the <see cref="TrashController.CheckAccess"/> endpoint.
/// </summary>
public class TrashControllerAccessTests : IDisposable
{
    private readonly string _testRoot = TestDataGenerator.CreateTempDirectory("TrashCtrlAccess");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient file locks must not fail the test suite.
        }
    }

    private TrashController CreateController(PluginConfiguration config, List<string> libraryFolders)
    {
        var libraryManager = TestMockFactory.CreateLibraryManager();
        var mockPluginLog = new Mock<IPluginLogService>();
        var logger = TestMockFactory.CreateLogger<TrashController>();
        var pluginLogConcrete = TestMockFactory.CreatePluginLogService(config);
        var trashService = new TrashService(pluginLogConcrete);
        var configHelper = TestMockFactory.CreateCleanupConfigHelper(config);

        configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<MediaBrowser.Controller.Library.ILibraryManager>()))
            .Returns(libraryFolders);

        configHelper.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns((string lib) => Path.Join(lib, config.TrashFolderPath ?? ".jellyfin-trash"));

        return new TrashController(
            libraryManager.Object,
            mockPluginLog.Object,
            logger.Object,
            configHelper.Object,
            trashService);
    }

    [Fact]
    public void CheckAccess_EmptyPath_ReturnsBadRequest()
    {
        var config = new PluginConfiguration { UseTrash = true, TrashFolderPath = ".jellyfin-trash" };
        var controller = CreateController(config, new List<string> { _testRoot });

        var result = controller.CheckAccess(new TrashPathQueryRequest { TrashFolderPath = "" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void CheckAccess_AbsoluteWritablePath_ReturnsAllAccessible()
    {
        var config = new PluginConfiguration { UseTrash = true, TrashFolderPath = ".jellyfin-trash" };
        var controller = CreateController(config, new List<string> { _testRoot });

        // Use a subdirectory of the library root - not the root itself, which IsPathSafeForDeletion
        // correctly rejects (a path equal to the library root would delete the library).
        var checkPath = Path.Join(_testRoot, ".jellyfin-trash");
        Directory.CreateDirectory(checkPath);

        var result = controller.CheckAccess(new TrashPathQueryRequest { TrashFolderPath = checkPath });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = okResult.Value;
        var allAccessible = data!.GetType().GetProperty("AllAccessible")!.GetValue(data);
        Assert.True((bool)allAccessible!);
    }

    [Fact]
    public void CheckAccess_RelativePath_ResolvesPerLibrary()
    {
        var config = new PluginConfiguration { UseTrash = true, TrashFolderPath = ".my-trash" };
        var controller = CreateController(config, new List<string> { _testRoot });

        var result = controller.CheckAccess(new TrashPathQueryRequest { TrashFolderPath = ".my-trash" });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = okResult.Value;
        var allAccessible = data!.GetType().GetProperty("AllAccessible")!.GetValue(data);
        Assert.True((bool)allAccessible!);
    }
}
