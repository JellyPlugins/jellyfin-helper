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
    public async Task Disconnect_RemovesStoredToken()
    {
        var userId = Guid.NewGuid();
        await _store.SaveAsync(userId, new TraktUserToken { AccessToken = "acc", RefreshToken = "ref", ExpiresAtUtc = _now.AddHours(1) }, CancellationToken.None);

        await CreateService().DisconnectAsync(userId, CancellationToken.None);

        Assert.Null(_store.GetToken(userId));
    }
}
