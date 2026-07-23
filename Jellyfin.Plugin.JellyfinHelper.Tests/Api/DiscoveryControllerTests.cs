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
///     Tests for <see cref="DiscoveryController"/> (admin-level discovery endpoints).
///     The <see cref="DiscoveryCacheService"/> is instantiated once per test class.
///     Current tests are stateless (no test mutates the cache), so shared state is safe.
///     If future tests add cache-mutating scenarios, consider per-test isolation
///     via a temp-directory-backed cache or mocked <c>IDiscoveryCacheService</c>.
/// </summary>
public class DiscoveryControllerTests
{
    private readonly Mock<ISeerrDiscoveryService> _discoveryMock;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock;
    private readonly DiscoveryCacheService _cache;

    public DiscoveryControllerTests()
    {
        var pluginLog = new Mock<IPluginLogService>();
        var cacheLogger = new Mock<ILogger<DiscoveryCacheService>>();
        _cache = new DiscoveryCacheService(pluginLog.Object, cacheLogger.Object, filePath: Path.GetTempFileName());
        _discoveryMock = new Mock<ISeerrDiscoveryService>();
        _feedbackStoreMock = new Mock<IDiscoveryFeedbackStore>();
    }

    private DiscoveryController CreateController(Mock<ISeerrDiscoveryService>? discovery = null)
    {
        var disc = discovery ?? _discoveryMock;
        return new DiscoveryController(_cache, disc.Object, _feedbackStoreMock.Object);
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
        // then delegates to the mocked Seerr service which returns success → 200 OK.
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
