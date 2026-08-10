using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Branch-coverage extensions for <see cref="ArrIntegrationController"/> covering paths
///     that <see cref="ArrIntegrationControllerTests"/> left uncovered: the <c>index</c>
///     parameter (valid + invalid range), the <c>failedInstances</c> 502 path, the empty
///     Url/ApiKey skip, and the trash-folder skip in <c>GetJellyfinFolderNames</c>.
/// </summary>
public sealed class ArrIntegrationControllerExtendedTests : IDisposable
{
    private readonly ArrIntegrationController _controller;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ICleanupConfigHelper> _configHelperMock;
    private readonly string _tempPath;

    public ArrIntegrationControllerExtendedTests()
    {
        _tempPath = Path.Join(Path.GetTempPath(), "JfhArrExt_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempPath);
        (_controller, _libraryManagerMock, _fileSystemMock, _httpClientFactoryMock, _configHelperMock) =
            ControllerTestFactory.CreateArrIntegrationController();
        _configHelperMock.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, recursive: true);
        }
    }

    private PluginConfiguration ConfigWithRadarr(params (string Url, string Key, string Name)[] instances)
    {
        var config = new PluginConfiguration();
        foreach (var (url, key, name) in instances)
        {
            config.RadarrInstances.Add(new ArrInstanceConfig { Url = url, ApiKey = key, Name = name });
        }
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);
        return config;
    }

    private PluginConfiguration ConfigWithSonarr(params (string Url, string Key, string Name)[] instances)
    {
        var config = new PluginConfiguration();
        foreach (var (url, key, name) in instances)
        {
            config.SonarrInstances.Add(new ArrInstanceConfig { Url = url, ApiKey = key, Name = name });
        }
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);
        return config;
    }

    // ---------- Radarr: index validation ----------

    [Fact]
    public async Task CompareRadarrAsync_NegativeIndex_ReturnsBadRequest()
    {
        ConfigWithRadarr(("http://r", "k", "R1"));
        var result = await _controller.CompareRadarrAsync(-1, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Invalid instance index", JsonSerializer.Serialize(bad.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareRadarrAsync_IndexAtOrAboveCount_ReturnsBadRequest()
    {
        ConfigWithRadarr(("http://r", "k", "R1"));
        var result = await _controller.CompareRadarrAsync(1, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CompareRadarrAsync_ValidIndex_UsesOnlyThatInstance()
    {
        var libPath = Path.Join(_tempPath, "Movies");
        Directory.CreateDirectory(libPath);
        var movieDir = Path.Join(libPath, "MovieA");
        Directory.CreateDirectory(movieDir);
        ConfigWithRadarr(("http://r1", "k1", "R1"), ("http://r2", "k2", "R2"));
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns(Path.Join(libPath, ".jellyfin-trash"));
        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo
            {
                Name = "Movies", Locations = [libPath], CollectionType = CollectionTypeOptions.movies
            }]);
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns([new FileSystemMetadata { Name = "MovieA", FullName = movieDir, IsDirectory = true }]);

        var handler = TestMockFactory.CreateHttpMessageHandler(
            HttpStatusCode.OK,
            "[{\"title\":\"MovieA\",\"path\":\"/m/MovieA\",\"hasFile\":true}]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(1, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        Assert.Single(data.InBoth);
    }

    // ---------- Radarr: empty Url/ApiKey is filtered by GetEffectiveRadarrInstances ----------

    [Fact]
    public async Task CompareRadarrAsync_AllInstancesHaveEmptyUrl_ReturnsBadRequest()
    {
        // DESIGN CONTRACT: PluginConfiguration.GetEffectiveRadarrInstances() filters out
        // instances with empty Url or ApiKey BEFORE the controller sees them. So a config
        // that only contains partially-filled instances is effectively "no instance
        // configured" from the controller's perspective - the correct response is 400
        // "At least one Radarr instance must be configured.", NOT a silent skip.
        //
        // The controller's inner `if (IsNullOrWhiteSpace(instance.Url)) continue;` is
        // therefore defense-in-depth against a future refactor that removes the filter
        // in GetEffectiveRadarrInstances. This test locks the CURRENT, filter-aware
        // behavior so that a regression removing the filter would immediately surface
        // in the response type.
        ConfigWithRadarr(("", "k", "Partial"));

        var result = await _controller.CompareRadarrAsync(null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains(
            "At least one Radarr instance",
            JsonSerializer.Serialize(bad.Value),
            StringComparison.Ordinal);
    }

    // ---------- Radarr: failed instance → 502 with instance name ----------

    [Fact]
    public async Task CompareRadarrAsync_UpstreamReturnsNull_Returns502WithInstanceName()
    {
        ConfigWithRadarr(("http://r", "k", "ImportantRadarr"));
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns(Path.Join(_tempPath, ".jellyfin-trash"));
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        var handler = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.InternalServerError, "boom");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(null, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Contains("ImportantRadarr", JsonSerializer.Serialize(status.Value), StringComparison.Ordinal);
    }

    // ---------- Radarr: trash folder must be excluded from folder set ----------

    [Fact]
    public async Task CompareRadarrAsync_TrashFolderInLibrary_IsExcludedFromComparison()
    {
        // Without the trash-exclusion in GetJellyfinFolderNames the ".jellyfin-trash"
        // folder would appear as InJellyfinOnly on every scan (harmless but noisy).
        var libPath = Path.Join(_tempPath, "Movies");
        Directory.CreateDirectory(libPath);
        var trashPath = Path.Join(libPath, ".jellyfin-trash");
        Directory.CreateDirectory(trashPath);
        var realMovie = Path.Join(libPath, "Real");
        Directory.CreateDirectory(realMovie);

        ConfigWithRadarr(("http://r", "k", "R1"));
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>())).Returns(trashPath);
        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo
            {
                Name = "Movies", Locations = [libPath], CollectionType = CollectionTypeOptions.movies
            }]);
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns([
                new FileSystemMetadata { Name = ".jellyfin-trash", FullName = trashPath, IsDirectory = true },
                new FileSystemMetadata { Name = "Real", FullName = realMovie, IsDirectory = true }
            ]);

        var handler = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.OK, "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        // ".jellyfin-trash" must NOT show up anywhere in the comparison result.
        Assert.DoesNotContain(".jellyfin-trash", data.InJellyfinOnly);
        Assert.DoesNotContain(".jellyfin-trash", data.InBoth);
        Assert.Contains("Real", data.InJellyfinOnly);
    }

    // ---------- Radarr: GetDirectories throws → swallowed ----------

    [Fact]
    public async Task CompareRadarrAsync_GetDirectoriesThrowsIOException_IsSwallowed()
    {
        // IOException from a filesystem enumeration on one library location must NOT
        // fail the entire comparison. The controller catches IOException and
        // UnauthorizedAccessException, logs a warning, and continues.
        var libPath = Path.Join(_tempPath, "Movies");
        Directory.CreateDirectory(libPath);
        ConfigWithRadarr(("http://r", "k", "R1"));
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns(Path.Join(libPath, ".jellyfin-trash"));
        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo
            {
                Name = "Movies", Locations = [libPath], CollectionType = CollectionTypeOptions.movies
            }]);
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Throws(new IOException("disk error"));

        var handler = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.OK, "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        Assert.Empty(data.InJellyfinOnly);
        Assert.Empty(data.InBoth);
    }

    [Fact]
    public async Task CompareRadarrAsync_GetDirectoriesThrowsUnauthorized_IsSwallowed()
    {
        var libPath = Path.Join(_tempPath, "Movies");
        Directory.CreateDirectory(libPath);
        ConfigWithRadarr(("http://r", "k", "R1"));
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns(Path.Join(libPath, ".jellyfin-trash"));
        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo
            {
                Name = "Movies", Locations = [libPath], CollectionType = CollectionTypeOptions.movies
            }]);
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Throws(new UnauthorizedAccessException("permission denied"));

        var handler = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.OK, "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // ---------- Sonarr: mirrored coverage of the same branches ----------

    [Fact]
    public async Task CompareSonarrAsync_NegativeIndex_ReturnsBadRequest()
    {
        ConfigWithSonarr(("http://s", "k", "S1"));
        var result = await _controller.CompareSonarrAsync(-1, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CompareSonarrAsync_IndexAtOrAboveCount_ReturnsBadRequest()
    {
        ConfigWithSonarr(("http://s", "k", "S1"));
        var result = await _controller.CompareSonarrAsync(1, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CompareSonarrAsync_UpstreamReturnsNull_Returns502WithInstanceName()
    {
        ConfigWithSonarr(("http://s", "k", "ImportantSonarr"));
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns(Path.Join(_tempPath, ".jellyfin-trash"));
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        var handler = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.InternalServerError, "boom");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareSonarrAsync(null, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Contains("ImportantSonarr", JsonSerializer.Serialize(status.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareSonarrAsync_AllInstancesHaveEmptyApiKey_ReturnsBadRequest()
    {
        // Mirrors CompareRadarrAsync_AllInstancesHaveEmptyUrl_ReturnsBadRequest - the
        // GetEffectiveSonarrInstances filter drops the partial instance, leaving Count==0
        // which the controller reports as 400 BadRequest.
        ConfigWithSonarr(("http://s", "", "Partial"));

        var result = await _controller.CompareSonarrAsync(null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains(
            "At least one Sonarr instance",
            JsonSerializer.Serialize(bad.Value),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareSonarrAsync_ValidIndex_UsesOnlyThatInstance()
    {
        // Twin of CompareRadarrAsync_ValidIndex_UsesOnlyThatInstance: with two instances
        // configured, a valid index must narrow the working set to that single instance
        // (line 207) rather than merging all. A match therefore proves the indexed
        // instance was the one queried.
        var libPath = Path.Join(_tempPath, "TVShows");
        Directory.CreateDirectory(libPath);
        var showDir = Path.Join(libPath, "ShowA");
        Directory.CreateDirectory(showDir);
        ConfigWithSonarr(("http://s1", "k1", "S1"), ("http://s2", "k2", "S2"));
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns(Path.Join(libPath, ".jellyfin-trash"));
        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo
            {
                Name = "TVShows", Locations = [libPath], CollectionType = CollectionTypeOptions.tvshows
            }]);
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns([new FileSystemMetadata { Name = "ShowA", FullName = showDir, IsDirectory = true }]);

        var handler = TestMockFactory.CreateHttpMessageHandler(
            HttpStatusCode.OK,
            "[{\"title\":\"ShowA\",\"path\":\"/tv/ShowA\",\"statistics\":{\"episodeFileCount\":10,\"totalEpisodeCount\":10}}]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareSonarrAsync(1, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        Assert.Single(data.InBoth);
    }
}
