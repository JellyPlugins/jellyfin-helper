using System.Net;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class ArrIntegrationControllerTests : IDisposable
{
    private readonly ArrIntegrationController _controller;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ICleanupConfigHelper> _configHelperMock;
    private readonly string _tempPath;

    public ArrIntegrationControllerTests()
    {
        var tempDirectoryName = "JellyfinHelperArrTests_" + Guid.NewGuid();
        _tempPath = Path.Join(Path.GetTempPath(), tempDirectoryName);
        Directory.CreateDirectory(_tempPath);

        (_controller, _libraryManagerMock, _fileSystemMock, _httpClientFactoryMock, _configHelperMock) = ControllerTestFactory.CreateArrIntegrationController();

        _configHelperMock.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, true);
        }
    }

    [Fact]
    public async Task TestArrConnectionAsync_ValidConnection_ReturnsSuccess()
    {
        var request = new ArrTestConnectionRequest { Url = "http://localhost:8989", ApiKey = "valid-api-key" };
        var handlerMock = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.OK, "{\"version\": \"1.0\"}");
        using var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.TestArrConnectionAsync(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ConnectionTestResponse>(okResult.Value);
        Assert.True(payload.Success);
    }

    [Theory]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://metadata.google.internal")]
    [InlineData("http://100.100.100.200")]
    public async Task TestArrConnectionAsync_CloudMetadataHost_ReturnsBadRequestWithoutRequest(string url)
    {
        // SSRF guard at the controller: metadata endpoints are rejected before any HTTP client is used.
        var handlerMock = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.OK, "{}");
        using var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var request = new ArrTestConnectionRequest { Url = url, ApiKey = "key" };
        var result = await _controller.TestArrConnectionAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task TestArrConnectionAsync_InvalidConnection_ReturnsFailure()
    {
        var request = new ArrTestConnectionRequest { Url = "http://localhost:8989", ApiKey = "invalid-api-key" };
        var handlerMock = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.Unauthorized, "Unauthorized");
        using var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.TestArrConnectionAsync(request, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, statusResult.StatusCode);
        var payload = Assert.IsType<ConnectionTestResponse>(statusResult.Value);
        Assert.False(payload.Success);
        Assert.False(string.IsNullOrWhiteSpace(payload.Message));
    }

    [Fact]
    public async Task CompareRadarrAsync_NoInstancesConfigured_ReturnsBadRequest()
    {
        _configHelperMock.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());
        // RadarrInstances is empty by default

        var result = await _controller.CompareRadarrAsync(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CompareRadarrAsync_ValidComparison_ReturnsResult()
    {
        var libPath = Path.Join(_tempPath, "Movies");
        Directory.CreateDirectory(libPath);
        var movieDir = Path.Join(libPath, "Movie1");
        Directory.CreateDirectory(movieDir);

        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://localhost:7878", ApiKey = "key", Name = "Radarr" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>())).Returns(Path.Join(libPath, ".jellyfin-trash"));

        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Movies", Locations = [libPath], CollectionType = CollectionTypeOptions.movies }
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(folders);

        var dirMock = new FileSystemMetadata { Name = "Movie1", FullName = movieDir, IsDirectory = true };
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), It.IsAny<bool>())).Returns([dirMock]);

        var handlerMock = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.OK, "[{\"title\": \"Movie1\", \"path\": \"/movies/Movie1\", \"hasFile\": true}]");
        using var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareRadarrAsync(null, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(okResult.Value);
        Assert.Single(data.InBoth);
        Assert.Equal("Movie1", data.InBoth[0]);
    }

    // ===== T1: SSRF scheme validation =====

    [Theory]
    [InlineData("ftp://internal-server/api")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ldap://192.168.1.1")]
    [InlineData("gopher://evil.com")]
    [InlineData("javascript:alert(1)")]
    public async Task TestArrConnectionAsync_InvalidScheme_Returns400(string url)
    {
        var request = new ArrTestConnectionRequest { Url = url, ApiKey = "some-key" };

        var result = await _controller.TestArrConnectionAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("http://localhost:8989")]
    [InlineData("http://127.0.0.1:7878")]
    [InlineData("https://radarr.example.com")]
    public async Task TestArrConnectionAsync_ValidHttpUrl_Passes(string url)
    {
        var request = new ArrTestConnectionRequest { Url = url, ApiKey = "valid-api-key" };
        var handlerMock = TestMockFactory.CreateHttpMessageHandler(
            System.Net.HttpStatusCode.OK, "{\"version\": \"1.0\"}");
        using var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.TestArrConnectionAsync(request, CancellationToken.None);

        // Must not return 400 - the request reached the service layer
        Assert.IsNotType<BadRequestObjectResult>(result);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ConnectionTestResponse>(okResult.Value);
        Assert.True(payload.Success);
    }

    [Fact]
    public async Task TestArrConnectionAsync_EmptyUrl_Returns400()
    {
        // Empty URL fails URI parsing - the SSRF guard returns 400 before reaching the service layer
        var request = new ArrTestConnectionRequest { Url = "", ApiKey = "key" };

        var result = await _controller.TestArrConnectionAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CompareSonarrAsync_NoInstancesConfigured_ReturnsBadRequest()
    {
        _configHelperMock.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());

        var result = await _controller.CompareSonarrAsync(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CompareSonarrAsync_ValidComparison_ReturnsResult()
    {
        var libPath = Path.Join(_tempPath, "TVShows");
        Directory.CreateDirectory(libPath);
        var showDir = Path.Join(libPath, "Show1");
        Directory.CreateDirectory(showDir);

        var config = new PluginConfiguration();
        config.SonarrInstances.Add(new ArrInstanceConfig { Url = "http://localhost:8989", ApiKey = "key", Name = "Sonarr" });
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>())).Returns(Path.Join(libPath, ".jellyfin-trash"));

        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "TVShows", Locations = [libPath], CollectionType = CollectionTypeOptions.tvshows }
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(folders);

        var dirMock = new FileSystemMetadata { Name = "Show1", FullName = showDir, IsDirectory = true };
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), It.IsAny<bool>())).Returns([dirMock]);

        var handlerMock = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.OK, "[{\"title\": \"Show1\", \"path\": \"/tv/Show1\", \"statistics\": {\"episodeFileCount\": 5, \"totalEpisodeCount\": 10}}]");
        using var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.CompareSonarrAsync(null, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<ArrComparisonResult>(okResult.Value);
        Assert.Single(data.InBoth);
        Assert.Equal("Show1", data.InBoth[0]);
    }
}