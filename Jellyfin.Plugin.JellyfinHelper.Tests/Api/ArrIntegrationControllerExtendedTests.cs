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
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Branch-coverage extensions for ArrIntegrationController covering paths that ArrIntegrationControllerTests left
///     like the index parameter (valid + invalid range), the failedInstances 502 path, the empty Url/ApiKey skip, and the trash-folder skip in GetJellyfinFolderNames.
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

    private void ConfigWithRadarr(params (string Url, string Key, string Name)[] instances)
    {
        var config = new PluginConfiguration();
        foreach (var (url, key, name) in instances)
        {
            config.RadarrInstances.Add(new ArrInstanceConfig { Url = url, ApiKey = key, Name = name });
        }
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);
    }

    private void ConfigWithSonarr(params (string Url, string Key, string Name)[] instances)
    {
        var config = new PluginConfiguration();
        foreach (var (url, key, name) in instances)
        {
            config.SonarrInstances.Add(new ArrInstanceConfig { Url = url, ApiKey = key, Name = name });
        }
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);
    }

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

    [Fact]
    public async Task CompareRadarrAsync_AllInstancesHaveEmptyUrl_ReturnsBadRequest()
    {
        // DESIGN CONTRACT: PluginConfiguration.GetEffectiveRadarrInstances() filters out instances with empty Url or ApiKey BEFORE the controller sees them.
        ConfigWithRadarr(("", "k", "Partial"));

        var result = await _controller.CompareRadarrAsync(null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains(
            "At least one Radarr instance",
            JsonSerializer.Serialize(bad.Value),
            StringComparison.Ordinal);
    }

    // Radarr: failed instance -> 502 with instance name

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

    // Radarr: GetDirectories throws -> swallowed

    [Fact]
    public async Task CompareRadarrAsync_GetDirectoriesThrowsIOException_IsSwallowed()
    {
        // IOException from a filesystem enumeration on one library location must NOT fail the entire comparison.
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
        // Mirrors CompareRadarrAsync_AllInstancesHaveEmptyUrl_ReturnsBadRequest - the GetEffectiveSonarrInstances filter drops the partial instance, leaving Count==0 which the controller reports as 400 BadRequest.
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
        // Twin of CompareRadarrAsync_ValidIndex_UsesOnlyThatInstance: with two instances configured, a valid index must narrow the working set to that single instance (line 207) rather than merging all.
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

    // Routes responses by request path so a single client can answer both the movie fetch and the
    // root-folder fetch that the auto-match path issues.
    private static Mock<HttpMessageHandler> CreateRoutingHandler(string movieJson, string rootFolderJson)
    {
        var mock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mock.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var isRootFolder = req.RequestUri != null
                    && req.RequestUri.AbsolutePath.Contains("rootfolder", StringComparison.OrdinalIgnoreCase);
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(isRootFolder ? rootFolderJson : movieJson)
                };
            });
        return mock;
    }

    private void SetupTwoMovieLibraries(out string fourKPath, out string hdPath)
    {
        var fourK = Path.Join(_tempPath, "Movies4K");
        var hd = Path.Join(_tempPath, "Movies1080p");
        Directory.CreateDirectory(fourK);
        Directory.CreateDirectory(hd);
        var fourKDir = Path.Join(fourK, "MovieA");
        var hdDir = Path.Join(hd, "MovieB");
        Directory.CreateDirectory(fourKDir);
        Directory.CreateDirectory(hdDir);

        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>())).Returns("__no_trash__");
        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Movies 4K", Locations = [fourK], CollectionType = CollectionTypeOptions.movies },
                new VirtualFolderInfo { Name = "Movies 1080p", Locations = [hd], CollectionType = CollectionTypeOptions.movies }
            ]);

        var fourKMeta = new FileSystemMetadata { Name = "MovieA", FullName = fourKDir, IsDirectory = true };
        var hdMeta = new FileSystemMetadata { Name = "MovieB", FullName = hdDir, IsDirectory = true };
        _fileSystemMock.Setup(f => f.GetDirectories(fourK, It.IsAny<bool>())).Returns([fourKMeta]);
        _fileSystemMock.Setup(f => f.GetDirectories(hd, It.IsAny<bool>())).Returns([hdMeta]);

        fourKPath = fourK;
        hdPath = hd;
    }

    [Fact]
    public async Task CompareRadarrAsync_ManualOverride_ScopesToAssignedLibraryOnly()
    {
        SetupTwoMovieLibraries(out _, out _);
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r1", ApiKey = "k1", Name = "4K", Libraries = "Movies 4K" });
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r2", ApiKey = "k2", Name = "HD" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);

        // Radarr 4K has MovieA; MovieB (1080p library) must NOT surface as InJellyfinOnly.
        var handler = CreateRoutingHandler("[{\"title\":\"MovieA\",\"path\":\"/m/MovieA\",\"hasFile\":true}]", "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        Assert.Single(data.InBoth);
        Assert.Contains("MovieA", data.InBoth);
        Assert.DoesNotContain("MovieB", data.InJellyfinOnly);
        Assert.Empty(data.InJellyfinOnly);
    }

    [Fact]
    public async Task CompareRadarrAsync_AutoMatchByRootFolder_ScopesToMatchedLibrary()
    {
        SetupTwoMovieLibraries(out var fourKPath, out _);
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r1", ApiKey = "k1", Name = "4K" });
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r2", ApiKey = "k2", Name = "HD" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);

        // No override: the instance's root folder points at the 4K library location, so only that
        // library is compared and the 1080p MovieB stays out of InJellyfinOnly.
        var rootJson = "[{\"path\":\"" + fourKPath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}]";
        var handler = CreateRoutingHandler("[{\"title\":\"MovieA\",\"path\":\"/m/MovieA\",\"hasFile\":true}]", rootJson);
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        Assert.Single(data.InBoth);
        Assert.DoesNotContain("MovieB", data.InJellyfinOnly);
    }

    [Fact]
    public async Task CompareRadarrAsync_AutoMatchNoRootFolders_FallsBackToAllLibraries()
    {
        SetupTwoMovieLibraries(out _, out _);
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r1", ApiKey = "k1", Name = "4K" });
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r2", ApiKey = "k2", Name = "HD" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);

        // Empty root folders -> no scoping -> both libraries compared, so MovieB is InJellyfinOnly.
        var handler = CreateRoutingHandler("[{\"title\":\"MovieA\",\"path\":\"/m/MovieA\",\"hasFile\":true}]", "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        Assert.Single(data.InBoth);
        Assert.Contains("MovieB", data.InJellyfinOnly);
    }

    [Fact]
    public async Task CompareRadarrAsync_SingleInstanceWithOverride_ComparesAllLibraries()
    {
        SetupTwoMovieLibraries(out _, out _);
        var config = new PluginConfiguration();
        // A single instance owns everything of its type; an override is ignored so both libraries compare.
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r1", ApiKey = "k1", Name = "Only", Libraries = "Movies 4K" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);

        var handler = CreateRoutingHandler("[{\"title\":\"MovieA\",\"path\":\"/m/MovieA\",\"hasFile\":true}]", "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        Assert.Contains("MovieB", data.InJellyfinOnly);
    }

    [Fact]
    public async Task CompareRadarrAsync_NoLibrariesAssignedNoRootFolders_ComparesAllLibraries()
    {
        SetupTwoMovieLibraries(out _, out _);
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r1", ApiKey = "k1", Name = "4K" });
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r2", ApiKey = "k2", Name = "HD" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);

        var handler = CreateRoutingHandler("[{\"title\":\"MovieA\",\"path\":\"/m/MovieA\",\"hasFile\":true}]", "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        // Both libraries in scope: MovieA matches, MovieB is Jellyfin-only.
        Assert.Single(data.InBoth);
        Assert.Contains("MovieB", data.InJellyfinOnly);
    }

    [Fact]
    public async Task CompareRadarrAsync_AllLibrariesAssigned_ComparesEveryLibrary()
    {
        SetupTwoMovieLibraries(out _, out _);
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r1", ApiKey = "k1", Name = "4K", Libraries = "Movies 4K, Movies 1080p" });
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r2", ApiKey = "k2", Name = "HD" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);

        var handler = CreateRoutingHandler("[{\"title\":\"MovieA\",\"path\":\"/m/MovieA\",\"hasFile\":true}]", "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        Assert.Single(data.InBoth);
        Assert.Contains("MovieB", data.InJellyfinOnly);
    }

    [Fact]
    public async Task CompareRadarrAsync_PartialLibrariesAssigned_ScopesToThoseOnly()
    {
        SetupTwoMovieLibraries(out _, out _);
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r1", ApiKey = "k1", Name = "4K", Libraries = "Movies 4K" });
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r2", ApiKey = "k2", Name = "HD" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);

        var handler = CreateRoutingHandler("[{\"title\":\"MovieA\",\"path\":\"/m/MovieA\",\"hasFile\":true}]", "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        // Only the 4K library is in scope, so the 1080p MovieB is not reported.
        Assert.Single(data.InBoth);
        Assert.Empty(data.InJellyfinOnly);
    }

    [Fact]
    public async Task CompareRadarrAsync_OverrideNamingTvLibrary_IsIgnoredByCollectionTypeFilter()
    {
        // A Radarr override that names a TV library resolves to a name that matches no movie library,
        // so GetJellyfinFolderNames filters it out and nothing is compared.
        var tvPath = Path.Join(_tempPath, "Anime");
        Directory.CreateDirectory(tvPath);
        Directory.CreateDirectory(Path.Join(tvPath, "ShowX"));
        var moviePath = Path.Join(_tempPath, "Movies");
        Directory.CreateDirectory(moviePath);
        Directory.CreateDirectory(Path.Join(moviePath, "MovieA"));

        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>())).Returns("__no_trash__");
        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Anime Series", Locations = [tvPath], CollectionType = CollectionTypeOptions.tvshows },
                new VirtualFolderInfo { Name = "Movies", Locations = [moviePath], CollectionType = CollectionTypeOptions.movies }
            ]);
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns([new FileSystemMetadata { Name = "MovieA", FullName = Path.Join(moviePath, "MovieA"), IsDirectory = true }]);

        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r1", ApiKey = "k1", Name = "4K", Libraries = "Anime Series" });
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://r2", ApiKey = "k2", Name = "HD" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);

        var handler = CreateRoutingHandler("[{\"title\":\"MovieA\",\"path\":\"/m/MovieA\",\"hasFile\":true}]", "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        // TV library filtered out by collection type; no movie library in scope.
        Assert.Empty(data.InBoth);
        Assert.Empty(data.InJellyfinOnly);
    }

    [Fact]
    public async Task CompareSonarrAsync_ManyTvLibraries_PartialAssignmentScopesCorrectly()
    {
        // A user may keep several TV libraries of the same collection type (TV Shows, TV Shows 4K,
        // Anime, Reality TV). Assigning only some to an instance must scope the compare to those.
        var names = new[] { "TV Shows", "TV Shows 4K", "Anime", "Reality TV" };
        var folders = new List<VirtualFolderInfo>();
        foreach (var name in names)
        {
            var path = Path.Join(_tempPath, name.Replace(" ", string.Empty, StringComparison.Ordinal));
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Join(path, "Show_" + name.Replace(" ", string.Empty, StringComparison.Ordinal)));
            folders.Add(new VirtualFolderInfo { Name = name, Locations = [path], CollectionType = CollectionTypeOptions.tvshows });
        }

        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>())).Returns("__no_trash__");
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(folders);
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, bool>((loc, _) =>
            {
                var leaf = Path.GetFileName(loc.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return [new FileSystemMetadata { Name = "Show_" + leaf, FullName = Path.Join(loc, "Show_" + leaf), IsDirectory = true }];
            });

        var config = new PluginConfiguration();
        config.SonarrInstances.Add(new ArrInstanceConfig { Url = "http://s1", ApiKey = "k1", Name = "Anime+4K", Libraries = "Anime, TV Shows 4K" });
        config.SonarrInstances.Add(new ArrInstanceConfig { Url = "http://s2", ApiKey = "k2", Name = "Rest" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);

        var handler = CreateRoutingHandler("[]", "[]");
        using var httpClient = new HttpClient(handler.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareSonarrAsync(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(ok.Value);
        // Only the two assigned libraries are in scope; their folders are Jellyfin-only (no series returned).
        Assert.Equal(2, data.InJellyfinOnly.Count);
        Assert.Contains("Show_Anime", data.InJellyfinOnly);
        Assert.Contains("Show_TVShows4K", data.InJellyfinOnly);
        Assert.DoesNotContain("Show_RealityTV", data.InJellyfinOnly);
        Assert.DoesNotContain("Show_TVShows", data.InJellyfinOnly);
    }
}
