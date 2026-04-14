using System.Net;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
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
        return new ArrIntegrationService(factoryMock.Object, new Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.PluginLogService(), logger.Object);
    }

    private static Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, string content)
        => TestMockFactory.CreateHttpMessageHandler(statusCode, content);

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

        var (success, message) = await service.TestConnectionAsync(null!, "apikey123");

        Assert.False(success);
    }

    [Fact]
    public async Task TestConnection_NullApiKey_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "{}");
        var service = CreateService(handler.Object);

        var (success, message) = await service.TestConnectionAsync("http://localhost:7878", null!);

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

        var (success, message) = await service.TestConnectionAsync("http://localhost:7878", "testapikey");

        Assert.False(success);
    }

    [Fact]
    public async Task TestConnection_InvalidJson_ReturnsFalse()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "not-json");
        var service = CreateService(handler.Object);

        var (success, message) = await service.TestConnectionAsync("http://localhost:7878", "testapikey");

        // The service deserializes the JSON; with invalid JSON it may still succeed
        // (returning null appName) or fail depending on implementation.
        // Our implementation deserializes with JsonSerializer which throws JsonException for "not-json".
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
                    !req.RequestUri.AbsoluteUri.Contains("//api")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json),
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
                Content = new StringContent(json),
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
                Content = new StringContent(json),
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
        cts.Cancel();

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

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => service.TestConnectionAsync("http://localhost:7878", "testapikey", cts.Token));
    }

    // === GetRadarrMoviesAsync ===

    [Fact]
    public async Task GetRadarrMovies_EmptyUrl_ReturnsEmptyList()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "[]");
        var service = CreateService(handler.Object);

        var movies = await service.GetRadarrMoviesAsync(string.Empty, "apikey");

        Assert.Empty(movies);
    }

    [Fact]
    public async Task GetRadarrMovies_EmptyApiKey_ReturnsEmptyList()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "[]");
        var service = CreateService(handler.Object);

        var movies = await service.GetRadarrMoviesAsync("http://localhost:7878", string.Empty);

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

        Assert.Equal(2, movies.Count);
        Assert.Equal("The Matrix", movies[0].Title);
        Assert.Equal(1999, movies[0].Year);
        Assert.True(movies[0].HasFile);
        Assert.Equal("Inception", movies[1].Title);
        Assert.False(movies[1].HasFile);
    }

    [Fact]
    public async Task GetRadarrMovies_ServerError_ReturnsEmptyList()
    {
        var handler = CreateMockHandler(HttpStatusCode.InternalServerError, "Error");
        var service = CreateService(handler.Object);

        var movies = await service.GetRadarrMoviesAsync("http://localhost:7878", "testapikey");

        Assert.Empty(movies);
    }

    // === GetSonarrSeriesAsync ===

    [Fact]
    public async Task GetSonarrSeries_EmptyUrl_ReturnsEmptyList()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, "[]");
        var service = CreateService(handler.Object);

        var series = await service.GetSonarrSeriesAsync(string.Empty, "apikey");

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

        Assert.Equal(2, series.Count);
        Assert.Equal("Breaking Bad", series[0].Title);
        Assert.Equal(62, series[0].EpisodeFileCount);
        Assert.Equal("The Wire", series[1].Title);
        Assert.Equal(0, series[1].EpisodeFileCount);
    }

    [Fact]
    public async Task GetSonarrSeries_ServerError_ReturnsEmptyList()
    {
        var handler = CreateMockHandler(HttpStatusCode.InternalServerError, "Error");
        var service = CreateService(handler.Object);

        var series = await service.GetSonarrSeriesAsync("http://localhost:8989", "testapikey");

        Assert.Empty(series);
    }

    // === CompareRadarrWithJellyfin ===

    [Fact]
    public void CompareRadarr_MoviesInBoth_AreDetected()
    {
        var movies = new[]
        {
            new ArrMovie { Title = "The Matrix", Year = 1999, HasFile = true, Path = "/movies/The Matrix (1999)" },
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
            new ArrMovie { Title = "Inception", Year = 2010, HasFile = true, Path = "/movies/Inception (2010)" },
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
            new ArrMovie { Title = "Future Movie", Year = 2025, HasFile = false, Path = "/movies/Future Movie (2025)" },
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
            new ArrSeries { Title = "Breaking Bad", Year = 2008, Path = "/tv/Breaking Bad", EpisodeFileCount = 62, TotalEpisodeCount = 62 },
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
            new ArrSeries { Title = "The Wire", Year = 2002, Path = "/tv/The Wire", EpisodeFileCount = 60, TotalEpisodeCount = 60 },
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
            new ArrSeries { Title = "Upcoming Show", Year = 2025, Path = "/tv/Upcoming Show", EpisodeFileCount = 0, TotalEpisodeCount = 10 },
        };
        var jellyfinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = ArrIntegrationService.CompareSonarrWithJellyfin(series, jellyfinFolders);

        Assert.Single(result.InArrOnlyMissing);
    }
}
