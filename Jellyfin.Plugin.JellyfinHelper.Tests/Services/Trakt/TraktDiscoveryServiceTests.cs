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
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt;

/// <summary>
///     Verifies the Trakt discovery service: guards, cache-first serving, per-user rescoring,
///     refresh isolation, and ctor guards. HTTP is scripted; no real network is touched.
/// </summary>
[Collection("ConfigOverride")]
public sealed class TraktDiscoveryServiceTests : IDisposable
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    private readonly Mock<IHttpClientFactory> _factory;
    private readonly Mock<ISeerrDiscoveryService> _discovery;
    private readonly TraktCacheService _cache;
    private readonly Mock<Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.IOfficialTraktPluginReader> _officialPlugin;
    private readonly Mock<IUserManager> _userManager = new();

    // Cache clock: tests that exercise backoff expiry advance this; everyone else leaves it at the start value.
    private DateTime _cacheNow = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Not readonly: a couple of logging tests swap in a DEBUG-level log service before building the SUT.
    private PluginLogService _pluginLog;

    public TraktDiscoveryServiceTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        Plugin.Instance!.Configuration.TraktSourcingEnabled = true;

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

        _discovery = new Mock<ISeerrDiscoveryService>();
        // Consumed-item filtering is the seam's job; pass through by default so these
        // tests exercise the Trakt orchestration, not the filter (covered at the seam).
        _discovery.Setup(d => d.FilterConsumedItems(It.IsAny<Guid>(), It.IsAny<DiscoveryResult>()))
            .Returns((Guid u, DiscoveryResult r) => r);
        _cache = new TraktCacheService(() => _cacheNow);
        // Trakt is sourced only through the official plugin: present by default with a usable token for every
        // user, so fetch-path tests reach the network. Tests for the absent/no-token cases override this.
        _officialPlugin = new Mock<Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.IOfficialTraktPluginReader>();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("official-tok", FarFuture));
        _officialPlugin.Setup(p => p.GetLinkedUserIds(It.IsAny<DateTimeOffset>())).Returns(System.Array.Empty<Guid>());
        _pluginLog = TestMockFactory.CreatePluginLogService();
    }

    public void Dispose()
    {
        Plugin.Instance!.Configuration.TraktSourcingEnabled = true;
        ControllerTestFactory.TeardownPluginInstance();
        GC.SuppressFinalize(this);
    }

    private TraktDiscoveryService CreateService()
        => CreateService(() => DateTimeOffset.Now);

    private TraktDiscoveryService CreateService(Func<DateTimeOffset> now)
        => CreateServiceWithFactory(_factory.Object, now);

    private TraktDiscoveryService CreateServiceWithFactory(IHttpClientFactory factory)
        => CreateServiceWithFactory(factory, () => DateTimeOffset.Now);

    private TraktDiscoveryService CreateServiceWithFactory(IHttpClientFactory factory, Func<DateTimeOffset> now)
        => new(
            factory,
            new TraktPersonalSourceService(_officialPlugin.Object, now),
            _discovery.Object,
            _cache,
            _pluginLog,
            _userManager.Object,
            NullLogger<TraktDiscoveryService>.Instance)
        {
            UtcNow = now,
        };

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
        var sources = new Mock<ITraktPersonalSourceService>().Object;
        var discovery = new Mock<ISeerrDiscoveryService>().Object;
        var cache = new TraktCacheService();
        var pluginLog = TestMockFactory.CreatePluginLogService();
        var userManager = new Mock<IUserManager>().Object;
        var logger = NullLogger<TraktDiscoveryService>.Instance;

        Assert.Throws<ArgumentNullException>(() => index switch
        {
            0 => new TraktDiscoveryService(null!, sources, discovery, cache, pluginLog, userManager, logger),
            1 => new TraktDiscoveryService(factory, null!, discovery, cache, pluginLog, userManager, logger),
            2 => new TraktDiscoveryService(factory, sources, null!, cache, pluginLog, userManager, logger),
            3 => new TraktDiscoveryService(factory, sources, discovery, null!, pluginLog, userManager, logger),
            4 => new TraktDiscoveryService(factory, sources, discovery, cache, null!, userManager, logger),
            5 => new TraktDiscoveryService(factory, sources, discovery, cache, pluginLog, null!, logger),
            _ => new TraktDiscoveryService(factory, sources, discovery, cache, pluginLog, userManager, null!),
        });
    }

    [Fact]
    public async Task GetPersonal_Disabled_ReturnsNullWithoutFetch()
    {
        Plugin.Instance!.Configuration.TraktSourcingEnabled = false;
        var sut = CreateService();

        Assert.Null(await sut.GetPersonalAsync(Guid.NewGuid(), CancellationToken.None));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPersonal_CacheHit_ServesWithoutRefetch()
    {
        var userId = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) => Scored(u, (11, "movie", "Alpha"), (22, "tv", "Beta")));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        var sut = CreateService();

        var first = await sut.GetPersonalAsync(userId, CancellationToken.None);
        Assert.NotNull(first);

        // The queue is now empty: a Strict handler would throw on any further fetch,
        // so serving the second call proves it came from the cache. Served copies are
        // fresh instances (the cache is never handed out for mutation).
        var second = await sut.GetPersonalAsync(userId, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(
            first!.Recommendations.Select(r => r.TmdbId),
            second!.Recommendations.Select(r => r.TmdbId));
    }

    [Fact]
    public async Task InvalidatePersonal_DropsCache_NextCallRefetches()
    {
        var userId = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) => Scored(u, (11, "movie", "Alpha"), (22, "tv", "Beta")));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        var sut = CreateService();

        Assert.NotNull(await sut.GetPersonalAsync(userId, CancellationToken.None));

        // After invalidation the warm pool is gone, so the next call must fetch again: queue a fresh pair.
        // A Strict handler would throw on an un-queued fetch, so completing proves the re-fetch happened.
        sut.InvalidatePersonal(userId);
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));

        Assert.NotNull(await sut.GetPersonalAsync(userId, CancellationToken.None));
        Assert.Empty(_responses); // both queued fetches were consumed - no stale cache served
    }

    [Fact]
    public async Task GetPersonal_ServesSeamFilteredResult()
    {
        var userId = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) => Scored(u, (11, "movie", "Alpha"), (22, "tv", "Beta")));
        var filtered = Scored(userId, (22, "tv", "Beta"));
        _discovery.Setup(d => d.FilterConsumedItems(userId, It.IsAny<DiscoveryResult>())).Returns(filtered);
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        var sut = CreateService();

        // Consumed-item filtering belongs to the seam; the Trakt service serves whatever it returns.
        var result = await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Same(filtered, result);
        _discovery.Verify(d => d.FilterConsumedItems(userId, It.IsAny<DiscoveryResult>()), Times.Once);
    }

    [Fact]
    public async Task GetTrending_ServesSeamFilteredResult()
    {
        var userId = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) => Scored(u, (33, "movie", "T1"), (44, "tv", "T2")));
        var filtered = Scored(userId, (44, "tv", "T2"));
        _discovery.Setup(d => d.FilterConsumedItems(userId, It.IsAny<DiscoveryResult>())).Returns(filtered);
        _responses.Enqueue((HttpStatusCode.OK, TrendingMoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, TrendingShowsJson));
        var sut = CreateService();

        var result = await sut.GetTrendingAsync(userId, CancellationToken.None);

        Assert.Same(filtered, result);
    }

    [Fact]
    public async Task GetPersonal_NoToken_ReturnsNullWithoutFetch()
    {
        var userId = Guid.NewGuid();
        var sut = CreateService();

        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPersonal_FetchScoresCachesAndDropsIdless()
    {
        var userId = Guid.NewGuid();
        IReadOnlyList<ExternalDiscoveryCandidate>? seen = null;
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .Callback((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) => seen = c)
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) => Scored(u));
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
        _responses.Enqueue((HttpStatusCode.InternalServerError, "boom"));
        _responses.Enqueue((HttpStatusCode.InternalServerError, "boom"));
        var sut = CreateService();

        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPersonal_NullScore_DoesNotCacheResult_AndSetsNoBackoff()
    {
        // Scoring-null is ambiguous (owned-collapse, or not-ready: no profile / Seerr unconfigured). It must
        // cache no result AND set no backoff, so the next call retries the moment the user's data could have
        // changed rather than being suppressed. Only a definite empty Trakt fetch dampens; this does not.
        var userId = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DiscoveryResult?)null);
        // Two full fetch rounds queued: the second call must be free to refetch (no backoff).
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        var sut = CreateService();

        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        Assert.Null(_cache.GetPersonal(userId, TimeSpan.FromHours(12))); // no positive result cached
        Assert.False(_cache.IsPersonalBackoffActive(userId)); // ambiguous null sets no backoff

        // The second call refetches (consumes the second queued pair) instead of being suppressed.
        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        Assert.Empty(_responses);
    }

    [Fact]
    public async Task GetPersonal_EmptyTraktFetch_SetsShortBackoff()
    {
        // A definite empty Trakt result (Trakt itself returned nothing) is stable, so it dampens duplicate loads
        // with the short empty backoff; a second immediate call is suppressed rather than refetching.
        var userId = Guid.NewGuid();
        _responses.Enqueue((HttpStatusCode.OK, "[]"));
        _responses.Enqueue((HttpStatusCode.OK, "[]"));
        var sut = CreateService();

        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        Assert.True(_cache.IsPersonalBackoffActive(userId));
        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        // The second call was suppressed by the backoff, so no extra fetch ran.
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetTrending_Disabled_ReturnsNullWithoutFetch()
    {
        Plugin.Instance!.Configuration.TraktSourcingEnabled = false;
        var sut = CreateService();

        Assert.Null(await sut.GetTrendingAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetTrending_FetchCachesGloballyButRescoresPerUser()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.Is<Guid>(u => u == userA), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scored(userA, (33, "movie", "T1"), (44, "tv", "T2")));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.Is<Guid>(u => u == userB), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
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
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
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
        Plugin.Instance!.Configuration.TraktSourcingEnabled = false;
        var sut = CreateService();

        await sut.RefreshAllAsync(CancellationToken.None);

        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RefreshAll_WarmsLinkedUsersAndIsolatesFailures()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _officialPlugin.Setup(p => p.GetLinkedUserIds(It.IsAny<DateTimeOffset>())).Returns(new[] { userA, userB });
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.Is<Guid>(u => u == userA), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scored(userA, (11, "movie", "Alpha")));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.Is<Guid>(u => u == userB), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
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
        _officialPlugin.Setup(p => p.GetLinkedUserIds(It.IsAny<DateTimeOffset>())).Returns(new[] { Guid.NewGuid() });
        var sut = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.RefreshAllAsync(cts.Token));
    }

    [Fact]
    public async Task GetTrending_SecondCall_ServedFromScoredCacheWithoutRescoring()
    {
        var userId = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) => Scored(u, (33, "movie", "T1"), (44, "tv", "T2")));
        _responses.Enqueue((HttpStatusCode.OK, TrendingMoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, TrendingShowsJson));
        var sut = CreateService();

        var first = await sut.GetTrendingAsync(userId, CancellationToken.None);
        Assert.NotNull(first);

        // The queue is empty and the handler is Strict: any refetch would throw, and any
        // rescoring would show up in the verify below. The second call serves the cache.
        var second = await sut.GetTrendingAsync(userId, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(
            first!.Recommendations.Select(r => r.TmdbId),
            second!.Recommendations.Select(r => r.TmdbId));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetPersonal_TransportFailure_ReturnsNullWithoutScoring()
    {
        var userId = Guid.NewGuid();
        var throwingHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        throwingHandler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        throwingHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("trakt down"));
        var throwingFactory = new Mock<IHttpClientFactory>();
        throwingFactory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(throwingHandler.Object));
        var sut = CreateServiceWithFactory(throwingFactory.Object);

        // A network failure degrades to null instead of an HTTP 500, and never scores.
        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetTrending_TransportFailure_ReturnsNullWithoutThrowing()
    {
        var throwingHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        throwingHandler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        throwingHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("trakt down"));
        var throwingFactory = new Mock<IHttpClientFactory>();
        throwingFactory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(throwingHandler.Object));
        var sut = CreateServiceWithFactory(throwingFactory.Object);

        Assert.Null(await sut.GetTrendingAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetTrending_MoviesFailure_StillServesShows()
    {
        var userId = Guid.NewGuid();
        var calls = 0;
        var flakyHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        flakyHandler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        flakyHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new HttpRequestException("movies down");
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TrendingShowsJson) };
            });
        var flakyFactory = new Mock<IHttpClientFactory>();
        flakyFactory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(flakyHandler.Object));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) =>
                Scored(u, c.Select(x => (x.TmdbId, x.MediaType, x.Title ?? string.Empty)).ToArray()));
        var sut = CreateServiceWithFactory(flakyFactory.Object);

        // A failed movies fetch no longer aborts the shows fetch: the partial pool is served.
        var result = await sut.GetTrendingAsync(userId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.TmdbId == 44);
    }

    [Fact]
    public async Task RefreshAll_InvalidatesTrendingPool()
    {
        var userId = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) =>
                Scored(u, c.Select(x => (x.TmdbId, x.MediaType, x.Title ?? string.Empty)).ToArray()));
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

    // --- Official-Trakt-plugin source mode -------------------------------------------------------------
    private static readonly DateTimeOffset FarFuture = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record CapturedRequest(string? ApiKey, string? Authorization);

    // Builds a service wired with a capturing handler so a test can assert WHICH Trakt app (client id) and token
    // the fetch actually used. Captures the first request's headers.
    private (TraktDiscoveryService Sut, List<CapturedRequest> Captured) CreateCapturingService()
    {
        var captured = new List<CapturedRequest>();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var apiKey = req.Headers.TryGetValues("trakt-api-key", out var keys) ? keys.FirstOrDefault() : null;
                captured.Add(new CapturedRequest(apiKey, req.Headers.Authorization?.ToString()));
                var (status, body) = _responses.Dequeue();
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));

        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken t) =>
                Scored(u, c.Select(x => (x.TmdbId, x.MediaType, x.Title ?? string.Empty)).ToArray()));

        var sut = new TraktDiscoveryService(
            factory.Object,
            new TraktPersonalSourceService(_officialPlugin.Object, () => FarFuture),
            _discovery.Object,
            _cache,
            _pluginLog,
            new Mock<IUserManager>().Object,
            NullLogger<TraktDiscoveryService>.Instance);
        return (sut, captured);
    }

    [Fact]
    public async Task GetPersonal_OfficialPresent_SourcesViaOfficialToken()
    {
        var userId = Guid.NewGuid();
        // The official plugin holds a token for the user.
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("official-tok", FarFuture));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));

        var (sut, captured) = CreateCapturingService();
        var result = await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.NotNull(result);
        // Proves the official plugin's app id + its token were used.
        Assert.All(captured, c =>
        {
            Assert.Equal(Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktPluginReader.OfficialTraktClientId, c.ApiKey);
            Assert.Equal("Bearer official-tok", c.Authorization);
        });
    }

    [Fact]
    public async Task GetPersonal_OfficialTokenExpired_NoSourceReturnsNull()
    {
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        // Reader returns null for an expired token (its own responsibility); discovery then has no source.
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns((Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken?)null);

        var (sut, _) = CreateCapturingService();
        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPersonal_OfficialSource401_ReturnsNullWithoutScoring()
    {
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("official-tok", FarFuture));
        // Both fetches 401: the official token went stale; serve null without scoring.
        _responses.Enqueue((HttpStatusCode.Unauthorized, "{}"));
        _responses.Enqueue((HttpStatusCode.Unauthorized, "{}"));

        var (sut, _) = CreateCapturingService();
        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));

    }

    [Fact]
    public async Task GetPersonal_OfficialSource403_LogsAtDebugNotWarn_WithEndpoint()
    {
        // A stale/revoked official token (403/401) is expected and unactionable here - only the official plugin
        // can re-link it. It must NOT spam WARN on every movies+shows pass; it is downgraded to a single DEBUG
        // line that names the endpoint so the admin can diagnose which user needs a re-link.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("official-tok", FarFuture));
        _responses.Enqueue((HttpStatusCode.Forbidden, "{}"));
        _responses.Enqueue((HttpStatusCode.Forbidden, "{}"));

        var sut = CreateService();
        await sut.GetPersonalAsync(userId, CancellationToken.None);

        var traktEntries = _pluginLog.GetEntries(source: "Trakt");
        Assert.NotEmpty(traktEntries);
        // No WARN for the foreign auth failure; the entry is DEBUG and carries the endpoint.
        Assert.DoesNotContain(traktEntries, e => e.Level == "WARN");
        Assert.Contains(traktEntries, e =>
            e.Level == "DEBUG"
            && e.Message.Contains("403", StringComparison.Ordinal)
            && e.Message.Contains("/recommendations/", StringComparison.Ordinal));
        // The shows call is skipped after the movies auth-failure, so exactly ONE forbidden fetch is logged -
        // one Forbidden response stays un-consumed in the queue.
        Assert.Single(traktEntries, e => e.Message.Contains("403", StringComparison.Ordinal));
        Assert.Single(_responses);
    }

    [Fact]
    public async Task GetPersonal_AuthFailure_NamesTheAffectedUserInDebugLog()
    {
        // The admin-only plugin log must name WHO needs a re-link, not just that "a user" does; otherwise the
        // admin cannot act. Resolve the username from the user manager and surface it in the DEBUG line.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var userId = Guid.NewGuid();
        _userManager.Setup(m => m.GetUserById(userId)).Returns(new Jellyfin.Database.Implementations.Entities.User("alice", "Default", "Default"));
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("official-tok", FarFuture));
        _responses.Enqueue((HttpStatusCode.Unauthorized, "{}"));

        var sut = CreateService();
        await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "DEBUG" && e.Message.Contains("alice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_AuthFailure_FallsBackToUserIdWhenNameUnavailable()
    {
        // A renamed/removed account (user manager returns null) must not blank the message or throw: fall back
        // to the raw id so the admin still has an unambiguous handle.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var userId = Guid.NewGuid();
        _userManager.Setup(m => m.GetUserById(userId)).Returns((Jellyfin.Database.Implementations.Entities.User?)null);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("official-tok", FarFuture));
        _responses.Enqueue((HttpStatusCode.Unauthorized, "{}"));

        var sut = CreateService();
        await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "DEBUG" && e.Message.Contains(userId.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_OfficialSource403OnMovies_SkipsShowsFetch()
    {
        // Core of the dead-foreign-token fix: a 403/401 on the movies call means the same token would fail the
        // shows call identically, so the shows request must NOT fire (no repeated doomed call / duplicate log).
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("official-tok", FarFuture));
        // Only the movies response is enqueued: if the shows fetch wrongly fired, the strict handler would throw
        // on the empty queue, failing the test.
        _responses.Enqueue((HttpStatusCode.Forbidden, "{}"));

        var sut = CreateService();
        var result = await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(_responses); // movies consumed, shows never attempted
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPersonal_Success_LogsOutcomeAtDebugNotInfo()
    {
        // A non-empty personal result is routine and must stay at DEBUG so a per-tab-load Info line never spams
        // the admin log. The summary still records the fetched-to-final counts for diagnosis.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var userId = Guid.NewGuid();
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scored(userId, (11, "movie", "Alpha")));

        var sut = CreateService();
        await sut.GetPersonalAsync(userId, CancellationToken.None);

        var entries = _pluginLog.GetEntries(source: "Trakt");
        Assert.Contains(entries, e => e.Level == "DEBUG" && e.Message.Contains("final", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Level == "INFO" && e.Message.Contains("Personal discovery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_ScoringReturnsNull_LogsEmptyOutcomeAtInfo()
    {
        // Scoring that returns null (for example every candidate excluded as already owned) is the case an admin
        // needs to see without turning on DEBUG: it is the symptom behind an empty "Trakt for you" tab.
        _pluginLog = TestMockFactory.CreatePluginLogService();
        var userId = Guid.NewGuid();
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DiscoveryResult?)null);

        var sut = CreateService();
        await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "INFO"
            && e.Message.Contains("Personal discovery", StringComparison.Ordinal)
            && e.Message.Contains("→ 0 final", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_EmptyFetch_LogsEmptyOutcomeAtInfo()
    {
        // Trakt returning nothing (empty arrays) is distinct from a scoring collapse; the outcome line names it
        // so the admin can tell "Trakt had nothing" apart from "we filtered it all away".
        _pluginLog = TestMockFactory.CreatePluginLogService();
        var userId = Guid.NewGuid();
        _responses.Enqueue((HttpStatusCode.OK, "[]"));
        _responses.Enqueue((HttpStatusCode.OK, "[]"));

        var sut = CreateService();
        await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "INFO"
            && e.Message.Contains("Personal discovery", StringComparison.Ordinal)
            && e.Message.Contains("no personal recommendations", StringComparison.Ordinal));
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPersonal_SourceUnresolvableAfterGate_LogsOutcomeAndServesNull()
    {
        // Linked at the cheap gate but the token is gone by the time the fetch resolves it (a transient refresh
        // failure): serve null without caching and record why, so the empty tab is explained.
        _pluginLog = TestMockFactory.CreatePluginLogService();
        var userId = Guid.NewGuid();
        _officialPlugin.SetupSequence(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("tok", FarFuture))
            .Returns((Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken?)null);

        var result = await CreateService().GetPersonalAsync(userId, CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "INFO" && e.Message.Contains("source unresolvable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_SourceLostBeforeShows_LogsOutcomeAndServesNull()
    {
        // The token survives the gate and the movies fetch but vanishes before the shows re-resolve. The movies
        // already fetched are discarded, the fetch backs off, and the outcome names the lost-source reason.
        _pluginLog = TestMockFactory.CreatePluginLogService();
        var userId = Guid.NewGuid();
        _officialPlugin.SetupSequence(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("tok", FarFuture)) // IsLinked gate
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("tok", FarFuture)) // movies resolve
            .Returns((Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken?)null); // shows resolve lost
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));

        var result = await CreateService().GetPersonalAsync(userId, CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "INFO" && e.Message.Contains("source lost before shows fetch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTrending_ScoringReturnsNull_LogsDebugAndServesNull()
    {
        // A non-empty trending pool that scores to nothing (for example every candidate already owned) serves
        // null; the Debug line records the collapse without spamming Info on a working feature.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var userId = Guid.NewGuid();
        _responses.Enqueue((HttpStatusCode.OK, TrendingMoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, TrendingShowsJson));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DiscoveryResult?)null);

        var result = await CreateService().GetTrendingAsync(userId, CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "DEBUG" && e.Message.Contains("Trending scoring produced no result", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_Source5xx_StaysWarn()
    {
        // Regression guard against over-suppression: a 5xx is a real, actionable failure and must remain a WARN
        // (only a 401/403 on a bearer call is downgraded to DEBUG).
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var userId = Guid.NewGuid();
        _responses.Enqueue((HttpStatusCode.InternalServerError, "{}"));
        _responses.Enqueue((HttpStatusCode.InternalServerError, "{}"));

        var sut = CreateService();
        await sut.GetPersonalAsync(userId, CancellationToken.None);

        var traktEntries = _pluginLog.GetEntries(source: "Trakt");
        Assert.Contains(traktEntries, e =>
            e.Level == "WARN"
            && e.Message.Contains("500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshAll_WarmsOfficialUsers()
    {
        // The official plugin holds a token for a user; RefreshAll warms that user through it.
        var officialUser = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.GetLinkedUserIds(It.IsAny<DateTimeOffset>())).Returns(new[] { officialUser });
        _officialPlugin.Setup(p => p.TryGetToken(officialUser, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("official-tok", FarFuture));

        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));

        var (sut, captured) = CreateCapturingService();
        await sut.RefreshAllAsync(CancellationToken.None);

        // The user was warmed: a fetch ran under the official plugin's app + seeded token.
        AssertOfficialCaptured(captured);
    }

    [Fact]
    public async Task RefreshAll_OfficialPluginAbsent_WarmsNothing()
    {
        // Trakt is sourced only through the official plugin; with it absent RefreshAll must return before any
        // fetch so no stale pool is built.
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);

        var sut = CreateService();
        await sut.RefreshAllAsync(CancellationToken.None);

        // No response was dequeued: a strict handler would have thrown on an unexpected fetch.
        Assert.Empty(_pluginLog.GetEntries(source: "Trakt"));
    }

    [Fact]
    public async Task GetPersonal_AuthFailure_UserLookupThrows_FallsBackToId()
    {
        // A user-manager that throws during the diagnostic name lookup must not break logging: the message
        // falls back to the raw id so the admin still has a handle.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var userId = Guid.NewGuid();
        _userManager.Setup(m => m.GetUserById(userId)).Throws(new InvalidOperationException("boom"));
        _responses.Enqueue((HttpStatusCode.Unauthorized, "{}"));

        var sut = CreateService();
        await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Message.Contains(userId.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_EmptyUserId_AuthFailure_UsesGenericIdentity()
    {
        // Guid.Empty has no meaningful identity; the diagnostic line uses the generic "this user" rather than
        // an empty or malformed handle.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        _officialPlugin.Setup(p => p.TryGetToken(Guid.Empty, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("tok", FarFuture));
        _responses.Enqueue((HttpStatusCode.Unauthorized, "{}"));

        var sut = CreateService();
        await sut.GetPersonalAsync(Guid.Empty, CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Message.Contains("this user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_FetchFailure_WithVipHeader_IncludesItInDiagnosticLog()
    {
        // Trakt returns an X-VIP-User header on some failures; the diagnostic line surfaces it so the admin can
        // correlate the failure with the account Trakt saw.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var userId = Guid.NewGuid();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
                response.Headers.TryAddWithoutValidation("X-VIP-User", "vip-123");
                return response;
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));

        var sut = CreateServiceWithFactory(factory.Object);
        await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Message.Contains("X-VIP-User=vip-123", StringComparison.Ordinal));
    }

    // Builds a service whose single scripted response carries the given status, Retry-After header, and body.
    private TraktDiscoveryService ServiceWithResponse(HttpStatusCode status, Action<HttpResponseMessage> configure, Func<DateTimeOffset>? now = null)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent("{}") };
                configure(response);
                return response;
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));
        return CreateServiceWithFactory(factory.Object, now ?? (() => DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task GetPersonal_RateLimited_WithDeltaRetryAfter_LogsSeconds()
    {
        // A 429 with a delta-seconds Retry-After is a real backoff signal (not an auth failure), so it stays a
        // WARN and surfaces the delay the server asked for.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var sut = ServiceWithResponse(
            HttpStatusCode.TooManyRequests,
            r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42)));

        await sut.GetPersonalAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "WARN" && e.Message.Contains("Retry-After=42s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_RateLimited_WithHttpDateRetryAfter_ResolvedAgainstClock()
    {
        // An HTTP-date Retry-After is resolved against the injected clock into a remaining delay.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sut = ServiceWithResponse(
            HttpStatusCode.TooManyRequests,
            r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(30)),
            now: () => now);

        await sut.GetPersonalAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "WARN" && e.Message.Contains("Retry-After=30s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_RateLimited_WithPastHttpDate_OmitsRetryAfter()
    {
        // A Retry-After date already in the past yields no backoff hint rather than a negative one.
        _pluginLog = TestMockFactory.CreatePluginLogService(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { PluginLogLevel = "DEBUG" });
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sut = ServiceWithResponse(
            HttpStatusCode.TooManyRequests,
            r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(-30)),
            now: () => now);

        await sut.GetPersonalAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "WARN" && !e.Message.Contains("Retry-After=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_AuthFailure_WithRetryAfter_NamesRateLimitInOutcome()
    {
        // A 401 carrying Retry-After is both an auth failure (Debug, no re-link spam) and a rate limit: the
        // user-facing outcome names the backoff so an admin sees why the tab is empty right now.
        _pluginLog = TestMockFactory.CreatePluginLogService();
        var userId = Guid.NewGuid();
        var sut = ServiceWithResponse(
            HttpStatusCode.Unauthorized,
            r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90)));

        await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Contains(_pluginLog.GetEntries(source: "Trakt"), e =>
            e.Level == "INFO" && e.Message.Contains("rate-limited for 90s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPersonal_AfterFailure_SecondCallIsSuppressedByBackoff()
    {
        // The negative cache must stop a second live fetch while the backoff is active: a rate-limited or down
        // Trakt is hit once, then served null from the backoff until it elapses (the duplicate-tab-load dampener).
        var userId = Guid.NewGuid();
        var fetches = 0;
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                fetches++;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));
        var sut = CreateServiceWithFactory(factory.Object);

        await sut.GetPersonalAsync(userId, CancellationToken.None);
        var afterFirst = fetches;
        var second = await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.Equal(1, afterFirst); // movies fetch failed with 401; shows is skipped on auth failure
        Assert.Equal(1, fetches); // the second call never reached the network
        Assert.Null(second);
    }

    [Fact]
    public async Task GetPersonal_SuccessAfterBackoffExpires_FetchesLiveAndServes()
    {
        // The real path CodeRabbit asked for: a first fetch fails and sets the backoff; once the backoff window
        // elapses a second live call fetches successfully and serves the fresh result.
        var userId = Guid.NewGuid();
        var step = 0;
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                // First call (movies) 401 to set the backoff; later calls succeed with real payloads.
                step++;
                return step == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(step % 2 == 0 ? MoviesJson : ShowsJson) };
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken _) =>
                Scored(u, c.Select(x => (x.TmdbId, x.MediaType, x.Title ?? string.Empty)).ToArray()));
        var sut = CreateServiceWithFactory(factory.Object);

        var first = await sut.GetPersonalAsync(userId, CancellationToken.None);
        Assert.Null(first); // failed, backoff set
        Assert.True(_cache.IsPersonalBackoffActive(userId));
        Assert.Equal(1, step); // only the movies 401 fired

        // While the backoff is active the live fetch is still suppressed.
        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        Assert.Equal(1, step); // no extra fetch during backoff

        // Advance past the failure backoff; the next call fetches live and serves the result.
        _cacheNow = _cacheNow.Add(TraktCacheService.DefaultFailureBackoff).AddSeconds(1);
        Assert.False(_cache.IsPersonalBackoffActive(userId));
        var served = await sut.GetPersonalAsync(userId, CancellationToken.None);

        Assert.NotNull(served);
        Assert.NotEmpty(served!.Recommendations);
    }

    [Fact]
    public async Task GetPersonal_ConcurrentCallsForSameUser_ShareOneFetch()
    {
        // Single-flight: two simultaneous requests for the same user (e.g. two devices) must coalesce onto one
        // live fetch, not fire two. The gate holds the first fetch open until both callers are awaiting.
        var userId = Guid.NewGuid();
        var fetchCount = 0;
        var gate = new TaskCompletionSource();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async () =>
            {
                var n = System.Threading.Interlocked.Increment(ref fetchCount);
                if (n == 1)
                {
                    await gate.Task.ConfigureAwait(false); // hold the first (movies) fetch open
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(n % 2 == 1 ? MoviesJson : ShowsJson),
                };
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken _) =>
                Scored(u, c.Select(x => (x.TmdbId, x.MediaType, x.Title ?? string.Empty)).ToArray()));
        var sut = CreateServiceWithFactory(factory.Object);

        var first = sut.GetPersonalAsync(userId, CancellationToken.None);
        var second = sut.GetPersonalAsync(userId, CancellationToken.None);
        gate.SetResult(); // release the shared fetch
        var results = await Task.WhenAll(first, second);

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        // Only one movies + one shows fetch ran total, proving the second caller joined the first's fetch.
        Assert.Equal(2, fetchCount);
    }

    [Fact]
    public async Task GetPersonal_InvalidatedMidFetch_DoesNotCacheStaleResult()
    {
        // A fetch in flight when a mutation invalidates the user must not resurrect its now-stale result into
        // the cache. The gate holds the fetch open until the invalidation has bumped the generation.
        var userId = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        var calls = 0;
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async () =>
            {
                var n = System.Threading.Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    await gate.Task.ConfigureAwait(false);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(n % 2 == 1 ? MoviesJson : ShowsJson),
                };
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Trakt")).Returns(() => new HttpClient(handler.Object));
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken _) =>
                Scored(u, c.Select(x => (x.TmdbId, x.MediaType, x.Title ?? string.Empty)).ToArray()));
        var sut = CreateServiceWithFactory(factory.Object);

        var fetch = sut.GetPersonalAsync(userId, CancellationToken.None);
        sut.InvalidatePersonal(userId); // mutation races the in-flight fetch
        gate.SetResult();
        await fetch;

        // The stale result was not written: the cache holds nothing for this user.
        Assert.Null(_cache.GetPersonal(userId, TimeSpan.FromHours(12)));
    }

    [Fact]
    public async Task GetPersonal_SequentialCalls_AfterCompletion_StartFreshFetch()
    {
        // The in-flight slot clears once the shared fetch settles, so a later call is not stuck awaiting the
        // old completed task: with the warm cache dropped it fetches again.
        var userId = Guid.NewGuid();
        _discovery.Setup(d => d.ScoreExternalCandidatesAsync(userId, It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid u, IReadOnlyList<ExternalDiscoveryCandidate> c, CancellationToken _) =>
                Scored(u, c.Select(x => (x.TmdbId, x.MediaType, x.Title ?? string.Empty)).ToArray()));
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));
        var sut = CreateService();

        Assert.NotNull(await sut.GetPersonalAsync(userId, CancellationToken.None));

        // Drop the warm cache and the in-flight slot; the next call must fetch again (strict handler would throw
        // if it did not and the queue were empty).
        sut.InvalidatePersonal(userId);
        _responses.Enqueue((HttpStatusCode.OK, MoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, ShowsJson));

        Assert.NotNull(await sut.GetPersonalAsync(userId, CancellationToken.None));
        Assert.Empty(_responses);
    }

    [Fact]
    public async Task IsLinkedFor_OfficialHasToken_ReturnsTrue()
    {
        // An official-plugin user must report linked so the controller fetches their recommendations
        // instead of showing the not-linked message.
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.TryGetToken(userId, It.IsAny<DateTimeOffset>()))
            .Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("official-tok", FarFuture));

        var (sut, _) = CreateCapturingService();
        Assert.True(await sut.IsLinkedForAsync(userId, CancellationToken.None));
    }

    [Fact]
    public async Task IsLinkedFor_NeitherSource_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);

        var (sut, _) = CreateCapturingService();
        Assert.False(await sut.IsLinkedForAsync(userId, CancellationToken.None));
    }

    [Fact]
    public async Task GetTrending_UsesOfficialClientId()
    {
        // Trending is client-id-only (no bearer) and uses the official app's public id. (Regression guard for B2.)
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _responses.Enqueue((HttpStatusCode.OK, TrendingMoviesJson));
        _responses.Enqueue((HttpStatusCode.OK, TrendingShowsJson));

        var (sut, captured) = CreateCapturingService();
        var result = await sut.GetTrendingAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(captured);
        Assert.All(captured, c =>
        {
            Assert.Equal(Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktPluginReader.OfficialTraktClientId, c.ApiKey);
            Assert.Null(c.Authorization);
        });
    }

    [Fact]
    public async Task MasterSwitchOff_DisablesAllSourcing()
    {
        // The user-facing master switch kills personal, trending, link-status and refresh regardless of own
        // creds or official-plugin presence - the admin's global off-switch for Trakt.
        Plugin.Instance!.Configuration.TraktSourcingEnabled = false;
        var userId = Guid.NewGuid();
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);
        _officialPlugin.Setup(p => p.GetLinkedUserIds(It.IsAny<DateTimeOffset>())).Returns(new[] { userId });

        var (sut, _) = CreateCapturingService();

        Assert.Null(await sut.GetPersonalAsync(userId, CancellationToken.None));
        Assert.Null(await sut.GetTrendingAsync(userId, CancellationToken.None));
        Assert.False(await sut.IsLinkedForAsync(userId, CancellationToken.None));
        await sut.RefreshAllAsync(CancellationToken.None); // must not throw and must warm nothing

        // No HTTP fetch happened on any path (a Strict handler would have thrown on an un-queued dequeue).
        _discovery.Verify(
            d => d.ScoreExternalCandidatesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ExternalDiscoveryCandidate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetConfigNull_AllPathsReturnEmptyWithoutThrowing()
    {
        // With no plugin configuration available, every entry point degrades to empty rather than throwing.
        ControllerTestFactory.TeardownPluginInstance();
        try
        {
            var (sut, _) = CreateCapturingService();
            Assert.Null(await sut.GetPersonalAsync(Guid.NewGuid(), CancellationToken.None));
            Assert.Null(await sut.GetTrendingAsync(Guid.NewGuid(), CancellationToken.None));
            Assert.False(await sut.IsLinkedForAsync(Guid.NewGuid(), CancellationToken.None));
            await sut.RefreshAllAsync(CancellationToken.None);
        }
        finally
        {
            // Restore the singleton for the Dispose teardown + any later test in this collection.
            ControllerTestFactory.InitializePluginInstance();
            Plugin.Instance!.Configuration.TraktSourcingEnabled = true;
        }
    }

    private static void AssertOfficialCaptured(List<CapturedRequest> captured)
    {
        Assert.NotEmpty(captured);
        Assert.All(captured, c =>
        {
            Assert.Equal(Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktPluginReader.OfficialTraktClientId, c.ApiKey);
            Assert.Equal("Bearer official-tok", c.Authorization);
        });
    }
}
