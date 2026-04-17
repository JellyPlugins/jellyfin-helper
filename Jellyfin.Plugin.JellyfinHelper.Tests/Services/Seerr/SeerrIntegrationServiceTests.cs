using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr;

/// <summary>
///     Comprehensive tests for <see cref="SeerrIntegrationService" />.
/// </summary>
public class SeerrIntegrationServiceTests : IDisposable
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
            status = 2,
            media = new { mediaType = "movie", tmdbId = r.Id * 100, status = 5 }
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            pageInfo = new { page, pages, results = totalResults, pageSize = 50 },
            results
        });
    }

    private static string MakeMovieDetails(string title) =>
        JsonSerializer.Serialize(new { title, name = (string?)null });

    private static string MakeTvDetails(string name) =>
        JsonSerializer.Serialize(new { title = (string?)null, name });

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

    // ===== TestConnectionAsync =====

    [Fact]
    public async Task TestConnection_Success_ReturnsTrueWithTitle()
    {
        var handler = CreateMockHandler(
            HttpStatusCode.OK,
            "{\"applicationTitle\":\"My Jellyseerr\"}");

        var service = CreateService(handler.Object, out _, out _);
        var (success, message) = await service.TestConnectionAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.True(success);
        Assert.Contains("My Jellyseerr", message);
    }

    [Fact]
    public async Task TestConnection_EmptyTitle_ReturnsSeerrFallback()
    {
        var handler = CreateMockHandler(
            HttpStatusCode.OK,
            "{\"applicationTitle\":\"\"}");

        var service = CreateService(handler.Object, out _, out _);
        var (success, message) = await service.TestConnectionAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.True(success);
        Assert.Contains("Seerr", message);
    }

    [Fact]
    public async Task TestConnection_NullTitle_ReturnsSeerrFallback()
    {
        var handler = CreateMockHandler(
            HttpStatusCode.OK,
            "{}");

        var service = CreateService(handler.Object, out _, out _);
        var (success, message) = await service.TestConnectionAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.True(success);
        Assert.Contains("Seerr", message);
    }

    [Fact]
    public async Task TestConnection_HttpError_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.Unauthorized, "");

        var service = CreateService(handler.Object, out _, out _);
        var (success, message) = await service.TestConnectionAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("401", message);
    }

    [Fact]
    public async Task TestConnection_NetworkError_ReturnsFalse()
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var service = CreateService(mock.Object, out _, out _);
        var (success, message) = await service.TestConnectionAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("Connection refused", message);
    }

    [Fact]
    public async Task TestConnection_SetsApiKeyHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var response = CreateResponse(
            HttpStatusCode.OK,
            "{\"applicationTitle\":\"Test\"}");
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(response);

        var service = CreateService(mock.Object, out _, out _);
        await service.TestConnectionAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.Contains("X-Api-Key"));
        Assert.Contains(ApiKey, capturedRequest.Headers.GetValues("X-Api-Key"));
    }

    [Fact]
    public async Task TestConnection_CallsCorrectEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        var response = CreateResponse(
            HttpStatusCode.OK,
            "{}");
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(response);

        var service = CreateService(mock.Object, out _, out _);
        await service.TestConnectionAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Contains("api/v1/settings/main", capturedRequest!.RequestUri!.ToString());
    }

    // ===== CleanupExpiredRequestsAsync =====

    [Fact]
    public async Task Cleanup_NoRequests_ReturnsZeroCounts()
    {
        var emptyPage = MakeRequestPage([], 0);
        var handler = CreateMockHandler(HttpStatusCode.OK, emptyPage);

        var service = CreateService(handler.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(0, result.TotalChecked);
        Assert.Equal(0, result.ExpiredFound);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.False(result.DryRun);
    }

    [Fact]
    public async Task Cleanup_AllRequestsYoung_NoneExpired()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-10)),
            (2, DateTimeOffset.UtcNow.AddDays(-5)),
            (3, DateTimeOffset.UtcNow.AddDays(-1))
        };
        var page = MakeRequestPage(requests, 3);
        var handler = CreateMockHandler(HttpStatusCode.OK, page);

        var service = CreateService(handler.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(3, result.TotalChecked);
        Assert.Equal(0, result.ExpiredFound);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task Cleanup_SomeExpired_DryRun_CountsButNoDeletes()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400)), // expired
            (2, DateTimeOffset.UtcNow.AddDays(-10)),   // young
            (3, DateTimeOffset.UtcNow.AddDays(-500))   // expired
        };
        var page = MakeRequestPage(requests, 3);

        // GET page → resolve title #1 → resolve title #3 (no deletes in dry run)
        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.OK, MakeMovieDetails("Expired Movie 1")),
            (HttpStatusCode.OK, MakeMovieDetails("Expired Movie 3")));

        var service = CreateService(handler.Object, out _, out var pluginLogMock);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, true, CancellationToken.None);

        Assert.Equal(3, result.TotalChecked);
        Assert.Equal(2, result.ExpiredFound);
        Assert.Equal(0, result.Deleted);
        Assert.True(result.DryRun);

        // Verify dry run logs go to plugin log
        pluginLogMock.Verify(
            x => x.LogInfo(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("[Dry Run]")),
                It.IsAny<ILogger>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Cleanup_ExpiredRequests_ActiveMode_DeletesSuccessfully()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400)),
            (2, DateTimeOffset.UtcNow.AddDays(-10))
        };
        var page = MakeRequestPage(requests, 2);

        // GET requests → resolve title → DELETE
        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.OK, MakeMovieDetails("The Matrix")),
            (HttpStatusCode.NoContent, ""));

        var service = CreateService(handler.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(2, result.TotalChecked);
        Assert.Equal(1, result.ExpiredFound);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.False(result.DryRun);
    }

    [Fact]
    public async Task Cleanup_DeleteFails_CountsAsFailure()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400))
        };
        var page = MakeRequestPage(requests, 1);

        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.OK, MakeMovieDetails("Broken Movie")),
            (HttpStatusCode.InternalServerError, ""));

        var service = CreateService(handler.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(1, result.TotalChecked);
        Assert.Equal(1, result.ExpiredFound);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Cleanup_CancellationToken_ThrowsWhenCancelled()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400))
        };
        var page = MakeRequestPage(requests, 1);
        var handler = CreateMockHandler(HttpStatusCode.OK, page);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var service = CreateService(handler.Object, out _, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CleanupExpiredRequestsAsync(BaseUrl, ApiKey, 365, false, cts.Token));
    }

    [Fact]
    public async Task Cleanup_JustBeforeCutoff_NotExpired()
    {
        // Request created 364 days ago should NOT be expired (well within the 365-day threshold)
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-364))
        };
        var page = MakeRequestPage(requests, 1);
        var handler = CreateMockHandler(HttpStatusCode.OK, page);

        var service = CreateService(handler.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(1, result.TotalChecked);
        Assert.Equal(0, result.ExpiredFound);
    }

    [Fact]
    public async Task Cleanup_OneDayOverCutoff_IsExpired()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-366))
        };
        var page = MakeRequestPage(requests, 1);

        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.OK, MakeMovieDetails("Old Movie")),
            (HttpStatusCode.NoContent, ""));

        var service = CreateService(handler.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(1, result.TotalChecked);
        Assert.Equal(1, result.ExpiredFound);
        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public async Task Cleanup_MaxAgeDaysZero_AllExpired()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-1)),
            (2, DateTimeOffset.UtcNow.AddMinutes(-5))
        };
        var page = MakeRequestPage(requests, 2);

        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.OK, MakeMovieDetails("Movie 1")),
            (HttpStatusCode.NoContent, ""),
            (HttpStatusCode.OK, MakeMovieDetails("Movie 2")),
            (HttpStatusCode.NoContent, ""));

        var service = CreateService(handler.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 0, false, CancellationToken.None);

        Assert.Equal(2, result.TotalChecked);
        Assert.Equal(2, result.ExpiredFound);
        Assert.Equal(2, result.Deleted);
    }

    [Fact]
    public async Task Cleanup_EmptyResultsList_HandlesGracefully()
    {
        var json = JsonSerializer.Serialize(new
        {
            pageInfo = new { page = 1, pages = 1, results = 0, pageSize = 50 },
            results = Array.Empty<object>()
        });
        var handler = CreateMockHandler(HttpStatusCode.OK, json);

        var service = CreateService(handler.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(0, result.TotalChecked);
    }

    [Fact]
    public async Task Cleanup_RequestWithoutMedia_StillProcessed()
    {
        // Request without media property
        var json = JsonSerializer.Serialize(new
        {
            pageInfo = new { page = 1, pages = 1, results = 1, pageSize = 50 },
            results = new[]
            {
                new
                {
                    id = 42,
                    createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"),
                    status = 2,
                    media = (object?)null
                }
            }
        });

        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, json),
            (HttpStatusCode.NoContent, ""));

        var service = CreateService(handler.Object, out _, out var pluginLogMock);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, true, CancellationToken.None);

        Assert.Equal(1, result.TotalChecked);
        Assert.Equal(1, result.ExpiredFound);

        // Verify fallback log message goes to plugin log
        pluginLogMock.Verify(
            x => x.LogInfo(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("request #42")),
                It.IsAny<ILogger>()),
            Times.AtLeastOnce);
    }

    // ===== DTO / Model Tests =====

    [Fact]
    public void SeerrCleanupResult_DefaultValues()
    {
        var result = new SeerrCleanupResult();
        Assert.Equal(0, result.TotalChecked);
        Assert.Equal(0, result.ExpiredFound);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.False(result.DryRun);
    }

    [Fact]
    public void SeerrRequest_DateTimeOffsetParsesUtc()
    {
        var json = "{\"id\":1,\"createdAt\":\"2024-01-15T10:30:00.000Z\",\"status\":2}";
        var request = JsonSerializer.Deserialize<SeerrRequest>(json);

        Assert.NotNull(request);
        Assert.Equal(1, request!.Id);
        Assert.Equal(2024, request.CreatedAt.Year);
        Assert.Equal(1, request.CreatedAt.Month);
        Assert.Equal(15, request.CreatedAt.Day);
        Assert.Equal(TimeSpan.Zero, request.CreatedAt.Offset);
    }

    [Fact]
    public void SeerrMedia_DeserializesCorrectly()
    {
        var json = "{\"mediaType\":\"tv\",\"tmdbId\":12345,\"status\":5}";
        var media = JsonSerializer.Deserialize<SeerrMedia>(json);

        Assert.NotNull(media);
        Assert.Equal("tv", media!.MediaType);
        Assert.Equal(12345, media.TmdbId);
        Assert.Equal(5, media.Status);
    }

    [Fact]
    public void SeerrPageInfo_DeserializesCorrectly()
    {
        var json = "{\"page\":2,\"pages\":5,\"results\":250,\"pageSize\":50}";
        var info = JsonSerializer.Deserialize<SeerrPageInfo>(json);

        Assert.NotNull(info);
        Assert.Equal(2, info!.Page);
        Assert.Equal(5, info.Pages);
        Assert.Equal(250, info.Results);
        Assert.Equal(50, info.PageSize);
    }

    [Fact]
    public void SeerrRequestPage_DeserializesCorrectly()
    {
        var json = """
        {
            "pageInfo": {"page":1,"pages":1,"results":1,"pageSize":50},
            "results": [{"id":7,"createdAt":"2024-06-01T00:00:00Z","status":2}]
        }
        """;
        var page = JsonSerializer.Deserialize<SeerrRequestPage>(json);

        Assert.NotNull(page);
        Assert.Single(page!.Results);
        Assert.Equal(7, page.Results[0].Id);
        Assert.Equal(1, page.PageInfo.Page);
    }

    [Fact]
    public void SeerrMainSettings_DeserializesCorrectly()
    {
        var json = "{\"applicationTitle\":\"My Overseerr\"}";
        var settings = JsonSerializer.Deserialize<SeerrMainSettings>(json);

        Assert.NotNull(settings);
        Assert.Equal("My Overseerr", settings!.ApplicationTitle);
    }

    [Fact]
    public void SeerrMainSettings_EmptyJson_DefaultsEmpty()
    {
        var json = "{}";
        var settings = JsonSerializer.Deserialize<SeerrMainSettings>(json);

        Assert.NotNull(settings);
        Assert.Equal(string.Empty, settings!.ApplicationTitle);
    }

    [Fact]
    public void PageSize_Is50()
    {
        Assert.Equal(50, SeerrIntegrationService.PageSize);
    }

    // ===== Title Resolution in Cleanup Logs =====

    [Fact]
    public async Task Cleanup_DryRun_LogsResolvedTitle()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400))
        };
        var page = MakeRequestPage(requests, 1);

        // GET requests → resolve title (movie detail)
        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.OK, MakeMovieDetails("Inception")));

        var service = CreateService(handler.Object, out _, out var pluginLogMock);
        await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, true, CancellationToken.None);

        pluginLogMock.Verify(
            x => x.LogInfo(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("\"Inception\"") && s.Contains("[Dry Run]")),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public async Task Cleanup_ActiveMode_LogsResolvedTitle()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400))
        };
        var page = MakeRequestPage(requests, 1);

        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.OK, MakeMovieDetails("Interstellar")),
            (HttpStatusCode.NoContent, ""));

        var service = CreateService(handler.Object, out _, out var pluginLogMock);
        await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        pluginLogMock.Verify(
            x => x.LogInfo(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("\"Interstellar\"") && s.Contains("Deleted")),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public async Task Cleanup_TitleResolutionFails_FallsBackToUnknown()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400))
        };
        var page = MakeRequestPage(requests, 1);

        // GET requests → title resolution returns 404
        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.NotFound, ""));

        var service = CreateService(handler.Object, out _, out var pluginLogMock);
        await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, true, CancellationToken.None);

        pluginLogMock.Verify(
            x => x.LogInfo(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("\"Unknown\"") && s.Contains("[Dry Run]")),
                It.IsAny<ILogger>()),
            Times.Once);
    }
}
