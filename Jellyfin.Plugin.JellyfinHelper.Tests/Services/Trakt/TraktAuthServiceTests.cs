using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt;

/// <summary>
///     Verifies the Trakt device-flow auth service: start, poll status mapping, token persistence, proactive
///     refresh on expiry, and disconnect. HTTP is scripted via a queued handler; no real network is touched.
/// </summary>
[Collection("ConfigOverride")]
public sealed class TraktAuthServiceTests : IDisposable
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    private readonly ITraktUserStore _store;
    private readonly Mock<IHttpClientFactory> _factory;
    private readonly DateTime _now = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public TraktAuthServiceTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        Plugin.Instance!.Configuration.TraktClientId = "client-id";
        Plugin.Instance!.Configuration.TraktClientSecret = "client-secret";

        var protector = TestMockFactory.CreateSecretProtector();
        _store = new TraktUserStore(protector, TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<TraktUserStore>().Object, dataPath: null);

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var (status, body) = _responses.Dequeue();
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            });

        _factory = new Mock<IHttpClientFactory>();
        _factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));
    }

    public void Dispose()
    {
        ControllerTestFactory.TeardownPluginInstance();
        GC.SuppressFinalize(this);
    }

    private TraktAuthService CreateService()
        => new(_factory.Object, _store, TestMockFactory.CreateSecretProtector(), TestMockFactory.CreatePluginLogService(), NullLogger<TraktAuthService>.Instance, () => _now);

    private void Enqueue(HttpStatusCode status, string body) => _responses.Enqueue((status, body));

    // Builds a service whose "Trakt" client throws TaskCanceledException with no outer cancellation,
    // reproducing an HttpClient.Timeout fire. The service must swallow it, not let it reach the caller.
    private TraktAuthService CreateTimingOutService()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("timeout"));

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));
        return new(factory.Object, _store, TestMockFactory.CreateSecretProtector(), TestMockFactory.CreatePluginLogService(), NullLogger<TraktAuthService>.Instance, () => _now);
    }

    [Fact]
    public async Task StartDeviceAuth_ReturnsNull_OnTimeout()
    {
        var result = await CreateTimingOutService().StartDeviceAuthAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task PollDeviceAuth_ReturnsError_OnTimeout()
    {
        var status = await CreateTimingOutService().PollDeviceAuthAsync(Guid.NewGuid(), "dev", CancellationToken.None);
        Assert.Equal(TraktDevicePollStatus.Error, status);
    }

    [Fact]
    public async Task GetValidAccessToken_ReturnsNull_WhenRefreshTimesOut()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "old", RefreshToken = "ref", ExpiresAtUtc = _now.AddMinutes(-1) }, CancellationToken.None);

        var token = await CreateTimingOutService().GetValidAccessTokenAsync(userId, CancellationToken.None);

        Assert.Null(token);
        // A timed-out refresh must leave the link intact so the user can retry.
        Assert.NotNull(_store.GetToken(userId));
    }

    [Fact]
    public async Task StartDeviceAuth_ReturnsCodes()
    {
        Enqueue(HttpStatusCode.OK, """{"device_code":"dev","user_code":"ABCD","verification_url":"https://trakt.tv/activate","expires_in":600,"interval":5}""");

        // The service protector differs from the store's, but the client secret is read from config via the
        // service's own protector; here we only need a non-empty client id, which is set in the ctor.
        var result = await CreateService().StartDeviceAuthAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("dev", result!.DeviceCode);
        Assert.Equal("ABCD", result.UserCode);
        Assert.Equal(600, result.ExpiresIn);
        Assert.Equal(5, result.Interval);
    }

    [Fact]
    public async Task StartDeviceAuth_ReturnsNull_WhenClientIdMissing()
    {
        Plugin.Instance!.Configuration.TraktClientId = string.Empty;
        var result = await CreateService().StartDeviceAuthAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, TraktDevicePollStatus.Pending)]
    [InlineData(HttpStatusCode.TooManyRequests, TraktDevicePollStatus.SlowDown)]
    [InlineData(HttpStatusCode.Gone, TraktDevicePollStatus.Expired)]
    [InlineData((HttpStatusCode)418, TraktDevicePollStatus.Denied)]
    [InlineData(HttpStatusCode.InternalServerError, TraktDevicePollStatus.Error)]
    public async Task PollDeviceAuth_MapsStatusCodes(HttpStatusCode http, TraktDevicePollStatus expected)
    {
        Enqueue(http, "{}");
        var status = await CreateService().PollDeviceAuthAsync(Guid.NewGuid(), "dev", CancellationToken.None);
        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task PollDeviceAuth_OnApproval_StoresTokenAndReturnsLinked()
    {
        var userId = Guid.NewGuid();
        Enqueue(HttpStatusCode.OK, """{"access_token":"acc","refresh_token":"ref","expires_in":7776000,"created_at":1893456000}""");

        var status = await CreateService().PollDeviceAuthAsync(userId, "dev", CancellationToken.None);

        Assert.Equal(TraktDevicePollStatus.Linked, status);
        var token = _store.GetToken(userId);
        Assert.NotNull(token);
        Assert.Equal("acc", token!.AccessToken);
        Assert.Equal("ref", token.RefreshToken);
        Assert.Equal(_now.AddSeconds(7776000), token.ExpiresAtUtc);
    }

    [Fact]
    public async Task PollDeviceAuth_EmptyDeviceCode_ReturnsError()
    {
        var status = await CreateService().PollDeviceAuthAsync(Guid.NewGuid(), "   ", CancellationToken.None);
        Assert.Equal(TraktDevicePollStatus.Error, status);
    }

    [Fact]
    public async Task PollDeviceAuth_AnchorsExpiryOnCreatedAt_NotLocalClock()
    {
        var userId = Guid.NewGuid();

        // created_at is one hour before this server's clock (clock drift): expiry must anchor on
        // Trakt's issue time (created_at + expires_in), not _utcNow() + expires_in.
        var createdAt = new DateTimeOffset(_now.AddHours(-1), TimeSpan.Zero).ToUnixTimeSeconds();
        Enqueue(HttpStatusCode.OK, $$"""{"access_token":"acc","refresh_token":"ref","expires_in":7776000,"created_at":{{createdAt}}}""");

        var status = await CreateService().PollDeviceAuthAsync(userId, "dev", CancellationToken.None);

        Assert.Equal(TraktDevicePollStatus.Linked, status);
        Assert.Equal(_now.AddHours(-1).AddSeconds(7776000), _store.GetToken(userId)!.ExpiresAtUtc);
    }

    [Fact]
    public async Task PollDeviceAuth_FallsBackToLocalClock_WhenCreatedAtAbsent()
    {
        var userId = Guid.NewGuid();

        // A response without created_at (0) falls back to the local clock so expiry stays well-defined.
        Enqueue(HttpStatusCode.OK, """{"access_token":"acc","refresh_token":"ref","expires_in":7776000}""");

        var status = await CreateService().PollDeviceAuthAsync(userId, "dev", CancellationToken.None);

        Assert.Equal(TraktDevicePollStatus.Linked, status);
        Assert.Equal(_now.AddSeconds(7776000), _store.GetToken(userId)!.ExpiresAtUtc);
    }

    [Fact]
    public async Task GetValidAccessToken_ReturnsStoredToken_WhenNotExpired()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "acc", RefreshToken = "ref", ExpiresAtUtc = _now.AddHours(1) }, CancellationToken.None);

        var token = await CreateService().GetValidAccessTokenAsync(userId, CancellationToken.None);

        Assert.Equal("acc", token);
    }

    [Fact]
    public async Task GetValidAccessToken_RefreshesExactlyOnce_WhenExpired()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "old", RefreshToken = "ref", ExpiresAtUtc = _now.AddMinutes(-1) }, CancellationToken.None);
        Enqueue(HttpStatusCode.OK, """{"access_token":"fresh","refresh_token":"ref2","expires_in":7776000,"created_at":1893456000}""");

        var token = await CreateService().GetValidAccessTokenAsync(userId, CancellationToken.None);

        Assert.Equal("fresh", token);
        // The refreshed pair must be persisted so the next call does not refresh again.
        Assert.Equal("fresh", _store.GetToken(userId)!.AccessToken);
        Assert.Equal("ref2", _store.GetToken(userId)!.RefreshToken);
    }

    [Fact]
    public async Task GetValidAccessToken_ReturnsNull_WhenRefreshFails()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "old", RefreshToken = "ref", ExpiresAtUtc = _now.AddMinutes(-1) }, CancellationToken.None);
        Enqueue(HttpStatusCode.Unauthorized, "{}");

        var token = await CreateService().GetValidAccessTokenAsync(userId, CancellationToken.None);

        Assert.Null(token);
        // The stored token must survive a failed refresh so the user can retry rather than losing the link silently.
        Assert.NotNull(_store.GetToken(userId));
    }

    [Fact]
    public async Task GetValidAccessToken_ReturnsNull_WhenUnlinked()
    {
        var token = await CreateService().GetValidAccessTokenAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(token);
    }

    [Fact]
    public async Task RefreshAccessToken_ReturnsStoredToken_WhenAlreadyRotated()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "acc", RefreshToken = "ref", ExpiresAtUtc = _now.AddHours(1) }, CancellationToken.None);

        // The stored token already differs from the rejected one: a concurrent refresh rotated it.
        // No Enqueue: the Strict handler throws on any HTTP, proving no grant is attempted.
        var token = await CreateService().RefreshAccessTokenAsync(userId, "rejected", CancellationToken.None);

        Assert.Equal("acc", token);
    }

    [Fact]
    public async Task RefreshAccessToken_ForcesGrant_WhenStoredTokenRejected()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "acc", RefreshToken = "ref", ExpiresAtUtc = _now.AddHours(1) }, CancellationToken.None);
        Enqueue(HttpStatusCode.OK, """{"access_token":"forced","refresh_token":"ref2","expires_in":7776000,"created_at":1893456000}""");

        // A revoked token is usually far from expiry: the rejected token must still force a grant.
        var token = await CreateService().RefreshAccessTokenAsync(userId, "acc", CancellationToken.None);

        Assert.Equal("forced", token);
        Assert.Equal("forced", _store.GetToken(userId)!.AccessToken);
    }

    [Fact]
    public async Task RefreshAccessToken_ForcesGrant_WhenExpired()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "old", RefreshToken = "ref", ExpiresAtUtc = _now.AddMinutes(-1) }, CancellationToken.None);
        Enqueue(HttpStatusCode.OK, """{"access_token":"forced","refresh_token":"ref2","expires_in":7776000,"created_at":1893456000}""");

        var token = await CreateService().RefreshAccessTokenAsync(userId, "old", CancellationToken.None);

        Assert.Equal("forced", token);
        Assert.Equal("forced", _store.GetToken(userId)!.AccessToken);
        Assert.Equal("ref2", _store.GetToken(userId)!.RefreshToken);
    }

    [Fact]
    public async Task RefreshAccessToken_ReturnsNull_WhenUnlinked()
    {
        var token = await CreateService().RefreshAccessTokenAsync(Guid.NewGuid(), null, CancellationToken.None);
        Assert.Null(token);
    }

    [Fact]
    public async Task RefreshAccessToken_KeepsPreviousRefreshToken_WhenResponseOmitsIt()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "old", RefreshToken = "ref-keep", ExpiresAtUtc = _now.AddMinutes(-1) }, CancellationToken.None);
        Enqueue(HttpStatusCode.OK, """{"access_token":"forced","expires_in":7776000,"created_at":1893456000}""");

        var token = await CreateService().RefreshAccessTokenAsync(userId, "old", CancellationToken.None);

        Assert.Equal("forced", token);
        Assert.Equal("ref-keep", _store.GetToken(userId)!.RefreshToken);
        Assert.True(_store.GetToken(userId)!.IsLinked);
    }

    [Fact]
    public async Task Disconnect_RemovesStoredToken()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "acc", RefreshToken = "ref", ExpiresAtUtc = _now.AddHours(1) }, CancellationToken.None);

        await CreateService().DisconnectAsync(userId, CancellationToken.None);

        Assert.Null(_store.GetToken(userId));
    }

    [Fact]
    public async Task TestClientId_ReturnsSuccess_OnHttp200()
    {
        Enqueue(HttpStatusCode.OK, "[]");
        var (success, message) = await CreateService().TestClientIdAsync("client-id", CancellationToken.None);
        Assert.True(success);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TestClientId_ReturnsRejected_OnAuthFailure(HttpStatusCode status)
    {
        Enqueue(status, "{}");
        var (success, message) = await CreateService().TestClientIdAsync("client-id", CancellationToken.None);
        Assert.False(success);
        Assert.Contains("rejected", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestClientId_ReturnsGenericFailure_OnServerError()
    {
        Enqueue(HttpStatusCode.InternalServerError, "{}");
        var (success, message) = await CreateService().TestClientIdAsync("client-id", CancellationToken.None);
        Assert.False(success);
        Assert.DoesNotContain("rejected", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TestClientId_ReturnsFailure_WhenClientIdBlank(string clientId)
    {
        var (success, _) = await CreateService().TestClientIdAsync(clientId, CancellationToken.None);
        Assert.False(success);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task PollDeviceAuth_RejectsBlankCode_WithoutHttp(string? deviceCode)
    {
        var before = _responses.Count;

        var status = await CreateService().PollDeviceAuthAsync(Guid.NewGuid(), deviceCode!, CancellationToken.None);

        Assert.Equal(TraktDevicePollStatus.Error, status);
        Assert.Equal(before, _responses.Count);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("code\r\nX: 1")]
    [InlineData("code\nnewline")]
    [InlineData("code\twith-tab")]
    [InlineData("code\0nul")]
    public async Task PollDeviceAuth_RejectsControlCharacters_WithoutHttp(string deviceCode)
    {
        var before = _responses.Count;

        var status = await CreateService().PollDeviceAuthAsync(Guid.NewGuid(), deviceCode, CancellationToken.None);

        Assert.Equal(TraktDevicePollStatus.Error, status);
        Assert.Equal(before, _responses.Count);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PollDeviceAuth_RejectsOversizedCode_WithoutHttp()
    {
        var oversized = new string('a', 513);
        var before = _responses.Count;

        var status = await CreateService().PollDeviceAuthAsync(Guid.NewGuid(), oversized, CancellationToken.None);

        Assert.Equal(TraktDevicePollStatus.Error, status);
        Assert.Equal(before, _responses.Count);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PollDeviceAuth_StoresTokenOnlyForPollingUser()
    {
        var pollingUser = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        Enqueue(HttpStatusCode.OK, """{"access_token":"tok-poller","refresh_token":"ref-poller","expires_in":7776000,"created_at":1893456000}""");

        var status = await CreateService().PollDeviceAuthAsync(pollingUser, "device-code-123", CancellationToken.None);

        Assert.Equal(TraktDevicePollStatus.Linked, status);
        Assert.NotNull(_store.GetToken(pollingUser));
        Assert.Equal("tok-poller", _store.GetToken(pollingUser)!.AccessToken);
        Assert.Null(_store.GetToken(otherUser));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("id\r\ninjected")]
    [InlineData("id\twith-tab")]
    [InlineData("id\0nul")]
    public async Task TestClientId_RejectsControlCharacters_WithoutHttp(string clientId)
    {
        var before = _responses.Count;

        var (success, _) = await CreateService().TestClientIdAsync(clientId, CancellationToken.None);

        Assert.False(success);
        Assert.Equal(before, _responses.Count);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TestClientId_RejectsOversizedId_WithoutHttp()
    {
        var oversized = new string('a', 513);
        var before = _responses.Count;

        var (success, _) = await CreateService().TestClientIdAsync(oversized, CancellationToken.None);

        Assert.False(success);
        Assert.Equal(before, _responses.Count);
    }
}
