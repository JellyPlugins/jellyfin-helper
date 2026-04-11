using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

[Collection("ConfigOverride")]
public class MediaStatisticsControllerTrashTests : IDisposable
{
    private readonly MediaStatisticsController _controller;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly string _tempPath;

    public MediaStatisticsControllerTrashTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "JellyfinHelperTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempPath);

        _libraryManagerMock = new Mock<ILibraryManager>();
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());

        var fileSystemMock = new Mock<IFileSystem>();
        var appPathsMock = new Mock<IApplicationPaths>();
        appPathsMock.Setup(p => p.DataPath).Returns(_tempPath);
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var loggerMock = new Mock<ILogger<MediaStatisticsController>>();
        var serviceLoggerMock = new Mock<ILogger<MediaStatisticsService>>();
        var historyLoggerMock = new Mock<ILogger<StatisticsHistoryService>>();

        _controller = new MediaStatisticsController(
            _libraryManagerMock.Object,
            fileSystemMock.Object,
            appPathsMock.Object,
            httpClientFactoryMock.Object,
            cache,
            loggerMock.Object,
            serviceLoggerMock.Object,
            historyLoggerMock.Object);
            
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration();
    }

    public void Dispose()
    {
        CleanupConfigHelper.ConfigOverride = null;
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, true);
        }
    }

    private void SetupLibraries(params string[] paths)
    {
        var folders = new List<VirtualFolderInfo>();
        foreach (var path in paths)
        {
            var folder = new VirtualFolderInfo
            {
                Name = Path.GetFileName(path),
                Locations = new[] { path },
                CollectionType = CollectionTypeOptions.movies
            };
            folders.Add(folder);
        }
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(folders);
    }

    [Fact]
    public void GetTrashFolders_AbsoluteTrashPath_ReturnsExistingPath()
    {
        var trashPath = Path.Combine(_tempPath, "GlobalTrash");
        Directory.CreateDirectory(trashPath);
        
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrashFolderPath = trashPath
        };

        var result = _controller.GetTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic data = okResult.Value!;
        Assert.True(data.IsAbsolute);
        var paths = Assert.IsType<List<string>>(data.Paths);
        Assert.Single(paths);
        Assert.Equal(trashPath, paths[0]);
    }

    [Fact]
    public void GetTrashFolders_AbsoluteTrashPath_ReturnsEmptyIfNotExist()
    {
        var trashPath = Path.Combine(_tempPath, "NonExistentTrash");
        
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrashFolderPath = trashPath
        };

        var result = _controller.GetTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic data = okResult.Value!;
        Assert.True(data.IsAbsolute);
        var paths = Assert.IsType<List<string>>(data.Paths);
        Assert.Empty(paths);
    }

    [Fact]
    public void GetTrashFolders_RelativeTrashPath_ReturnsExistingLibraryTrash()
    {
        var lib1 = Path.Combine(_tempPath, "Movies");
        var lib2 = Path.Combine(_tempPath, "TV");
        Directory.CreateDirectory(lib1);
        Directory.CreateDirectory(lib2);
        
        var trash1 = Path.Combine(lib1, ".jellyfin-trash");
        Directory.CreateDirectory(trash1);
        
        SetupLibraries(lib1, lib2);
        
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrashFolderPath = ".jellyfin-trash"
        };

        var result = _controller.GetTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic data = okResult.Value!;
        Assert.False(data.IsAbsolute);
        var paths = Assert.IsType<List<string>>(data.Paths);
        Assert.Single(paths);
        Assert.Equal(trash1, paths[0]);
    }

    [Fact]
    public void GetTrashFolders_RelativeTrashPath_ReturnsMultipleFolders()
    {
        var lib1 = Path.Combine(_tempPath, "Movies");
        var lib2 = Path.Combine(_tempPath, "TV");
        Directory.CreateDirectory(lib1);
        Directory.CreateDirectory(lib2);
        
        var trash1 = Path.Combine(lib1, ".jellyfin-trash");
        var trash2 = Path.Combine(lib2, ".jellyfin-trash");
        Directory.CreateDirectory(trash1);
        Directory.CreateDirectory(trash2);
        
        SetupLibraries(lib1, lib2);
        
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrashFolderPath = ".jellyfin-trash"
        };

        var result = _controller.GetTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic data = okResult.Value!;
        Assert.False(data.IsAbsolute);
        var paths = Assert.IsType<List<string>>(data.Paths);
        Assert.Equal(2, paths.Count);
        Assert.Contains(trash1, paths);
        Assert.Contains(trash2, paths);
    }

    [Fact]
    public void DeleteTrashFolders_AbsoluteTrashPath_DeletesFolder()
    {
        var trashPath = Path.Combine(_tempPath, "GlobalTrash");
        Directory.CreateDirectory(trashPath);
        File.WriteAllText(Path.Combine(trashPath, "test.txt"), "content");
        
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrashFolderPath = trashPath
        };

        var result = _controller.DeleteTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic data = okResult.Value!;
        var deleted = Assert.IsType<List<string>>(data.Deleted);
        Assert.Single(deleted);
        Assert.Equal(Path.GetFullPath(trashPath), Path.GetFullPath(deleted[0]));
        Assert.False(Directory.Exists(trashPath));
    }

    [Fact]
    public void DeleteTrashFolders_RelativeTrashPath_DeletesMultipleFolders()
    {
        var lib1 = Path.Combine(_tempPath, "Movies");
        var lib2 = Path.Combine(_tempPath, "TV");
        Directory.CreateDirectory(lib1);
        Directory.CreateDirectory(lib2);
        
        var trash1 = Path.Combine(lib1, ".jellyfin-trash");
        var trash2 = Path.Combine(lib2, ".jellyfin-trash");
        Directory.CreateDirectory(trash1);
        Directory.CreateDirectory(trash2);
        
        SetupLibraries(lib1, lib2);
        
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrashFolderPath = ".jellyfin-trash"
        };

        var result = _controller.DeleteTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic data = okResult.Value!;
        var deleted = Assert.IsType<List<string>>(data.Deleted);
        Assert.Equal(2, deleted.Count);
        Assert.False(Directory.Exists(trash1));
        Assert.False(Directory.Exists(trash2));
    }

    [Fact]
    public void DeleteTrashFolders_UnsafePath_ReturnsBadRequest()
    {
        var lib1 = Path.Combine(_tempPath, "Movies");
        Directory.CreateDirectory(lib1);
        
        SetupLibraries(lib1);
        
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration
        {
            TrashFolderPath = lib1
        };

        var result = _controller.DeleteTrashFolders();

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        dynamic data = badRequest.Value!;
        Assert.Contains("unsafe", (string)data.Error);
        Assert.True(Directory.Exists(lib1));
    }
}
