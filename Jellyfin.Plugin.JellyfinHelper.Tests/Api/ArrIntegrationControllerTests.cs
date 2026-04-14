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
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

[Collection("ConfigOverride")]
public class ArrIntegrationControllerTests : IDisposable
{
    private readonly ArrIntegrationController _controller;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly string _tempPath;

    public ArrIntegrationControllerTests()
    {
        var tempDirectoryName = "JellyfinHelperArrTests_" + Guid.NewGuid();
        _tempPath = Path.Combine(Path.GetTempPath(), tempDirectoryName);
        Directory.CreateDirectory(_tempPath);

        (_controller, _libraryManagerMock, _fileSystemMock, _httpClientFactoryMock) = ControllerTestFactory.CreateArrIntegrationController();

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

    [Fact]
    public async Task TestArrConnectionAsync_ValidConnection_ReturnsSuccess()
    {
        var request = new ArrTestConnectionRequest { Url = "http://localhost:8989", ApiKey = "valid-api-key" };
        var handlerMock = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.OK, "{\"version\": \"1.0\"}");
        using var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.TestArrConnectionAsync(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(okResult.Value));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task TestArrConnectionAsync_InvalidConnection_ReturnsFailure()
    {
        var request = new ArrTestConnectionRequest { Url = "http://localhost:8989", ApiKey = "invalid-api-key" };
        var handlerMock = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.Unauthorized, "Unauthorized");
        using var httpClient = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);

        var result = await _controller.TestArrConnectionAsync(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(okResult.Value));
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Unauthorized", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CompareRadarrAsync_NoInstancesConfigured_ReturnsBadRequest()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration();
        // RadarrInstances is empty by default

        var result = await _controller.CompareRadarrAsync(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CompareRadarrAsync_ValidComparison_ReturnsResult()
    {
        var libPath = Path.Combine(_tempPath, "Movies");
        Directory.CreateDirectory(libPath);
        var movieDir = Path.Combine(libPath, "Movie1");
        Directory.CreateDirectory(movieDir);

        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Url = "http://localhost:7878", ApiKey = "key", Name = "Radarr" });
        CleanupConfigHelper.ConfigOverride = config;

        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Movies", Locations = [libPath], CollectionType = CollectionTypeOptions.movies }
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(folders);
        
        var dirMock = new FileSystemMetadata { Name = "Movie1", IsDirectory = true };
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

    [Fact]
    public async Task CompareSonarrAsync_NoInstancesConfigured_ReturnsBadRequest()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration();

        var result = await _controller.CompareSonarrAsync(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CompareSonarrAsync_ValidComparison_ReturnsResult()
    {
        var libPath = Path.Combine(_tempPath, "TVShows");
        Directory.CreateDirectory(libPath);
        var showDir = Path.Combine(libPath, "Show1");
        Directory.CreateDirectory(showDir);

        var config = new PluginConfiguration();
        config.SonarrInstances.Add(new ArrInstanceConfig { Url = "http://localhost:8989", ApiKey = "key", Name = "Sonarr" });
        CleanupConfigHelper.ConfigOverride = config;

        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "TVShows", Locations = [libPath], CollectionType = CollectionTypeOptions.tvshows }
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns(folders);

        var dirMock = new FileSystemMetadata { Name = "Show1", IsDirectory = true };
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
