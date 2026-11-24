using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
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
///     Tests GenerateDiscoveryRecommendationsAsync.
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryGenerationTests : IDisposable
{
    private readonly Mock<IWatchHistoryService> _history;
    private readonly DiscoveryCacheService _cache;
    private readonly Mock<IDiscoveryFeedbackStore> _feedback;
    private readonly ScriptedHttpHandler _handler;
    private readonly PerUserEnsembleRegistry _perUserRegistry;
    private readonly SeerrDiscoveryService _sut;

    public SeerrDiscoveryGenerationTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();

        _handler = new ScriptedHttpHandler();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_handler, disposeHandler: false));

        _history = new Mock<IWatchHistoryService>();
        _history.Setup(history => history.GetAllUserIds()).Returns(() => _history.Object.GetAllUserWatchProfiles().Select(profile => profile.UserId).ToArray());
        _history.Setup(h => h.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());
        _history.Setup(h => h.GetSeriesEpisodeCounts())
            .Returns(new Dictionary<Guid, int>());

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
        _cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object);
        _feedback = new Mock<IDiscoveryFeedbackStore>();

        var perUserRegistry = new PerUserEnsembleRegistry(
            ensemble,
            null,
            null,
            new EnsembleBlendBounds(
                EnsembleScoringStrategy.DefaultAlphaMin,
                EnsembleScoringStrategy.DefaultAlphaMax,
                EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
            pluginLog.Object);
        _perUserRegistry = perUserRegistry;

        _sut = new SeerrDiscoveryService(
            httpFactory.Object,
            _history.Object,
            arr.Object,
            libraryManager.Object,
            perUserRegistry,
            _cache,
            _feedback.Object,
            pluginLog.Object,
            TestFixtures.TestMockFactory.CreateSecretProtector(),
            TestFixtures.TestMockFactory.CreateVirtualItemDetector(),
            new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    public void Dispose()
    {
        _perUserRegistry.Dispose();
        _handler.Dispose();
        _cache.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_ConfigMissing_ReturnsWithoutError()
    {
        // BUG GUARD: empty SeerrUrl/ApiKey must short-circuit BEFORE fetching profiles. A regression that fetched anyway would spike the DB on every scheduled tick for admins who never configured Seerr.
        Plugin.Instance!.Configuration.SeerrUrl = string.Empty;
        Plugin.Instance!.Configuration.SeerrApiKey = string.Empty;

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _history.Verify(h => h.GetAllUserWatchProfiles(), Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_TaskModeDeactivate_ShortCircuits()
    {
        // BUG GUARD: TaskMode.Deactivate MUST short-circuit BEFORE any HTTP or DB work, even with a fully-configured Seerr.
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Deactivate;

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _history.Verify(h => h.GetAllUserWatchProfiles(), Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_EnabledUserWithEmptyProviderResults_SkipsFeedbackRecording()
    {
        // An enabled user without taste may use cold-start retrieval, but an
        // empty provider pool must not create generation feedback.
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Activate;

        var quietUser = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            UserName = "quiet",
            WatchedMovieCount = 0,
            WatchedEpisodeCount = 0,
            FavoriteCount = 0,
            WatchedItems = []
        };
        _history.Setup(h => h.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { quietUser });
        _history.Setup(history => history.GetAllUserIds()).Returns([quietUser.UserId]);

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _history.Verify(h => h.GetAllUserWatchProfiles(), Times.Once);
        _feedback.Verify(
            f => f.RecordGeneratedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_CancelledToken_Throws()
    {
        // Cancellation must escape the per-user try/catch - a swallowed cancellation
        // would leave a task registered as still-running in Jellyfin's scheduler.
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Activate;

        _history.Setup(h => h.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>
            {
                new()
                {
                    UserId = Guid.NewGuid(),
                    UserName = "active",
                    WatchedMovieCount = 5,
                    FavoriteCount = 5,
                    WatchedItems =
                    [
                        new()
                        {
                            ItemId = Guid.NewGuid(),
                            Name = "seed",
                            ItemType = "Movie",
                            Played = true,
                            PlayCount = 3,
                            Genres = ["Action"]
                        }
                    ]
                }
            });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The outer cancellation check at the top of the per-user loop (or during `BuildExclusionSetAsync`) MUST fire before any real HTTP work happens.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _sut.GenerateDiscoveryRecommendationsAsync(cts.Token));
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_DryRunMode_DoesNotCallFeedbackRecord()
    {
        // BUG GUARD: DryRun mode must NEVER persist to disk or record shown feedback.
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.DryRun;

        var user = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = user, UserName = "active", WatchedMovieCount = 3,
            WatchedItems = new Collection<WatchedItemInfo>(Enumerable.Range(0, 3).Select(_ => new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Action"]
            }).ToList())
        };
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(new Collection<UserWatchProfile> { profile });
        _handler.RegisterResponse(HttpMethod.Get, "/genre/28?page=1", HttpStatusCode.OK,
            """{"results":[{"id":801,"title":"Unseen action","voteAverage":8.5,"popularity":50,"genreIds":[28],"releaseDate":"2023-01-01"}]}""");
        _handler.RegisterResponse(HttpMethod.Get, "/movie/801", HttpStatusCode.OK, """{"id":801,"genres":[{"id":28}]}""");
        _handler.RegisterResponse(HttpMethod.Get, "/tv/801", HttpStatusCode.OK, """{"id":801,"genres":[{"id":28}]}""");
        var prior = new DiscoveryResult { UserId = user, GeneratedAt = DateTime.UtcNow.AddDays(-1), Recommendations = [new DiscoveryRecommendation { TmdbId = 77, Title = "prior pool" }] };
        Assert.True(_cache.Save([prior]));
        var before = System.Text.Json.JsonSerializer.Serialize(_cache.Load());
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user?take=50&skip=0&sort=displayname", HttpStatusCode.OK,
            $$"""{"pageInfo":{"pages":1,"results":1},"results":[{"id":17,"jellyfinUserId":"{{user:N}}"}]}""");
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/request?take=50&skip=0&sort=added&filter=all&requestedBy=17", HttpStatusCode.OK,
            """{"pageInfo":{"results":1},"results":[{"media":{"tmdbId":77,"mediaType":"movie"}}]}""");
        _feedback.Setup(store => store.RecordRequestedAsync(user, 77, "movie", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(_cache.Load()));
        _feedback.Verify(store => store.RecordRequestedAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        _feedback.Verify(
            f => f.RecordGeneratedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Prove this same fixture reaches a persistable pool, rather than an early empty-user return.
        Plugin.Instance.Configuration.RecommendationsTaskMode = TaskMode.Activate;
        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        Assert.Contains(Assert.Single(_cache.Load()).Recommendations, item => item.TmdbId == 801);
        _feedback.Verify(store => store.RecordRequestedAsync(user, 77, "movie", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmappedBroadGenresStillRetrieveABoundedSupportedAnimeLaneAfterGenericFallback(bool genreEvidence)
    {
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance.Configuration.SeerrApiKey = "test-key";
        Plugin.Instance.Configuration.RecommendationsTaskMode = TaskMode.Activate;
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = new Collection<WatchedItemInfo>(Enumerable.Range(90, 2).Select(id => new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(), ItemType = "Series", TmdbId = id, Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(90 - id),
            Genres = genreEvidence ? ["unmapped-provider-a", "unmapped-provider-b", "unmapped-provider-c", "Animation"] : ["unmapped-provider-a", "unmapped-provider-b", "unmapped-provider-c"], TitleMetadata = new Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.TitleMetadataSnapshot { TmdbId = id, MediaType = "tv", OriginalLanguage = "ja", AnimeHint = true, FetchedAtUtc = DateTime.UtcNow }
        }).ToList()) };
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Series", TmdbId = 92, Played = true, Genres = ["unmapped-provider-a", "unmapped-provider-b", "unmapped-provider-c"] });
        // Dominant retrieval genres are still unmapped. The real minority
        // Animation evidence qualifies scoring; retrieval never grants a score boost.
        var dominantGenres = PreferenceBuilder.BuildGenrePreferenceVector(profile).OrderByDescending(pair => pair.Value).Take(3).Select(pair => pair.Key).ToArray();
        Assert.Equal(3, dominantGenres.Length);
        Assert.All(dominantGenres, genre => { Assert.Null(TmdbGenreMap.ToMovieTmdbId(genre)); Assert.Null(TmdbGenreMap.ToTvTmdbId(genre)); });
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        var seeds = PersonalDiscoveryRetrieval.Seeds(profile);
        Assert.True(seeds.ContainsKey("tv:anime"));
        foreach (var route in new[] { "/api/v1/discover/movies?page=1", "/api/v1/discover/tv?page=1", "/api/v1/discover/trending?mediaType=all&timeWindow=week&page=1" })
        {
            _handler.RegisterResponse(HttpMethod.Get, route, HttpStatusCode.OK, """{"results":[]}""");
        }
        _handler.RegisterResponse(HttpMethod.Get, "/tv/90/recommendations?page=1", HttpStatusCode.OK, """{"results":[{"id":801,"name":"Lane candidate","voteAverage":8.5,"popularity":50,"genreIds":[16]}]}""");
        _handler.RegisterResponse(HttpMethod.Get, "/tv/801", HttpStatusCode.OK, """{"id":801,"genres":[{"id":16}],"originalLanguage":"ja"}""");
        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        Assert.Single(_handler.Requests, request => request.Path.EndsWith("/tv/90/recommendations?page=1", StringComparison.Ordinal));
        Assert.InRange(_handler.Requests.Count(request => request.Path.Contains("/recommendations?", StringComparison.Ordinal)), 1, 2);
        Assert.Contains(_handler.Requests, request => request.Path.Contains("/discover/trending?", StringComparison.Ordinal));
        var saved = Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken)).Recommendations;
        if (genreEvidence)
        {
            Assert.Contains(saved, item => item.TmdbId == 801 && item.MediaType == "tv");
        }
        else
        {
            Assert.DoesNotContain(saved, item => item.TmdbId == 801);
        }
    }
}
