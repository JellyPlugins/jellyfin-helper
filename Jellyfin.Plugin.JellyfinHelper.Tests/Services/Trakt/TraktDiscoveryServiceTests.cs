using System;
using System.Collections.Generic;
using System.Linq;
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
///     Verifies the Trakt discovery service: disabled/no-token/error short-circuits, cache-first serving,
///     per-user rescoring of the shared trending pool, refresh isolation across users, and ctor guards.
///     HTTP is scripted via a queued handler; no real network is touched.
/// </summary>
[Collection("ConfigOverride")]
public sealed class TraktDiscoveryServiceTests : IDisposable
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    private readonly Mock<IHttpClientFactory> _factory;
    private readonly Mock<ITraktAuthService> _auth;
    private readonly Mock<ITraktUserStore> _store;
    private readonly Mock<ISeerrDiscoveryService> _discovery;
    private readonly TraktCacheService _cache;
    private readonly IPluginLogService _pluginLog;

    public TraktDiscoveryServiceTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        Plugin.Instance!.Configuration.TraktEnabled = true;
        Plugin.Instance!.Configuration.TraktClientId = "client-id";

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

        _auth = new Mock<ITraktAuthService>();
        _store = new Mock<ITraktUserStore>();
        _discovery = new Mock<ISeerrDiscoveryService>();
        _cache = new TraktCacheService();
        _pluginLog = TestMockFactory.CreatePluginLogService();
    }

    public void Dispose()
    {
        Plugin.Instance!.Configuration.TraktEnabled = false;
        Plugin.Instance!.Configuration.TraktClientId = string.Empty;
        ControllerTestFactory.TeardownPluginInstance();
        GC.SuppressFinalize(this);
    }

    private TraktDiscoveryService CreateService()
        => new(
            _factory.Object,
            _auth.Object,
            _store.Object,
            _discovery.Object,
            _cache,
            _pluginLog,
            NullLogger<TraktDiscoveryService>.Instance);

    private static DiscoveryResult Scored(Guid userId, params (int TmdbId, string MediaType, string Title)[] items)
        => new()
        {
            UserId = userId,
            Recommendations = items.Select(i => new DiscoveryRecommendation
            {
                TmdbId = i.TmdbId,
                MediaType = i.MediaType,
                Title = i.Title,
            }).ToList(),
        };

    private const string MoviesJson =
        """[{"title":"Alpha","year":2020,"overview":"o","rating":8.1,"ids":{"tmdb":11}},{"title":"NoId","year":2021,"ids":{}}]""";

    private const string ShowsJson =
        """[{"title":"Beta","year":2019,"ids":{"tmdb":22}}]""";

    private const string TrendingMoviesJson =
        """[{"watchers":5,"movie":{"title":"T1","year":2022,"ids":{"tmdb":33}}}]""";

    private const string TrendingShowsJson =
        """[{"watchers":7,"show":{"title":"T2","year":2023,"ids":{"tmdb":44}}}]""";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Ctor_NullDependency_Throws(int index)
    {
        var factory = new Mock<IHttpClientFactory>().Object;
        var auth = new Mock<ITraktAuthService>().Object;
        var store = new Mock<ITraktUserStore>().Object;
        var discovery = new Mock<ISeerrDiscoveryService>().Object;
        var cache = new TraktCacheService();
        var pluginLog = TestMockFactory.CreatePluginLogService();
        var logger = NullLogger<TraktDiscoveryService>.Instance;

        Assert.Throws<ArgumentNullException>(() => index switch
        {
            0 => new TraktDiscoveryService(null!, auth, store, discovery, cache, pluginLog, logger),
            1 => new TraktDiscoveryService(factory, null!, store, discovery, cache, pluginLog, logger),
            2 => new TraktDiscoveryService(factory, auth, null!, discovery, cache, pluginLog, logger),
            3 => new TraktDiscoveryService(factory, auth, store, null!, cache, pluginLog, logger),
            4 => new TraktDiscoveryService(factory, auth, store, discovery, null!, pluginLog, logger),
            5 => new TraktDiscoveryService(factory, auth, store, discovery, cache, null!, logger),
            _ => new TraktDiscoveryService(factory, auth, store, discovery, cache, pluginLog, null!),
        });
    }

    [Fact]
    public async Task GetPersonal_Disabled_ReturnsNullWithoutFetch()
    {
        Plugin.Instance!.Configuration.TraktEnabled = false;
        var sut = CreateService();

        Assert.Null(await sut.GetPersonalAsync(Guid.NewGuid(), CancellationToken.None));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPersonal_CacheHit_ServesWithoutRefetch()
    {
        var userId = Guid.NewGuid();
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync("token");
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, string r, CancellationToken t) => Scored(u, (11, "movie", "Alpha"), (22, "tv", "Beta")));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        var sut = CreateService();

        var first = await sut.GetPersonalAsync(userId, CancellationToken.None);
        Assert.NotNull(first);

        // The queue is now empty: a Strict handler would throw on any further fetch,
        // so serving the second call proves it came from the cache.
        var second = await sut.GetPersonalAsync(userId, CancellationToken.None);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetPersonal_NoToken_ReturnsNullWithoutFetch()
    {
        var userId = Guid.NewGuid();
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var sut = CreateService();

        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPersonal_FetchScoresCachesAndDropsIdless()
    {
        var userId = Guid.NewGuid();
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync("token");
        IReadOnlyList<ExternalDiscoveryCandidate>? seen = null;
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, string r, CancellationToken t) => seen = c)
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, string r, CancellationToken t) => Scored(u));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        var sut = CreateService();

        var result = await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.NotNull(result);
        // The id-less "NoId" item is dropped before scoring: 1 movie + 1 show reach the scorer.
        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Count);
        Assert.Contains(seen, c => c.TmdbId == 11 && c.MediaType == "movie");
        Assert.Contains(seen, c => c.TmdbId == 22 && c.MediaType == "tv");
    }

    [Fact]
    public async Task GetPersonal_TraktError_ReturnsNullWithoutScoring()
    {
        var userId = Guid.NewGuid();
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync("token");
        _responses.Enqueue((HttpStatusCode.InternalServerError, "boom"));
        _responses.Enqueue((HttpStatusCode.InternalServerError, "boom"));
        var sut = CreateService();

        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPersonal_NullScore_DoesNotCache()
    {
        var userId = Guid.NewGuid();
        _auth.Setup(a => a.GetValidAccessTokenAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync("token");
        _discovery.SetupSequence(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DiscoveryResult?)null)
            .ReturnsAsync(Scored(userId, (11, "movie", "Alpha")));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        var sut = CreateService();

        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));

        // The null result was not cached: the second call refetches and serves fresh data.
        var second = await sut.GetPersonalAsync(userId, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Single(second!.Recommendations);
    }

    [Fact]
    public async Task GetTrending_Disabled_ReturnsNullWithoutFetch()
    {
        Plugin.Instance!.Configuration.TraktEnabled = false;
        var sut = CreateService();

        Assert.Null(await sut.GetTrendingAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetTrending_FetchCachesGloballyButRescoresPerUser()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.Is<Guid>(u => u == userA), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scored(userA, (33, "movie", "T1"), (44, "tv", "T2")));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.Is<Guid>(u => u == userB), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scored(userB, (33, "movie", "T1"), (44, "tv", "T2")));
        _responses.Enqueue((HttpStatusCode.OK, TrendingMoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, TrendingShowsJson));
        var sut = CreateService();

        var forA = await sut.GetTrendingAsync(userA, CancellationToken.None);
        Assert.NotNull(forA);
        Assert.Equal(userA, forA!.UserId);

        // The queue is empty now: user B must be served by rescoring the shared pool, not refetching.
        var forB = await sut.GetTrendingAsync(userB, CancellationToken.None);
        Assert.NotNull(forB);
        Assert.Equal(userB, forB!.UserId);
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task GetTrending_EmptyPool_ReturnsNull()
    {
        _responses.Enqueue((HttpStatusCode.OK, "[]"));
        _responses.Enqueue((HttpStatusCode.OK, "[]"));
        var sut = CreateService();

        Assert.Null(await sut.GetTrendingAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task RefreshAll_Disabled_DoesNothing()
    {
        Plugin.Instance!.Configuration.TraktEnabled = false;
        _store.Setup(s => s.GetLinkedUserIds()).Returns(new List<Guid> { Guid.NewGuid() });
        var sut = CreateService();

        await sut.RefreshAllAsync(CancellationToken.None);

        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RefreshAll_WarmsLinkedUsersAndIsolatesFailures()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _store.Setup(s => s.GetLinkedUserIds()).Returns(new List<Guid> { userA, userB });
        _auth.Setup(a => a.GetValidAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("token");
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.Is<Guid>(u => u == userA), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scored(userA, (11, "movie", "Alpha")));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.Is<Guid>(u => u == userB), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("trakt down"));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        var sut = CreateService();

        await sut.RefreshAllAsync(CancellationToken.None);

        // User A was warmed despite user B failing: served from cache with an empty queue
        // (the Strict handler would throw on any live fetch).
        var served = await sut.GetPersonalAsync(userA, CancellationToken.None);
        Assert.NotNull(served);
        Assert.Single(served!.Recommendations);
    }

    [Fact]
    public async Task RefreshAll_Cancelled_Throws()
    {
        _store.Setup(s => s.GetLinkedUserIds()).Returns(new List<Guid> { Guid.NewGuid() });
        var sut = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.RefreshAllAsync(cts.Token));
    }

    [Fact]
    public async Task RefreshAll_InvalidatesTrendingPool()
    {
        var userId = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, string r, CancellationToken t) =>
                Scored(u, c.Select(x => (x.TmdbId, x.MediaType, x.Title ?? string.Empty)).ToArray()));
        _store.Setup(s => s.GetLinkedUserIds()).Returns(new List<Guid>());
        var sut = CreateService();

        // Seed tested via the public path would fetch; seed the cache entry directly instead.
        _responses.Enqueue((HttpStatusCode.OK, TrendingMoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, TrendingShowsJson));
        var seeded = await sut.GetTrendingAsync(userId, CancellationToken.None);
        Assert.NotNull(seeded);

        await sut.RefreshAllAsync(CancellationToken.None);

        // The stale pool is gone: the next call refetches (fresh titles from the new fixtures).
        _responses.Enqueue((HttpStatusCode.OK, """[{"watchers":9,"movie":{"title":"New","year":2024,"ids":{"tmdb":55}}}]"""));
        _responses.Enqueue((HttpStatusCode.OK, "[]"));
        var fresh = await sut.GetTrendingAsync(userId, CancellationToken.None);
        Assert.NotNull(fresh);
        Assert.Contains(fresh!.Recommendations, r => r.TmdbId == 55);
    }
}
