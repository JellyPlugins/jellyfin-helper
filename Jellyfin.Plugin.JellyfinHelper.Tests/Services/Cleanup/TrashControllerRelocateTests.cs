using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Unit tests for the <see cref="TrashController.GetTrashFoldersForPath"/>
///     and <see cref="TrashController.RelocateTrash"/> endpoints.
/// </summary>
public class TrashControllerRelocateTests : IDisposable
{
    private readonly string _testRoot = TestDataGenerator.CreateTempDirectory("TrashCtrlReloc");

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    private TrashController CreateController(PluginConfiguration config, List<string> libraryFolders, Mock<ITrashService>? trashServiceMock = null)
    {
        var libraryManager = TestMockFactory.CreateLibraryManager();
        var pluginLog = TestMockFactory.CreatePluginLogService();
        var logger = TestMockFactory.CreateLogger<TrashController>();
        var trashService = trashServiceMock ?? new Mock<ITrashService>();
        var configHelper = TestMockFactory.CreateCleanupConfigHelper(config);

        configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<MediaBrowser.Controller.Library.ILibraryManager>()))
            .Returns(libraryFolders);

        // Default: GetExistingTrashFoldersForPath returns the list of folders that actually exist on disk
        // (delegates to a real CleanupConfigHelper-like logic for test accuracy)
        configHelper.Setup(c => c.GetExistingTrashFoldersForPath(
                It.IsAny<MediaBrowser.Controller.Library.ILibraryManager>(),
                It.IsAny<string>()))
            .Returns((MediaBrowser.Controller.Library.ILibraryManager _, string trashPath) =>
            {
                var existing = new List<string>();
                if (string.IsNullOrWhiteSpace(trashPath)) return existing;

                if (Path.IsPathRooted(trashPath))
                {
                    var fullPath = Path.GetFullPath(trashPath);
                    if (Directory.Exists(fullPath)) existing.Add(fullPath);
                }
                else
                {
                    foreach (var folder in libraryFolders)
                    {
                        var resolved = Path.GetFullPath(Path.Join(folder, trashPath));
                        if (Directory.Exists(resolved)) existing.Add(resolved);
                    }
                }

                return existing;
            });

        return new TrashController(
            libraryManager.Object,
            pluginLog,
            logger.Object,
            configHelper.Object,
            trashService.Object);
    }

    // ===== FoldersForPath Tests =====

    [Fact]
    public void GetTrashFoldersForPath_EmptyPath_ReturnsBadRequest()
    {
        var config = new PluginConfiguration();
        var controller = CreateController(config, []);

        var result = controller.GetTrashFoldersForPath(new TrashPathQueryRequest { TrashFolderPath = "" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetTrashFoldersForPath_RelativePath_ReturnsExistingFolders()
    {
        // Arrange: create a library root with a trash subfolder
        var libraryRoot = Path.Combine(_testRoot, "movies");
        Directory.CreateDirectory(libraryRoot);
        var trashDir = Path.Combine(libraryRoot, ".old-trash");
        Directory.CreateDirectory(trashDir);

        var config = new PluginConfiguration();
        var controller = CreateController(config, [libraryRoot]);

        // Act
        var result = controller.GetTrashFoldersForPath(new TrashPathQueryRequest { TrashFolderPath = ".old-trash" });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value!;
        var pathsProp = value.GetType().GetProperty("Paths");
        var paths = (IEnumerable<string>)pathsProp!.GetValue(value)!;
        var pathsList = paths.ToList();
        Assert.Single(pathsList);
        Assert.Equal(trashDir, pathsList[0]);
    }

    [Fact]
    public void GetTrashFoldersForPath_AbsolutePath_ReturnsExistingFolder()
    {
        // Arrange
        var absoluteTrash = Path.Combine(_testRoot, "absolute-trash");
        Directory.CreateDirectory(absoluteTrash);

        var config = new PluginConfiguration();
        var controller = CreateController(config, []);

        // Act
        var result = controller.GetTrashFoldersForPath(new TrashPathQueryRequest { TrashFolderPath = absoluteTrash });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value!;
        var pathsProp = value.GetType().GetProperty("Paths");
        var paths = (IEnumerable<string>)pathsProp!.GetValue(value)!;
        var pathsList = paths.ToList();
        Assert.Single(pathsList);
        Assert.Equal(absoluteTrash, pathsList[0]);
    }

    [Fact]
    public void GetTrashFoldersForPath_NonExistentAbsolutePath_ReturnsEmptyList()
    {
        var config = new PluginConfiguration();
        var controller = CreateController(config, []);

        var result = controller.GetTrashFoldersForPath(new TrashPathQueryRequest { TrashFolderPath = Path.Combine(_testRoot, "nonexistent") });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value!;
        var pathsProp = value.GetType().GetProperty("Paths");
        var paths = (IEnumerable<string>)pathsProp!.GetValue(value)!;
        Assert.Empty(paths);
    }

    // ===== Relocate Tests =====

    [Fact]
    public void RelocateTrash_EmptyPaths_ReturnsBadRequest()
    {
        var config = new PluginConfiguration();
        var controller = CreateController(config, []);

        var result = controller.RelocateTrash(new TrashRelocateRequest { OldTrashPath = "", NewTrashPath = "new" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void RelocateTrash_BothRelative_CallsRelocatePerLibrary()
    {
        // Arrange
        var libraryRoot = Path.Combine(_testRoot, "movies");
        var oldTrashPath = Path.Combine(libraryRoot, ".old-trash");
        Directory.CreateDirectory(oldTrashPath);
        File.WriteAllText(Path.Combine(oldTrashPath, "item.txt"), "data");

        var trashServiceMock = new Mock<ITrashService>();
        trashServiceMock.Setup(ts => ts.RelocateTrashContents(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Microsoft.Extensions.Logging.ILogger>()))
            .Returns((3, 0));

        var config = new PluginConfiguration();
        var controller = CreateController(config, [libraryRoot], trashServiceMock);

        // Act
        var result = controller.RelocateTrash(new TrashRelocateRequest { OldTrashPath = ".old-trash", NewTrashPath = ".new-trash" });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        trashServiceMock.Verify(ts => ts.RelocateTrashContents(
            It.Is<string>(s => s.Contains(".old-trash")),
            It.Is<string>(s => s.Contains(".new-trash")),
            It.IsAny<Microsoft.Extensions.Logging.ILogger>()), Times.Once);
    }

    [Fact]
    public void RelocateTrash_BothAbsolute_CallsRelocateOnce()
    {
        // Arrange
        var oldPath = Path.Combine(_testRoot, "old-absolute");
        var newPath = Path.Combine(_testRoot, "new-absolute");
        Directory.CreateDirectory(oldPath);

        var trashServiceMock = new Mock<ITrashService>();
        trashServiceMock.Setup(ts => ts.RelocateTrashContents(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Microsoft.Extensions.Logging.ILogger>()))
            .Returns((5, 1));

        var libraryRoot = Path.Combine(_testRoot, "movies");
        var config = new PluginConfiguration();
        var controller = CreateController(config, [libraryRoot], trashServiceMock);

        // Act
        var result = controller.RelocateTrash(new TrashRelocateRequest { OldTrashPath = oldPath, NewTrashPath = newPath });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value!;
        var movedProp = value.GetType().GetProperty("Moved");
        var failedProp = value.GetType().GetProperty("Failed");
        Assert.Equal(5, (int)movedProp!.GetValue(value)!);
        Assert.Equal(1, (int)failedProp!.GetValue(value)!);
    }

    [Fact]
    public void RelocateTrash_UnsafeAbsoluteOldPath_ReturnsBadRequest()
    {
        // Arrange: old path IS the library root (unsafe)
        var libraryRoot = Path.Combine(_testRoot, "movies");
        Directory.CreateDirectory(libraryRoot);

        var config = new PluginConfiguration();
        var controller = CreateController(config, [libraryRoot]);

        var result = controller.RelocateTrash(new TrashRelocateRequest { OldTrashPath = libraryRoot, NewTrashPath = Path.Combine(_testRoot, "new") });

        Assert.IsType<BadRequestObjectResult>(result);
    }
}