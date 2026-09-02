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
///     Tests SeerrDiscoveryService.ReconcileRequestedItemsAsync end to end against a real feedback store and cache, driving the Seerr user-resolution and requestedBy-scoped request endpoints through a scripted HttpMessageHandler.
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryReconcileTests : IDisposable
{
    private const int SeerrUserId = 42;
    private static readonly Guid JellyfinUserId = new("11111111-2222-3333-4444-555555555555");
    private const string JellyfinUserIdHex = "11111111222233334444555555555555";

    private readonly ScriptedHttpHandler _handler;
    private readonly SeerrDiscoveryService _sut;
    private readonly DiscoveryCacheService _cache;
    private readonly DiscoveryFeedbackStore _feedbackStore;
    private readonly EnsembleScoringStrategy _ensemble;
    private readonly string _feedbackDir;

    public SeerrDiscoveryReconcileTests()
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

        _feedbackDir = Path.Join(Path.GetTempPath(), "reconcile-fb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_feedbackDir);
        _feedbackStore = new DiscoveryFeedbackStore(pluginLog.Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, _feedbackDir);

        var learned = new LearnedScoringStrategy(null, new Mock<ILogger<LearnedScoringStrategy>>().Object);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy(null, new Mock<ILogger<NeuralScoringStrategy>>().Object);
        _ensemble = new EnsembleScoringStrategy(
            learned, heuristic, neural, null,
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
            _feedbackStore,
            pluginLog.Object,
            new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    public void Dispose()
    {
        _handler.Dispose();
        _cache.Dispose();
        _ensemble.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
        try
        {
            Directory.Delete(_feedbackDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort test cleanup.
        }
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

    private void RegisterRequestPage(string body)
    {
        // The requestedBy segment is last in the query string, so registering it as the suffix
        // is sufficient for the scripted handler's EndsWith match.
        _handler.RegisterResponse(HttpMethod.Get, $"requestedBy={SeerrUserId}", HttpStatusCode.OK, body);
    }

    private void SeedCache(params DiscoveryRecommendation[] recommendations)
    {
        var result = new DiscoveryResult
        {
            UserId = JellyfinUserId,
            UserName = "alice",
            GeneratedAt = DateTime.UtcNow
        };
        foreach (var rec in recommendations)
        {
            result.Recommendations.Add(rec);
        }

        _cache.Save(new List<DiscoveryResult> { result });
    }

    private static DiscoveryRecommendation Rec(int tmdbId, string mediaType) =>
        new() { TmdbId = tmdbId, MediaType = mediaType, Title = $"item-{tmdbId}" };

    private static string RequestPageJson(params (int TmdbId, string MediaType)[] items)
    {
        var rows = string.Join(",\n", items.Select(i =>
            $$"""{ "id": {{i.TmdbId}}, "status": 2, "media": { "tmdbId": {{i.TmdbId}}, "mediaType": "{{i.MediaType}}", "status": 3 } }"""));
        return $$"""
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": {{items.Length}}, "page": 1 },
          "results": [ {{rows}} ]
        }
        """;
    }

    [Fact]
    public async Task Reconcile_RecordsAndMarksOnlyCachedItemsAlsoRequestedInSeerr()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "movie"), Rec(200, "tv"), Rec(300, "movie"));
        // Seerr reports 100 and 200 requested; 300 was never requested; 999 is requested but not in the pool.
        RegisterRequestPage(RequestPageJson((100, "movie"), (200, "tv"), (999, "movie")));

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(2, count);
        var requested = _feedbackStore.GetRequestedItems(JellyfinUserId);
        Assert.Contains((100, "movie"), requested);
        Assert.Contains((200, "tv"), requested);
        Assert.DoesNotContain((300, "movie"), requested);
        // 999 was never in the cached pool, so no phantom feedback entry is created.
        Assert.DoesNotContain((999, "movie"), requested);

        var pool = _cache.Load().First(r => r.UserId == JellyfinUserId).Recommendations;
        Assert.True(pool.Single(r => r.TmdbId == 100).AlreadyRequested);
        Assert.True(pool.Single(r => r.TmdbId == 200).AlreadyRequested);
        Assert.False(pool.Single(r => r.TmdbId == 300).AlreadyRequested);
    }

    [Fact]
    public async Task Reconcile_ItemRequestedButNotInPool_IsNoOp()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "movie"));
        RegisterRequestPage(RequestPageJson((555, "movie")));

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_feedbackStore.GetRequestedItems(JellyfinUserId));
    }

    [Fact]
    public async Task Reconcile_IsIdempotent_AlreadyRecordedItemsAreSkipped()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "movie"));
        RegisterRequestPage(RequestPageJson((100, "movie")));

        var first = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);
        var second = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task Reconcile_DistinguishesMovieAndTvWithSameTmdbId()
    {
        RegisterUserResolution();
        SeedCache(Rec(550, "movie"), Rec(550, "tv"));
        // Only the TV variant of 550 was requested.
        RegisterRequestPage(RequestPageJson((550, "tv")));

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(1, count);
        var requested = _feedbackStore.GetRequestedItems(JellyfinUserId);
        Assert.Contains((550, "tv"), requested);
        Assert.DoesNotContain((550, "movie"), requested);
    }

    [Fact]
    public async Task Reconcile_NormalizesMediaTypeCasingAndUnknownValues()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "  Movie "), Rec(200, "TV"));
        // Seerr returns mixed-case and an unknown type that must collapse to movie.
        RegisterRequestPage(RequestPageJson((100, "MOVIE"), (200, "Tv")));

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(2, count);
        var requested = _feedbackStore.GetRequestedItems(JellyfinUserId);
        Assert.Contains((100, "movie"), requested);
        Assert.Contains((200, "tv"), requested);
    }

    [Fact]
    public async Task Reconcile_EmptyGuid_ReturnsZero()
    {
        var count = await _sut.ReconcileRequestedItemsAsync(Guid.Empty, CancellationToken.None);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Reconcile_NotConfigured_ReturnsZero()
    {
        Plugin.Instance!.Configuration.SeerrUrl = string.Empty;
        SeedCache(Rec(100, "movie"));

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_feedbackStore.GetRequestedItems(JellyfinUserId));
    }

    [Fact]
    public async Task Reconcile_UserNotResolvable_ReturnsZeroAndTouchesNothing()
    {
        // Roster fetched but no matching Jellyfin user -> ResolveSeerrUserIdAsync returns null.
        var json = """
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 1, "page": 1 },
          "results": [ { "id": 7, "displayName": "stranger", "jellyfinUserId": "ffffffffffffffffffffffffffffffff" } ]
        }
        """;
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK, json);
        SeedCache(Rec(100, "movie"));

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_feedbackStore.GetRequestedItems(JellyfinUserId));
    }

    [Fact]
    public async Task Reconcile_RequestEndpointReturnsError_ReturnsZeroAndTouchesNothing()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "movie"));
        _handler.RegisterResponse(HttpMethod.Get, $"requestedBy={SeerrUserId}", HttpStatusCode.InternalServerError, "boom");

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_feedbackStore.GetRequestedItems(JellyfinUserId));
        Assert.False(_cache.Load().First(r => r.UserId == JellyfinUserId).Recommendations.Single().AlreadyRequested);
    }

    [Fact]
    public async Task Reconcile_RequestEndpointReturnsGarbageJson_ReturnsZero()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "movie"));
        _handler.RegisterResponse(HttpMethod.Get, $"requestedBy={SeerrUserId}", HttpStatusCode.OK, "{ not-json ");

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_feedbackStore.GetRequestedItems(JellyfinUserId));
    }

    [Fact]
    public async Task Reconcile_RequestTransportException_ReturnsZero()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "movie"));
        // The user roster resolves from the first call; the second call (request page) throws.
        _handler.ThrowAfter = new HttpRequestException("connection refused");
        _handler.ThrowAfterCallIndex = 1;

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_feedbackStore.GetRequestedItems(JellyfinUserId));
    }

    [Fact]
    public async Task Reconcile_EmptyRequestList_ReturnsZero()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "movie"));
        RegisterRequestPage(RequestPageJson());

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Reconcile_NoCachedResultsForUser_ReturnsZero()
    {
        RegisterUserResolution();
        RegisterRequestPage(RequestPageJson((100, "movie")));
        // No SeedCache call: the user has no cached discovery pool.

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Reconcile_PaginatesAcrossMultiplePages()
    {
        RegisterUserResolution();
        SeedCache(Rec(1, "movie"), Rec(2, "movie"));

        // Page 1 returns a full page (50 rows) so pagination continues; item 1 is on page 1.
        var page1Rows = string.Join(",\n", Enumerable.Range(1000, 50).Select(i =>
            $$"""{ "id": {{i}}, "status": 2, "media": { "tmdbId": {{(i == 1000 ? 1 : i)}}, "mediaType": "movie", "status": 3 } }"""));
        var page1 = $$"""
        { "pageInfo": { "pages": 2, "pageSize": 50, "results": 51, "page": 1 }, "results": [ {{page1Rows}} ] }
        """;
        var page2 = $$"""
        { "pageInfo": { "pages": 2, "pageSize": 50, "results": 51, "page": 2 }, "results": [ { "id": 2, "status": 2, "media": { "tmdbId": 2, "mediaType": "movie", "status": 3 } } ] }
        """;
        _handler.RegisterResponse(HttpMethod.Get, $"skip=0&sort=added&filter=all&requestedBy={SeerrUserId}", HttpStatusCode.OK, page1);
        _handler.RegisterResponse(HttpMethod.Get, $"skip=50&sort=added&filter=all&requestedBy={SeerrUserId}", HttpStatusCode.OK, page2);

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        // Item 1 (page 1) and item 2 (page 2) are both reconciled.
        Assert.Equal(2, count);
        var requested = _feedbackStore.GetRequestedItems(JellyfinUserId);
        Assert.Contains((1, "movie"), requested);
        Assert.Contains((2, "movie"), requested);
    }

    [Fact]
    public async Task Reconcile_SkipsRowsWithMissingOrInvalidMedia()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "movie"));
        // One row has null media, one has tmdbId 0, one is the valid target.
        var body = """
        {
          "pageInfo": { "pages": 1, "pageSize": 50, "results": 3, "page": 1 },
          "results": [
            { "id": 1, "status": 2, "media": null },
            { "id": 2, "status": 2, "media": { "tmdbId": 0, "mediaType": "movie", "status": 3 } },
            { "id": 3, "status": 2, "media": { "tmdbId": 100, "mediaType": "movie", "status": 3 } }
          ]
        }
        """;
        RegisterRequestPage(body);

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Contains((100, "movie"), _feedbackStore.GetRequestedItems(JellyfinUserId));
    }

    [Fact]
    public async Task Reconcile_CancelledToken_Throws()
    {
        RegisterUserResolution();
        SeedCache(Rec(100, "movie"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.ReconcileRequestedItemsAsync(JellyfinUserId, cts.Token));
    }

    [Fact]
    public async Task Reconcile_FullPageWithTotalBelowSkip_TreatsSnapshotAsIncompleteAndTouchesNothing()
    {
        // A full page (50 rows) whose reported total is smaller than the next skip is inconsistent
        // pagination metadata. The fetch must fail closed rather than act on the partial snapshot.
        RegisterUserResolution();
        SeedCache(Rec(1000, "movie"));
        var rows = string.Join(",\n", Enumerable.Range(1000, 50).Select(i =>
            $$"""{ "id": {{i}}, "status": 2, "media": { "tmdbId": {{i}}, "mediaType": "movie", "status": 3 } }"""));
        var page = $$"""
        { "pageInfo": { "pages": 1, "pageSize": 50, "results": 10, "page": 1 }, "results": [ {{rows}} ] }
        """;
        RegisterRequestPage(page);

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_feedbackStore.GetRequestedItems(JellyfinUserId));
        Assert.False(_cache.Load().First(r => r.UserId == JellyfinUserId).Recommendations.Single().AlreadyRequested);
    }

    [Fact]
    public async Task Reconcile_FullPageWithMissingTotal_KeepsPagingUntilShortPage()
    {
        // A full page reporting a zero/absent total must not be trusted as complete. Reconciliation
        // keeps paging until a short page and reconciles items found across both pages.
        RegisterUserResolution();
        SeedCache(Rec(1, "movie"), Rec(2, "movie"));

        var page1Rows = string.Join(",\n", Enumerable.Range(1000, 50).Select(i =>
            $$"""{ "id": {{i}}, "status": 2, "media": { "tmdbId": {{(i == 1000 ? 1 : i)}}, "mediaType": "movie", "status": 3 } }"""));
        var page1 = $$"""
        { "pageInfo": { "pages": 1, "pageSize": 50, "results": 0, "page": 1 }, "results": [ {{page1Rows}} ] }
        """;
        var page2 = $$"""
        { "pageInfo": { "pages": 1, "pageSize": 50, "results": 0, "page": 2 }, "results": [ { "id": 2, "status": 2, "media": { "tmdbId": 2, "mediaType": "movie", "status": 3 } } ] }
        """;
        _handler.RegisterResponse(HttpMethod.Get, $"skip=0&sort=added&filter=all&requestedBy={SeerrUserId}", HttpStatusCode.OK, page1);
        _handler.RegisterResponse(HttpMethod.Get, $"skip=50&sort=added&filter=all&requestedBy={SeerrUserId}", HttpStatusCode.OK, page2);

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(2, count);
        var requested = _feedbackStore.GetRequestedItems(JellyfinUserId);
        Assert.Contains((1, "movie"), requested);
        Assert.Contains((2, "movie"), requested);
    }

    [Fact]
    public async Task Reconcile_HitsPageCapWithoutExhaustingResults_ReturnsZeroAndTouchesNothing()
    {
        // Every page is full and the reported total always exceeds the next skip, so the scan never
        // completes and hits the 20-page cap. That is a partial snapshot and must reconcile nothing.
        RegisterUserResolution();
        SeedCache(Rec(1, "movie"));
        for (var pageIndex = 0; pageIndex < 20; pageIndex++)
        {
            var skip = pageIndex * 50;
            var baseId = 1000 + skip;
            var rows = string.Join(",\n", Enumerable.Range(baseId, 50).Select(i =>
                $$"""{ "id": {{i}}, "status": 2, "media": { "tmdbId": {{(i == 1000 ? 1 : i)}}, "mediaType": "movie", "status": 3 } }"""));
            var body = $$"""
            { "pageInfo": { "pages": 999, "pageSize": 50, "results": 100000, "page": {{pageIndex + 1}} }, "results": [ {{rows}} ] }
            """;
            _handler.RegisterResponse(HttpMethod.Get, $"skip={skip}&sort=added&filter=all&requestedBy={SeerrUserId}", HttpStatusCode.OK, body);
        }

        var count = await _sut.ReconcileRequestedItemsAsync(JellyfinUserId, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_feedbackStore.GetRequestedItems(JellyfinUserId));
    }
}
