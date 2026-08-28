using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for DiscoveryController (admin-level discovery endpoints). The DiscoveryCacheService is instantiated once per test class.
/// </summary>
public class DiscoveryControllerTests : IDisposable
{
    private readonly Mock<ISeerrDiscoveryService> _discoveryMock;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock;
    private readonly DiscoveryCacheService _cache;
    private readonly string _tempCachePath;

    public DiscoveryControllerTests()
    {
        var pluginLog = new Mock<IPluginLogService>();
        var cacheLogger = new Mock<ILogger<DiscoveryCacheService>>();
        _tempCachePath = Path.GetTempFileName();
        _cache = new DiscoveryCacheService(pluginLog.Object, cacheLogger.Object, filePath: _tempCachePath);
        _discoveryMock = new Mock<ISeerrDiscoveryService>();
        _feedbackStoreMock = new Mock<IDiscoveryFeedbackStore>();
    }

    public void Dispose()
    {
        _cache.Dispose();
        try { File.Delete(_tempCachePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private DiscoveryController CreateController(Mock<ISeerrDiscoveryService>? discovery = null, Guid? userId = null)
    {
        var disc = discovery ?? _discoveryMock;
        var controller = new DiscoveryController(_cache, disc.Object, _feedbackStoreMock.Object, new Mock<ILogger<DiscoveryController>>().Object);
        var claims = new System.Collections.Generic.List<System.Security.Claims.Claim>();
        var id = userId ?? Guid.NewGuid();
        claims.Add(new System.Security.Claims.Claim("Jellyfin-UserId", id.ToString()));
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "Test"))
            }
        };
        return controller;
    }

    [Fact]
    public void Get_ReturnsOkWithResults()
    {
        var controller = CreateController();
        var result = controller.GetDiscoveryResults();
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task PostRequest_InvalidTmdbId_ReturnsBadRequest()
    {
        var controller = CreateController();
        var dto = new DiscoveryRequestDto { TmdbId = 0, MediaType = "movie" };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task PostRequest_InvalidMediaType_ReturnsBadRequest()
    {
        var controller = CreateController();
        var dto = new DiscoveryRequestDto { TmdbId = 123, MediaType = "invalid" };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task PostRequest_NullDto_ReturnsBadRequest()
    {
        var controller = CreateController();
        var result = await controller.SubmitRequest(null!, CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var body = Assert.IsType<RequestResult>(badRequest.Value);
        Assert.Contains("body", body.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostRequest_RootFolderTooLong_ReturnsBadRequest()
    {
        var controller = CreateController();
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = new string('a', 600)
        };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var body = Assert.IsType<RequestResult>(badRequest.Value);
        Assert.Contains("length", body.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostRequest_RootFolderWithPathTraversal_ReturnsBadRequest()
    {
        var controller = CreateController();
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = "/media/../etc/passwd"
        };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task PostRequest_RootFolderWithTilde_ReturnsBadRequest()
    {
        var controller = CreateController();
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = "~/movies"
        };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task PostRequest_RootFolderWithControlChars_ReturnsBadRequest()
    {
        var controller = CreateController();
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = "/media/\0movies"
        };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var body = Assert.IsType<RequestResult>(badRequest.Value);
        Assert.Contains("invalid characters", body.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostRequest_CaseInsensitiveMediaType_Accepted()
    {
        var discovery = new Mock<ISeerrDiscoveryService>();
        discovery.Setup(d => d.SubmitRequestAsync(
            It.IsAny<int>(), "movie", It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "OK"));

        var controller = CreateController(discovery);
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "Movie" };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);

        // The controller normalizes "Movie" to "movie" so validation passes,
        // then delegates to the mocked Seerr service which returns success -> 200 OK.
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<RequestResult>(okResult.Value);
        Assert.True(body.Success);

        // Verify the normalized value "movie" (not "Movie") was forwarded to the service
        discovery.Verify(d => d.SubmitRequestAsync(
            100, "movie", It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostRequest_SubmissionFailure_Returns502()
    {
        var discovery = new Mock<ISeerrDiscoveryService>();
        discovery.Setup(d => d.SubmitRequestAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Seerr returned HTTP 409: Already requested"));

        var controller = CreateController(discovery);
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "movie" };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, statusResult.StatusCode);
        var body = Assert.IsType<RequestResult>(statusResult.Value);
        Assert.False(body.Success);
        Assert.Contains("409", body.Message);
    }
}
