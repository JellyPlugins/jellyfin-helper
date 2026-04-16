using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Security tests for <see cref="TrashController" />.
///     Verifies that the DeleteTrashFolders endpoint rejects unsafe paths
///     and does not allow deletion outside expected directories.
/// </summary>
public class TrashControllerSecurityTests : IDisposable
{
    private readonly string _testRoot = TestDataGenerator.CreateTempDirectory("TrashCtrlSec");

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    /// <summary>
    ///     Creates a TrashController with the given configuration and library folders.
    /// </summary>
    private TrashController CreateController(PluginConfiguration config, List<string> libraryFolders)
    {
        var libraryManager = TestMockFactory.CreateLibraryManager();
        var pluginLog = TestMockFactory.CreatePluginLogService();
        var logger = TestMockFactory.CreateLogger<TrashController>();
        var trashService = new Mock<ITrashService>();
        var configHelper = TestMockFactory.CreateCleanupConfigHelper(config);

        configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<MediaBrowser.Controller.Library.ILibraryManager>()))
            .Returns(libraryFolders);

        return new TrashController(
            libraryManager.Object,
            pluginLog,
            logger.Object,
            configHelper.Object,
            trashService.Object);
    }

    // ===== DeleteTrashFolders: Absolute path safety =====

    [Fact]
    [Trait("Category", "Security")]
    public void DeleteTrashFolders_AbsolutePathIsFilesystemRoot_ReturnsBadRequest()
    {
        var root = Path.GetPathRoot(Path.GetTempPath()) ?? "/";
        var config = new PluginConfiguration { TrashFolderPath = root };
        var libraryFolders = new List<string> { Path.Join(_testRoot, "movies") };

        var controller = CreateController(config, libraryFolders);
        var result = controller.DeleteTrashFolders();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DeleteTrashFolders_AbsolutePathEqualsLibraryRoot_ReturnsBadRequest()
    {
        var libraryPath = Path.Join(_testRoot, "movies");
        Directory.CreateDirectory(libraryPath);

        var config = new PluginConfiguration { TrashFolderPath = libraryPath };
        var libraryFolders = new List<string> { libraryPath };

        var controller = CreateController(config, libraryFolders);
        var result = controller.DeleteTrashFolders();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DeleteTrashFolders_AbsolutePathContainsLibraryRoot_ReturnsBadRequest()
    {
        // If the trash path is a parent of a library path, deletion would destroy library contents
        var parentPath = Path.Join(_testRoot, "media");
        var libraryPath = Path.Join(parentPath, "movies");
        Directory.CreateDirectory(libraryPath);

        var config = new PluginConfiguration { TrashFolderPath = parentPath };
        var libraryFolders = new List<string> { libraryPath };

        var controller = CreateController(config, libraryFolders);
        var result = controller.DeleteTrashFolders();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ===== DeleteTrashFolders: Relative path safety =====

    [Fact]
    [Trait("Category", "Security")]
    public void DeleteTrashFolders_RelativePathEscapingLibrary_SkipsDeletion()
    {
        // A relative trash path like "../../other" would escape the library root
        var libraryPath = Path.Join(_testRoot, "media", "movies");
        Directory.CreateDirectory(libraryPath);

        // Create a directory outside the library that would be targeted
        var outsideDir = Path.Join(_testRoot, "outside_trash");
        Directory.CreateDirectory(outsideDir);

        var config = new PluginConfiguration { TrashFolderPath = "../../outside_trash" };
        var libraryFolders = new List<string> { libraryPath };

        var controller = CreateController(config, libraryFolders);
        var result = controller.DeleteTrashFolders();

        // The controller should skip paths that escape the library root
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        // The outside directory should still exist (not deleted)
        Assert.True(Directory.Exists(outsideDir), "Directory outside library should not be deleted");
    }

    // ===== DeleteTrashFolders: Non-existent paths =====

    [Fact]
    [Trait("Category", "Security")]
    public void DeleteTrashFolders_NonExistentTrashFolder_ReturnsEmptyResult()
    {
        var libraryPath = Path.Join(_testRoot, "movies");
        Directory.CreateDirectory(libraryPath);

        var config = new PluginConfiguration { TrashFolderPath = ".jellyfin-helper-trash" };
        var libraryFolders = new List<string> { libraryPath };

        var controller = CreateController(config, libraryFolders);
        var result = controller.DeleteTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    // ===== DeleteTrashFolders: Valid relative path inside library =====

    [Fact]
    [Trait("Category", "Security")]
    public void DeleteTrashFolders_ValidRelativePath_DeletesSuccessfully()
    {
        var libraryPath = Path.Join(_testRoot, "movies");
        var trashDir = Path.Join(libraryPath, ".jellyfin-helper-trash");
        Directory.CreateDirectory(trashDir);
        File.WriteAllBytes(Path.Join(trashDir, "test.txt"), new byte[10]);

        var config = new PluginConfiguration { TrashFolderPath = ".jellyfin-helper-trash" };
        var libraryFolders = new List<string> { libraryPath };

        var controller = CreateController(config, libraryFolders);
        var result = controller.DeleteTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        Assert.False(Directory.Exists(trashDir), "Trash directory should be deleted");
    }
}