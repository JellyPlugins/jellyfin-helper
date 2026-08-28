using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for FolderBrowserController. The controller is a thin wrapper over IFolderBrowserService and ILibraryManager.
/// </summary>
public class FolderBrowserControllerTests
{
    private static FolderBrowserController CreateController(Mock<ILibraryManager>? libraryManagerMock = null)
    {
        var libraryManager = libraryManagerMock ?? TestMockFactory.CreateLibraryManager();
        var folderBrowser = new FolderBrowserService(
            TestMockFactory.CreateLogger<FolderBrowserService>().Object);
        return new FolderBrowserController(folderBrowser, libraryManager.Object);
    }

    [Fact]
    public void BrowseFolders_NullPath_ReturnsRoots()
    {
        var controller = CreateController();

        var result = controller.BrowseFolders(null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowseResult>(ok.Value);

        // Roots contract from FolderBrowserService: no error, cannot go up, non-empty directories
        Assert.Null(payload.Error);
        Assert.False(payload.CanGoUp);
        Assert.NotEmpty(payload.Directories);
    }

    [Fact]
    public void BrowseFolders_EmptyPath_ReturnsRoots()
    {
        var controller = CreateController();

        var result = controller.BrowseFolders(string.Empty);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowseResult>(ok.Value);
        Assert.Null(payload.Error);
        Assert.False(payload.CanGoUp);
    }

    [Fact]
    public void BrowseFolders_Whitespace_ReturnsRoots()
    {
        var controller = CreateController();

        var result = controller.BrowseFolders("   ");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowseResult>(ok.Value);
        Assert.Null(payload.Error);
    }

    [Fact]
    public void BrowseFolders_ValidPath_ReturnsChildren()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "FBC_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Join(tempDir, "sub1"));
        Directory.CreateDirectory(Path.Join(tempDir, "sub2"));

        try
        {
            var controller = CreateController();

            var result = controller.BrowseFolders(tempDir);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsType<FolderBrowseResult>(ok.Value);
            Assert.Null(payload.Error);
            Assert.Equal(2, payload.Directories.Count);
            Assert.True(payload.CanGoUp);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    [Fact]
    public void BrowseFolders_InvalidPath_ReturnsErrorPayload()
    {
        var controller = CreateController();

        var result = controller.BrowseFolders("relative/path");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowseResult>(ok.Value);
        Assert.NotNull(payload.Error);
        Assert.Empty(payload.Directories);
    }

    [Fact]
    public void BrowseFolders_TraversalAttempt_ReturnsErrorPayload()
    {
        var controller = CreateController();
        var evilPath = OperatingSystem.IsWindows() ? @"C:\foo\..\bar" : "/foo/../bar";

        var result = controller.BrowseFolders(evilPath);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowseResult>(ok.Value);
        Assert.Contains("..", payload.Error!);
    }

    [Fact]
    public void GetLibraryPaths_NoLibraries_ReturnsEmptyList()
    {
        var libraryManager = TestMockFactory.CreateLibraryManager();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns([]);

        var controller = CreateController(libraryManager);

        var result = controller.GetLibraryPaths();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowserResponse>(ok.Value);
        Assert.Empty(payload.LibraryPaths);
    }

    [Fact]
    public void GetLibraryPaths_WithLibraries_ReturnsEntries()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { Name = "Movies", Locations = ["/mnt/movies"] },
            new() { Name = "TV Shows", Locations = ["/mnt/tv", "/mnt/tv2"] }
        });

        var controller = CreateController(libraryManager);

        var result = controller.GetLibraryPaths();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowserResponse>(ok.Value);

        // 3 total: Movies (1 loc) + TV Shows (2 locs)
        Assert.Equal(3, payload.LibraryPaths.Count);
    }

    [Fact]
    public void GetLibraryPaths_LibraryWithoutName_IsSkipped()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { Name = "", Locations = ["/mnt/nameless"] },  // no name, skipped
            new() { Name = "  ", Locations = ["/mnt/whitespace"] },  // whitespace, skipped
            new() { Name = "Movies", Locations = ["/mnt/movies"] }
        });

        var controller = CreateController(libraryManager);

        var result = controller.GetLibraryPaths();
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowserResponse>(ok.Value);

        Assert.Single(payload.LibraryPaths);
    }

    [Fact]
    public void GetLibraryPaths_LibraryWithNullLocations_IsHandled()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { Name = "Empty", Locations = null! }
        });

        var controller = CreateController(libraryManager);

        var result = controller.GetLibraryPaths();
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowserResponse>(ok.Value);

        Assert.Empty(payload.LibraryPaths);
    }

    [Fact]
    public void GetLibraryPaths_LibraryWithEmptyLocationString_IsSkipped()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { Name = "PartlyBroken", Locations = ["", "  ", "/mnt/ok"] }
        });

        var controller = CreateController(libraryManager);

        var result = controller.GetLibraryPaths();
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowserResponse>(ok.Value);

        // Only "/mnt/ok" survives filter
        Assert.Single(payload.LibraryPaths);
    }

    [Fact]
    public void GetLibraryPaths_ResultsSortedByNameCaseInsensitive()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { Name = "zeta", Locations = ["/z"] },
            new() { Name = "Alpha", Locations = ["/a"] },
            new() { Name = "middle", Locations = ["/m"] }
        });

        var controller = CreateController(libraryManager);

        var result = controller.GetLibraryPaths();
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowserResponse>(ok.Value);

        Assert.Equal(3, payload.LibraryPaths.Count);
        var names = payload.LibraryPaths.Select(e => e.Name).ToList();
        Assert.Equal(new[] { "Alpha", "middle", "zeta" }, names);
    }

    [Fact]
    public void GetLibraryPaths_MixedValidAndBrokenLibraries_ReturnsOnlyValid()
    {
        // Combined edge-case: some libraries have no name, some have null locations,
        // some have mixed valid/invalid location strings. Only the fully-valid rows survive.
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { Name = "", Locations = ["/should/be/skipped"] },       // no name
            new() { Name = "Valid1", Locations = ["/mnt/one", ""] },       // 1 valid loc
            new() { Name = "NoLocs", Locations = null! },                  // no locations
            new() { Name = "Valid2", Locations = ["/mnt/two"] }            // 1 valid loc
        });

        var controller = CreateController(libraryManager);

        var result = controller.GetLibraryPaths();
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<FolderBrowserResponse>(ok.Value);

        // Only Valid1 (1 loc) and Valid2 (1 loc) = 2 entries
        Assert.Equal(2, payload.LibraryPaths.Count);
        var names = payload.LibraryPaths.Select(e => e.Name).ToList();
        Assert.Contains("Valid1", names);
        Assert.Contains("Valid2", names);
    }
}
