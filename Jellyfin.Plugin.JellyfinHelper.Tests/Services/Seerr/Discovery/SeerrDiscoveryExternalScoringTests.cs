using System;
using System.Collections.Generic;
using System.Linq;
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
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests the external-candidate scoring seam used by the Trakt source: config/profile guards, mapping of
///     public candidates, parental filtering, and reason-key stamping. Scoring internals are covered by the
///     existing discovery suite; here we assert the seam's observable contract.
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryExternalScoringTests : IDisposable
{
    private readonly List<PerUserEnsembleRegistry> _registries = [];
    private readonly List<IDisposable> _owned = [];
    private readonly Mock<IWatchHistoryService> _history = new();
    private readonly Mock<MediaBrowser.Controller.Library.ILibraryManager> _libraryManager =
        TestMockFactory.CreateLibraryManager();
    private readonly Dictionary<int, string> _detailById = new();

    public SeerrDiscoveryExternalScoringTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "seerr-key";
    }

    public void Dispose()
    {
        foreach (var registry in _registries)
        {
            registry.Dispose();
        }

        // The service under test never takes ownership: the ensemble (which owns its neural strategy)
        // and the cache created below are disposed here.
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        ControllerTestFactory.TeardownPluginInstance();
        GC.SuppressFinalize(this);
    }

    private SeerrDiscoveryService CreateService(Action<Mock<IDiscoveryFeedbackStore>>? configureFeedback = null)
    {
        var factory = new Mock<System.Net.Http.IHttpClientFactory>();
        // Strict handler routing Seerr detail calls by TMDb id. Unregistered ids answer 404, which the
        // enrichment treats as "unenriched" - and no real network is ever touched.
        var handler = new Mock<System.Net.Http.HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<System.Net.Http.HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<System.Net.Http.HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((System.Net.Http.HttpRequestMessage request, CancellationToken _) =>
            {
                var segments = request.RequestUri?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    ?? [];
                var last = segments.Length > 0 ? segments[^1] : string.Empty;
                if (int.TryParse(last, out var id) && _detailById.TryGetValue(id, out var json))
                {
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent(json),
                    };
                }

                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new System.Net.Http.HttpClient(handler.Object));
        var arr = new Mock<IArrIntegrationService>();
        _libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        var learned = new LearnedScoringStrategy(null, new Mock<ILogger<LearnedScoringStrategy>>().Object);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy(null, new Mock<ILogger<NeuralScoringStrategy>>().Object);
        var ensemble = new EnsembleScoringStrategy(
            learned, heuristic, neural, null,
            EnsembleScoringStrategy.DefaultAlphaMin,
            EnsembleScoringStrategy.DefaultAlphaMax,
            EnsembleScoringStrategy.DefaultGenrePenaltyFloor,
            new Mock<ILogger<EnsembleScoringStrategy>>().Object,
            ownsNeural: false);
        var pluginLog = new Mock<IPluginLogService>();
        var cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, filePath: Path.GetTempFileName());
        _owned.Add(ensemble);
        // The ensemble does not own neural here (ownsNeural: false, like per-user ensembles in
        // production), so it is disposed explicitly alongside it - never implicitly twice.
        _owned.Add(neural);
        _owned.Add(cache);
        var feedbackStore = new Mock<IDiscoveryFeedbackStore>();
        feedbackStore.Setup(f => f.GetDismissedItems(It.IsAny<Guid>())).Returns(new HashSet<(int, string)>());
        feedbackStore.Setup(f => f.GetRequestedItems(It.IsAny<Guid>())).Returns(new HashSet<(int, string)>());
        configureFeedback?.Invoke(feedbackStore);
        var perUserRegistry = new PerUserEnsembleRegistry(
            ensemble, null, null,
            new EnsembleBlendBounds(
                EnsembleScoringStrategy.DefaultAlphaMin,
                EnsembleScoringStrategy.DefaultAlphaMax,
                EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
            pluginLog.Object);
        _registries.Add(perUserRegistry);
        return new SeerrDiscoveryService(
            factory.Object, _history.Object, arr.Object, _libraryManager.Object,
            perUserRegistry, cache, feedbackStore.Object, pluginLog.Object,
            TestMockFactory.CreateSecretProtector(), new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    private void SetupProfile(Guid userId, int? maxParentalRating = null)
    {
        // A profile with genre history (so BuildGenrePreferenceVector is non-empty) and no people prefs
        // (so the credits-enrichment HTTP phase is skipped) and no parental cap (so the filter passes).
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "tester",
            MaxParentalRating = maxParentalRating,
            GenreDistribution = new Dictionary<string, int> { ["Action"] = 10, ["Drama"] = 5 },
        };
        _history.Setup(h => h.GetUserWatchProfile(userId)).Returns(profile);
        _history.Setup(h => h.GetSeriesEpisodeCounts()).Returns(new Dictionary<Guid, int>());
    }

    private void ReturnsDetail(int tmdbId, string genresJson, string? posterPath = "/enriched.jpg", bool adult = false, int? mediaStatus = null)
    {
        var mediaInfo = mediaStatus.HasValue ? $"\"mediaInfo\":{{\"status\":{mediaStatus.Value}}}," : string.Empty;
        _detailById[tmdbId] =
            $"{{\"id\":{tmdbId},\"genres\":{genresJson},\"posterPath\":\"{posterPath}\",\"voteAverage\":7,\"popularity\":10,\"adult\":{(adult ? "true" : "false")},{mediaInfo}\"credits\":null}}";
    }

    private static ExternalDiscoveryCandidate Candidate(int tmdbId) => new()
    {
        TmdbId = tmdbId,
        MediaType = "movie",
        Title = "Movie " + tmdbId,
        Year = 2020,
        VoteAverage = 7.5,
        Popularity = 50,
        GenreIds = [28],
        PosterPath = "/p" + tmdbId + ".jpg",
    };

    [Fact]
    public async Task ReturnsNull_WhenCandidatesEmpty()
    {
        var result = await CreateService().ScoreExternalCandidatesAsync(Guid.NewGuid(), [], "reasonTrakt", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenSeerrNotConfigured()
    {
        Plugin.Instance!.Configuration.SeerrApiKey = string.Empty;
        var result = await CreateService().ScoreExternalCandidatesAsync(Guid.NewGuid(), [Candidate(1)], "reasonTrakt", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenUserHasNoProfile()
    {
        var userId = Guid.NewGuid();
        _history.Setup(h => h.GetUserWatchProfile(userId)).Returns((UserWatchProfile?)null);
        var result = await CreateService().ScoreExternalCandidatesAsync(userId, [Candidate(1)], "reasonTrakt", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ScoresAndStampsReason_ForScorableCandidates()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [Candidate(101), Candidate(102), Candidate(103)],
            "reasonTrakt",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.NotEmpty(result.Recommendations);
        Assert.All(result.Recommendations, r => Assert.Equal("reasonTrakt", r.ReasonKey));
    }

    [Fact]
    public async Task SurvivingCandidate_AppearsInResult()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [Candidate(101)],
            "reasonTrakt",
            CancellationToken.None);

        // With the default empty feedback store the single scorable candidate survives filtering.
        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.TmdbId == 101);
    }

    [Fact]
    public async Task EnrichedHorrorGenre_DroppedForTeenProfile()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId, maxParentalRating: 90);
        ReturnsDetail(101, "[{\"id\":27,\"name\":\"Horror\"}]");

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [Candidate(101)],
            "reasonTrakt",
            CancellationToken.None);

        // The enriched Horror genre hits the teen blacklist; nothing survives.
        Assert.Null(result);
    }

    [Fact]
    public async Task UnenrichedCandidate_DroppedForRestricted_KeptForUnrestricted()
    {
        // No detail fixture registered: the enrichment 404s for both users.
        var teenId = Guid.NewGuid();
        SetupProfile(teenId, maxParentalRating: 90);
        var teenResult = await CreateService().ScoreExternalCandidatesAsync(
            teenId,
            [Candidate(101)],
            "reasonTrakt",
            CancellationToken.None);

        // Fail closed: without enriched genres the blacklist cannot judge, so the item is dropped.
        Assert.Null(teenResult);

        var openId = Guid.NewGuid();
        SetupProfile(openId);
        var openResult = await CreateService().ScoreExternalCandidatesAsync(
            openId,
            [Candidate(101)],
            "reasonTrakt",
            CancellationToken.None);

        Assert.NotNull(openResult);
        Assert.Contains(openResult!.Recommendations, r => r.TmdbId == 101);
    }

    [Fact]
    public async Task Enrichment_FillsPosterKeepsTraktRatingAndAppliesAvailability()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        ReturnsDetail(101, "[{\"id\":28,\"name\":\"Action\"}]", posterPath: "/enriched.jpg", mediaStatus: null);

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [new ExternalDiscoveryCandidate
            {
                TmdbId = 101,
                MediaType = "movie",
                Title = "Movie 101",
                Year = 2020,
                VoteAverage = 8.5,
                PosterPath = null,
            }],
            "reasonTrakt",
            CancellationToken.None);

        Assert.NotNull(result);
        var rec = Assert.Single(result!.Recommendations);
        Assert.Equal("/enriched.jpg", rec.PosterPath);
        Assert.Equal(8.5, rec.TmdbRating);
    }

    [Fact]
    public async Task AvailableTitle_DroppedEvenWhenEnriched()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        ReturnsDetail(101, "[{\"id\":28,\"name\":\"Action\"}]", mediaStatus: 5);

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [Candidate(101)],
            "reasonTrakt",
            CancellationToken.None);

        // Seerr reports the title as available in the library: no duplicate request is offered.
        Assert.Null(result);
    }

    [Fact]
    public async Task AdultFlag_LatchedFromDetail_ExcludesForTeen()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId, maxParentalRating: 90);
        ReturnsDetail(101, "[{\"id\":28,\"name\":\"Action\"}]", adult: true);

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [Candidate(101)],
            "reasonTrakt",
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SecondScoring_ReusesCachedExclusionSet()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        var service = CreateService();

        await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], "reasonTrakt", CancellationToken.None);
        await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], "reasonTrakt", CancellationToken.None);

        // The library scan backing the shared exclusion set runs once; the second scoring reuses the cache.
        _libraryManager.Verify(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once);
    }

    private static DiscoveryRecommendation Rec(int tmdbId, string mediaType = "movie", bool requested = false) => new()
    {
        TmdbId = tmdbId,
        MediaType = mediaType,
        Title = "Title " + tmdbId,
        AlreadyRequested = requested,
    };

    [Fact]
    public void FilterConsumedItems_RemovesDismissedAndRequested()
    {
        var userId = Guid.NewGuid();
        var input = new DiscoveryResult
        {
            UserId = userId,
            Recommendations = [Rec(1), Rec(2), Rec(3)],
        };
        var service = CreateService(f =>
        {
            f.Setup(s => s.GetDismissedItems(userId)).Returns(new HashSet<(int, string)> { (1, "movie") });
            f.Setup(s => s.GetRequestedItems(userId)).Returns(new HashSet<(int, string)> { (2, "movie") });
        });

        var result = service.FilterConsumedItems(userId, input);

        var remaining = Assert.Single(result.Recommendations);
        Assert.Equal(3, remaining.TmdbId);
        // The input is never mutated: caches holding it stay intact.
        Assert.Equal(3, input.Recommendations.Count);
    }

    [Fact]
    public void FilterConsumedItems_ExcludesAlreadyRequestedAndNormalizesType()
    {
        var userId = Guid.NewGuid();
        var input = new DiscoveryResult
        {
            UserId = userId,
            Recommendations = [Rec(1, requested: true), Rec(2, " Movie ")],
        };
        var service = CreateService(f =>
        {
            f.Setup(s => s.GetDismissedItems(userId)).Returns(new HashSet<(int, string)> { (2, "movie") });
            f.Setup(s => s.GetRequestedItems(It.IsAny<Guid>())).Returns(new HashSet<(int, string)>());
        });

        var result = service.FilterConsumedItems(userId, input);

        Assert.Empty(result.Recommendations);
    }

    [Fact]
    public void FilterConsumedItems_ServesUnfiltered_WhenStoreFails()
    {
        var userId = Guid.NewGuid();
        var input = new DiscoveryResult
        {
            UserId = userId,
            Recommendations = [Rec(1), Rec(2)],
        };
        var service = CreateService(f =>
        {
            f.Setup(s => s.GetDismissedItems(It.IsAny<Guid>())).Throws(new InvalidOperationException("store down"));
        });

        var result = service.FilterConsumedItems(userId, input);

        Assert.Equal(2, result.Recommendations.Count);
    }
}
