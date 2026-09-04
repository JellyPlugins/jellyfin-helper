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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for the in-memory TTL cache inside SeerrDiscoveryService.GetCachedSeerrUsersAsync.
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryServiceCacheTests : IDisposable
{
    // Single-user roster JSON returned by the mock HTTP handler.
    // The Jellyfin user ID below is the "N"-format of LinkedJellyfinUserId.
    private const string SingleUserJson = """
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [
            { "id": 1, "displayName": "alice", "jellyfinUserId": "11111111222233334444555555555555", "permissions": 32 }
          ]
        }
        """;

    private static readonly Guid LinkedJellyfinUserId = new("11111111-2222-3333-4444-555555555555");

    // Infrastructure

    private readonly DiscoveryCacheService _cache;

    public SeerrDiscoveryServiceCacheTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-api-key";

        var pluginLog = new Mock<IPluginLogService>();
        _cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object);
    }

    public void Dispose()
    {
        _cache.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
    }

    // Factory helpers

    private SeerrDiscoveryService BuildSut(FailableCountingHttpHandler handler)
    {
        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var history = new Mock<IWatchHistoryService>();
        var arr = new Mock<IArrIntegrationService>();
        var libraryManager = TestMockFactory.CreateLibraryManager();
        libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
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
        var feedbackStore = new Mock<IDiscoveryFeedbackStore>();

        return new SeerrDiscoveryService(
            httpFactoryMock.Object,
            history.Object,
            arr.Object,
            libraryManager.Object,
            ensemble,
            _cache,
            feedbackStore.Object,
            pluginLog.Object,
            new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    // Warm cache: second sequential call must NOT hit the network.

    [Fact]
    public async Task ResolveSeerrUserIdAsync_WarmCache_NoAdditionalHttpCallsMade()
    {
        // Arrange: handler that counts every GET /api/v1/user hit.
        using var handler = new FailableCountingHttpHandler(HttpStatusCode.OK, SingleUserJson, fetchDelayMs: 0);
        var sut = BuildSut(handler);

        // Act: first call populates the cache.
        var first = await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);
        var callsAfterFirst = handler.CallCount;

        // Second call - should be served from the in-memory cache.
        var second = await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);
        var callsAfterSecond = handler.CallCount;

        // Assert
        Assert.Equal(1, first);  // Correctly resolved on first call
        Assert.Equal(1, second); // Same result served from cache
        Assert.Equal(callsAfterFirst, callsAfterSecond); // No new HTTP request issued
        Assert.True(callsAfterFirst >= 1, "At least one HTTP call must have occurred to warm the cache.");
    }

    // Stampede: concurrent cold-cache callers all fan out to HTTP (documents current "allow stampede" behaviour and verifies that once any caller has written the cache the result is consistent).

    [Fact]
    public async Task ResolveSeerrUserIdAsync_ConcurrentColdCacheMisses_AllReceiveConsistentResult()
    {
        // Arrange: give each HTTP response a small delay so all concurrent tasks
        // are guaranteed to pass the fast-path check simultaneously before any
        // of them returns and writes to the cache.
        const int concurrency = 8;
        using var handler = new FailableCountingHttpHandler(HttpStatusCode.OK, SingleUserJson, fetchDelayMs: 50);
        var sut = BuildSut(handler);

        // Act: fire all callers at exactly the same moment.
        var tasks = new Task<int?>[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            tasks[i] = sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);
        }

        var results = await Task.WhenAll(tasks);

        // Assert: every caller must receive the same, correct Seerr user ID.
        Assert.All(results, r => Assert.Equal(1, r));

        // The current implementation does NOT coalesce concurrent fetches - each concurrent caller independently reaches FetchSeerrUsersInternalAsync.
        Assert.InRange(handler.CallCount, 1, concurrency);
    }

    // After the stampede settles the cache is warm. The NEXT call
    //           (after all concurrent ones complete) must not re-fetch.

    [Fact]
    public async Task ResolveSeerrUserIdAsync_AfterStampedeSettles_SubsequentCallUsesCache()
    {
        // Arrange
        const int concurrency = 4;
        using var handler = new FailableCountingHttpHandler(HttpStatusCode.OK, SingleUserJson, fetchDelayMs: 20);
        var sut = BuildSut(handler);

        // Warm the cache via a stampede.
        var stampedeTasks = new Task<int?>[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            stampedeTasks[i] = sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);
        }
        await Task.WhenAll(stampedeTasks);

        var callsAfterStampede = handler.CallCount;

        // Act: one more call - cache should now be warm.
        await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);

        // Assert: handler call count must not have increased.
        Assert.Equal(callsAfterStampede, handler.CallCount);
    }

    // Incomplete fetch (upstream error) must NOT be written to cache.
    // Callers must keep retrying rather than getting a stale empty list
    // for the full TTL.

    [Fact]
    public async Task ResolveSeerrUserIdAsync_FailedFetch_NotCached_NextCallRetries()
    {
        // Arrange: first two calls fail (500), third succeeds.
        using var handler = new FailableCountingHttpHandler(
            failFirstN: 2,
            successStatus: HttpStatusCode.OK,
            successBody: SingleUserJson,
            fetchDelayMs: 0);
        var sut = BuildSut(handler);

        // Act: first call - upstream error, empty list returned, null resolved.
        var first = await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);

        // Second call - still failing, must retry rather than return cached empty.
        var second = await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);

        // Third call - success; must resolve correctly and cache the result.
        var third = await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);

        // Fourth call - cache warm, no new HTTP request.
        var callsBeforeFourth = handler.CallCount;
        var fourth = await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);

        // Assert
        Assert.Null(first);   // Upstream error -> null (not linked)
        Assert.Null(second);  // Still retried, still failed -> null
        Assert.Equal(1, third);  // Success -> resolved
        Assert.Equal(1, fourth); // Cache hit -> resolved
        Assert.Equal(callsBeforeFourth, handler.CallCount); // Fourth call did not re-fetch
        // Exactly 3 HTTP calls must have been made (2 failures + 1 success).
        Assert.Equal(3, callsBeforeFourth);
    }

    // A fetch that settles must not strand a replacement fetch. The completion cleanup
    // clears the in-flight slot only when it still holds its own task, so a caller that
    // installed a new fetch after the old one settled keeps a live, coalescable request.
    // Regression guard for the identity-guarded slot cleanup.

    [Fact]
    public async Task ResolveSeerrUserIdAsync_FirstFetchFailsThenSucceeds_SecondFetchNotStrandedByStaleCleanup()
    {
        // Arrange: the first fetch fails (not cached), so the cache stays cold and the second call
        // must start a fresh fetch. If the first fetch's cleanup wiped that second fetch, a third
        // caller would have to launch yet another request.
        using var handler = new FailableCountingHttpHandler(
            failFirstN: 1,
            successStatus: HttpStatusCode.OK,
            successBody: SingleUserJson,
            fetchDelayMs: 0);
        var sut = BuildSut(handler);

        // Act: first call fails and settles, clearing its own slot.
        var first = await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);

        // Second call must succeed and warm the cache; its slot must survive its own cleanup.
        var second = await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);

        // Third call must be a pure cache hit, proving the second fetch's result was retained.
        var callsBeforeThird = handler.CallCount;
        var third = await sut.ResolveSeerrUserIdAsync(LinkedJellyfinUserId, CancellationToken.None);

        // Assert
        Assert.Null(first);      // Upstream error -> not linked, not cached
        Assert.Equal(1, second); // Fresh fetch succeeded and cached
        Assert.Equal(1, third);  // Served from cache
        Assert.Equal(callsBeforeThird, handler.CallCount); // Third call issued no new request
        Assert.Equal(2, callsBeforeThird); // Exactly one failure + one success
    }
}

// FailableCountingHttpHandler - thread-safe scripted handler that counts /api/v1/user
// GET calls and can inject a configurable per-response delay for stampede tests.

/// <summary>
///     A scripted HttpMessageHandler that: counts every GET request to any URL containing /api/v1/user; optionally injects a per-response delay to widen the stampede window; can fail the first failFirstN requests with HTTP 500.
/// </summary>
internal sealed class FailableCountingHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _successStatus;
    private readonly string _successBody;
    private readonly int _fetchDelayMs;
    private readonly int _failFirstN;
    private int _callCount;

    /// <summary>Initializes a new instance of the <see cref="FailableCountingHttpHandler"/> class.Initialises a handler where every call succeeds.</summary>
    public FailableCountingHttpHandler(HttpStatusCode successStatus, string successBody, int fetchDelayMs)
        : this(failFirstN: 0, successStatus, successBody, fetchDelayMs) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FailableCountingHttpHandler"/> class.
    ///     Initialises a handler where the first <paramref name="failFirstN"/> calls return
    ///     HTTP 500 and subsequent calls succeed.
    /// </summary>
    public FailableCountingHttpHandler(int failFirstN, HttpStatusCode successStatus, string successBody, int fetchDelayMs)
    {
        _failFirstN = failFirstN;
        _successStatus = successStatus;
        _successBody = successBody;
        _fetchDelayMs = fetchDelayMs;
    }

    /// <summary>Gets total number of calls that have been dispatched through this handler.</summary>
    public int CallCount => _callCount;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Only count user-list requests to keep the counter focused on the path under test.
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var isUserListRequest = path.Contains("/api/v1/user", StringComparison.Ordinal);
        if (isUserListRequest)
        {
            Interlocked.Increment(ref _callCount);
        }

        // Optional delay to guarantee concurrent callers all enter the slow-path
        // before any of them writes the result back to the cache.
        if (_fetchDelayMs > 0)
        {
            await Task.Delay(_fetchDelayMs, cancellationToken).ConfigureAwait(false);
        }

        // Fail the first N requests (regardless of URL) to test non-caching of errors.
        if (_callCount <= _failFirstN)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("simulated upstream error")
            };
        }

        return new HttpResponseMessage(_successStatus)
        {
            Content = new StringContent(_successBody)
        };
    }
}
