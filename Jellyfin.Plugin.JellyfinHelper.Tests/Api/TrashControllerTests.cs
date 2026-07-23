using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class TrashControllerTests : IDisposable
{
    private readonly Mock<ICleanupConfigHelper> _configHelperMock;
    private readonly TrashController _controller;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly string _tempPath;

    public TrashControllerTests()
    {
        _tempPath = Path.Join(Path.GetTempPath(), "JellyfinHelperTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempPath);

        (_controller, _libraryManagerMock, _configHelperMock, _) = ControllerTestFactory.CreateTrashController();

        // Default: return empty config
        _configHelperMock.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempPath, true);
        }
        catch (DirectoryNotFoundException)
        {
            // best-effort cleanup
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup
        }
    }

    private void SetupLibraries(params string[] paths)
    {
        var folders = paths.Select(path => new VirtualFolderInfo
        { Name = Path.GetFileName(path), Locations = [path], CollectionType = CollectionTypeOptions.movies })
            .ToList();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(folders);
        _configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(paths.ToList());
    }

    private void SetupConfig(PluginConfiguration config)
    {
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);
    }

    [Fact]
    public void GetTrashFolders_AbsoluteTrashPath_ReturnsExistingPath()
    {
        var trashPath = Path.Join(_tempPath, "GlobalTrash");
        Directory.CreateDirectory(trashPath);

        SetupConfig(new PluginConfiguration { TrashFolderPath = trashPath });

        var result = _controller.GetTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashFoldersResponse>(okResult.Value);
        Assert.True(data.IsAbsolute);
        Assert.Single(data.Paths);
        Assert.Equal(trashPath, data.Paths[0]);
    }

    [Fact]
    public void GetTrashFolders_AbsoluteTrashPath_ReturnsEmptyIfNotExist()
    {
        var trashPath = Path.Join(_tempPath, "NonExistentTrash");

        SetupConfig(new PluginConfiguration { TrashFolderPath = trashPath });

        var result = _controller.GetTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashFoldersResponse>(okResult.Value);
        Assert.True(data.IsAbsolute);
        Assert.Empty(data.Paths);
    }

    [Fact]
    public void GetTrashFolders_RelativeTrashPath_ReturnsExistingLibraryTrash()
    {
        var lib1 = Path.Join(_tempPath, "Movies");
        var lib2 = Path.Join(_tempPath, "TV");
        Directory.CreateDirectory(lib1);
        Directory.CreateDirectory(lib2);

        var trash1 = Path.Join(lib1, ".jellyfin-trash");
        Directory.CreateDirectory(trash1);

        SetupLibraries(lib1, lib2);

        SetupConfig(new PluginConfiguration { TrashFolderPath = ".jellyfin-trash" });

        _configHelperMock.Setup(c => c.GetTrashPath(lib1)).Returns(trash1);
        _configHelperMock.Setup(c => c.GetTrashPath(lib2)).Returns(Path.Join(lib2, ".jellyfin-trash"));

        var result = _controller.GetTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashFoldersResponse>(okResult.Value);
        Assert.False(data.IsAbsolute);
        Assert.Single(data.Paths);
        Assert.Equal(trash1, data.Paths[0]);
    }

    [Fact]
    public void GetTrashFolders_RelativeTrashPath_ReturnsMultipleFolders()
    {
        var lib1 = Path.Join(_tempPath, "Movies");
        var lib2 = Path.Join(_tempPath, "TV");
        Directory.CreateDirectory(lib1);
        Directory.CreateDirectory(lib2);

        var trash1 = Path.Join(lib1, ".jellyfin-trash");
        var trash2 = Path.Join(lib2, ".jellyfin-trash");
        Directory.CreateDirectory(trash1);
        Directory.CreateDirectory(trash2);

        SetupLibraries(lib1, lib2);

        SetupConfig(new PluginConfiguration { TrashFolderPath = ".jellyfin-trash" });

        _configHelperMock.Setup(c => c.GetTrashPath(lib1)).Returns(trash1);
        _configHelperMock.Setup(c => c.GetTrashPath(lib2)).Returns(trash2);

        var result = _controller.GetTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashFoldersResponse>(okResult.Value);
        Assert.False(data.IsAbsolute);
        Assert.Equal(2, data.Paths.Count);
        Assert.Contains(trash1, data.Paths);
        Assert.Contains(trash2, data.Paths);
    }

    [Fact]
    public void DeleteTrashFolders_AbsoluteTrashPath_DeletesFolder()
    {
        var trashPath = Path.Join(_tempPath, "GlobalTrash");
        Directory.CreateDirectory(trashPath);
        File.WriteAllText(Path.Join(trashPath, "test.txt"), "content");

        SetupConfig(new PluginConfiguration { TrashFolderPath = trashPath });

        // Need to setup filtered library locations so safety check works
        _configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string>());

        var result = _controller.DeleteTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashDeleteResponse>(okResult.Value);
        Assert.Equal(1, data.Deleted);
        Assert.Equal(0, data.Failed);
        Assert.False(Directory.Exists(trashPath));
    }

    [Fact]
    public void DeleteTrashFolders_RelativeTrashPath_DeletesMultipleFolders()
    {
        var lib1 = Path.Join(_tempPath, "Movies");
        var lib2 = Path.Join(_tempPath, "TV");
        Directory.CreateDirectory(lib1);
        Directory.CreateDirectory(lib2);

        var trash1 = Path.Join(lib1, ".jellyfin-trash");
        var trash2 = Path.Join(lib2, ".jellyfin-trash");
        Directory.CreateDirectory(trash1);
        Directory.CreateDirectory(trash2);

        SetupLibraries(lib1, lib2);

        SetupConfig(new PluginConfiguration { TrashFolderPath = ".jellyfin-trash" });

        _configHelperMock.Setup(c => c.GetTrashPath(lib1)).Returns(trash1);
        _configHelperMock.Setup(c => c.GetTrashPath(lib2)).Returns(trash2);

        var result = _controller.DeleteTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashDeleteResponse>(okResult.Value);
        Assert.Equal(2, data.Deleted);
        Assert.Equal(0, data.Failed);
        Assert.False(Directory.Exists(trash1));
        Assert.False(Directory.Exists(trash2));
    }

    [Fact]
    public void DeleteTrashFolders_UnsafePath_ReturnsBadRequest()
    {
        var lib1 = Path.Join(_tempPath, "Movies");
        Directory.CreateDirectory(lib1);

        SetupLibraries(lib1);

        // Set TrashFolderPath to the library root itself (unsafe)
        SetupConfig(new PluginConfiguration { TrashFolderPath = lib1 });

        var result = _controller.DeleteTrashFolders();

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var data = badRequest.Value as dynamic;
        Assert.Contains("unsafe", (string)data!.Error);
        Assert.True(Directory.Exists(lib1));
    }
}