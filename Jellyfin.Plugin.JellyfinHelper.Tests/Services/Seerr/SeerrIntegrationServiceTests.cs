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
            status = 1,
            media = new { mediaType = "movie", tmdbId = r.Id * 100, status = 1 }
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            pageInfo = new { page, pages, results = totalResults, pageSize = 50 },
            results
        });
    }

    private static string MakeMovieDetails(string title) =>
        JsonSerializer.Serialize(new { title, name = (string?)null });

    private static string MakeRequestPageWithMediaType(
        string mediaType,
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
            media = new { mediaType, tmdbId = r.Id * 100, status = 1 }
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            pageInfo = new { page, pages, results = totalResults, pageSize = 50 },
            results
        });
    }

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

        // Verify no DELETE requests were sent during dry run
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Delete),
            ItExpr.IsAny<CancellationToken>());
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

    // Fail-CLOSED on unknown creation date (audit finding seerr-external-1). A missing/null/default/
    // future createdAt must NEVER be treated as expired — non-dry-run so a regression would DELETE.

    private static string MakeRawRequestPage(string requestObjectJson, int totalResults = 1) =>
        "{\"pageInfo\":{\"page\":1,\"pages\":1,\"results\":" + totalResults
        + ",\"pageSize\":50},\"results\":[" + requestObjectJson + "]}";

    [Fact]
    public async Task Cleanup_MissingCreatedAt_PendingStatus_IsNotDeleted()
    {
        // No createdAt key at all → previously deserialized to MinValue → wrongly deleted. Must be kept.
        var page = MakeRawRequestPage("{\"id\":1,\"status\":1,\"media\":{\"mediaType\":\"movie\",\"tmdbId\":100,\"status\":1}}");
        var handler = CreateMockHandler(HttpStatusCode.OK, page);
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 90, false, CancellationToken.None);

        Assert.Equal(1, result.TotalChecked);
        Assert.Equal(0, result.ExpiredFound);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task Cleanup_NullCreatedAt_IsNotDeleted()
    {
        var page = MakeRawRequestPage("{\"id\":1,\"createdAt\":null,\"status\":1,\"media\":{\"mediaType\":\"movie\",\"tmdbId\":100,\"status\":1}}");
        var handler = CreateMockHandler(HttpStatusCode.OK, page);
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 90, false, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task Cleanup_DefaultMinValueCreatedAt_IsNotDeleted()
    {
        var page = MakeRawRequestPage("{\"id\":1,\"createdAt\":\"0001-01-01T00:00:00+00:00\",\"status\":1,\"media\":{\"mediaType\":\"movie\",\"tmdbId\":100,\"status\":1}}");
        var handler = CreateMockHandler(HttpStatusCode.OK, page);
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 90, false, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task Cleanup_FutureCreatedAt_IsNotDeleted()
    {
        var future = DateTimeOffset.UtcNow.AddDays(5).ToString("O");
        var page = MakeRawRequestPage($"{{\"id\":1,\"createdAt\":\"{future}\",\"status\":1,\"media\":{{\"mediaType\":\"movie\",\"tmdbId\":100,\"status\":1}}}}");
        var handler = CreateMockHandler(HttpStatusCode.OK, page);
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 90, false, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public void SeerrRequest_MissingCreatedAt_DeserializesToNull()
    {
        var request = JsonSerializer.Deserialize<SeerrRequest>("{\"id\":1,\"status\":1}");
        Assert.NotNull(request);
        Assert.Null(request!.CreatedAt);
    }

    [Fact]
    public async Task Cleanup_MaxAgeDaysZero_ThrowsArgumentOutOfRange()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(handler.Object, out _, out _);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.CleanupExpiredRequestsAsync(
                BaseUrl, ApiKey, 0, false, CancellationToken.None));
    }

    [Fact]
    public async Task Cleanup_MaxAgeDaysNegative_ThrowsArgumentOutOfRange()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(handler.Object, out _, out _);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.CleanupExpiredRequestsAsync(
                BaseUrl, ApiKey, -1, false, CancellationToken.None));
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
                    status = 1,
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
        Assert.NotNull(request.CreatedAt);
        Assert.Equal(2024, request.CreatedAt!.Value.Year);
        Assert.Equal(1, request.CreatedAt.Value.Month);
        Assert.Equal(15, request.CreatedAt.Value.Day);
        Assert.Equal(TimeSpan.Zero, request.CreatedAt.Value.Offset);
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
    public async Task Cleanup_DryRun_TwoPages_ProcessesAllExpired()
    {
        var page1Requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400)),
            (2, DateTimeOffset.UtcNow.AddDays(-500))
        };
        var page2Requests = new List<(int, DateTimeOffset)>
        {
            (3, DateTimeOffset.UtcNow.AddDays(-600))
        };
        // totalResults must exceed PageSize (50) so that skip += PageSize keeps hasMore true
        // after the first page, allowing the second page to be fetched.
        var page1 = MakeRequestPage(page1Requests, 51, page: 1, pages: 2);
        var page2 = MakeRequestPage(page2Requests, 51, page: 2, pages: 2);

        // Phase 1: two pages fetched, Phase 2: three title resolutions
        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page1),
            (HttpStatusCode.OK, page2),
            (HttpStatusCode.OK, MakeMovieDetails("Film A")),
            (HttpStatusCode.OK, MakeMovieDetails("Film B")),
            (HttpStatusCode.OK, MakeMovieDetails("Film C")));

        var service = CreateService(handler.Object, out _, out var pluginLogMock);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, true, CancellationToken.None);

        Assert.Equal(3, result.TotalChecked);
        Assert.Equal(3, result.ExpiredFound);
        Assert.True(result.DryRun);

        pluginLogMock.Verify(
            x => x.LogInfo(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("[Dry Run]")),
                It.IsAny<ILogger>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task Cleanup_DryRun_TvMedia_ResolvesTvTitle()
    {
        var requests = new List<(int, DateTimeOffset)>
        {
            (1, DateTimeOffset.UtcNow.AddDays(-400))
        };
        var page = MakeRequestPageWithMediaType("tv", requests, 1);

        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page),
            (HttpStatusCode.OK, MakeTvDetails("Breaking Bad")));

        var service = CreateService(handler.Object, out _, out var pluginLogMock);
        await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, true, CancellationToken.None);

        pluginLogMock.Verify(
            x => x.LogInfo(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("\"Breaking Bad\"") && s.Contains("[Dry Run]")),
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

    // =========================================================================
    // Error-path & bug-surface coverage
    // =========================================================================

    [Fact]
    public async Task Cleanup_InvalidBaseUrl_MarksFailure_DoesNotThrow()
    {
        // BUG SURFACE: an invalid base URL used to throw straight to the caller,
        // producing a stack trace instead of a graceful Failed=1 result the scheduler can log.
        var handler = new Mock<HttpMessageHandler>();
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            "not-a-url", ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.TotalChecked);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task Cleanup_EmptyApiKey_MarksFailure_DoesNotThrow()
    {
        // Empty/whitespace API key must be caught inside CreateClient and mapped to Failed=1.
        var handler = new Mock<HttpMessageHandler>();
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, "   ", 365, false, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.TotalChecked);
    }

    [Fact]
    public async Task Cleanup_HttpErrorOnFirstPage_MarksFailedAndBreaks()
    {
        // HttpRequestException during page fetch must be caught and log-and-break,
        // not propagate up and abort the entire cleanup pipeline.
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection reset"));

        var service = CreateService(mock.Object, out _, out _);
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.TotalChecked);
    }

    [Fact]
    public async Task Cleanup_MalformedJsonOnFirstPage_MarksFailedAndBreaks()
    {
        // JsonException must NOT crash; break pagination gracefully with a Failed count.
        var handler = CreateMockHandler(HttpStatusCode.OK, "not-json{");
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Cleanup_ResponseMissingPageInfo_MarksFailedAndBreaks()
    {
        // BUG SURFACE: a response with results but no pageInfo used to loop forever
        // (skip never advanced because pageInfo.Results was undefined). The guard clause
        // now marks it as Failed and breaks the loop.
        var json = JsonSerializer.Serialize(new
        {
            pageInfo = (object?)null,
            results = new[]
            {
                new
                {
                    id = 1,
                    createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"),
                    status = 2
                }
            }
        });

        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, true, CancellationToken.None);

        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task ResolveMediaTitleAsync_MediaNull_ReturnsUnknown_NoHttpCall()
    {
        // Direct-invocation test of the internal helper — the null-guard branch must
        // short-circuit BEFORE any HTTP call.
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Should not be called"));

        var service = CreateService(mock.Object, out _, out _);
        using var httpClient = new HttpClient(mock.Object);
        var baseUri = new Uri(BaseUrl + "/");

        var title = await service.ResolveMediaTitleAsync(httpClient, baseUri, ApiKey, null, CancellationToken.None);
        Assert.Equal("Unknown", title);
        // Sentinel-exception observation alone is insufficient: a future broad catch could
        // swallow it and still return "Unknown". An explicit Times.Never verify ensures
        // the short-circuit really happens BEFORE the handler is touched.
        mock.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ResolveMediaTitleAsync_ZeroTmdbId_ReturnsUnknown_NoHttpCall()
    {
        // TMDB ids <= 0 must short-circuit — an HTTP call would waste Seerr API quota
        // on a request that cannot possibly resolve.
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Should not be called"));

        var service = CreateService(mock.Object, out _, out _);
        using var httpClient = new HttpClient(mock.Object);
        var baseUri = new Uri(BaseUrl + "/");

        var title = await service.ResolveMediaTitleAsync(
            httpClient,
            baseUri,
            ApiKey,
            new SeerrMedia { MediaType = "movie", TmdbId = 0 },
            CancellationToken.None);

        Assert.Equal("Unknown", title);
        // Same defence-in-depth as the null-media case: the short-circuit must be
        // observable at the handler level, not just via the sentinel exception.
        mock.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ResolveMediaTitleAsync_HttpError_ReturnsUnknown_DoesNotThrow()
    {
        // A 500 from the movie-detail endpoint must degrade gracefully to "Unknown",
        // never propagate a stack trace up into the cleanup summary.
        var handler = CreateMockHandler(HttpStatusCode.InternalServerError, string.Empty);
        var service = CreateService(handler.Object, out _, out _);
        using var httpClient = new HttpClient(handler.Object);
        var baseUri = new Uri(BaseUrl + "/");

        var title = await service.ResolveMediaTitleAsync(
            httpClient,
            baseUri,
            ApiKey,
            new SeerrMedia { MediaType = "movie", TmdbId = 42 },
            CancellationToken.None);

        Assert.Equal("Unknown", title);
    }

    [Fact]
    public async Task ResolveMediaTitleAsync_NetworkException_ReturnsUnknown()
    {
        // HttpRequestException must be caught inside the helper and produce "Unknown".
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("timeout"));

        var service = CreateService(mock.Object, out _, out _);
        using var httpClient = new HttpClient(mock.Object);
        var baseUri = new Uri(BaseUrl + "/");

        var title = await service.ResolveMediaTitleAsync(
            httpClient,
            baseUri,
            ApiKey,
            new SeerrMedia { MediaType = "movie", TmdbId = 42 },
            CancellationToken.None);

        Assert.Equal("Unknown", title);
    }

    // TryAddWithoutValidation for X-Api-Key =====

    [Fact]
    public async Task TestConnection_NonAsciiApiKey_DoesNotThrow()
    {
        var handler = CreateMockHandler(HttpStatusCode.Unauthorized, string.Empty);
        var service = CreateService(handler.Object, out _, out _);

        var nonAsciiKey = "キー12345";
        var exception = await Record.ExceptionAsync(
            () => service.TestConnectionAsync(BaseUrl, nonAsciiKey, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task TestConnection_ApiKeyWithInternalSpace_DoesNotThrow()
    {
        // RFC 7230 forbids whitespace inside header values — Add() throws FormatException.
        // TryAddWithoutValidation() must silently pass it through so the server can reject
        // the key with a proper HTTP error instead of crashing the plugin.
        var handler = CreateMockHandler(HttpStatusCode.Unauthorized, string.Empty);
        var service = CreateService(handler.Object, out _, out _);

        var exception = await Record.ExceptionAsync(
            () => service.TestConnectionAsync(BaseUrl, "key with spaces", CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task TestConnectionAsync_ApiKeyWithCrlf_ThrowsArgumentException()
    {
        var handler = new Mock<HttpMessageHandler>();
        var service = CreateService(handler.Object, out _, out _);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.TestConnectionAsync("http://seerr.local", "key\r\nX-Injected: evil", CancellationToken.None));
        Assert.Contains("CR, LF", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Pagination behaviour =====

    [Fact]
    public async Task CleanupExpiredRequests_PartialPageResponse_UsesPageSizeForOffset()
    {
        // Arrange: first page returns 30 results (< PageSize=50) but pageInfo.Results=80
        // so hasMore is true after page 1 (skip=0 < 80).  The second page fetch must use
        // skip=50 (PageSize), NOT skip=30 (page1.Results.Count).
        // This verifies that skip is advanced by the constant PageSize, not by the
        // variable number of items actually returned in a page.

        var page1Requests = Enumerable.Range(1, 30)
            .Select(i => (i, DateTimeOffset.UtcNow.AddDays(-10))) // young — not expired
            .ToList();

        // Build a page JSON with 30 results but totalResults=80 so pagination continues.
        var results1 = page1Requests.Select(r => new
        {
            id = r.i,
            createdAt = r.Item2.ToString("O"),
            status = 2,
            media = new { mediaType = "movie", tmdbId = r.i * 100, status = 5 }
        }).ToList<object>();

        var page1Json = JsonSerializer.Serialize(new
        {
            pageInfo = new { page = 1, pages = 2, results = 80, pageSize = 50 },
            results = results1
        });

        // Second page: also young requests; totalResults=80, skip will be 50 after page1.
        var page2Requests = Enumerable.Range(51, 30)
            .Select(i => (i, DateTimeOffset.UtcNow.AddDays(-10)))
            .ToList();

        var results2 = page2Requests.Select(r => new
        {
            id = r.i,
            createdAt = r.Item2.ToString("O"),
            status = 2,
            media = new { mediaType = "movie", tmdbId = r.i * 100, status = 5 }
        }).ToList<object>();

        var page2Json = JsonSerializer.Serialize(new
        {
            pageInfo = new { page = 2, pages = 2, results = 80, pageSize = 50 },
            results = results2
        });

        var capturedUrls = new List<string>();
        var mock = new Mock<HttpMessageHandler>();
        var seq = mock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        // Track every outgoing request URI before returning the canned responses.
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                if (req.RequestUri != null)
                {
                    capturedUrls.Add(req.RequestUri.ToString());
                }
            })
            .ReturnsAsync(() =>
            {
                // Return page1 for the first call, page2 for the second.
                var responseIndex = capturedUrls.Count - 1;
                var (statusCode, content) = responseIndex == 0
                    ? (HttpStatusCode.OK, page1Json)
                    : (HttpStatusCode.OK, page2Json);
                return CreateResponse(statusCode, content);
            });

        var service = CreateService(mock.Object, out _, out _);

        // Act
        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 365, false, CancellationToken.None);

        // Assert: two page GETs were made (pagination really continued past the partial page)
        var pageGetUrls = capturedUrls.Where(u => u.Contains("api/v1/request")).ToList();
        Assert.Equal(2, pageGetUrls.Count);

        // The first GET must use skip=0
        Assert.Contains("skip=0", pageGetUrls[0]);

        // The second GET must use skip=50 (PageSize), NOT skip=30 (count of results returned)
        Assert.Contains("skip=50", pageGetUrls[1]);
        Assert.DoesNotContain("skip=30", pageGetUrls[1]);

        // Sanity: all 60 requests were young so none should be marked expired
        Assert.Equal(60, result.TotalChecked);
        Assert.Equal(0, result.ExpiredFound);
    }

    // ===== Cleanup must not delete approved/available requests =====

    [Fact]
    public async Task Cleanup_ApprovedRequest_IsNotDeleted()
    {
        var json = JsonSerializer.Serialize(new
        {
            pageInfo = new { page = 1, pages = 1, results = 1, pageSize = 50 },
            results = new[]
            {
                new
                {
                    id = 99,
                    createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"),
                    status = 2, // approved
                    media = new { mediaType = "movie", tmdbId = 9900, status = 2 }
                }
            }
        });

        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 30, false, CancellationToken.None);

        // Request is old enough, but status=2 (approved) must prevent deletion
        Assert.Equal(1, result.TotalChecked);
        Assert.Equal(0, result.ExpiredFound);
        Assert.Equal(0, result.Deleted);

        // Verify DELETE was never sent
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(), // only the GET
            ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
            ItExpr.IsAny<CancellationToken>());
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Delete),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task Cleanup_MixedStatuses_OnlyDeletesPending()
    {
        var json = JsonSerializer.Serialize(new
        {
            pageInfo = new { page = 1, pages = 1, results = 3, pageSize = 50 },
            results = new[]
            {
                new { id = 1, createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"), status = 1, media = new { mediaType = "movie", tmdbId = 100, status = 1 } },
                new { id = 2, createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"), status = 2, media = new { mediaType = "movie", tmdbId = 200, status = 2 } },
                new { id = 3, createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"), status = 3, media = new { mediaType = "movie", tmdbId = 300, status = 3 } }
            }
        });

        // GET page, then GET title for id=1, DELETE id=1, GET title for id=3, DELETE id=3
        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, json),
            (HttpStatusCode.OK, MakeMovieDetails("Movie1")),
            (HttpStatusCode.NoContent, ""),
            (HttpStatusCode.OK, MakeMovieDetails("Movie3")),
            (HttpStatusCode.NoContent, ""));

        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 30, false, CancellationToken.None);

        Assert.Equal(3, result.TotalChecked);
        Assert.Equal(2, result.ExpiredFound);  // id=1 (pending) and id=3 (declined), not id=2 (approved)
        Assert.Equal(2, result.Deleted);
    }

    // ===== Available/partially-available requests (status 4, 5) must never be deleted =====

    [Theory]
    [InlineData(4)] // available
    [InlineData(5)] // partially available
    public async Task Cleanup_AvailableOrPartiallyAvailableRequest_IsNotDeleted(int status)
    {
        // Requests with status=4 (available) or status=5 (partially available) represent
        // content that has already been downloaded. Deleting them would break status tracking
        // and could trigger unwanted re-requests.
        var json = JsonSerializer.Serialize(new
        {
            pageInfo = new { page = 1, pages = 1, results = 1, pageSize = 50 },
            results = new[]
            {
                new
                {
                    id = 42,
                    createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"),
                    status,
                    media = new { mediaType = "movie", tmdbId = 4200, status }
                }
            }
        });

        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 30, false, CancellationToken.None);

        Assert.Equal(1, result.TotalChecked);
        Assert.Equal(0, result.ExpiredFound);
        Assert.Equal(0, result.Deleted);

        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Delete),
            ItExpr.IsAny<CancellationToken>());
    }

    // ===== phaseOneFailed circuit-breaker =====

    [Fact]
    public async Task Cleanup_Page2FetchFails_SkipsDeletion_EvenWhenPage1HadExpiredItems()
    {
        // This test pins the most critical safety guarantee in the service: when Phase 1
        // pagination does not complete cleanly, Phase 2 must not delete anything — acting
        // on a partial snapshot would permanently remove requests whose expiry status could
        // not be confirmed from the missing pages.
        //
        // Setup: page 1 returns two expired, pending (deletable) requests with totalResults=100
        // so the service knows there is a page 2.  The page 2 GET throws HttpRequestException.
        // Expected: result.Deleted == 0 and no DELETE request is ever sent.

        var page1 = JsonSerializer.Serialize(new
        {
            pageInfo = new { page = 1, pages = 2, results = 100, pageSize = 50 },
            results = new[]
            {
                new
                {
                    id = 1,
                    createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"),
                    status = 1, // pending — would normally be deleted
                    media = new { mediaType = "movie", tmdbId = 100, status = 1 }
                },
                new
                {
                    id = 2,
                    createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"),
                    status = 3, // declined — would normally be deleted
                    media = new { mediaType = "movie", tmdbId = 200, status = 3 }
                }
            }
        });

        var callCount = 0;
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call: page 1 succeeds — two expired deletable requests
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent(page1, System.Text.Encoding.UTF8, "application/json")
                    });
                }

                // Second call: page 2 fetch fails — pagination is incomplete
                throw new HttpRequestException("connection reset by peer");
            });

        var service = CreateService(mock.Object, out _, out var pluginLogMock);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 30, false, CancellationToken.None);

        // Phase 1 failed partway through: no deletion must occur regardless of what page 1 found
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Failed);

        // The phaseOneFailed warning must have been logged
        pluginLogMock.Verify(
            x => x.LogWarning(
                "SeerrCleanup",
                It.Is<string>(s => s.Contains("pagination") || s.Contains("incomplete") || s.Contains("snapshot")),
                null,
                It.IsAny<ILogger>()),
            Times.Once);

        // No DELETE request must ever leave the service
        mock.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Delete),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task Cleanup_AllPagesSucceed_DeletionProceeds()
    {
        // Counter-test: when all pages fetch successfully, Phase 2 deletion runs normally.
        // This guards against a regression where phaseOneFailed is set too eagerly.
        var page1 = JsonSerializer.Serialize(new
        {
            pageInfo = new { page = 1, pages = 1, results = 1, pageSize = 50 },
            results = new[]
            {
                new
                {
                    id = 7,
                    createdAt = DateTimeOffset.UtcNow.AddDays(-400).ToString("O"),
                    status = 1, // pending — must be deleted
                    media = new { mediaType = "movie", tmdbId = 700, status = 1 }
                }
            }
        });

        var handler = CreateSequenceHandler(
            (HttpStatusCode.OK, page1),
            (HttpStatusCode.OK, MakeMovieDetails("OldMovie")),
            (HttpStatusCode.NoContent, ""));

        var service = CreateService(handler.Object, out _, out _);

        var result = await service.CleanupExpiredRequestsAsync(
            BaseUrl, ApiKey, 30, false, CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Failed);
    }
}
