using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
///     Exercises the best-effort persistence and feedback-resilience branches of the discovery generation task, plus the per-server detail-exception branch of the service-info fetch.
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryServicePersistenceFailureTests : IDisposable
{
    private readonly ScriptedHttpHandler _handler;
    private readonly Mock<IWatchHistoryService> _history;
    private readonly Mock<IArrIntegrationService> _arr;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStore;
    private readonly Mock<IHttpClientFactory> _httpFactory;
    private readonly EnsembleScoringStrategy _ensemble;
    private readonly Mock<IPluginLogService> _pluginLog;
    private readonly List<PerUserEnsembleRegistry> _registries = [];

    public SeerrDiscoveryServicePersistenceFailureTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-api-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Activate;

        _handler = new ScriptedHttpHandler();

        _httpFactory = new Mock<IHttpClientFactory>();
        _httpFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_handler, disposeHandler: false));

        _history = new Mock<IWatchHistoryService>();
        _history.Setup(h => h.GetSeriesEpisodeCounts()).Returns(new Dictionary<Guid, int>());

        _arr = new Mock<IArrIntegrationService>();

        _feedbackStore = new Mock<IDiscoveryFeedbackStore>();
        _feedbackStore.Setup(f => f.GetDismissedItems(It.IsAny<Guid>()))
            .Returns(new HashSet<(int, string)>());
        _feedbackStore.Setup(f => f.GetRequestedItems(It.IsAny<Guid>()))
            .Returns(new HashSet<(int, string)>());

        var learned = new LearnedScoringStrategy(null, new Mock<ILogger<LearnedScoringStrategy>>().Object);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy(null, new Mock<ILogger<NeuralScoringStrategy>>().Object);
        _ensemble = new EnsembleScoringStrategy(
            learned, heuristic, neural, null,
            EnsembleScoringStrategy.DefaultAlphaMin,
            EnsembleScoringStrategy.DefaultAlphaMax,
            EnsembleScoringStrategy.DefaultGenrePenaltyFloor,
            new Mock<ILogger<EnsembleScoringStrategy>>().Object);

        _pluginLog = new Mock<IPluginLogService>();
    }

    public void Dispose()
    {
        foreach (var registry in _registries)
        {
            registry.Dispose();
        }

        _handler.Dispose();
        _ensemble.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
    }

    private SeerrDiscoveryService CreateSut(DiscoveryCacheService cache)
    {
        var libraryManager = TestMockFactory.CreateLibraryManager();
        libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        var perUserRegistry = new PerUserEnsembleRegistry(
            _ensemble,
            null,
            null,
            new EnsembleBlendBounds(
                EnsembleScoringStrategy.DefaultAlphaMin,
                EnsembleScoringStrategy.DefaultAlphaMax,
                EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
            _pluginLog.Object);
        _registries.Add(perUserRegistry);
        return new(
            _httpFactory.Object,
            _history.Object,
            _arr.Object,
            libraryManager.Object,
            perUserRegistry,
            cache,
            _feedbackStore.Object,
            _pluginLog.Object,
            new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    private DiscoveryCacheService NewFileCache(out string filePath)
    {
        filePath = Path.Join(Path.GetTempPath(), "JellyfinHelperDiscoveryPersist_" + Guid.NewGuid() + ".json");
        return new DiscoveryCacheService(
            _pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, filePath);
    }

    private static Collection<UserWatchProfile> Profiles(params UserWatchProfile[] profiles)
    {
        var collection = new Collection<UserWatchProfile>();
        foreach (var p in profiles)
        {
            collection.Add(p);
        }

        return collection;
    }

    private static UserWatchProfile NewProfile()
    {
        return new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            UserName = "user-" + Guid.NewGuid().ToString("N")[..6],
            WatchedMovieCount = 5,
            GenreDistribution = new Dictionary<string, int> { ["Action"] = 10, ["Comedy"] = 5, ["Drama"] = 3 }
        };
    }

    private void RegisterMovieGenre(int genreId, params (int Id, double Vote)[] items)
    {
        var results = string.Join(",", items.Select(i =>
            $$"""
            { "id": {{i.Id}}, "title": "Title{{i.Id}}", "voteAverage": {{i.Vote.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "popularity": 50, "genreIds": [28], "releaseDate": "2023-01-01" }
            """));
        var json = $$"""{ "results": [ {{results}} ] }""";
        _handler.RegisterResponse(HttpMethod.Get, $"/genre/{genreId}?page=1", HttpStatusCode.OK, json);
    }

    [Fact]
    public async Task GenerateDiscovery_PerUserGenerationThrowsUnexpectedException_LogsAndContinuesOtherUsers()
    {
        // The first user's first discover query throws an exception NOT in ExecuteDiscoverQuery's catch filter (InvalidOperationException).
        var cache = NewFileCache(out var filePath);
        try
        {
            using var cacheGuard = cache;
            var first = NewProfile();
            var second = NewProfile();
            _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(first, second));

            // Out-of-band reconciliation resolves the Seerr user first; return an empty roster so
            // reconciliation is a clean no-op and does not consume the one-shot exception below.
            _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK,
                """{ "pageInfo": { "pages": 1, "pageSize": 50, "results": 0, "page": 1 }, "results": [] }""");

            // Fire on the first user's first genre query (call index 1: the user fetch is index 0),
            // an exception NOT in ExecuteDiscoverQuery's catch filter (InvalidOperationException).
            _handler.ThrowAfter = new InvalidOperationException("unexpected");
            _handler.ThrowAfterCallIndex = 1;
            RegisterMovieGenre(28, (2601, 8.0));

            var recorded = new List<Guid>();
            _feedbackStore
                .Setup(f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
                .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((id, _, _) => recorded.Add(id));

            await CreateSut(cache).GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

            // The second user's generation survived the first user's unexpected failure.
            Assert.Contains(second.UserId, recorded);
            Assert.DoesNotContain(first.UserId, recorded);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task GenerateDiscovery_CacheSaveFails_SkipsFeedbackRecording()
    {
        // Point the cache at a path that is actually an existing directory so Save() catches the IO/UnauthorizedAccess/Argument exception and returns false.
        var dirPath = Path.Join(Path.GetTempPath(), "JellyfinHelperDiscoveryPersistDir_" + Guid.NewGuid());
        Directory.CreateDirectory(dirPath);
        var cache = new DiscoveryCacheService(
            _pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, dirPath);
        try
        {
            using var cacheGuard = cache;
            var profile = NewProfile();
            _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
            RegisterMovieGenre(28, (2701, 8.0));

            await CreateSut(cache).GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

            _feedbackStore.Verify(
                f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(dirPath, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateDiscovery_RecordShownThrows_DoesNotFailTheRun()
    {
        // Save succeeds, but the feedback store's RecordShown throws a non-fatal exception.
        // The best-effort feedback catch must swallow it so the whole task still completes.
        var cache = NewFileCache(out var filePath);
        try
        {
            using var cacheGuard = cache;
            var profile = NewProfile();
            _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
            RegisterMovieGenre(28, (2801, 8.0));

            _feedbackStore
                .Setup(f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
                .Throws(new InvalidOperationException("feedback write failed"));

            // Must not throw despite the failing RecordShown.
            await CreateSut(cache).GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

            Assert.True(File.Exists(filePath));
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task GetServiceInfoAsync_DetailRequestThrows_KeepsServerWithoutProfiles()
    {
        // The service list is served normally but the per-server detail request throws a transport exception. The detail-loop catch logs and keeps the server (without enriched profiles) - distinct from the existing 500-status detail test.
        var cache = NewFileCache(out var filePath);
        try
        {
            using var cacheGuard = cache;
            const string listJson = """[ { "id": 9, "name": "Radarr-Flaky", "isDefault": true, "is4k": false } ]""";
            var throwingHandler = new DetailThrowingHttpHandler(
                listPathSuffix: "/api/v1/service/radarr",
                listBody: listJson,
                detailPathSuffix: "/api/v1/service/radarr/9",
                detailException: new HttpRequestException("detail fetch failed"));

            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(throwingHandler, disposeHandler: false));

            var libraryManager = TestMockFactory.CreateLibraryManager();
            libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);

            var perUserRegistry = new PerUserEnsembleRegistry(
                _ensemble,
                null,
                null,
                new EnsembleBlendBounds(
                    EnsembleScoringStrategy.DefaultAlphaMin,
                    EnsembleScoringStrategy.DefaultAlphaMax,
                    EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
                _pluginLog.Object);
            _registries.Add(perUserRegistry);

            var svc = new SeerrDiscoveryService(
                factory.Object, _history.Object, _arr.Object, libraryManager.Object, perUserRegistry, cache,
                _feedbackStore.Object, _pluginLog.Object,
                new Mock<ILogger<SeerrDiscoveryService>>().Object);

            var services = await svc.GetServiceInfoAsync("radarr", CancellationToken.None);

            var server = Assert.Single(services);
            Assert.Equal(9, server.Id);
            Assert.Empty(server.Profiles);

            throwingHandler.Dispose();
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    // Seeds a prior discovery pool for a user directly into the on-disk cache so a subsequent run's
    // carry-forward behaviour can be observed.
    private static void SeedPriorPool(DiscoveryCacheService cache, Guid userId, int tmdbId)
    {
        var prior = new DiscoveryResult
        {
            UserId = userId,
            UserName = "seeded",
            Recommendations =
            [
                new DiscoveryRecommendation { TmdbId = tmdbId, MediaType = "movie", Title = "Prior", Score = 0.9 }
            ]
        };
        Assert.True(cache.Save([prior]));
    }

    private static HashSet<int> CachedTmdbIdsForUser(DiscoveryCacheService cache, Guid userId)
    {
        var pool = cache.Load().FirstOrDefault(r => r.UserId == userId);
        return pool == null ? [] : pool.Recommendations.Select(r => r.TmdbId).ToHashSet();
    }

    [Fact]
    public async Task GenerateDiscovery_TransientPerUserFailure_PreservesPreviousPool()
    {
        // A transient (non-fatal) exception during a user's generation must NOT wipe their last-known-good
        // pool: the full-overwrite Save would otherwise empty the user's sidebar until the next good run.
        var cache = NewFileCache(out var filePath);
        try
        {
            using var cacheGuard = cache;
            var profile = NewProfile();
            SeedPriorPool(cache, profile.UserId, tmdbId: 680);

            _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
            _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK,
                """{ "pageInfo": { "pages": 1, "pageSize": 50, "results": 0, "page": 1 }, "results": [] }""");

            // Fail this user's first genre query (index 0 is the reconcile user fetch above).
            _handler.ThrowAfter = new InvalidOperationException("transient");
            _handler.ThrowAfterCallIndex = 1;
            RegisterMovieGenre(28, (2601, 8.0));

            await CreateSut(cache).GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

            Assert.Contains(680, CachedTmdbIdsForUser(cache, profile.UserId));
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task GenerateDiscovery_EmptyByDesign_ClearsPreviousPool()
    {
        // A legitimately empty generation (no candidates survive) is the correct case to clear the pool
        // carry-forward must NOT keep a stale pool alive when the fresh run genuinely produced nothing.
        var cache = NewFileCache(out var filePath);
        try
        {
            using var cacheGuard = cache;
            var profile = NewProfile();
            SeedPriorPool(cache, profile.UserId, tmdbId: 680);

            _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
            _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK,
                """{ "pageInfo": { "pages": 1, "pageSize": 50, "results": 0, "page": 1 }, "results": [] }""");

            // Every discover query returns an empty result set, so no viable candidate survives filtering
            // and GenerateForUserAsync returns null (EmptyByDesign). Unregistered paths default to empty.
            _handler.RegisterResponse(HttpMethod.Get, "/genre/28?page=1", HttpStatusCode.OK,
                """{ "results": [] }""");

            await CreateSut(cache).GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

            Assert.Empty(CachedTmdbIdsForUser(cache, profile.UserId));
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task GenerateDiscovery_FreshGeneration_OverwritesPreviousPool()
    {
        // A successful fresh generation replaces the prior pool: the old tmdbId must be gone and the new
        // candidate present.
        var cache = NewFileCache(out var filePath);
        try
        {
            using var cacheGuard = cache;
            var profile = NewProfile();
            SeedPriorPool(cache, profile.UserId, tmdbId: 680);

            _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
            RegisterMovieGenre(28, (2601, 8.0));

            await CreateSut(cache).GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

            var ids = CachedTmdbIdsForUser(cache, profile.UserId);
            Assert.Contains(2601, ids);
            Assert.DoesNotContain(680, ids);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task GenerateDiscovery_CarriedForwardPool_IsNotRecordedAsShownAgain()
    {
        // A carried-forward pool was already recorded as shown when first generated; re-recording it would
        // double-count training signal. The failing user must be excluded from feedback, a fresh user must not.
        var cache = NewFileCache(out var filePath);
        try
        {
            using var cacheGuard = cache;
            var failing = NewProfile();
            SeedPriorPool(cache, failing.UserId, tmdbId: 680);
            _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(failing));

            _handler.RegisterResponse(HttpMethod.Get, "/api/v1/user", HttpStatusCode.OK,
                """{ "pageInfo": { "pages": 1, "pageSize": 50, "results": 0, "page": 1 }, "results": [] }""");
            _handler.ThrowAfter = new InvalidOperationException("transient");
            _handler.ThrowAfterCallIndex = 1;
            RegisterMovieGenre(28, (2601, 8.0));

            var recorded = new List<Guid>();
            _feedbackStore
                .Setup(f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
                .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((id, _, _) => recorded.Add(id));

            await CreateSut(cache).GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

            // Pool preserved on disk, but NOT re-recorded as shown.
            Assert.Contains(680, CachedTmdbIdsForUser(cache, failing.UserId));
            Assert.DoesNotContain(failing.UserId, recorded);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task GenerateDiscovery_InvalidSeerrConfigForUser_PreservesPreviousPool()
    {
        // An invalid Seerr URL is non-blank (so it clears the global blank-config gate) but makes
        // ValidateSeerrConfig throw inside per-user generation. That is a "could not try", not a
        // legitimate empty result, so it must be treated as a transient failure and preserve the pool.
        var cache = NewFileCache(out var filePath);
        try
        {
            using var cacheGuard = cache;
            var profile = NewProfile();
            SeedPriorPool(cache, profile.UserId, tmdbId: 680);
            _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

            // Non-blank but unparseable as an absolute http(s) URL: passes IsNullOrWhiteSpace, fails
            // Uri.TryCreate in ValidateSeerrConfig -> UriFormatException -> transient failure.
            Plugin.Instance!.Configuration.SeerrUrl = "not-a-valid-url";

            await CreateSut(cache).GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

            Assert.Contains(680, CachedTmdbIdsForUser(cache, profile.UserId));
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}

/// <summary>
///     Serves a registered list response but throws a supplied exception when the per-server detail path is requested.
/// </summary>
internal sealed class DetailThrowingHttpHandler : HttpMessageHandler
{
    private readonly string _listPathSuffix;
    private readonly string _listBody;
    private readonly string _detailPathSuffix;
    private readonly Exception _detailException;

    public DetailThrowingHttpHandler(
        string listPathSuffix,
        string listBody,
        string detailPathSuffix,
        Exception detailException)
    {
        _listPathSuffix = listPathSuffix;
        _listBody = listBody;
        _detailPathSuffix = detailPathSuffix;
        _detailException = detailException;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        // Order matters: the detail suffix contains the list suffix, so check detail first.
        if (path.EndsWith(_detailPathSuffix, StringComparison.Ordinal))
        {
            throw _detailException;
        }

        if (path.EndsWith(_listPathSuffix, StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_listBody)
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No route for {path}")
        });
    }
}
