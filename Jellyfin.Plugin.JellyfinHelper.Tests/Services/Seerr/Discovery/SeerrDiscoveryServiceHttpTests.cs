using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
        // Only http/https allowed - file://, ftp://, javascript: etc must be rejected.
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
        // BUG GUARD: the earlier version of this test asserted `Contains("42")` on the
        // raw JSON string, which also matches the existing mediaId 1234 - so a broken
        // implementation that dropped the userId field entirely (or wrote the wrong
        // value) would still pass. We now parse the payload as JSON and assert the
        // strongly-typed value of the `userId` property.
        _handler.RegisterResponse(HttpMethod.Post, "/api/v1/request", HttpStatusCode.Created, "{}");

        await _sut.SubmitRequestAsync(1234, "movie", 42, null, null, null, CancellationToken.None);

        Assert.NotNull(_handler.LastRequestBody);

        using var payload = JsonDocument.Parse(_handler.LastRequestBody!);
        // The property MUST exist and carry exactly 42.
        Assert.True(payload.RootElement.TryGetProperty("userId", out var userIdElement),
            $"payload missing 'userId' property; body was: {_handler.LastRequestBody}");
        Assert.Equal(42, userIdElement.GetInt32());
        // Sanity: the mediaId was serialised alongside as expected - this makes the
        // parsing sanity check tight and future-proofs against accidental field renames.
        Assert.True(payload.RootElement.TryGetProperty("mediaId", out var mediaIdElement));
        Assert.Equal(1234, mediaIdElement.GetInt32());
    }

    [Fact]
    public async Task SubmitRequestAsync_ZeroSeerrUserId_NotIncludedInPayload()
    {
        // Contract: only positive user IDs are forwarded - 0 or null means "use API key owner".
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
    public async Task GetSeerrUsersAsync_MultiPage_FetchesAllPages()
    {
        // BUG GUARD: pagination loop must continue when results.Count == take AND currentPage < totalPages.
        // Page 1 must return exactly 50 results (== take) so the loop does not early-exit on the
        // "fewer than take results" guard before checking totalPages.

        // Build 50 users for page 1 (ids 1–50)
        var page1Results = string.Join(",\n", Enumerable.Range(1, 50).Select(i =>
            $"{{ \"id\": {i}, \"displayName\": \"user{i:D3}\", \"jellyfinUserId\": \"{i:D32}\" }}"));
        var page1Json = $$"""
        {
          "pageInfo": { "pages": 2, "pageSize": 50, "results": 51, "page": 1 },
          "results": [{{page1Results}}]
        }
        """;
        const string page2Json = """
        {
          "pageInfo": { "pages": 2, "pageSize": 50, "results": 51, "page": 2 },
          "results": [
            { "id": 51, "displayName": "carol", "jellyfinUserId": "cccccccccccccccccccccccccccccccc" }
          ]
        }
        """;

        _handler.RegisterResponse(HttpMethod.Get, "skip=0&sort=displayname", HttpStatusCode.OK, page1Json);
        _handler.RegisterResponse(HttpMethod.Get, "skip=50&sort=displayname", HttpStatusCode.OK, page2Json);

        var users = await _sut.GetSeerrUsersAsync(CancellationToken.None);

        Assert.Equal(51, users.Count);
        Assert.Contains(users, u => u.DisplayName == "user001");
        Assert.Contains(users, u => u.DisplayName == "carol");
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
    public async Task GetServiceInfoAsync_InvalidServiceType_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.GetServiceInfoAsync("plex", CancellationToken.None));
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
        // Empty roster (upstream error) means "temporary unavailable" - must set IsTransient=true.
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

    // -----------------------------------------------------------------------
    // Happy-path branches for GetUserRequestPermissionsAsync
    //
    // Coverage report flagged Step 2..5 of the method as untested (14 of the 22
    // cyclomatic branches were unhit). The tests below execute:
    //   - user found but LACKS Request permission (Step 2 denial)
    //   - user found + can request + no services configured (Step 3 short-return)
    //   - user found + can request + service lookup transient failure (Step 3 fallback)
    //   - user found + admin → all profiles exposed (Step 4)
    //   - user found + normal → only default profiles exposed (Step 5)
    // Each test also transitively exercises GetServiceInfoWithStatusAsync and
    // BuildAllowedProfileList, so a single test file lift bumps coverage on
    // three separate methods at once.
    //
    // JellyfinUserId shape: the discovery service matches via GUID with hyphens
    // stripped and case-lowered (see FindSeerrUserByJellyfinId). We use a fixed
    // GUID here for reproducibility and its lowercase-no-hyphens form for the
    // Seerr roster payload.
    // -----------------------------------------------------------------------

    private static readonly Guid LinkedJellyfinUserId = new("11111111-2222-3333-4444-555555555555");
    private const string LinkedJellyfinUserIdJson = "11111111222233334444555555555555";

    [Fact]
    public async Task GetUserRequestPermissionsAsync_UserLacksRequestPermission_ReturnsCannotRequest()
    {
        // BUG GUARD: user is correctly linked (jellyfinUserId matches) but their permissions
        // bitmask does NOT include Request (32), RequestMovie (1024), RequestTv (2048), or
        // Admin (2). The service MUST refuse the request with a NON-transient denial -
        // upgrading them to "transient" would let a client-side retry loop hammer Seerr
        // indefinitely on what is really a persistent authorization failure.
        var json = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [
            { "id": 1, "displayName": "readonly-user", "jellyfinUserId": "{{LinkedJellyfinUserIdJson}}", "permissions": 64 }
          ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, json);

        var result = await _sut.GetUserRequestPermissionsAsync(
            LinkedJellyfinUserId, "movie", "radarr", CancellationToken.None);

        Assert.False(result.CanRequest);
        Assert.False(result.IsTransient);
        Assert.NotNull(result.DeniedReason);
        Assert.Contains("permission", result.DeniedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUserRequestPermissionsAsync_UserCanRequest_NoServicesConfigured_ReturnsSuccessWithEmptyProfiles()
    {
        // BUG GUARD: user has Request perm, service list is empty (Seerr admin never linked
        // any Radarr/Sonarr). The service must ALLOW the request with an empty profile list
        // (Seerr's own server defaults will apply). Denying here would break the "single-user
        // no-config" install path where the operator wants to submit a request and let Seerr
        // route it to whatever default is configured server-side.
        var userJson = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [
            { "id": 1, "displayName": "requester", "jellyfinUserId": "{{LinkedJellyfinUserIdJson}}", "permissions": 32 }
          ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, userJson);
        // Successful fetch but empty list - this is the "configured Seerr with no Arr servers" case.
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr", HttpStatusCode.OK, "[]");

        var result = await _sut.GetUserRequestPermissionsAsync(
            LinkedJellyfinUserId, "movie", "radarr", CancellationToken.None);

        Assert.True(result.CanRequest);
        Assert.False(result.IsTransient);
        Assert.Empty(result.Profiles);
    }

    [Fact]
    public async Task GetUserRequestPermissionsAsync_UserCanRequest_ServiceLookupFails_StillAllowsRequestWithoutProfiles()
    {
        // BUG GUARD: user has Request perm, but Seerr's /service/radarr endpoint fails
        // (500 Internal Server Error). This is a TRANSIENT failure - the service must
        // still allow the request (with empty profiles = Seerr server-side defaults)
        // rather than blocking the user on what could be a 5-second outage. Blocking
        // here would produce visible "we can't verify your Seerr" errors on every retry
        // during any Seerr degradation.
        var userJson = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [
            { "id": 1, "displayName": "requester", "jellyfinUserId": "{{LinkedJellyfinUserIdJson}}", "permissions": 32 }
          ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, userJson);
        // Service list returns 500 → GetServiceInfoWithStatusAsync returns ([], false).
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr", HttpStatusCode.InternalServerError, "");

        var result = await _sut.GetUserRequestPermissionsAsync(
            LinkedJellyfinUserId, "movie", "radarr", CancellationToken.None);

        Assert.True(result.CanRequest, "transient Seerr failure must not block requests");
        Assert.Empty(result.Profiles);
    }

    // -----------------------------------------------------------------------
    // Step 4 - quality-profile exposure depends on user's permission level.
    //
    // Admin / ManageRequests / RequestAdvanced  → filterToDefault=false → ALL profiles.
    // Normal Request-only user                  → filterToDefault=true  → default profile only.
    //
    // These tests cover the two branches of BuildAllowedProfileList that the earlier
    // happy-path tests leave dark (they return before reaching Step 4 because the
    // service list is empty).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetUserRequestPermissionsAsync_AdminUser_ExposesAllProfiles()
    {
        // BUG GUARD: admin users must receive ALL configured profiles (filterToDefault=false).
        // A regression that forces filterToDefault=true for admins would silently strip profile
        // choice from every admin request form - they would see only the server's active profile
        // and have no way to override quality without knowing the workaround.
        //
        // Permissions=2 → Admin. Service list: one Radarr server, two profiles (HD=100, 4K=200).
        var userJson = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [
            { "id": 1, "displayName": "admin-user", "jellyfinUserId": "{{LinkedJellyfinUserIdJson}}", "permissions": 2 }
          ]
        }
        """;
        const string listJson = """[ { "id": 1, "name": "Radarr", "isDefault": true, "is4k": false } ]""";
        const string detailJson = """
        {
          "id": 1, "name": "Radarr",
          "profiles": [ { "id": 100, "name": "HD" }, { "id": 200, "name": "4K" } ],
          "rootFolders": [ { "path": "/movies" } ],
          "activeProfileId": 100,
          "activeDirectory": "/movies"
        }
        """;

        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, userJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr", HttpStatusCode.OK, listJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr/1", HttpStatusCode.OK, detailJson);

        var result = await _sut.GetUserRequestPermissionsAsync(
            LinkedJellyfinUserId, "movie", "radarr", CancellationToken.None);

        Assert.True(result.CanRequest);
        // Admin → all profiles exposed: HD (default) + 4K (non-default) - both for the single root folder.
        Assert.Equal(2, result.Profiles.Count);
        Assert.Contains(result.Profiles, p => p.ProfileId == 100);
        Assert.Contains(result.Profiles, p => p.ProfileId == 200);
    }

    [Fact]
    public async Task GetUserRequestPermissionsAsync_NormalUser_ExposesOnlyDefaultProfile()
    {
        // BUG GUARD: a user with only Request (32) must be restricted to the server's active profile.
        // Over-exposing all profiles to normal users bypasses the Seerr quality-tier access model
        // and could allow unprivileged users to request 4K content they are not entitled to.
        //
        // Permissions=32 → Request only (no RequestAdvanced/ManageRequests/Admin).
        var userJson = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [
            { "id": 1, "displayName": "normal-user", "jellyfinUserId": "{{LinkedJellyfinUserIdJson}}", "permissions": 32 }
          ]
        }
        """;
        const string listJson = """[ { "id": 1, "name": "Radarr", "isDefault": true, "is4k": false } ]""";
        const string detailJson = """
        {
          "id": 1, "name": "Radarr",
          "profiles": [ { "id": 100, "name": "HD" }, { "id": 200, "name": "4K" } ],
          "rootFolders": [ { "path": "/movies" } ],
          "activeProfileId": 100,
          "activeDirectory": "/movies"
        }
        """;

        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, userJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr", HttpStatusCode.OK, listJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr/1", HttpStatusCode.OK, detailJson);

        var result = await _sut.GetUserRequestPermissionsAsync(
            LinkedJellyfinUserId, "movie", "radarr", CancellationToken.None);

        Assert.True(result.CanRequest);
        // Normal user → only the default (active) profile.
        var profile = Assert.Single(result.Profiles);
        Assert.Equal(100, profile.ProfileId);
        Assert.Equal("HD", profile.ProfileName);
        Assert.True(profile.IsDefault);
    }

    [Fact]
    public async Task GetUserRequestPermissionsAsync_RequestAdvancedUser_ExposesAllProfiles()
    {
        // Users with RequestAdvanced (2097152) should be treated the same as admin for
        // profile exposure. This verifies the CanSelectQualityProfile() path that grants
        // full-profile access via RequestAdvanced without also having Admin/ManageRequests.
        var userJson = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [
            { "id": 1, "displayName": "power-user", "jellyfinUserId": "{{LinkedJellyfinUserIdJson}}", "permissions": 2097184 }
          ]
        }
        """;
        // permissions: 32 (Request) | 2097152 (RequestAdvanced) = 2097184
        const string listJson = """[ { "id": 1, "name": "Radarr", "isDefault": true, "is4k": false } ]""";
        const string detailJson = """
        {
          "id": 1, "name": "Radarr",
          "profiles": [ { "id": 100, "name": "HD" }, { "id": 200, "name": "4K" } ],
          "rootFolders": [ { "path": "/movies" } ],
          "activeProfileId": 100,
          "activeDirectory": "/movies"
        }
        """;

        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, userJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr", HttpStatusCode.OK, listJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/radarr/1", HttpStatusCode.OK, detailJson);

        var result = await _sut.GetUserRequestPermissionsAsync(
            LinkedJellyfinUserId, "movie", "radarr", CancellationToken.None);

        Assert.True(result.CanRequest);
        Assert.Equal(2, result.Profiles.Count);
    }

    [Fact]
    public async Task GetUserRequestPermissionsAsync_SonarrServiceType_HappyPath_AdminUser()
    {
        // Verifies that "sonarr" is accepted as a valid serviceType and routes to the correct
        // /api/v1/service/sonarr endpoint. Previously, only radarr paths were exercised.
        var userJson = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [
            { "id": 1, "displayName": "admin-user", "jellyfinUserId": "{{LinkedJellyfinUserIdJson}}", "permissions": 2 }
          ]
        }
        """;
        const string listJson = """[ { "id": 3, "name": "Sonarr", "isDefault": true, "is4k": false } ]""";
        const string detailJson = """
        {
          "id": 3, "name": "Sonarr",
          "profiles": [ { "id": 10, "name": "HDTV" } ],
          "rootFolders": [ { "path": "/tv" } ],
          "activeProfileId": 10,
          "activeDirectory": "/tv"
        }
        """;

        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, userJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/sonarr", HttpStatusCode.OK, listJson);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/service/sonarr/3", HttpStatusCode.OK, detailJson);

        var result = await _sut.GetUserRequestPermissionsAsync(
            LinkedJellyfinUserId, "tv", "sonarr", CancellationToken.None);

        Assert.True(result.CanRequest);
        Assert.Single(result.Profiles);
        Assert.Equal(10, result.Profiles[0].ProfileId);
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
        var fullUrl = request.RequestUri?.PathAndQuery ?? string.Empty;
        foreach (var kvp in _routes)
        {
            if (kvp.Key.Method == request.Method &&
                (url.EndsWith(kvp.Key.PathSuffix, StringComparison.Ordinal) ||
                 fullUrl.EndsWith(kvp.Key.PathSuffix, StringComparison.Ordinal)))
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
