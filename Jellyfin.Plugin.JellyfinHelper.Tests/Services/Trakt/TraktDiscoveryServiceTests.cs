using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt;

/// <summary>
///     Verifies the Trakt discovery service: enablement + link guards, cache hit short-circuits, personal and
///     trending fetch mapping into the scoring seam, and that results are cached. Trakt HTTP is scripted; the
///     scoring seam and auth are mocked so the test isolates the orchestration.
/// </summary>
[Collection("ConfigOverride")]
public sealed class TraktDiscoveryServiceTests : IDisposable
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    private readonly Mock<ITraktAuthService> _auth = new();
    private readonly Mock<ITraktUserStore> _store = new();
    private readonly Mock<ISeerrDiscoveryService> _seam = new();
    private readonly TraktCacheService _cache = new();
    private readonly Mock<IHttpClientFactory> _factory = new();

    public TraktDiscoveryServiceTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        Plugin.Instance!.Configuration.TraktEnabled = true;
        Plugin.Instance!.Configuration.TraktClientId = "client-id";
        Plugin.Instance!.Configuration.TraktLimit = 20;

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "[]");
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            });
        _factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));
    }

    public void Dispose()
    {
        ControllerTestFactory.TeardownPluginInstance();
        GC.SuppressFinalize(this);
    }

    private TraktDiscoveryService Create()
        => new(_factory.Object, _auth.Object, _store.Object, _seam.Object, _cache, TestMockFactory.CreatePluginLogService(), NullLogger<TraktDiscoveryService>.Instance);

    private void Enqueue(string body) => _responses.Enqueue((HttpStatusCode.OK, body));

    private static DiscoveryResult SeamResult(Guid userId) => new()
    {
        UserId = userId,
        Recommendations = [new DiscoveryRecommendation { TmdbId = 1, ReasonKey = "reasonTrakt" }],
    };

    [Fact]
    public async Task Personal_ReturnsNull_WhenTraktDisabled()
    {
        Plugin.Instance!.Configuration.TraktEnabled = false;
        Assert.Null(await Create().GetPersonalAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Personal_ReturnsNull_WhenUserNotLinked()
    {
        var userId = Guid.NewGuid();
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        Assert.Null(await Create().GetPersonalAsync(userId, CancellationToken.None));
    }

    [Fact]
    public async Task Personal_FetchesMapsScoresAndCaches()
    {
        var userId = Guid.NewGuid();
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync("access");

        // Movies then shows.
        Enqueue("""[{"title":"M","year":2020,"rating":8.0,"ids":{"tmdb":100,"slug":"m"}}]""");
        Enqueue("""[{"title":"S","year":2019,"rating":7.0,"ids":{"tmdb":200,"slug":"s"}}]""");

        List<ExternalDiscoveryCandidate>? captured = null;
        _seam.Setup(s => s.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), "reasonTrakt", It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyList<ExternalDiscoveryCandidate>, string, CancellationToken>((_, c, _, _) => captured = [.. c])
            .ReturnsAsync(SeamResult(userId));

        var result = await Create().GetPersonalAsync(userId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        // Served from cache on the next call (seam invoked only once).
        var again = await Create().GetPersonalAsync(userId, CancellationToken.None);
        Assert.NotNull(again);
        _seam.Verify(s => s.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), "reasonTrakt", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Personal_ReturnsNull_WhenNoCandidates()
    {
        var userId = Guid.NewGuid();
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync("access");
        Enqueue("[]");
        Enqueue("[]");
        Assert.Null(await Create().GetPersonalAsync(userId, CancellationToken.None));
    }

    [Fact]
    public async Task Trending_FetchesMapsScoresAndCaches()
    {
        var userId = Guid.NewGuid();
        Enqueue("""[{"watchers":50,"movie":{"title":"M","year":2020,"rating":8.0,"ids":{"tmdb":100}}}]""");
        Enqueue("""[{"watchers":40,"show":{"title":"S","year":2019,"rating":7.0,"ids":{"tmdb":200}}}]""");

        _seam.Setup(s => s.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), "reasonTrakt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SeamResult(userId));

        var result = await Create().GetTrendingAsync(userId, CancellationToken.None);

        Assert.NotNull(result);
        // Cached: a same-user second call serves the cached entry without another seam call.
        await Create().GetTrendingAsync(userId, CancellationToken.None);
        _seam.Verify(s => s.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), "reasonTrakt", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Trending_ReturnsNull_WhenTraktDisabled()
    {
        Plugin.Instance!.Configuration.TraktEnabled = false;
        Assert.Null(await Create().GetTrendingAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task RefreshAll_WarmsEachLinkedUser()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        _store.Setup(s => s.GetLinkedUserIds()).Returns([u1, u2]);
        _auth.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("access");
        _seam.Setup(s => s.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), "reasonTrakt", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid uid, IReadOnlyList<ExternalDiscoveryCandidate> _, string _, CancellationToken _) => SeamResult(uid));

        // Each user: 2 personal fetches (movies + shows) returning one candidate each.
        for (var i = 0; i < 2; i++)
        {
            Enqueue("""[{"title":"M","year":2020,"rating":8.0,"ids":{"tmdb":100}}]""");
            Enqueue("""[{"title":"S","year":2019,"rating":7.0,"ids":{"tmdb":200}}]""");
        }

        await Create().RefreshAllAsync(CancellationToken.None);

        _seam.Verify(s => s.ScoreExternalCandidatesAsync(u1, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), "reasonTrakt", It.IsAny<CancellationToken>()), Times.Once);
        _seam.Verify(s => s.ScoreExternalCandidatesAsync(u2, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), "reasonTrakt", It.IsAny<CancellationToken>()), Times.Once);
    }
}
