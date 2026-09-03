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
        _handler.Dispose();
        _ensemble.Dispose();
        ControllerTestFactory.ResetPluginConfiguration();
    }

    private SeerrDiscoveryService CreateSut(DiscoveryCacheService cache)
    {
        var libraryManager = TestMockFactory.CreateLibraryManager();
        libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        return new(
            _httpFactory.Object,
            _history.Object,
            _arr.Object,
            libraryManager.Object,
            _ensemble,
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

            var svc = new SeerrDiscoveryService(
                factory.Object, _history.Object, _arr.Object, libraryManager.Object, _ensemble, cache,
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
