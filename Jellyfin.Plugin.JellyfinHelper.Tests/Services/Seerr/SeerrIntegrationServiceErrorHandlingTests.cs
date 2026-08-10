using System.Net;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr;

/// <summary>
///     Cancellation, timeout, and fail-closed error-path tests for
///     <see cref="SeerrIntegrationService" />. These pin the distinction between a caller
///     cancellation (must rethrow) and an HTTP-client timeout (must be counted as a failure and
///     never propagate), plus the header-injection guard on a control-char API key.
/// </summary>
public sealed class SeerrIntegrationServiceErrorHandlingTests : IDisposable
{
    private const string BaseUrl = "http://localhost:5055";
    private const string ApiKey = "test-api-key-123";

    private readonly List<HttpResponseMessage> _trackedResponses = [];
    private readonly List<HttpClient> _trackedClients = [];

    private SeerrIntegrationService CreateService(
        HttpMessageHandler handler,
        out Mock<ILogger<SeerrIntegrationService>> loggerMock,
        out Mock<IPluginLogService> pluginLogMock)
    {
        loggerMock = new Mock<ILogger<SeerrIntegrationService>>();
        pluginLogMock = new Mock<IPluginLogService>();
        var httpClient = new HttpClient(handler, disposeHandler: false);
        _trackedClients.Add(httpClient);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("SeerrIntegration")).Returns(httpClient);
        return new SeerrIntegrationService(factoryMock.Object, pluginLogMock.Object, loggerMock.Object);
    }

    private HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string content)
    {
        var response = new HttpResponseMessage
        {
            StatusCode = statusCode,
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
        _trackedResponses.Add(response);
        return response;
    }

    private Mock<HttpMessageHandler> CreateMockHandler(
        HttpStatusCode statusCode,
        string content)
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(CreateResponse(statusCode, content));
        return mock;
    }

    private Mock<HttpMessageHandler> CreateSequenceHandler(
        params (HttpStatusCode Code, string Content)[] responses)
    {
        var mock = new Mock<HttpMessageHandler>();
        var seq = mock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        foreach (var (code, content) in responses)
        {
            seq.ReturnsAsync(CreateResponse(code, content));
        }

        return mock;
    }

    private static string MakeRequestPage(
        List<(int Id, DateTimeOffset CreatedAt)> requests,
        int totalResults,
        int page = 1,
        int pages = 1)
    {
        var results = requests.Select(r => new
        {
            id = r.Id,
            createdAt = r.CreatedAt.ToString("O"),
            status = 1,
            media = new { mediaType = "movie", tmdbId = r.Id * 100, status = 1 }
        }).ToList();

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            pageInfo = new { page, pages, results = totalResults, pageSize = 50 },
            results
        });
    }

    private static string MakeMovieDetails(string title) =>
        System.Text.Json.JsonSerializer.Serialize(new { title, name = (string?)null });

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var response in _trackedResponses)
        {
            response.Dispose();
        }

        foreach (var client in _trackedClients)
        {
            client.Dispose();
        }
    }

    // ===== Caller cancellation must rethrow, never degrade to a false/"Unknown" result =====

    [Fact]
    public async Task TestConnection_CancelledMidFlight_PropagatesOperationCanceled()
    {
        // The settings GET observes a caller cancellation mid-flight. The
        // when(cancellationToken.IsCancellationRequested) filter must rethrow so callers can
        // distinguish a genuine cancel from a "Connection failed" network error.
        using var cts = new CancellationTokenSource();
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var service = CreateService(mock.Object, out _, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TestConnectionAsync(BaseUrl, ApiKey, cts.Token));
    }

    [Fact]
    public async Task Cleanup_CancelledDuringPageFetch_Rethrows()
    {
        // First-page GET gets cancelled mid-send. The loop's cancellation catch must rethrow
        // rather than mask the cancel as a counted page failure.
        using var cts = new CancellationTokenSource();
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var service = CreateService(mock.Object, out _, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CleanupExpiredRequestsAsync(BaseUrl, ApiKey, 365, false, cts.Token));
    }

    [Fact]
    public async Task Cleanup_PageFetchTimesOut_MarksFailedAndBreaks()
    {
        // An HTTP-client timeout (TaskCanceledException while the caller token is NOT cancelled)
        // is a page failure, not a cancel: it must count Failed, trip the phaseOneFailed
        // circuit-breaker so no deletion happens, and log a "Timed out" warning.
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("The request timed out."));

        var service = CreateService(mock.Object, out _, out var pluginLogMock);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Deleted);

        pluginLogMock.Verify(
            x => x.LogWarning(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("Timed out")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public async Task Cleanup_PageDeserializesToNull_MarksFailedAndBreaks()
    {
        // A 200 OK whose entire body is JSON null deserializes to a null page. This must fail closed:
        // count Failed, skip deletion, and log a "null response" warning - never treat a null page as
        // "nothing to clean".
        var json = "null";
        var handler = CreateMockHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler.Object, out _, out var pluginLogMock);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Deleted);

        pluginLogMock.Verify(
            x => x.LogWarning(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("null response")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public async Task Cleanup_TwoExpiredSameTmdbId_ResolvesTitleOnceViaCache()
    {
        // Two expired pending requests share the same mediaType:tmdbId. The per-run title cache
        // must serve the second from memory so only one movie-detail GET is issued - redundant
        // Seerr API calls for an identical key defeat the whole point of the cache.
        var created = DateTimeOffset.UtcNow.AddDays(-400).ToString("O");
        var page = "{\"pageInfo\":{\"page\":1,\"pages\":1,\"results\":2,\"pageSize\":50},\"results\":["
            + "{\"id\":1,\"createdAt\":\"" + created + "\",\"status\":1,\"media\":{\"mediaType\":\"movie\",\"tmdbId\":100,\"status\":1}},"
            + "{\"id\":2,\"createdAt\":\"" + created + "\",\"status\":1,\"media\":{\"mediaType\":\"movie\",\"tmdbId\":100,\"status\":1}}"
            + "]}";

        // GET page -> single movie-detail GET (cache hit for the 2nd) -> two DELETEs.
        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.OK, MakeMovieDetails("Shared Movie")),
            (HttpStatusCode.NoContent, string.Empty),
            (HttpStatusCode.NoContent, string.Empty));

        var service = CreateService(handler.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(2, result.ExpiredFound);

        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().Contains("api/v1/movie/")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task Cleanup_DeleteTimesOut_CountsFailure_ContinuesDelay()
    {
        // A DELETE that times out (TaskCanceledException, caller token NOT cancelled) must be
        // counted as a failure and logged, never propagated out of the cleanup loop.
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400))
        };
        var page = MakeRequestPage(requests, 1);

        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(CreateResponse(HttpStatusCode.OK, page))
            .ReturnsAsync(CreateResponse(HttpStatusCode.OK, MakeMovieDetails("Timeout Movie")))
            .ThrowsAsync(new TaskCanceledException("delete timed out"));

        var service = CreateService(mock.Object, out _, out var pluginLogMock);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Failed);

        pluginLogMock.Verify(
            x => x.LogWarning(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("timeout")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public async Task Cleanup_DeleteThrowsHttpRequestException_CountsFailure()
    {
        // A network error on DELETE must count as a failure and be logged with the exception
        // message, not propagate up and abort the run.
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400))
        };
        var page = MakeRequestPage(requests, 1);

        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(CreateResponse(HttpStatusCode.OK, page))
            .ReturnsAsync(CreateResponse(HttpStatusCode.OK, MakeMovieDetails("Broken Delete")))
            .ThrowsAsync(new HttpRequestException("connection reset"));

        var service = CreateService(mock.Object, out _, out var pluginLogMock);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Failed);

        pluginLogMock.Verify(
            x => x.LogWarning(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("connection reset")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public async Task Cleanup_CancelledDuringInterDeleteDelay_BreaksLoop()
    {
        // The DELETE succeeds but the token is cancelled during that send, so the inter-delete
        // Task.Delay throws. That catch must BREAK the loop and return the partial result
        // (Deleted=1) rather than throw out of the method.
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400))
        };
        var page = MakeRequestPage(requests, 1);

        using var cts = new CancellationTokenSource();
        var callCount = 0;
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(CreateResponse(HttpStatusCode.OK, page));
                }

                if (callCount == 2)
                {
                    return Task.FromResult(CreateResponse(HttpStatusCode.OK, MakeMovieDetails("Delayed Movie")));
                }

                // DELETE succeeds, but cancel the token so the subsequent Task.Delay(100, ct) throws.
                cts.Cancel();
                return Task.FromResult(CreateResponse(HttpStatusCode.NoContent, string.Empty));
            });

        var service = CreateService(mock.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, cts.Token);

        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public async Task ResolveMediaTitleAsync_CancelledMidRequest_Rethrows()
    {
        // Direct helper call: the detail GET is cancelled mid-flight. The
        // when(IsCancellationRequested) filter must rethrow rather than degrade to "Unknown".
        using var cts = new CancellationTokenSource();
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var service = CreateService(mock.Object, out _, out _);
        using var httpClient = new HttpClient(mock.Object);
        var baseUri = new Uri(BaseUrl + "/");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ResolveMediaTitleAsync(
                httpClient,
                baseUri,
                ApiKey,
                new SeerrMedia { MediaType = "movie", TmdbId = 42 },
                cts.Token));
    }

    // ===== Header-injection guard on the API key =====

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cleanup_ApiKeyWithControlChars_MarksFailure_NoHttpCall()
    {
        // A CR/LF-laced API key is a header-injection vector. The control-char guard must reject
        // it before any request leaves the plugin: map to Failed=1 with nothing checked, and never
        // touch the handler.
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Should not be called"));

        var service = CreateService(mock.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, "key\r\nX-Injected: evil", 365, false, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.TotalChecked);

        mock.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }
}
