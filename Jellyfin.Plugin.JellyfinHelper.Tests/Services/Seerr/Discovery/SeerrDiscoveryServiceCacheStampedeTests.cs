using System;
using System.Collections.Generic;
using System.Linq;
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
///     Concurrent correctness tests for GetCachedSeerrUsersAsync (exercised through the public GetSeerrUsersAsync entry point).
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryServiceCacheStampedeTests : IDisposable
{
    // Shared user-page JSON returned by all scripted responses

    private const string SinglePageUserJson = """
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 3, "page": 1 },
          "results": [
            { "id": 1, "displayName": "alice", "jellyfinUserId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            { "id": 2, "displayName": "bob",   "jellyfinUserId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            { "id": 3, "displayName": "carol", "jellyfinUserId": "cccccccccccccccccccccccccccccccc" }
          ]
        }
        """;

    private readonly DiscoveryCacheService _cache;

    public SeerrDiscoveryServiceCacheStampedeTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-api-key";

        _cache = new DiscoveryCacheService(
            TestMockFactory.CreatePluginLogService(),
            new Mock<ILogger<DiscoveryCacheService>>().Object);
    }

    public void Dispose()
    {
        _cache.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
    }

    // Factory helpers

    /// <summary>
    ///     Creates a SeerrDiscoveryService backed by handler. Every call to IHttpClientFactory.CreateClient returns a fresh HttpClient wrapping the same handler so that the request-count tracking in CountingHttpHandler accumulates across all calls.
    /// </summary>
    private SeerrDiscoveryService BuildService(CountingHttpHandler handler)
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

    // Tests

    [Fact]
    public async Task GetSeerrUsersAsync_WarmCache_SecondCallMakesNoHttpRequest()
    {
        // Arrange: one response queued - only the first call should consume it.
        using var handler = new CountingHttpHandler();
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        var sut = BuildService(handler);

        // Act: prime the cache with a first call, then call again.
        var first = await sut.GetSeerrUsersAsync(CancellationToken.None);
        var second = await sut.GetSeerrUsersAsync(CancellationToken.None);

        // Assert: both calls return the same users, but the handler only saw one request.
        Assert.Equal(3, first.Count);
        Assert.Equal(3, second.Count);
        Assert.Equal(1, handler.RequestCount("/api/v1/user"));
    }

    [Fact]
    public async Task GetSeerrUsersAsync_ConcurrentColdMiss_AllCallersReceiveValidResult()
    {
        // Arrange: 8 concurrent callers hit an empty cache simultaneously. The handler introduces a brief response delay to maximise the chance that multiple callers pass the "cache expired?" check before any response arrives.
        const int concurrency = 8;
        using var handler = new CountingHttpHandler(responseDelayMs: 30);
        for (var i = 0; i < concurrency; i++)
        {
            handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        }

        var sut = BuildService(handler);

        // Act: launch all 8 callers simultaneously from a cold cache.
        var tasks = Enumerable
            .Range(0, concurrency)
            .Select(_ => sut.GetSeerrUsersAsync(CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert correctness: every caller got a non-empty list.
        Assert.All(results, list => Assert.NotEmpty(list));

        // Assert consistency: every caller got the same 3 users.
        var displayNames = results[0].Select(u => u.DisplayName).OrderBy(x => x).ToList();
        foreach (var list in results.Skip(1))
        {
            var names = list.Select(u => u.DisplayName).OrderBy(x => x).ToList();
            Assert.Equal(displayNames, names);
        }
    }

    [Fact]
    public async Task GetSeerrUsersAsync_ConcurrentColdMiss_CoalescesToSingleHttpRequest()
    {
        // A cold-cache burst must collapse onto one shared fetch rather than stampede Seerr with one
        // roster read per caller. Only a single response is queued, so any second request would fall
        // through to a 404 and the assertion below would fail.
        const int concurrency = 8;
        using var handler = new CountingHttpHandler(responseDelayMs: 30);
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);

        var sut = BuildService(handler);

        var tasks = Enumerable
            .Range(0, concurrency)
            .Select(_ => sut.GetSeerrUsersAsync(CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, list => Assert.Equal(3, list.Count));
        Assert.Equal(1, handler.RequestCount("/api/v1/user"));
    }

    [Fact]
    public async Task GetSeerrUsersAsync_OneCallerCancels_OthersStillComplete()
    {
        // A single caller cancelling its wait must not abort the shared fetch for the coalesced
        // callers. The cancelling caller observes its own cancellation; the rest still get the roster.
        using var handler = new CountingHttpHandler(responseDelayMs: 50);
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);

        var sut = BuildService(handler);

        using var cts = new CancellationTokenSource();
        var cancelling = sut.GetSeerrUsersAsync(cts.Token);
        var surviving = sut.GetSeerrUsersAsync(CancellationToken.None);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelling);

        var survivingResult = await surviving;
        Assert.Equal(3, survivingResult.Count);
        Assert.Equal(1, handler.RequestCount("/api/v1/user"));
    }

    [Fact]
    public async Task GetSeerrUsersAsync_AfterStampede_SubsequentCallServedFromCache()
    {
        // Arrange: 4 concurrent callers warm the cache, then a 5th call should be
        // served from cache without issuing another HTTP request.
        const int concurrency = 4;
        using var handler = new CountingHttpHandler(responseDelayMs: 20);
        for (var i = 0; i < concurrency; i++)
        {
            handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        }

        var sut = BuildService(handler);

        // Warm the cache (concurrent).
        var stampedeTasks = Enumerable
            .Range(0, concurrency)
            .Select(_ => sut.GetSeerrUsersAsync(CancellationToken.None))
            .ToArray();
        await Task.WhenAll(stampedeTasks);

        var countAfterStampede = handler.RequestCount("/api/v1/user");

        // Act: one more call after the cache has been populated.
        var followUp = await sut.GetSeerrUsersAsync(CancellationToken.None);

        // Assert: the follow-up did not generate an additional HTTP request.
        Assert.Equal(3, followUp.Count);
        // Follow-up call after a warmed cache must not issue an additional HTTP request.
        Assert.Equal(countAfterStampede, handler.RequestCount("/api/v1/user"));
    }

    [Fact]
    public async Task GetSeerrUsersAsync_FailedFetch_NotCached_NextCallRetries()
    {
        // Arrange: first call fails (500), second call succeeds.
        // The failed (empty) result must NOT be cached - the next caller must retry.
        using var handler = new CountingHttpHandler();
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.InternalServerError, "boom");
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        var sut = BuildService(handler);

        // Act
        var firstResult = await sut.GetSeerrUsersAsync(CancellationToken.None);
        var secondResult = await sut.GetSeerrUsersAsync(CancellationToken.None);

        // Assert: first call returns empty (upstream error), second call retries and succeeds.
        Assert.Empty(firstResult);
        Assert.Equal(3, secondResult.Count);
        // A failed fetch must not be cached; the next call must hit the upstream again.
        Assert.Equal(2, handler.RequestCount("/api/v1/user"));
    }
}

/// <summary>
///     A thread-safe HttpMessageHandler that: Serves scripted responses from a per-path FIFO queue. Counts how many times each path-suffix was requested.
/// </summary>
internal sealed class CountingHttpHandler : HttpMessageHandler
{
    private readonly int _responseDelayMs;
    private readonly Lock _lock = new();

    // per path-suffix -> FIFO queue of scripted responses
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _queues =
        new(StringComparer.Ordinal);

    // per path-suffix -> total number of requests received
    private readonly Dictionary<string, int> _counts =
        new(StringComparer.Ordinal);

    /// <param name="responseDelayMs">
    ///     How long (in milliseconds) to wait before returning the response.
    ///     0 means return immediately.  Set to a positive value in stampede tests
    ///     to give other concurrent callers time to pass the cache-miss check.
    /// </param>
    public CountingHttpHandler(int responseDelayMs = 0)
    {
        _responseDelayMs = responseDelayMs;
    }

    /// <summary>Queues one scripted response for requests whose path ends with <paramref name="pathSuffix"/>.</summary>
    public void EnqueueResponse(string pathSuffix, HttpStatusCode status, string body)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(pathSuffix, out var queue))
            {
                queue = new Queue<(HttpStatusCode, string)>();
                _queues[pathSuffix] = queue;
            }

            queue.Enqueue((status, body));
        }
    }

    /// <summary>Returns the total number of requests received for paths ending with <paramref name="pathSuffix"/>.</summary>
    /// <returns></returns>
    public int RequestCount(string pathSuffix)
    {
        lock (_lock)
        {
            return _counts.TryGetValue(pathSuffix, out var count) ? count : 0;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_responseDelayMs > 0)
        {
            await Task.Delay(_responseDelayMs, cancellationToken).ConfigureAwait(false);
        }

        var url = request.RequestUri?.AbsolutePath ?? string.Empty;

        HttpStatusCode status;
        string body;

        lock (_lock)
        {
            // Find the best-matching registered path suffix (longest match wins).
            string? matchedKey = null;
            foreach (var key in _queues.Keys)
            {
                if (url.EndsWith(key, StringComparison.Ordinal)
                    || (request.RequestUri?.PathAndQuery ?? string.Empty).EndsWith(key, StringComparison.Ordinal))
                {
                    if (matchedKey == null || key.Length > matchedKey.Length)
                    {
                        matchedKey = key;
                    }
                }
            }

            // Increment request counter for matched key (or the raw URL for unregistered paths).
            var countKey = matchedKey ?? url;
            _counts[countKey] = (_counts.TryGetValue(countKey, out var c) ? c : 0) + 1;

            if (matchedKey != null && _queues[matchedKey].TryDequeue(out var scripted))
            {
                (status, body) = scripted;
            }
            else
            {
                // Queue exhausted or no route registered.
                status = HttpStatusCode.NotFound;
                body = $"CountingHttpHandler: no queued response for {url}";
            }
        }

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        };
    }
}
