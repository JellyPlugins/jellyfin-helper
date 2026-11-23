using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
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
    private readonly List<PerUserEnsembleRegistry> _registries = [];

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
        foreach (var registry in _registries)
        {
            registry.Dispose();
        }

        _cache.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
    }

    // Factory helpers

    /// <summary>
    ///     Creates a SeerrDiscoveryService backed by handler. Every call to IHttpClientFactory.CreateClient returns a fresh HttpClient wrapping the same handler so that the request-count tracking in CountingHttpHandler accumulates across all calls.
    /// </summary>
    private SeerrDiscoveryService BuildService(CountingHttpHandler handler, Mock<IArrIntegrationService>? configuredArr = null, Mock<ILibraryManager>? configuredLibrary = null, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.TitleMetadataCache? metadata = null, Action? beforeRosterSelection = null, Mock<IDiscoveryFeedbackStore>? configuredFeedback = null)
    {
        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var history = new Mock<IWatchHistoryService>();
        var arr = configuredArr ?? new Mock<IArrIntegrationService>();
        var libraryManager = configuredLibrary ?? TestMockFactory.CreateLibraryManager();
        if (configuredLibrary is null) libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
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
        var feedbackStore = configuredFeedback ?? new Mock<IDiscoveryFeedbackStore>();

        var perUserRegistry = new PerUserEnsembleRegistry(
            ensemble,
            null,
            null,
            new EnsembleBlendBounds(
                EnsembleScoringStrategy.DefaultAlphaMin,
                EnsembleScoringStrategy.DefaultAlphaMax,
                EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
            pluginLog.Object);
        _registries.Add(perUserRegistry);

        return new SeerrDiscoveryService(
            httpFactoryMock.Object,
            history.Object,
            arr.Object,
            libraryManager.Object,
            perUserRegistry,
            _cache,
            feedbackStore.Object,
            pluginLog.Object,
            TestFixtures.TestMockFactory.CreateSecretProtector(),
            TestFixtures.TestMockFactory.CreateVirtualItemDetector(),
            new Mock<ILogger<SeerrDiscoveryService>>().Object,
            titleMetadataCache: metadata) { BeforeRosterCacheSelection = beforeRosterSelection };
    }

    // Tests

    [Fact]
    public async Task ASourceChangeBetweenCaptureAndWarmRosterSelectionCannotReturnTheOldAccounts()
    {
        using var handler = new CountingHttpHandler();
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        var change = false;
        var hookCalls = 0;
        var service = BuildService(handler, beforeRosterSelection: () =>
        {
            if (change) { hookCalls++; Plugin.Instance!.Configuration.SeerrUrl = "https://replacement.example.com"; }
        });
        Assert.NotEmpty(await service.GetSeerrUsersAsync(TestContext.Current.CancellationToken));
        change = true;
        Assert.Empty(await service.GetSeerrUsersAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, hookCalls);
        Assert.Equal(1, handler.RequestCount("/api/v1/user"));
    }

    [Fact]
    public async Task TheFinalReceiptAdmissionGuardRejectsAWithdrawnCapturedSource()
    {
        using var handler = new CountingHttpHandler();
        var feedback = new Mock<IDiscoveryFeedbackStore>(MockBehavior.Strict);
        var service = BuildService(handler, configuredFeedback: feedback);
        var scope = (string?)typeof(SeerrDiscoveryService).GetMethod("UserSourceScope", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Plugin.Instance?.Configuration]);
        Plugin.Instance!.Configuration.SeerrUrl = "https://replacement.example.com";
        var receipt = (Task<bool>)typeof(SeerrDiscoveryService).GetMethod("ReconcileSingleItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [Guid.NewGuid(), 77, "movie", scope, TestContext.Current.CancellationToken])!;
        Assert.False(await receipt.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        feedback.VerifyNoOtherCalls();
        Assert.Equal(0, handler.RequestCount("/api/v1/request"));
        Assert.Equal(0, handler.RequestCount("/api/v1/user"));
    }

    [Fact]
    public async Task ReconciliationRechecksItsCapturedSourceAfterWaitingForTheCache()
    {
        var cacheReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CountingHttpHandler();
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        handler.EnqueueResponse("/api/v1/request", HttpStatusCode.OK, """{"pageInfo":{"results":1},"results":[{"media":{"tmdbId":77,"mediaType":"movie"}}]}""");
        var user = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.True(_cache.Save([new DiscoveryResult { UserId = user, Recommendations = [new() { TmdbId = 77 }] }]));
        var feedback = new Mock<IDiscoveryFeedbackStore>(MockBehavior.Strict);
        var service = BuildService(handler, configuredFeedback: feedback);
        var cacheGate = (SemaphoreSlim)typeof(DiscoveryCacheService).GetField("_fileLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_cache)!;
        await cacheGate.WaitAsync(TestContext.Current.CancellationToken);
        _cache.BeforeLoadWait = () => cacheReached.TrySetResult();
        var reconciliation = service.ReconcileRequestedItemsAsync(user, TestContext.Current.CancellationToken);
        try
        {
            await cacheReached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(reconciliation.IsCompleted);
            Plugin.Instance!.Configuration.SeerrUrl = "https://replacement.example.com";
        }
        finally
        {
            cacheGate.Release();
            await reconciliation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _cache.BeforeLoadWait = null;
        }
        Assert.Equal(0, await reconciliation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount("/api/v1/user"));
        Assert.Equal(1, handler.RequestCount("/api/v1/request"));
        feedback.VerifyNoOtherCalls();
        Assert.False(Assert.Single(Assert.Single(_cache.Load()).Recommendations).AlreadyRequested);
    }

    [Fact]
    public async Task SourceWithdrawalDuringTheFirstReceiptWaitCannotAdmitAnotherOldSourceReceipt()
    {
        using var handler = new CountingHttpHandler();
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        handler.EnqueueResponse("/api/v1/request", HttpStatusCode.OK, """{"pageInfo":{"results":2},"results":[{"media":{"tmdbId":77,"mediaType":"movie"}},{"media":{"tmdbId":78,"mediaType":"movie"}}]}""");
        var user = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.True(_cache.Save([new DiscoveryResult { UserId = user, Recommendations = [new() { TmdbId = 77 }, new() { TmdbId = 78 }] }]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedback = new Mock<IDiscoveryFeedbackStore>(MockBehavior.Strict);
        feedback.Setup(store => store.GetPendingRequestedItems(user)).Returns(new HashSet<(int, string)>());
        feedback.Setup(store => store.GetRequestedItems(user)).Returns(new HashSet<(int, string)>());
        feedback.Setup(store => store.RecordRequestedAsync(user, It.IsAny<int>(), "movie", It.IsAny<CancellationToken>())).Returns(() => { entered.TrySetResult(); return release.Task; });
        var reconciliation = BuildService(handler, configuredFeedback: feedback).ReconcileRequestedItemsAsync(user, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Plugin.Instance!.Configuration.SeerrApiKey = "replacement-key";
            release.TrySetResult(true);
            Assert.Equal(0, await reconciliation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            feedback.Verify(store => store.RecordRequestedAsync(user, It.IsAny<int>(), "movie", It.IsAny<CancellationToken>()), Times.Once);
            Assert.All(Assert.Single(_cache.Load()).Recommendations, card => Assert.False(card.AlreadyRequested));
        }
        finally { release.TrySetResult(true); await reconciliation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData("url")]
    [InlineData("key")]
    [InlineData("tls")]
    public async Task ReconciliationCannotApplyAnOldRosterAccountAfterItsSourceChanges(string change)
    {
        using var handler = new CountingHttpHandler
        {
            BeforeResponse = (_, _) =>
            {
                var current = Plugin.Instance!.Configuration;
                typeof(Plugin).GetProperty(nameof(Plugin.Configuration))!.SetValue(Plugin.Instance, new PluginConfiguration
                {
                    SeerrUrl = change == "url" ? "https://replacement.example.com" : current.SeerrUrl,
                    SeerrApiKey = change == "key" ? "replacement-key" : current.SeerrApiKey,
                    SeerrSkipCertificateValidation = change == "tls"
                });
                return Task.CompletedTask;
            }
        };
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        var user = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.True(_cache.Save([new DiscoveryResult { UserId = user, Recommendations = [new() { TmdbId = 77 }] }]));
        Assert.Equal(0, await BuildService(handler).ReconcileRequestedItemsAsync(user, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount("/api/v1/user"));
        Assert.Equal(0, handler.RequestCount("/api/v1/request"));
        Assert.False(Assert.Single(Assert.Single(_cache.Load()).Recommendations).AlreadyRequested);
    }

    [Fact]
    public async Task ArrCacheCannotRetainWithdrawnLibraryAliasesButDirectLibraryOwnershipRemainsExcluded()
    {
        var config = Plugin.Instance!.Configuration;
        config.TraktSourcingEnabled = true;
        var metadata = new Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.TitleMetadataCache();
        metadata.StoreSupplemental(config, new Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.TitleMetadataSnapshot
        {
            TmdbId = 700, TvdbId = 800, MediaType = "tv", FetchedAtUtc = DateTime.UtcNow
        }, new("trakt", "https://api.trakt.tv", "public-metadata"), null);
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(
        [
            new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Path = "/media/alias", ProviderIds = new() { ["Tvdb"] = "800" } },
            new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Path = "/media/direct", ProviderIds = new() { ["Tmdb"] = "701" } }
        ]);
        using var handler = new CountingHttpHandler();
        var service = BuildService(handler, configuredLibrary: library, metadata: metadata);
        Task<HashSet<(int, string)>> Read() => (Task<HashSet<(int, string)>>)typeof(SeerrDiscoveryService).GetMethod("GetCachedExclusionsCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [config, true, TestContext.Current.CancellationToken])!;
        Assert.Contains((700, "tv"), await Read());
        config.TraktSourcingEnabled = false;
        var changed = await Read();
        Assert.DoesNotContain((700, "tv"), changed);
        Assert.Contains((701, "tv"), changed);
        Assert.Equal(0, handler.RequestCount("/api/v1/user"));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("key")]
    [InlineData("tls")]
    [InlineData("disabled")]
    public async Task RosterCacheIsBoundToTheConfiguredServerCredentialAndTransport(string change)
    {
        using var handler = new CountingHttpHandler();
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson.Replace("alice", "new-account", StringComparison.Ordinal));
        var service = BuildService(handler);
        Assert.Equal("alice", (await service.GetSeerrUsersAsync(TestContext.Current.CancellationToken))[0].DisplayName);
        var config = Plugin.Instance!.Configuration;
        if (change == "url") config.SeerrUrl = "https://replacement.example.com";
        if (change == "key") config.SeerrApiKey = "replacement-key";
        if (change == "tls") config.SeerrSkipCertificateValidation = true;
        if (change == "disabled") config.SeerrApiKey = string.Empty;
        var next = await service.GetSeerrUsersAsync(TestContext.Current.CancellationToken);
        if (change == "disabled") Assert.Empty(next);
        else Assert.Equal("new-account", next[0].DisplayName);
        Assert.Equal(change == "disabled" ? 1 : 2, handler.RequestCount("/api/v1/user"));
    }

    [Fact]
    public async Task LateOldRosterCannotServeANewSourceAndBackgroundWorkStaysSingleFlight()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CountingHttpHandler { BeforeResponse = async (request, token) =>
        {
            if (request.RequestUri!.Host == "seerr.example.com") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        } };
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson);
        handler.EnqueueResponse("/api/v1/user", HttpStatusCode.OK, SinglePageUserJson.Replace("alice", "new-account", StringComparison.Ordinal));
        var service = BuildService(handler);
        var old = service.GetSeerrUsersAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Plugin.Instance!.Configuration.SeerrUrl = "https://replacement.example.com";
            var current = service.GetSeerrUsersAsync(TestContext.Current.CancellationToken);
            Assert.False(current.IsCompleted);
            release.TrySetResult();
            Assert.Empty(await old.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal("new-account", (await current.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))[0].DisplayName);
            Assert.Equal("new-account", (await service.GetSeerrUsersAsync(TestContext.Current.CancellationToken))[0].DisplayName);
            Assert.Equal(2, handler.RequestCount("/api/v1/user"));
        }
        finally
        {
            release.TrySetResult();
            await old.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("url")]
    [InlineData("key")]
    [InlineData("tls")]
    [InlineData("disabled")]
    public async Task OwnershipCacheNeverCarriesTitlesAcrossArrConfigurationChanges(string change)
    {
        var config = Plugin.Instance!.Configuration;
        config.RadarrInstances = [new ArrInstanceConfig { Url = "https://arr.example.com", ApiKey = "old-key" }];
        var calls = 0;
        var arr = new Mock<IArrIntegrationService>();
        arr.Setup(value => value.GetRadarrMoviesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<ArrMovie> { new() { TmdbId = ++calls == 1 ? 100 : 200 } });
        using var handler = new CountingHttpHandler();
        var service = BuildService(handler, arr);
        Task<HashSet<(int, string)>> Read() => (Task<HashSet<(int, string)>>)typeof(SeerrDiscoveryService).GetMethod("GetCachedExclusionsCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [config, true, TestContext.Current.CancellationToken])!;
        Assert.Contains((100, "movie"), await Read());
        if (change == "url") config.RadarrInstances[0].Url = "https://replacement.example.com";
        if (change == "key") config.RadarrInstances[0].ApiKey = "replacement-key";
        if (change == "tls") config.RadarrInstances[0].SkipCertificateValidation = true;
        if (change == "disabled") config.RadarrInstances = [];
        var current = await Read();
        Assert.DoesNotContain((100, "movie"), current);
        if (change != "disabled") Assert.Contains((200, "movie"), current);
        Assert.Equal(change == "disabled" ? 1 : 2, calls);
    }

    [Fact]
    public async Task SourceChangesWaitForTheOwnedBuildWithoutCancellingOthersOrMixingMovieAndTvKeys()
    {
        var oldReply = new TaskCompletionSource<List<ArrMovie>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arr = new Mock<IArrIntegrationService>();
        arr.Setup(value => value.GetRadarrMoviesAsync("https://old.example.com", It.IsAny<string>(), false, It.IsAny<CancellationToken>())).Returns(oldReply.Task);
        arr.Setup(value => value.GetRadarrMoviesAsync("https://new.example.com", It.IsAny<string>(), false, It.IsAny<CancellationToken>())).ReturnsAsync([new ArrMovie { TmdbId = 200 }]);
        arr.Setup(value => value.GetSonarrSeriesAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<CancellationToken>())).ReturnsAsync([new ArrSeries { TmdbId = 100 }]);
        using var handler = new CountingHttpHandler();
        var service = BuildService(handler, arr);
        var config = new PluginConfiguration { RadarrInstances = [new ArrInstanceConfig { Url = "https://old.example.com", ApiKey = "key" }], SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.com", ApiKey = "key" }] };
        typeof(Plugin).GetProperty(nameof(Plugin.Configuration))!.SetValue(Plugin.Instance, config);
        Task<HashSet<(int, string)>> Read(CancellationToken token) => (Task<HashSet<(int, string)>>)typeof(SeerrDiscoveryService).GetMethod("GetCachedExclusionsCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [config, true, token])!;
        var old = Read(TestContext.Current.CancellationToken);
        config.RadarrInstances[0].Url = "https://new.example.com";
        var current = Read(TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        var cancelled = Read(cancel.Token);
        try
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(current.IsCompleted);
            arr.Verify(value => value.GetRadarrMoviesAsync("https://new.example.com", It.IsAny<string>(), false, It.IsAny<CancellationToken>()), Times.Never);
            oldReply.TrySetResult([new ArrMovie { TmdbId = 100 }]);
            Assert.Contains((200, "movie"), await old.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            var result = await current.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.DoesNotContain((100, "movie"), result);
            Assert.Contains((100, "tv"), result);
            Assert.Contains((200, "movie"), result);
            Assert.Equal(result.Order(), (await Read(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Order());
            arr.Verify(value => value.GetRadarrMoviesAsync("https://new.example.com", It.IsAny<string>(), false, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            oldReply.TrySetResult([new ArrMovie { TmdbId = 100 }]);
            await Task.WhenAll(old, current).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

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
    internal Func<HttpRequestMessage, CancellationToken, Task>? BeforeResponse { get; init; }
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
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            var matchedKey = _queues.Keys
                .Where(key => url.EndsWith(key, StringComparison.Ordinal) || pathAndQuery.EndsWith(key, StringComparison.Ordinal))
                .MaxBy(key => key.Length);

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

        if (BeforeResponse is not null) await BeforeResponse(request, cancellationToken).ConfigureAwait(false);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        };
    }
}
