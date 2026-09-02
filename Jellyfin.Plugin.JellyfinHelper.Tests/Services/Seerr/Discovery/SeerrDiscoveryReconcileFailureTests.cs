using System;
using System.Collections.Generic;
using System.IO;
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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Covers the fail-safe catch branches of discovery reconciliation that only fire when a
///     dependency throws: a feedback store whose read or write fails, and an invalid Seerr URL
///     reached after the user roster was already resolved from cache.
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryReconcileFailureTests : IDisposable
{
    private const int SeerrUserId = 42;
    private static readonly Guid JellyfinUserId = new("11111111-2222-3333-4444-555555555555");
    private const string JellyfinUserIdHex = "11111111222233334444555555555555";

    private readonly ScriptedHttpHandler _handler;
    private readonly SeerrDiscoveryService _sut;
    private readonly DiscoveryCacheService _cache;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStore;
    private readonly NeuralScoringStrategy _neural;
    private readonly EnsembleScoringStrategy _ensemble;

    public SeerrDiscoveryReconcileFailureTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-api-key";

        _handler = new ScriptedHttpHandler();

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_handler, disposeHandler: false));

        var pluginLog = new Mock<IPluginLogService>();
        _cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, filePath: Path.GetTempFileName());

        _feedbackStore = new Mock<IDiscoveryFeedbackStore>();
        _feedbackStore.Setup(f => f.GetDismissedItems(It.IsAny<Guid>()))
            .Returns(new HashSet<(int, string)>());
        _feedbackStore.Setup(f => f.GetRequestedItems(It.IsAny<Guid>()))
            .Returns(new HashSet<(int, string)>());

        var learned = new LearnedScoringStrategy(null, new Mock<ILogger<LearnedScoringStrategy>>().Object);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        _neural = new NeuralScoringStrategy(null, new Mock<ILogger<NeuralScoringStrategy>>().Object);
        _ensemble = new EnsembleScoringStrategy(
            learned, heuristic, _neural, null,
            EnsembleScoringStrategy.DefaultAlphaMin,
            EnsembleScoringStrategy.DefaultAlphaMax,
            EnsembleScoringStrategy.DefaultGenrePenaltyFloor,
            new Mock<ILogger<EnsembleScoringStrategy>>().Object);

        _sut = new SeerrDiscoveryService(
            httpFactory.Object,
            new Mock<IWatchHistoryService>().Object,
            new Mock<IArrIntegrationService>().Object,
            _ensemble,
            _cache,
            _feedbackStore.Object,
            pluginLog.Object,
            new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    public void Dispose()
    {
        _handler.Dispose();
        _cache.Dispose();
        _ensemble.Dispose();
        _neural.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
    }

    private void RegisterUserResolution()
    {
        var json = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [ { "id": {{SeerrUserId}}, "displayName": "alice", "jellyfinUserId": "{{JellyfinUserIdHex}}" } ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, json);
    }

    private void RegisterRequestPage(params (int TmdbId, string MediaType)[] items)
    {
        var rows = string.Join(",\n", Array.ConvertAll(items, i =>
            $$"""{ "id": {{i.TmdbId}}, "status": 2, "media": { "tmdbId": {{i.TmdbId}}, "mediaType": "{{i.MediaType}}", "status": 3 } }"""));
        var body = $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": {{items.Length}}, "page": 1 },
          "results": [ {{rows}} ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, $"requestedBy={SeerrUserId}", HttpStatusCode.OK, body);
    }

    private void SeedCache(int tmdbId, string mediaType)
    {
        var result = new DiscoveryResult
        {
            UserId = JellyfinUserId,
            UserName = "alice",
            GeneratedAt = DateTime.UtcNow
        };
        result.Recommendations.Add(new DiscoveryRecommendation { TmdbId = tmdbId, MediaType = mediaType, Title = $"item-{tmdbId}" });
        _cache.Save(new List<DiscoveryResult> { result });
    }

    [Fact]
    public async Task Reconcile_WhenGetRequestedItemsThrows_TreatsNothingAsAlreadyRecordedAndStillReconciles()
    {
        RegisterUserResolution();
        SeedCache(100, "movie");
        RegisterRequestPage((100, "movie"));
        // The already-recorded lookup fails; reconciliation must fall back to an empty set and proceed.
        _feedbackStore.Setup(f => f.GetRequestedItems(JellyfinUserId))
            .Throws(new InvalidOperationException("feedback read failed"));

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(1, count);
        _feedbackStore.Verify(f => f.RecordRequested(JellyfinUserId, 100, "movie"), Times.Once);
    }

    [Fact]
    public async Task Reconcile_WhenRecordRequestedThrows_SkipsItemAndLeavesCacheUnstamped()
    {
        RegisterUserResolution();
        SeedCache(100, "movie");
        RegisterRequestPage((100, "movie"));
        // The durable feedback signal is written first and throws, so the cache mark never runs.
        // The item is not counted and the cache stays unstamped, so the next reconciliation retries.
        _feedbackStore.Setup(f => f.RecordRequested(JellyfinUserId, 100, "movie"))
            .Throws(new InvalidOperationException("feedback write failed"));

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.False(_cache.Load().First(r => r.UserId == JellyfinUserId).Recommendations.Single().AlreadyRequested);
    }

    [Fact]
    public async Task Reconcile_WhenUrlBecomesInvalidAfterRosterCached_ReturnsZeroAndTouchesNothing()
    {
        RegisterUserResolution();
        SeedCache(100, "movie");
        RegisterRequestPage((100, "movie"));

        // Prime the 5-minute Seerr user roster cache with the valid URL.
        var primed = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);
        Assert.Equal(1, primed);
        _feedbackStore.Invocations.Clear();

        // Now corrupt the URL. Resolution still returns the cached user, but the request fetch
        // re-validates the config and hits the invalid-configuration catch.
        Plugin.Instance!.Configuration.SeerrUrl = "not a valid url";

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        _feedbackStore.Verify(f => f.RecordRequested(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }
}
