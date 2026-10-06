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

    private SeerrDiscoveryService CreateService()
    {
        var factory = new Mock<System.Net.Http.IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new System.Net.Http.HttpClient());
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
        var cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, filePath: Path.GetTempFileName());
        _owned.Add(ensemble);
        _owned.Add(neural);
        _owned.Add(cache);
        var feedbackStore = new Mock<IDiscoveryFeedbackStore>();
        feedbackStore.Setup(f => f.GetDismissedItems(It.IsAny<Guid>())).Returns(new HashSet<(int, string)>());
        feedbackStore.Setup(f => f.GetRequestedItems(It.IsAny<Guid>())).Returns(new HashSet<(int, string)>());
        var perUserRegistry = new PerUserEnsembleRegistry(
            ensemble, null, null,
            new EnsembleBlendBounds(
                EnsembleScoringStrategy.DefaultAlphaMin,
                EnsembleScoringStrategy.DefaultAlphaMax,
                EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
            pluginLog.Object);
        _registries.Add(perUserRegistry);
        return new SeerrDiscoveryService(
            factory.Object, _history.Object, arr.Object, libraryManager.Object,
            perUserRegistry, cache, feedbackStore.Object, pluginLog.Object,
            TestMockFactory.CreateSecretProtector(), new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    private void SetupProfile(Guid userId)
    {
        // A profile with genre history (so BuildGenrePreferenceVector is non-empty) and no people prefs
        // (so the credits-enrichment HTTP phase is skipped) and no parental cap (so the filter passes).
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "tester",
            MaxParentalRating = null,
            GenreDistribution = new Dictionary<string, int> { ["Action"] = 10, ["Drama"] = 5 },
        };
        _history.Setup(h => h.GetUserWatchProfile(userId)).Returns(profile);
        _history.Setup(h => h.GetSeriesEpisodeCounts()).Returns(new Dictionary<Guid, int>());
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
}
