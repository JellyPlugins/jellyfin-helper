using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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

    [Theory]
    [InlineData("Access to the path 'C:/secret/internal' is denied.", "Access denied")]
    [InlineData("Permission denied for /etc/shadow", "Access denied")]
    [InlineData("UnauthorizedAccessException at /root", "Access denied")]
    [InlineData("Could not find file '/mnt/data/x' - no such file or directory", "Path not found")]
    [InlineData("The system cannot find the path: does not exist", "Path not found")]
    [InlineData("Some other low-level IO glitch 0x8007", "Check failed")]
    public void CheckAccess_SanitizesRawErrorMessage_ToGenericCategory(string rawMessage, string expected)
    {
        // The raw OS error text (which can leak internal paths) must be normalized to a generic
        // category before being returned to the API caller.
        var libDir = Path.Join(_tempPath, "Movies");
        Directory.CreateDirectory(libDir);
        var trashDir = Path.Join(libDir, ".jellyfin-trash");

        var (controller, _, configHelperMock, trashServiceMock) = ControllerTestFactory.CreateTrashController();
        configHelperMock.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());
        configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string> { libDir });
        trashServiceMock.Setup(s => s.CheckPathAccess(It.IsAny<string>(), It.IsAny<ILogger>()))
            .Returns(new TrashPathAccessResult { Exists = true, CanRead = false, CanWrite = false, ErrorMessage = rawMessage });

        var result = controller.CheckAccess(new TrashPathQueryRequest { TrashFolderPath = trashDir });

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains(expected, json, StringComparison.Ordinal);
        // Raw internal detail must not leak through.
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shadow", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckAccess_NullOrEmptyErrorMessage_PassesThrough()
    {
        // Success path: no error message, nothing to sanitize (early return branch).
        var libDir = Path.Join(_tempPath, "Movies");
        Directory.CreateDirectory(libDir);
        var trashDir = Path.Join(libDir, ".jellyfin-trash");

        var (controller, _, configHelperMock, trashServiceMock) = ControllerTestFactory.CreateTrashController();
        configHelperMock.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());
        configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string> { libDir });
        trashServiceMock.Setup(s => s.CheckPathAccess(It.IsAny<string>(), It.IsAny<ILogger>()))
            .Returns(new TrashPathAccessResult { Exists = true, CanRead = true, CanWrite = true, ErrorMessage = null });

        var result = controller.CheckAccess(new TrashPathQueryRequest { TrashFolderPath = trashDir });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TrashAccessResponse>(ok.Value);
        Assert.True(response.AllAccessible);
        var entry = Assert.Single(response.Results);
        Assert.Null(entry.ErrorMessage);
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
    public void DeleteTrashFolders_TrashPathIsSymlink_RefusesAndDoesNotFollow()
    {
        // TOCTOU/symlink-swap guard: if the trash path is a reparse point (symlink/junction),
        // the recursive delete must be refused so it cannot be redirected into a real media tree.
        var realTarget = Path.Join(_tempPath, "RealMedia");
        Directory.CreateDirectory(realTarget);
        var keeper = Path.Join(realTarget, "keeper.mkv");
        File.WriteAllText(keeper, "precious");

        var linkPath = Path.Join(_tempPath, "GlobalTrashLink");
        try
        {
            Directory.CreateSymbolicLink(linkPath, realTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Symlinks unsupported in this environment - skip.
        }

        SetupConfig(new PluginConfiguration { TrashFolderPath = linkPath });
        _configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string>());

        var result = _controller.DeleteTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashDeleteResponse>(okResult.Value);
        Assert.Equal(0, data.Deleted);
        Assert.Equal(1, data.Failed);
        // The real target and its content must be untouched.
        Assert.True(File.Exists(keeper));
        Assert.Equal("precious", File.ReadAllText(keeper));
    }

    [Fact]
    public void HasReparsePointAncestor_AllAncestorsRealAndPlain_ReturnsFalse()
    {
        // A path whose every ancestor exists as a plain directory must NOT be flagged -
        // otherwise the guard would block legitimate deletions.
        var child = Path.Join(_tempPath, "lib", "show", "season");
        Directory.CreateDirectory(child);

        Assert.False(TrashController.HasReparsePointAncestor(child));
    }

    [Fact]
    public void HasReparsePointAncestor_MissingAncestor_FailsClosed()
    {
        // Regression guard: DirectoryInfo.Exists returns false (no throw) for a missing
        // ancestor. The old `Exists && isReparsePoint` short-circuit fell through and reported
        // the ancestry as safe, letting a later recursive delete run after an incomplete check.
        // An ancestor that cannot be proven not to be a reparse point must fail closed.
        var missingAncestor = Path.Join(_tempPath, "does-not-exist");
        var path = Path.Join(missingAncestor, "child");

        Assert.True(TrashController.HasReparsePointAncestor(path));
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

    [Fact]
    public void GetTrashSummary_SumsSizeAndCountAcrossLibraries_DeduplicatingSharedPaths()
    {
        // Two libraries whose trash resolves to the SAME absolute path plus a third distinct path.
        // The shared trash must be counted once (HashSet dedup), not twice.
        var (controller, _, configHelper, trashService) = ControllerTestFactory.CreateTrashController();
        var lib1 = Path.Join(_tempPath, "Movies");
        var lib2 = Path.Join(_tempPath, "TV");
        var lib3 = Path.Join(_tempPath, "Music");
        var sharedTrash = Path.Join(_tempPath, "shared-trash");
        var distinctTrash = Path.Join(lib3, ".trash");

        configHelper.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());
        configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns([lib1, lib2, lib3]);
        configHelper.Setup(c => c.GetTrashPath(lib1)).Returns(sharedTrash);
        configHelper.Setup(c => c.GetTrashPath(lib2)).Returns(sharedTrash);
        configHelper.Setup(c => c.GetTrashPath(lib3)).Returns(distinctTrash);

        trashService.Setup(t => t.GetTrashSummary(sharedTrash, It.IsAny<ILogger>())).Returns((100L, 2));
        trashService.Setup(t => t.GetTrashSummary(distinctTrash, It.IsAny<ILogger>())).Returns((50L, 1));

        var result = controller.GetTrashSummary();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashSizeResponse>(okResult.Value);
        Assert.Equal(150, data.TotalSize);
        Assert.Equal(3, data.TotalItems);
    }

    [Fact]
    public void GetTrashSummary_NoLibraries_ReturnsZeroTotals()
    {
        SetupLibraries();

        var result = _controller.GetTrashSummary();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashSizeResponse>(okResult.Value);
        Assert.Equal(0, data.TotalSize);
        Assert.Equal(0, data.TotalItems);
    }

    [Fact]
    public void GetTrashContents_GroupsNonEmptyLibrariesWithNamesAndConfig()
    {
        // Library A has trash items, library B is empty. Only A should be projected,
        // with its name derived from the folder and config values echoed back.
        var (controller, _, configHelper, trashService) = ControllerTestFactory.CreateTrashController();
        var libA = Path.Join(_tempPath, "Movies");
        var libB = Path.Join(_tempPath, "TV");
        var trashA = Path.Join(libA, ".trash");
        var trashB = Path.Join(libB, ".trash");

        configHelper.Setup(c => c.GetConfig())
            .Returns(new PluginConfiguration { UseTrash = true, TrashRetentionDays = 30 });
        configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns([libA, libB]);
        configHelper.Setup(c => c.GetTrashPath(libA)).Returns(trashA);
        configHelper.Setup(c => c.GetTrashPath(libB)).Returns(trashB);

        trashService.Setup(t => t.GetTrashContents(trashA, 30, It.IsAny<ILogger>()))
            .Returns(new List<TrashItemInfo> { new() { Name = "Old Movie" }, new() { Name = "Another" } });
        trashService.Setup(t => t.GetTrashContents(trashB, 30, It.IsAny<ILogger>()))
            .Returns(new List<TrashItemInfo>());

        var result = controller.GetTrashContents();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashConfigResponse>(okResult.Value);
        Assert.True(data.UseTrash);
        Assert.Equal(30, data.RetentionDays);
        var lib = Assert.Single(data.Libraries);
        Assert.Equal(libA, lib.LibraryPath);
        Assert.Equal(Path.GetFileName(libA), lib.LibraryName);
        Assert.Equal(2, lib.Items.Count);
    }

    [Fact]
    public void GetTrashContents_SkipsDuplicateTrashPathAcrossLibraries()
    {
        // Both libraries resolve to the same absolute trash path; it must appear once.
        var (controller, _, configHelper, trashService) = ControllerTestFactory.CreateTrashController();
        var libA = Path.Join(_tempPath, "Movies");
        var libB = Path.Join(_tempPath, "TV");
        var sharedTrash = Path.Join(_tempPath, "shared-trash");

        configHelper.Setup(c => c.GetConfig())
            .Returns(new PluginConfiguration { UseTrash = true, TrashRetentionDays = 7 });
        configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns([libA, libB]);
        configHelper.Setup(c => c.GetTrashPath(libA)).Returns(sharedTrash);
        configHelper.Setup(c => c.GetTrashPath(libB)).Returns(sharedTrash);

        trashService.Setup(t => t.GetTrashContents(sharedTrash, 7, It.IsAny<ILogger>()))
            .Returns(new List<TrashItemInfo> { new() { Name = "item" } });

        var result = controller.GetTrashContents();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashConfigResponse>(okResult.Value);
        Assert.Single(data.Libraries);
    }

    [Fact]
    public void DeleteTrashFolders_DeleteThrowsIOException_CountsAsFailedNotDeleted()
    {
        // A locked file inside the trash dir makes Directory.Delete throw; the controller
        // must catch it and tally the path as Failed, not propagate or count it Deleted.
        // The exclusive FileShare.None lock only blocks deletion on Windows; on POSIX the
        // open handle does not prevent Directory.Delete, so no IOException is thrown and
        // the contract cannot be exercised. Sibling failure tests gate the same way.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var trashPath = Path.Join(_tempPath, "LockedTrash");
        Directory.CreateDirectory(trashPath);
        var lockedFile = Path.Join(trashPath, "locked.dat");

        SetupConfig(new PluginConfiguration { TrashFolderPath = trashPath });
        // Empty library list so the absolute path passes the safety gate.
        _configHelperMock.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string>());

        using var fs = new FileStream(lockedFile, FileMode.Create, FileAccess.Write, FileShare.None);

        var result = _controller.DeleteTrashFolders();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<TrashDeleteResponse>(okResult.Value);
        Assert.Equal(0, data.Deleted);
        Assert.Equal(1, data.Failed);
    }
}