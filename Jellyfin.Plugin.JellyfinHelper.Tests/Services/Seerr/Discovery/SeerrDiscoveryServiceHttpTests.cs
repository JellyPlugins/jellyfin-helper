using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests <see cref="SeerrDiscoveryService"/>'s HTTP-driven public API surface using a
///     scripted <see cref="HttpMessageHandler"/> that pattern-matches on request URIs.
///     Covers <c>SubmitRequestAsync</c>, <c>GetServiceInfoAsync</c>, <c>GetSeerrUsersAsync</c>,
///     <c>ResolveSeerrUserIdAsync</c> and <c>GetUserRequestPermissionsAsync</c>.
///     Belongs to <c>ConfigOverride</c> because it mutates <c>Plugin.Instance.Configuration</c>.
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryServiceHttpTests : IDisposable
{
    private readonly List<HttpResponseMessage> _responsesInFlight = [];
    private readonly ScriptedHttpHandler _handler;
    private readonly Mock<IHttpClientFactory> _httpFactoryMock;
    private readonly SeerrDiscoveryService _sut;
    private readonly DiscoveryCacheService _cache;

    public SeerrDiscoveryServiceHttpTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-api-key";

        _handler = new ScriptedHttpHandler();

        _httpFactoryMock = new Mock<IHttpClientFactory>();
        _httpFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_handler, disposeHandler: false));

        var history = new Mock<IWatchHistoryService>();
        var arr = new Mock<IArrIntegrationService>();
        var learned = new LearnedScoringStrategy(null, new Mock<ILogger<LearnedScoringStrategy>>().Object);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy(null, new Mock<ILogger<NeuralScoringStrategy>>().Object);
        var ensemble = new EnsembleScoringStrategy(
            learned, heuristic, neural, null,
            EnsembleScoringStrategy.DefaultAlphaMin,
            EnsembleScoringStrategy.DefaultAlphaMax,
            EnsembleScoringStrategy.DefaultGenrePenaltyFloor,
            new Mock<ILogger<EnsembleScoringStrategy>>().Object);
        var pluginLog = new Mock<IPluginLogService>();
        _cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object);
        var feedbackStore = new Mock<IDiscoveryFeedbackStore>();

        _sut = new SeerrDiscoveryService(
            _httpFactoryMock.Object,
            history.Object,
            arr.Object,
            ensemble,
            _cache,
            feedbackStore.Object,
            pluginLog.Object,
            new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    public void Dispose()
    {
        foreach (var r in _responsesInFlight)
        {
            r.Dispose();
        }

        _handler.Dispose();
        _cache.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
    }

    // ============================================================
    // SubmitRequestAsync
    // ============================================================

    [Fact]
    public async Task SubmitRequestAsync_HappyPath_MovieRequest_ReturnsSuccess()
    {
        _handler.RegisterResponse(HttpMethod.Post, "/api/v1/request", HttpStatusCode.Created, "{}");

        var (success, message) = await _sut.SubmitRequestAsync(
            1234, "movie", null, null, null, null, CancellationToken.None);

        Assert.True(success);
        Assert.Contains("submitted", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequestAsync_HappyPath_TvRequest_IncludesSeasonsAllInPayload()
    {
        // BUG GUARD: Overseerr crashes if TV requests omit "seasons". The service must
        // always add "seasons": "all" for tv requests. We inspect the outgoing payload.
        // NOTE: JsonDefaults.Options uses WriteIndented=true, so the serialised payload
        // has spaces between keys and values (e.g. "seasons": "all"). We assert on the
        // property name only to stay resilient to whitespace formatting changes.
        _handler.RegisterResponse(HttpMethod.Post, "/api/v1/request", HttpStatusCode.Created, "{}");

        var (success, _) = await _sut.SubmitRequestAsync(
            999, "TV", null, null, null, null, CancellationToken.None);

        Assert.True(success);
        Assert.NotNull(_handler.LastRequestBody);
        Assert.Contains("\"seasons\"", _handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("\"all\"", _handler.LastRequestBody!, StringComparison.Ordinal);
        // Media type must be lowercased before wire submission (case-sensitive check).
        Assert.Contains("\"mediaType\"", _handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("\"tv\"", _handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitRequestAsync_MovieRequest_DoesNotIncludeSeasonsField()
    {
        // Symmetric to the TV BUG GUARD above: movie requests must NOT carry the seasons
        // field. Sending it may confuse Radarr backends that don't understand the key.
        _handler.RegisterResponse(HttpMethod.Post, "/api/v1/request", HttpStatusCode.Created, "{}");

        await _sut.SubmitRequestAsync(1234, "movie", null, null, null, null, CancellationToken.None);

        Assert.NotNull(_handler.LastRequestBody);
        Assert.DoesNotContain("\"seasons\"", _handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitRequestAsync_ServerReturnsError_ReturnsFailureWithStatusCode()
    {
        _handler.RegisterResponse(HttpMethod.Post, "/api/v1/request", HttpStatusCode.Conflict, "duplicate request");

        var (success, message) = await _sut.SubmitRequestAsync(
            1234, "movie", null, null, null, null, CancellationToken.None);

        Assert.False(success);
        // Only the status code, not the body, must be surfaced to the caller (info-disclosure guard).
        Assert.Contains("409", message, StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequestAsync_NegativeServerId_ReturnsFailure()
    {
        var (success, message) = await _sut.SubmitRequestAsync(
            1234, "movie", null, -1, null, null, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("serverId", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequestAsync_NegativeProfileId_ReturnsFailure()
    {
        var (success, message) = await _sut.SubmitRequestAsync(
            1234, "movie", null, null, -5, null, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("profileId", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequestAsync_InvalidBaseUrl_ReturnsFailure()
    {
        Plugin.Instance!.Configuration.SeerrUrl = "not-a-url";

        var (success, message) = await _sut.SubmitRequestAsync(
            1234, "movie", null, null, null, null, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("Invalid", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequestAsync_FtpBaseUrl_ReturnsFailure()
    {
        // Only http/https allowed — file://, ftp://, javascript: etc must be rejected.
        Plugin.Instance!.Configuration.SeerrUrl = "ftp://seerr.example.com";

        var (success, _) = await _sut.SubmitRequestAsync(
            1234, "movie", null, null, null, null, CancellationToken.None);

        Assert.False(success);
    }

    [Fact]
    public async Task SubmitRequestAsync_HttpRequestException_ReturnsFailure()
    {
        _handler.ThrowNext = new HttpRequestException("connection refused");

        var (success, message) = await _sut.SubmitRequestAsync(
            1234, "movie", null, null, null, null, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("failed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequestAsync_UpstreamTimeout_ReturnsTimeoutMessage()
    {
        // TaskCanceledException without cancellationToken firing represents a timeout.
        _handler.ThrowNext = new TaskCanceledException("timeout");

        var (success, message) = await _sut.SubmitRequestAsync(
            1234, "movie", null, null, null, null, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("timed out", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequestAsync_UserIdIncludedInPayloadWhenPositive()
    {
        // JsonDefaults.Options serialises with WriteIndented=true, so key:value pairs are
        // separated by ": " and lines are wrapped. We assert on presence of the property
        // name and the value separately to stay format-tolerant.
        _handler.RegisterResponse(HttpMethod.Post, "/api/v1/request", HttpStatusCode.Created, "{}");

        await _sut.SubmitRequestAsync(1234, "movie", 42, null, null, null, CancellationToken.None);

        Assert.NotNull(_handler.LastRequestBody);
        Assert.Contains("\"userId\"", _handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("42", _handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitRequestAsync_ZeroSeerrUserId_NotIncludedInPayload()
    {
        // Contract: only positive user IDs are forwarded — 0 or null means "use API key owner".
        // A defensive test to prevent the payload from carrying a nonsense userId=0.
        _handler.RegisterResponse(HttpMethod.Post, "/api/v1/request", HttpStatusCode.Created, "{}");

        await _sut.SubmitRequestAsync(1234, "movie", 0, null, null, null, CancellationToken.None);

        Assert.NotNull(_handler.LastRequestBody);
        Assert.DoesNotContain("\"userId\"", _handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitRequestAsync_RootFolderIncludedInPayloadWhenSet()
    {
        // Same rationale as SubmitRequestAsync_UserIdIncludedInPayloadWhenPositive:
        // JsonDefaults.Options is indented, so we assert on property name + value pairs
        // separately (not glued together) to stay resilient to whitespace formatting.
        _handler.RegisterResponse(HttpMethod.Post, "/api/v1/request", HttpStatusCode.Created, "{}");

        await _sut.SubmitRequestAsync(1234, "movie", null, 1, 2, "/movies/hd", CancellationToken.None);

        Assert.NotNull(_handler.LastRequestBody);
        Assert.Contains("\"rootFolder\"", _handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("/movies/hd", _handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("\"serverId\"", _handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("\"profileId\"", _handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitRequestAsync_CancellationTokenFired_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _sut.SubmitRequestAsync(1234, "movie", null, null, null, null, cts.Token));
    }

    // ============================================================
    // GetSeerrUsersAsync
    // ============================================================

    [Fact]
    public async Task GetSeerrUsersAsync_SinglePage_ReturnsUsers()
    {
        const string json = """
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 2, "page": 1 },
          "results": [
            { "id": 1, "displayName": "alice", "jellyfinUserId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            { "id": 2, "displayName": "bob",   "jellyfinUserId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
          ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, json);

        var users = await _sut.GetSeerrUsersAsync(CancellationToken.None);
        Assert.Equal(2, users.Count);
        Assert.Equal("alice", users[0].DisplayName);
    }

    [Fact]
    public async Task GetSeerrUsersAsync_UpstreamError_ReturnsEmptyList()
    {
        // BUG GUARD: A partial fetch must return empty rather than a truncated roster.
        // Callers assume "not in the roster → not linked". Handing them a partial list would
        // cause valid users to be treated as unlinked.
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.InternalServerError, "boom");

        var users = await _sut.GetSeerrUsersAsync(CancellationToken.None);
        Assert.Empty(users);
    }

    [Fact]
    public async Task GetSeerrUsersAsync_NotConfigured_ReturnsEmpty()
    {
        Plugin.Instance!.Configuration.SeerrUrl = string.Empty;

        var users = await _sut.GetSeerrUsersAsync(CancellationToken.None);
        Assert.Empty(users);
    }

    // ============================================================
    // ResolveSeerrUserIdAsync
    // ============================================================

    [Fact]
    public async Task ResolveSeerrUserIdAsync_EmptyGuid_ReturnsNull()
    {
        var result = await _sut.ResolveSeerrUserIdAsync(Guid.Empty, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveSeerrUserIdAsync_MatchByHyphenatedGuid_ReturnsUserId()
    {
        // BUG GUARD: Seerr may store the Jellyfin ID with OR without hyphens. The service
        // must normalise both sides before comparing. We store the ID with hyphens on Seerr
        // and query with the raw Guid.
        var jf = Guid.NewGuid();
        var stored = jf.ToString("D"); // With hyphens
        var json = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [ { "id": 77, "displayName": "match", "jellyfinUserId": "{{stored}}" } ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, json);

        var result = await _sut.ResolveSeerrUserIdAsync(jf, CancellationToken.None);
        Assert.Equal(77, result);
    }

    [Fact]
    public async Task ResolveSeerrUserIdAsync_MatchByHyphenlessGuid_ReturnsUserId()
    {
        var jf = Guid.NewGuid();
        var stored = jf.ToString("N"); // Without hyphens
        var json = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [ { "id": 88, "displayName": "match", "jellyfinUserId": "{{stored}}" } ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, json);

        var result = await _sut.ResolveSeerrUserIdAsync(jf, CancellationToken.None);
        Assert.Equal(88, result);
    }

    [Fact]
    public async Task ResolveSeerrUserIdAsync_NoMatch_ReturnsNull()
    {
        var json = """
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [ { "id": 99, "displayName": "stranger", "jellyfinUserId": "ffffffffffffffffffffffffffffffff" } ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, json);

        var result = await _sut.ResolveSeerrUserIdAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(result);
    }

    // ============================================================
    // GetServiceInfoAsync
    // ============================================================

    [Fact]
    public async Task GetServiceInfoAsync_InvalidServiceType_ReturnsEmpty()
    {
        var services = await _sut.GetServiceInfoAsync("plex", CancellationToken.None);
        Assert.Empty(services);
    }

    [Fact]
    public async Task GetServiceInfoAsync_NotConfigured_ReturnsEmpty()
    {
        Plugin.Instance!.Configuration.SeerrUrl = string.Empty;
        var services = await _sut.GetServiceInfoAsync("radarr", CancellationToken.None);
        Assert.Empty(services);
    }

    [Fact]
    public async Task GetServiceInfoAsync_HappyPath_EnrichesEachServer()
    {
        // First call: list of servers. Second call: details for server 5.
        const string listJson = """[ { "id": 5, "name": "Radarr", "isDefault": true, "is4k": false } ]""";
        const string detailJson = """
        {
          "id": 5,
          "name": "Radarr",
          "profiles": [ { "id": 100, "name": "1080p" } ],
          "rootFolders": [ { "path": "/movies" } ],
          "activeProfileId": 100,
          "activeDirectory": "/movies"
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr", HttpStatusCode.OK, listJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr/5", HttpStatusCode.OK, detailJson);

        var services = await _sut.GetServiceInfoAsync("radarr", CancellationToken.None);
        var svc = Assert.Single(services);
        Assert.Equal(5, svc.Id);
        Assert.Single(svc.Profiles);
        Assert.Single(svc.RootFolders);
        Assert.Equal(100, svc.ActiveProfileId);
    }

    [Fact]
    public async Task GetServiceInfoAsync_ListEndpointFails_ReturnsEmpty()
    {
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr", HttpStatusCode.Unauthorized, "");

        var services = await _sut.GetServiceInfoAsync("radarr", CancellationToken.None);
        Assert.Empty(services);
    }

    [Fact]
    public async Task GetServiceInfoAsync_ListEndpointReturnsEmptyArray_ReturnsEmpty()
    {
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr", HttpStatusCode.OK, "[]");

        var services = await _sut.GetServiceInfoAsync("radarr", CancellationToken.None);
        Assert.Empty(services);
    }

    [Fact]
    public async Task GetServiceInfoAsync_DetailFails_ServerKeptWithoutProfiles()
    {
        // Contract: if detail fetch fails, the server is still returned in the list
        // (partial data is better than dropping known configuration).
        const string listJson = """[ { "id": 7, "name": "Radarr-Dev", "isDefault": true, "is4k": false } ]""";
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr", HttpStatusCode.OK, listJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr/7", HttpStatusCode.InternalServerError, "");

        var services = await _sut.GetServiceInfoAsync("radarr", CancellationToken.None);
        var svc = Assert.Single(services);
        Assert.Equal(7, svc.Id);
        Assert.Empty(svc.Profiles);
    }

    // ============================================================
    // GetUserRequestPermissionsAsync
    // ============================================================

    [Fact]
    public async Task GetUserRequestPermissionsAsync_InvalidMediaType_ReturnsCannotRequest()
    {
        var result = await _sut.GetUserRequestPermissionsAsync(
            Guid.NewGuid(), "music", "radarr", CancellationToken.None);
        Assert.False(result.CanRequest);
        Assert.Contains("media", result.DeniedReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUserRequestPermissionsAsync_InvalidServiceType_ReturnsCannotRequest()
    {
        var result = await _sut.GetUserRequestPermissionsAsync(
            Guid.NewGuid(), "movie", "plex", CancellationToken.None);
        Assert.False(result.CanRequest);
        Assert.Contains("service", result.DeniedReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUserRequestPermissionsAsync_UsersListEmpty_ReturnsTransient()
    {
        // Empty roster (upstream error) means "temporary unavailable" — must set IsTransient=true.
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.InternalServerError, "");

        var result = await _sut.GetUserRequestPermissionsAsync(
            Guid.NewGuid(), "movie", "radarr", CancellationToken.None);

        Assert.False(result.CanRequest);
        Assert.True(result.IsTransient);
    }

    [Fact]
    public async Task GetUserRequestPermissionsAsync_UserNotInSeerr_ReturnsNonTransient()
    {
        // Roster fetched successfully but no matching user → permanent denial, not transient.
        var json = """
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [ { "id": 1, "displayName": "someone", "jellyfinUserId": "ffffffffffffffffffffffffffffffff", "permissions": 0 } ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, json);

        var result = await _sut.GetUserRequestPermissionsAsync(
            Guid.NewGuid(), "movie", "radarr", CancellationToken.None);

        Assert.False(result.CanRequest);
        Assert.False(result.IsTransient);
    }
}

/// <summary>
///     A scripted <see cref="HttpMessageHandler"/> that pattern-matches on request path suffixes
///     and returns a pre-registered response. Also captures the last request body so tests can
///     assert on outgoing payloads without hooking into the request pipeline.
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<(HttpMethod Method, string PathSuffix), (HttpStatusCode Status, string Body)> _routes =
        new();

    public string? LastRequestBody { get; private set; }

    public Exception? ThrowNext { get; set; }

    public void RegisterResponse(HttpMethod method, string pathSuffix, HttpStatusCode status, string body)
    {
        _routes[(method, pathSuffix)] = (status, body);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ThrowNext is not null)
        {
            var toThrow = ThrowNext;
            ThrowNext = null;
            throw toThrow;
        }

        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        var url = request.RequestUri?.AbsolutePath ?? string.Empty;
        foreach (var kvp in _routes)
        {
            if (kvp.Key.Method == request.Method && url.EndsWith(kvp.Key.PathSuffix, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(kvp.Value.Status)
                {
                    Content = new StringContent(kvp.Value.Body)
                };
            }
        }

        // No registered route → 404 so unregistered calls surface as failed assertions.
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No route registered for {request.Method} {url}")
        };
    }
}
