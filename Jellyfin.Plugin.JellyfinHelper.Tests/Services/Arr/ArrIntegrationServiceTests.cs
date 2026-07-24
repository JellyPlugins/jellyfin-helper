using System.Net;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Arr;

public class ArrIntegrationServiceTests
{
    private static ArrIntegrationService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);
        var logger = TestMockFactory.CreateLogger<ArrIntegrationService>();
        return new ArrIntegrationService(factoryMock.Object, TestMockFactory.CreatePluginLogService(), logger.Object);
    }

    // Variant that exposes the pluginLog mock so tests can verify log calls.
    private static ArrIntegrationService CreateServiceWithMockLog(
        HttpMessageHandler handler,
        out Mock<IPluginLogService> pluginLogMock)
    {
        pluginLogMock = new Mock<IPluginLogService>();
        var httpClient = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);
        var logger = TestMockFactory.CreateLogger<ArrIntegrationService>();
        return new ArrIntegrationService(factoryMock.Object, pluginLogMock.Object, logger.Object);
    }

    private static Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, string content)
    {
        return TestMockFactory.CreateHttpMessageHandler(statusCode, content);
    }

    // === TestConnectionAsync ===

    [Fact]
    public async Task TestConnection_EmptyUrl_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(handler.Object);

        var (success, message) = await service.TestConnectionAsync(string.Empty, "apikey123");

        Assert.False(success);
        Assert.Contains("URL", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnection_EmptyApiKey_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(handler.Object);

        var (success, message) = await service.TestConnectionAsync("http://localhost:7878", string.Empty);

        Assert.False(success);
        Assert.Contains("API", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnection_NullUrl_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(handler.Object);

        var (success, _) = await service.TestConnectionAsync(null!, "apikey123");

        Assert.False(success);
    }

    [Fact]
    public async Task TestConnection_NullApiKey_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(handler.Object);

        var (success, _) = await service.TestConnectionAsync("http://localhost:7878", null!);

        Assert.False(success);
    }

    [Fact]
    public async Task TestConnection_SuccessfulResponse_ReturnsTrue()
    {
        var json = """{"appName":"Radarr","version":"5.2.0.1234"}""";
        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var service = CreateService(handler.Object);

        var (success, message) = await service.TestConnectionAsync("http://localhost:7878", "testapikey");

        Assert.True(success);
        Assert.Contains("Radarr", message);
        Assert.Contains("5.2.0.1234", message);
    }

    [Fact]
    public async Task TestConnection_SuccessfulResponse_SonarrAppName()
    {
        var json = """{"appName":"Sonarr","version":"4.0.1.100"}""";
        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var service = CreateService(handler.Object);

        var (success, message) = await service.TestConnectionAsync("http://localhost:8989", "testapikey");

        Assert.True(success);
        Assert.Contains("Sonarr", message);
        Assert.Contains("4.0.1.100", message);
    }

    [Fact]
    public async Task TestConnection_UnauthorizedResponse_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.Unauthorized, "Unauthorized");
        var service = CreateService(handler.Object);

        var (success, message) = await service.TestConnectionAsync("http://localhost:7878", "wrongkey");

        Assert.False(success);
        Assert.Contains("failed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnection_ServerError_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.InternalServerError, "Error");
        var service = CreateService(handler.Object);

        var (success, _) = await service.TestConnectionAsync("http://localhost:7878", "testapikey");

        Assert.False(success);
    }

    [Fact]
    public async Task TestConnection_InvalidJson_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "not-json");
        var service = CreateService(handler.Object);

        var (success, message) = await service.TestConnectionAsync("http://localhost:7878", "testapikey");

        Assert.False(success);
        Assert.Contains("Error", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnection_EmptyJsonObject_ReturnsSuccessWithUnknown()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(handler.Object);

        var (success, message) = await service.TestConnectionAsync("http://localhost:7878", "testapikey");

        Assert.True(success);
        Assert.Contains("Unknown", message);
    }

    [Fact]
    public async Task TestConnection_TrailingSlashInUrl_IsHandled()
    {
        var json = """{"appName":"Radarr","version":"5.0.0"}""";
        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null &&
                    req.RequestUri.AbsoluteUri == "http://localhost:7878/api/v3/system/status"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json)
            })
            .Verifiable();
        mockHandler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());

        var service = CreateService(mockHandler.Object);

        var (success, _) = await service.TestConnectionAsync("http://localhost:7878/", "testapikey");

        Assert.True(success);
        mockHandler.Verify();
    }

    [Fact]
    public async Task TestConnection_SetsXApiKeyHeader()
    {
        var json = """{"appName":"Radarr","version":"5.0.0"}""";
        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Headers.Contains("X-Api-Key")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json)
            })
            .Verifiable();
        mockHandler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());

        var service = CreateService(mockHandler.Object);

        await service.TestConnectionAsync("http://localhost:7878", "my-secret-key");

        mockHandler.Verify();
    }

    [Fact]
    public async Task TestConnection_CallsCorrectEndpoint()
    {
        var json = """{"appName":"Radarr","version":"5.0.0"}""";
        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri != null &&
                    req.RequestUri.AbsoluteUri == "http://localhost:7878/api/v3/system/status"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json)
            })
            .Verifiable();
        mockHandler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());

        var service = CreateService(mockHandler.Object);

        await service.TestConnectionAsync("http://localhost:7878", "testapikey");

        mockHandler.Verify();
    }

    [Fact]
    public async Task TestConnection_CancellationToken_IsRespected()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("Request was canceled"))
            .Verifiable();
        mockHandler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());

        var service = CreateService(mockHandler.Object);

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            service.TestConnectionAsync("http://localhost:7878", "testapikey", cts.Token));
    }

    // === GetRadarrMoviesAsync ===

    [Fact]
    public async Task GetRadarrMovies_EmptyUrl_ReturnsEmptyList()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "[]");
        var service = CreateService(handler.Object);

        var movies = await service.GetRadarrMoviesAsync(string.Empty, "apikey");

        Assert.NotNull(movies);
        Assert.Empty(movies);
    }

    [Fact]
    public async Task GetRadarrMovies_EmptyApiKey_ReturnsEmptyList()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "[]");
        var service = CreateService(handler.Object);

        var movies = await service.GetRadarrMoviesAsync("http://localhost:7878", string.Empty);

        Assert.NotNull(movies);
        Assert.Empty(movies);
    }

    [Fact]
    public async Task GetRadarrMovies_ValidResponse_ParsesMovies()
    {
        var json = """
                   [
                       {"title":"The Matrix","year":1999,"imdbId":"tt0133093","tmdbId":603,"hasFile":true,"path":"/movies/The Matrix (1999)"},
                       {"title":"Inception","year":2010,"imdbId":"tt1375666","tmdbId":27205,"hasFile":false,"path":"/movies/Inception (2010)"}
                   ]
                   """;
        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var service = CreateService(handler.Object);

        var movies = await service.GetRadarrMoviesAsync("http://localhost:7878", "testapikey");

        Assert.NotNull(movies);
        Assert.Equal(2, movies.Count);
        Assert.Equal("The Matrix", movies[0].Title);
        Assert.Equal(1999, movies[0].Year);
        Assert.True(movies[0].HasFile);
        Assert.Equal("Inception", movies[1].Title);
        Assert.False(movies[1].HasFile);
    }

    [Fact]
    public async Task GetRadarrMovies_ServerError_ReturnsNull()
    {
        var handler = CreateMockHandler(HttpStatusCode.InternalServerError, "Error");
        var service = CreateService(handler.Object);

        var movies = await service.GetRadarrMoviesAsync("http://localhost:7878", "testapikey");

        Assert.Null(movies);
    }

    // === GetSonarrSeriesAsync ===

    [Fact]
    public async Task GetSonarrSeries_EmptyUrl_ReturnsEmptyList()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "[]");
        var service = CreateService(handler.Object);

        var series = await service.GetSonarrSeriesAsync(string.Empty, "apikey");

        Assert.NotNull(series);
        Assert.Empty(series);
    }

    [Fact]
    public async Task GetSonarrSeries_ValidResponse_ParsesSeries()
    {
        var json = """
                   [
                       {"title":"Breaking Bad","year":2008,"imdbId":"tt0903747","tvdbId":81189,"path":"/tv/Breaking Bad","statistics":{"episodeFileCount":62,"totalEpisodeCount":62}},
                       {"title":"The Wire","year":2002,"imdbId":"tt0306414","tvdbId":79126,"path":"/tv/The Wire","statistics":{"episodeFileCount":0,"totalEpisodeCount":60}}
                   ]
                   """;
        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var service = CreateService(handler.Object);

        var series = await service.GetSonarrSeriesAsync("http://localhost:8989", "testapikey");

        Assert.NotNull(series);
        Assert.Equal(2, series.Count);
        Assert.Equal("Breaking Bad", series[0].Title);
        Assert.Equal(62, series[0].EpisodeFileCount);
        Assert.Equal("The Wire", series[1].Title);
        Assert.Equal(0, series[1].EpisodeFileCount);
    }

    [Fact]
    public async Task GetSonarrSeries_ServerError_ReturnsNull()
    {
        var handler = CreateMockHandler(HttpStatusCode.InternalServerError, "Error");
        var service = CreateService(handler.Object);

        var series = await service.GetSonarrSeriesAsync("http://localhost:8989", "testapikey");

        Assert.Null(series);
    }

    [Fact]
    public async Task GetRadarrMovies_Timeout_ReturnsNull_AndLogsWarning_NotError()
    {
        var mock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mock.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException("HttpClient timeout"));

        var service = CreateServiceWithMockLog(mock.Object, out var pluginLogMock);

        var movies = await service.GetRadarrMoviesAsync("http://localhost:7878", "testapikey");

        Assert.Null(movies);

        pluginLogMock.Verify(
            p => p.LogWarning(
                "ArrIntegration",
                It.Is<string>(msg => msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);

    }

    [Fact]
    public async Task GetSonarrSeries_Timeout_ReturnsNull_AndLogsWarning_NotError()
    {
        var mock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mock.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException("HttpClient timeout"));

        var service = CreateServiceWithMockLog(mock.Object, out var pluginLogMock);

        var series = await service.GetSonarrSeriesAsync("http://localhost:8989", "testapikey");

        Assert.Null(series);

        pluginLogMock.Verify(
            p => p.LogWarning(
                "ArrIntegration",
                It.Is<string>(msg => msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);

    }

    // === CompareRadarrWithJellyfin ===

    [Fact]
    public void CompareRadarr_MoviesInBoth_AreDetected()
    {
        var movies = new[]
        {
            new ArrMovie { Title = "The Matrix", Year = 1999, HasFile = true, Path = "/movies/The Matrix (1999)" }
        };
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "The Matrix (1999)" };

        var result = ArrIntegrationService.CompareRadarrWithJellyfin(movies, jellyfinFolders);

        Assert.Single(result.InBoth);
        Assert.Contains("The Matrix", result.InBoth);
        Assert.Empty(result.InArrOnly);
        Assert.Empty(result.InJellyfinOnly);
    }

    [Fact]
    public void CompareRadarr_MovieOnlyInArr_WithFile()
    {
        var movies = new[]
        {
            new ArrMovie { Title = "Inception", Year = 2010, HasFile = true, Path = "/movies/Inception (2010)" }
        };
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = ArrIntegrationService.CompareRadarrWithJellyfin(movies, jellyfinFolders);

        Assert.Empty(result.InBoth);
        Assert.Single(result.InArrOnly);
        Assert.Empty(result.InArrOnlyMissing);
    }

    [Fact]
    public void CompareRadarr_MovieOnlyInArr_NoFile()
    {
        var movies = new[]
        {
            new ArrMovie { Title = "Future Movie", Year = 2025, HasFile = false, Path = "/movies/Future Movie (2025)" }
        };
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = ArrIntegrationService.CompareRadarrWithJellyfin(movies, jellyfinFolders);

        Assert.Empty(result.InBoth);
        Assert.Empty(result.InArrOnly);
        Assert.Single(result.InArrOnlyMissing);
    }

    [Fact]
    public void CompareRadarr_MovieOnlyInJellyfin()
    {
        var movies = Array.Empty<ArrMovie>();
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Old Movie (2000)" };

        var result = ArrIntegrationService.CompareRadarrWithJellyfin(movies, jellyfinFolders);

        Assert.Empty(result.InBoth);
        Assert.Single(result.InJellyfinOnly);
        Assert.Contains("Old Movie (2000)", result.InJellyfinOnly);
    }

    // === CompareSonarrWithJellyfin ===

    [Fact]
    public void CompareSonarr_SeriesInBoth_AreDetected()
    {
        var series = new[]
        {
            new ArrSeries
            {
                Title = "Breaking Bad", Year = 2008, Path = "/tv/Breaking Bad", EpisodeFileCount = 62,
                TotalEpisodeCount = 62
            }
        };
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Breaking Bad" };

        var result = ArrIntegrationService.CompareSonarrWithJellyfin(series, jellyfinFolders);

        Assert.Single(result.InBoth);
        Assert.Contains("Breaking Bad", result.InBoth);
    }

    [Fact]
    public void CompareSonarr_SeriesOnlyInArr_WithEpisodes()
    {
        var series = new[]
        {
            new ArrSeries
            {
                Title = "The Wire", Year = 2002, Path = "/tv/The Wire", EpisodeFileCount = 60, TotalEpisodeCount = 60
            }
        };
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = ArrIntegrationService.CompareSonarrWithJellyfin(series, jellyfinFolders);

        Assert.Single(result.InArrOnly);
        Assert.Empty(result.InBoth);
    }

    [Fact]
    public void CompareSonarr_SeriesOnlyInArr_NoEpisodes()
    {
        var series = new[]
        {
            new ArrSeries
            {
                Title = "Upcoming Show", Year = 2025, Path = "/tv/Upcoming Show", EpisodeFileCount = 0,
                TotalEpisodeCount = 10
            }
        };
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = ArrIntegrationService.CompareSonarrWithJellyfin(series, jellyfinFolders);

        Assert.Single(result.InArrOnlyMissing);
    }

    [Fact]
    public async Task TestConnection_Timeout_LogsWarning()
    {
        var mock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mock.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException("HttpClient timeout"));

        var service = CreateServiceWithMockLog(mock.Object, out var pluginLogMock);

        var (success, message) = await service.TestConnectionAsync(
            "http://localhost:7878", "testapikey");

        Assert.False(success);
        Assert.Contains("timed out", message, StringComparison.OrdinalIgnoreCase);

        // Must fire exactly once as LogWarning
        pluginLogMock.Verify(
            p => p.LogWarning(
                "ArrIntegration",
                It.Is<string>(msg => msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    // (Tested here via ArrIntegrationService which uses request.Headers.Add() directly on
    //  per-request HttpRequestMessage — this is safe and does not need the fix.
    //  The SeerrIntegrationService-specific test lives in SeerrIntegrationServiceTests.cs
    //  via the existing TestConnection_SetsApiKeyHeader test that validates the header is set.)

    [Fact]
    public async Task TestConnectionAsync_ApiKeyWithCrlf_ThrowsArgumentException()
    {
        var handler = new Mock<HttpMessageHandler>();
        var service = CreateService(handler.Object);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.TestConnectionAsync("http://radarr.local", "key\r\nX-Injected: evil", CancellationToken.None));
        Assert.Contains("CR, LF", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRadarrMoviesAsync_ApiKeyWithCrLf_ThrowsArgumentException()
    {
        var handler = new Mock<HttpMessageHandler>();
        var service = CreateService(handler.Object);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetRadarrMoviesAsync("http://radarr.local", "key\r\nX-Injected: evil", CancellationToken.None));
        Assert.Contains("CR, LF", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSonarrSeriesAsync_ApiKeyWithCrLf_ThrowsArgumentException()
    {
        var handler = new Mock<HttpMessageHandler>();
        var service = CreateService(handler.Object);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetSonarrSeriesAsync("http://sonarr.local", "key\nX-Injected: evil", CancellationToken.None));
        Assert.Contains("CR, LF", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // === HttpClient not disposed (IHttpClientFactory contract) ===

    [Fact]
    public async Task TestConnection_HttpClientNotDisposed_FactoryClientCanBeReused()
    {
        // IHttpClientFactory clients must NOT be disposed by callers — the factory manages handler lifetime.
        // This test ensures the client returned by the factory is still usable after TestConnectionAsync returns,
        // which would throw ObjectDisposedException if the service had incorrectly called client.Dispose().
        var json = "{\"appName\":\"Radarr\",\"version\":\"5.0\"}";
        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var httpClient = new HttpClient(handler.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);
        var service = new ArrIntegrationService(factoryMock.Object, TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<ArrIntegrationService>().Object);

        await service.TestConnectionAsync("http://arr.local", "apikey", CancellationToken.None);

        // After the call, the client must NOT be disposed — reuse it to verify.
        var ex = Record.Exception(() => httpClient.BaseAddress);
        Assert.Null(ex); // ObjectDisposedException would be thrown here if client was disposed
    }

    [Fact]
    public async Task GetRadarrMoviesAsync_HttpClientNotDisposed_FactoryClientCanBeReused()
    {
        var json = "[{\"title\":\"Movie\",\"year\":2020,\"imdbId\":\"tt1\",\"tmdbId\":1,\"hasFile\":true,\"path\":\"/movies/Movie\"}]";
        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var httpClient = new HttpClient(handler.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);
        var service = new ArrIntegrationService(factoryMock.Object, TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<ArrIntegrationService>().Object);

        await service.GetRadarrMoviesAsync("http://arr.local", "apikey", CancellationToken.None);

        var ex = Record.Exception(() => httpClient.BaseAddress);
        Assert.Null(ex);
    }

    [Fact]
    public async Task GetSonarrSeriesAsync_HttpClientNotDisposed_FactoryClientCanBeReused()
    {
        var json = "[{\"title\":\"Show\",\"year\":2020,\"imdbId\":\"tt2\",\"tvdbId\":2,\"tmdbId\":3,\"path\":\"/shows/Show\",\"statistics\":{\"episodeFileCount\":5,\"totalEpisodeCount\":10}}]";
        var handler = CreateMockHandler(HttpStatusCode.OK, json);
        var httpClient = new HttpClient(handler.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("ArrIntegration")).Returns(httpClient);
        var service = new ArrIntegrationService(factoryMock.Object, TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<ArrIntegrationService>().Object);

        await service.GetSonarrSeriesAsync("http://arr.local", "apikey", CancellationToken.None);

        var ex = Record.Exception(() => httpClient.BaseAddress);
        Assert.Null(ex);
    }

    // ArgumentException from ValidateArrUrl is caught inside each method:
    // TestConnectionAsync returns (false, ...), GetRadarrMoviesAsync/GetSonarrSeriesAsync return null.

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://internal.host/data")]
    [InlineData("ldap://internal.host")]
    [InlineData("javascript:alert(1)")]
    public async Task TestConnection_NonHttpScheme_ReturnsFalse(string url)
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(handler.Object);

        var (success, _) = await service.TestConnectionAsync(url, "apikey", CancellationToken.None);

        Assert.False(success);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://internal.host/data")]
    public async Task GetRadarrMoviesAsync_NonHttpScheme_ReturnsNull(string url)
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "[]");
        var service = CreateService(handler.Object);

        var result = await service.GetRadarrMoviesAsync(url, "apikey", CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://internal.host/data")]
    public async Task GetSonarrSeriesAsync_NonHttpScheme_ReturnsNull(string url)
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "[]");
        var service = CreateService(handler.Object);

        var result = await service.GetSonarrSeriesAsync(url, "apikey", CancellationToken.None);

        Assert.Null(result);
    }

    // === 100 MB response body guard (#28) ===

    [Fact]
    public async Task TestConnection_ResponseExceeds100MB_ReturnsFalse()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
                response.Content.Headers.ContentLength = 101L * 1024 * 1024;
                return response;
            });

        var service = CreateService(handlerMock.Object);

        var (success, message) = await service.TestConnectionAsync("http://arr.local", "apikey", CancellationToken.None);
        Assert.False(success);
        Assert.Contains("too large", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRadarrMoviesAsync_ResponseExceeds100MB_ReturnsNull()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                };
                response.Content.Headers.ContentLength = 101L * 1024 * 1024;
                return response;
            });

        var service = CreateService(handlerMock.Object);

        var result = await service.GetRadarrMoviesAsync("http://arr.local", "apikey", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSonarrSeriesAsync_ResponseExceeds100MB_ReturnsNull()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                };
                response.Content.Headers.ContentLength = 101L * 1024 * 1024;
                return response;
            });

        var service = CreateService(handlerMock.Object);

        var result = await service.GetSonarrSeriesAsync("http://arr.local", "apikey", CancellationToken.None);
        Assert.Null(result);
    }

    // === Chunked response guard: null ContentLength must not bypass the 100 MB limit ===
    // A chunked transfer-encoded response has ContentLength == null. The previous guard
    // (`ContentLength > limit`) silently passed null through. ReadLimitedAsync now enforces
    // the limit via a stream-based byte counter regardless of ContentLength.

    private static HttpContent MakeChunkedContent(int sizeBytes)
    {
        // Simulate chunked encoding: ContentLength remains null (not set) while the
        // stream yields the requested number of bytes.
        var bytes = new byte[sizeBytes];
        // Fill with valid JSON array so the body itself doesn't cause a parse error
        // before the size guard triggers (only relevant for under-limit tests).
        bytes[0] = (byte)'[';
        bytes[sizeBytes - 1] = (byte)']';
        var content = new ByteArrayContent(bytes);
        content.Headers.Remove("Content-Length"); // ensure no length header
        return content;
    }

    [Fact]
    public async Task TestConnection_ChunkedResponseExceeds100MB_ReturnsFalse()
    {
        const int over100Mb = 100 * 1024 * 1024 + 1;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeChunkedContent(over100Mb)
            });

        var service = CreateService(handlerMock.Object);

        var (success, message) = await service.TestConnectionAsync("http://arr.local", "apikey", CancellationToken.None);
        Assert.False(success);
        Assert.Contains("too large", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRadarrMoviesAsync_ChunkedResponseExceeds100MB_ReturnsNull()
    {
        const int over100Mb = 100 * 1024 * 1024 + 1;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeChunkedContent(over100Mb)
            });

        var service = CreateService(handlerMock.Object);

        var result = await service.GetRadarrMoviesAsync("http://arr.local", "apikey", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSonarrSeriesAsync_ChunkedResponseExceeds100MB_ReturnsNull()
    {
        const int over100Mb = 100 * 1024 * 1024 + 1;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeChunkedContent(over100Mb)
            });

        var service = CreateService(handlerMock.Object);

        var result = await service.GetSonarrSeriesAsync("http://arr.local", "apikey", CancellationToken.None);
        Assert.Null(result);
    }

    // === Comparer identity fix: ReferenceEquals correctly skips defensive copy ===

    [Fact]
    public void CompareRadarr_OrdinalIgnoreCaseComparer_IsNotCopied()
    {
        // Jellyfin folder in LOWERCASE, Arr path in MixedCase — only OrdinalIgnoreCase matches.
        // This ensures the ReferenceEquals fast-path actually exercises case-insensitive lookup,
        // not just a same-case coincidental match that would pass under any comparer.
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "movie a (2020)" };
        var movies = new[] { new ArrMovie { Title = "Movie A", Year = 2020, HasFile = true, Path = "/m/Movie A (2020)" } };

        var result = ArrIntegrationService.CompareRadarrWithJellyfin(movies, jellyfinFolders);

        Assert.Single(result.InBoth);
    }

    [Fact]
    public void CompareSonarr_OrdinalIgnoreCaseComparer_IsNotCopied()
    {
        // Jellyfin folder in LOWERCASE, Arr path in MixedCase — only OrdinalIgnoreCase matches.
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "show a" };
        var series = new[] { new ArrSeries { Title = "Show A", Year = 2020, EpisodeFileCount = 5, TotalEpisodeCount = 10, Path = "/tv/Show A" } };

        var result = ArrIntegrationService.CompareSonarrWithJellyfin(series, jellyfinFolders);

        Assert.Single(result.InBoth);
    }

    [Fact]
    public void CompareRadarr_NonOrdinalComparer_MakesDefensiveCopy()
    {
        // When the caller uses a different comparer (e.g. Ordinal), a defensive copy with
        // OrdinalIgnoreCase is made, ensuring case-insensitive matching still works.
        var jellyfinFolders = new HashSet<string>(StringComparer.Ordinal) { "movie a (2020)" };
        var movies = new[] { new ArrMovie { Title = "Movie A", Year = 2020, HasFile = true, Path = "/m/Movie A (2020)" } };

        var result = ArrIntegrationService.CompareRadarrWithJellyfin(movies, jellyfinFolders);

        // OrdinalIgnoreCase copy must match "Movie A (2020)" against "movie a (2020)"
        Assert.Single(result.InBoth);
    }
}