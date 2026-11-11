using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests the full GenerateDiscoveryRecommendationsAsync pipeline of SeerrDiscoveryService: the guard ladder (null config, not-configured, task-mode gates, no active profiles), the per-user generate->dedup->pre-score->enrich->score ->persist path, the child-account genre routing, language queries,.
/// </summary>
[Collection("ConfigOverride")]
public sealed class SeerrDiscoveryServiceGenerationTests : IDisposable
{
    // TMDb genre ids the child-account branch always queries (Family/Animation/Kids).
    private const int TmdbGenreFamily = 10751;
    private const int TmdbGenreAnimation = 16;
    private const int TmdbGenreTvKids = 10762;

    private readonly ScriptedHttpHandler _handler;
    private readonly Mock<IWatchHistoryService> _history;
    private readonly Mock<IArrIntegrationService> _arr;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStore;
    private readonly Mock<ILibraryManager> _libraryManager;
    private readonly Mock<ILinkedChildrenService> _linkedChildren = new();
    private readonly DiscoveryCacheService _cache;
    private readonly string _cacheFilePath;
    private readonly PerUserEnsembleRegistry _perUserRegistry;
    private readonly SeerrDiscoveryService _sut;

    public SeerrDiscoveryServiceGenerationTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-api-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Activate;

        _handler = new ScriptedHttpHandler();
        foreach (var path in new[] { "/api/v1/discover/movies?page=1", "/api/v1/discover/tv?page=1", "/api/v1/discover/trending?mediaType=all&timeWindow=week&page=1" })
        {
            _handler.RegisterResponse(HttpMethod.Get, path, HttpStatusCode.OK, """{"results":[]}""");
        }

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_handler, disposeHandler: false));

        _history = new Mock<IWatchHistoryService>();
        _history.Setup(h => h.GetSeriesEpisodeCounts())
            .Returns(new Dictionary<Guid, int>());

        _arr = new Mock<IArrIntegrationService>();

        _feedbackStore = new Mock<IDiscoveryFeedbackStore>();
        // GenerateForUser reads these unconditionally; Moq's default null would NRE at .Count.
        _feedbackStore.Setup(f => f.GetDismissedItems(It.IsAny<Guid>()))
            .Returns(new HashSet<(int, string)>());
        _feedbackStore.Setup(f => f.GetRequestedItems(It.IsAny<Guid>()))
            .Returns(new HashSet<(int, string)>());

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
        // Explicit temp file so Save() actually persists and RecordShown is reached.
        _cacheFilePath = Path.Join(Path.GetTempPath(), "JellyfinHelperDiscoveryGen_" + Guid.NewGuid() + ".json");
        _cache = new DiscoveryCacheService(
            pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, _cacheFilePath);

        var libraryManager = TestMockFactory.CreateLibraryManager();
        libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        _libraryManager = libraryManager;

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

        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetRatingScore("6", "DE")).Returns(new ParentalRatingScore(6, null));
        localization.Setup(l => l.GetRatingScore("18", "DE")).Returns(new ParentalRatingScore(18, null));

        _sut = new SeerrDiscoveryService(
            httpFactory.Object,
            _history.Object,
            _arr.Object,
            libraryManager.Object,
            perUserRegistry,
            _cache,
            _feedbackStore.Object,
            pluginLog.Object,
            TestFixtures.TestMockFactory.CreateSecretProtector(),
            TestFixtures.TestMockFactory.CreateVirtualItemDetector(),
            new Mock<ILogger<SeerrDiscoveryService>>().Object,
            localization.Object,
            linkedChildren: _linkedChildren.Object);
    }

    public void Dispose()
    {
        _perUserRegistry.Dispose();
        _handler.Dispose();
        _cache.Dispose();
        if (File.Exists(_cacheFilePath))
        {
            File.Delete(_cacheFilePath);
        }

        ControllerTestFactory.ResetPluginConfiguration();
    }

    // Guard ladder

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_PluginInstanceNull_LogsWarningAndReturns()
    {
        // With no plugin instance the null-config guard must short-circuit BEFORE loading
        // any profiles. Restore the instance afterwards so sibling tests still find it.
        ControllerTestFactory.TeardownPluginInstance();
        try
        {
            await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);
        }
        finally
        {
            ControllerTestFactory.InitializePluginInstance();
        }

        _history.Verify(h => h.GetAllUserWatchProfiles(), Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_SeerrNotConfigured_SkipsWithoutLoadingProfiles()
    {
        Plugin.Instance!.Configuration.SeerrUrl = string.Empty;
        Plugin.Instance!.Configuration.SeerrApiKey = string.Empty;

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _history.Verify(h => h.GetAllUserWatchProfiles(), Times.Never);
        _feedbackStore.Verify(
            f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_TaskModeDeactivate_Skips()
    {
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Deactivate;

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _history.Verify(h => h.GetAllUserWatchProfiles(), Times.Never);
        _feedbackStore.Verify(
            f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_NoActiveProfiles_Skips()
    {
        // The live provider returns no enabled accounts; cold-start users are not filtered out here.
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedbackStore.Verify(
            f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
        Assert.False(File.Exists(_cacheFilePath));
        _history.Verify(history => history.GetSeriesEpisodeCounts(), Times.Never);
    }

    // Full generate -> persist path

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_ActivateMode_PersistsResultsAndRecordsShown()
    {
        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(501, 8.0)]);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedbackStore.Verify(
            f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Once);
        Assert.NotNull(recorded);
        Assert.Contains(recorded!, r => r.TmdbId == 501 && r.MediaType == "movie");
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_DryRunMode_DoesNotPersistOrRecordShown()
    {
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.DryRun;
        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(501, 8.0)]);

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        // Dry-run must generate but never persist or feed the training store.
        _feedbackStore.Verify(
            f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
        Assert.False(File.Exists(_cacheFilePath));
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_ChildAccount_QueriesOnlyFamilyKidsAnimationGenres()
    {
        // Jellyfin's native score <= 6 selects the child branch. Only the fixed Family/Animation/Kids routes are
        // registered; the user's own top-genre routes are intentionally left unregistered.
        var profile = NewProfile();
        profile.MaxParentalRating = 6;
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        // Routing uses child genres; safety requires an actual verified certification as well.
        // A certified Family/Action movie matches this user's history; keep TV metadata on actual TV genre IDs.
        RegisterRoute($"/discover/movies/genre/{TmdbGenreFamily}?page=1", [TmdbGenreFamily, 28], [(9001, 6.0)]);
        RegisterRoute($"/genre/{TmdbGenreFamily}?page=1", [TmdbGenreFamily], [(9001, 6.0)]);
        RegisterRoute($"/genre/{TmdbGenreFamily}?page=2", [TmdbGenreFamily], []);
        RegisterRoute($"/genre/{TmdbGenreAnimation}?page=1", [TmdbGenreAnimation], []);
        RegisterRoute($"/genre/{TmdbGenreTvKids}?page=1", [TmdbGenreTvKids], [(9002, 6.0)]);
        RegisterRoute($"/genre/{TmdbGenreTvKids}?page=2", [TmdbGenreTvKids], []);
        // Family TV (page 1) shares the /genre/10751?page=1 suffix with Family movies above.
        _handler.RegisterResponse(HttpMethod.Get, "/movie/9001", HttpStatusCode.OK, """{"id":9001,"releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"6"}]}]}}""");
        _handler.RegisterResponse(HttpMethod.Get, "/tv/9001", HttpStatusCode.OK, """{"id":9001,"contentRatings":{"results":[{"iso_3166_1":"DE","rating":"6"}]}}""");
        _handler.RegisterResponse(HttpMethod.Get, "/tv/9002", HttpStatusCode.OK, """{"id":9002,"contentRatings":{"results":[{"iso_3166_1":"DE","rating":"6"}]}}""");

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.NotEmpty(recorded!);
        // Every persisted item came from a child route; the top-genre (28) route was never served.
        Assert.All(recorded!, r => Assert.True(r.TmdbId is 9001 or 9002));
        Assert.DoesNotContain(_handler.Requests, request => request.Path.Contains("/genre/28", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_UserWithLanguagePreference_IssuesLanguageQueries()
    {
        var profile = NewProfile();
        // ChosenCount >= 3 -> GetPrimaryLanguageForDiscovery returns "de".
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 5 };
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        RegisterGenreMovieResults(28, []);
        RegisterMovieRoute("/movies/language/de?page=1", [Candidate(7100, 8.2)]);
        RegisterMovieRoute("/tv/language/de?page=1", []);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Contains(recorded!, r => r.TmdbId == 7100);
    }

    [Fact]
    public async Task ColdStart_EmptyProviderResults_ProducesNoResultForThatUser()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "new-user" };
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedbackStore.Verify(
            f => f.RecordShown(profile.UserId, It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
    }

    [Fact]
    public async Task ColdStart_NewEnabledUserGetsPopularMoviesSeriesAndTrendingWithoutInventedHistory()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "new-user" };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        _history.Setup(history => history.GetUserParentalPolicy(profile.UserId)).Returns((true, (int?)null));
        RegisterMovieRoute("/api/v1/discover/movies?page=1", [Candidate(3001, 8.0)]);
        RegisterTvRoute("/api/v1/discover/tv?page=1", [Candidate(3001, 8.5)]);
        RegisterMovieRoute("/api/v1/discover/trending?mediaType=all&timeWindow=week&page=1", [Candidate(3002, 9.0)]);

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        var result = Assert.Single(await _cache.LoadAsync(CancellationToken.None));
        Assert.Equal(profile.UserId, result.UserId);
        Assert.Contains(result.Recommendations, item => item.TmdbId == 3001 && item.MediaType == "movie");
        Assert.Contains(result.Recommendations, item => item.TmdbId == 3001 && item.MediaType == "tv");
        Assert.Contains(result.Recommendations, item => item.TmdbId == 3002);
        Assert.All(result.Recommendations, item =>
        {
            var features = Assert.IsType<CandidateFeatures>(item.ScoringFeatures);
            Assert.Equal(0.0, features.GenreSimilarity);
            Assert.False(features.HasUserInteraction);
            Assert.Equal(EngineConstants.ComputeColdStartPrior(features.CombinedCriticScore, features.RecencyScore, features.PopularityScore), item.Score);
            Assert.DoesNotContain(item.ReasonKey, new[] { "reasonGenre", "reasonPersonNamed" });
        });
        Assert.NotEmpty(_sut.FilterConsumedItems(profile.UserId, result).Recommendations);
        Assert.Empty(profile.WatchedItems);
        Assert.Empty(profile.GenreDistribution);
        Assert.Equal(0, profile.FavoriteCount);
        Assert.False(_perUserRegistry.HasPerUserModel(profile.UserId));
        Assert.DoesNotContain(_handler.Requests, request => request.Path.Contains("/genre/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ColdStart_RestrictedNewUserStillRequiresVerifiedAllowedCertificatesAndOwnExclusions()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "restricted-new-user", MaxParentalRating = 12 };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterMovieRoute("/api/v1/discover/movies?page=1", [Candidate(3101, 8), Candidate(3102, 9), Candidate(3103, 9), Candidate(3104, 9), Candidate(3105, 9)]);
        _handler.RegisterResponse(HttpMethod.Get, "/movie/3101", HttpStatusCode.OK, """{"id":3101,"releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"6"}]}]}}""");
        _handler.RegisterResponse(HttpMethod.Get, "/movie/3102", HttpStatusCode.OK, """{"id":3102,"releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"18"}]}]}}""");
        _handler.RegisterResponse(HttpMethod.Get, "/movie/3103", HttpStatusCode.OK, """{"id":3103}""");
        _feedbackStore.Setup(store => store.GetDismissedItems(profile.UserId)).Returns(new HashSet<(int, string)> { (3104, "movie") });
        _feedbackStore.Setup(store => store.GetRequestedItems(profile.UserId)).Returns(new HashSet<(int, string)> { (3105, "movie") });

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.Equal(3101, Assert.Single(Assert.Single(await _cache.LoadAsync(CancellationToken.None)).Recommendations).TmdbId);
        Assert.Empty(profile.WatchedItems);
        Assert.False(_perUserRegistry.HasPerUserModel(profile.UserId));
    }

    [Fact]
    public async Task RestrictedMetadataEnrichment_DropsNewlyAvailableTitlesBeforeScoring()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "restricted-new-user", MaxParentalRating = 12 };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterMovieRoute("/api/v1/discover/movies?page=1", [Candidate(3150, 8)]);
        _handler.RegisterResponse(HttpMethod.Get, "/movie/3150", HttpStatusCode.OK, """{"id":3150,"mediaInfo":{"status":5},"releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"6"}]}]}}""");
        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);
        Assert.Empty(await _cache.LoadAsync(CancellationToken.None));
        _feedbackStore.Verify(store => store.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()), Times.Never);
    }

    [Fact]
    public async Task ColdStart_ChildUserKeepsChildRetrievalRoutesAndCertificateChecks()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "child-new-user", MaxParentalRating = 6 };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        foreach (var path in new[] { $"/api/v1/discover/movies/genre/{TmdbGenreFamily}?page=2", $"/api/v1/discover/movies/genre/{TmdbGenreAnimation}?page=1", $"/api/v1/discover/tv/genre/{TmdbGenreTvKids}?page=1", $"/api/v1/discover/tv/genre/{TmdbGenreTvKids}?page=2", $"/api/v1/discover/tv/genre/{TmdbGenreFamily}?page=1" })
        {
            _handler.RegisterResponse(HttpMethod.Get, path, HttpStatusCode.OK, """{"results":[]}""");
        }

        RegisterMovieRoute($"/api/v1/discover/movies/genre/{TmdbGenreFamily}?page=1", [Candidate(3201, 8), Candidate(3202, 8)]);
        _handler.RegisterResponse(HttpMethod.Get, "/movie/3201", HttpStatusCode.OK, """{"id":3201,"releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"6"}]}]}}""");
        _handler.RegisterResponse(HttpMethod.Get, "/movie/3202", HttpStatusCode.OK, """{"id":3202}""");

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.Equal(3201, Assert.Single(Assert.Single(await _cache.LoadAsync(CancellationToken.None)).Recommendations).TmdbId);
        Assert.DoesNotContain(_handler.Requests, request => request.Path.Contains("/discover/trending", StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.Requests, request => request.Path.EndsWith("/discover/movies?page=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ColdStart_RealPreferenceSwitchesBackToTheExistingPersonalizedPath()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "new-user" };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterMovieRoute("/api/v1/discover/movies?page=1", [Candidate(3301, 8)]);
        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);
        Assert.Equal(3301, Assert.Single(Assert.Single(await _cache.LoadAsync(CancellationToken.None)).Recommendations).TmdbId);
        profile.GenreDistribution["Action"] = 1;
        profile.FavoriteCount = 1;
        RegisterGenreMovieResults(28, [Candidate(3302, 8)]);

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.Equal(3302, Assert.Single(Assert.Single(await _cache.LoadAsync(CancellationToken.None)).Recommendations).TmdbId);
        Assert.Contains(_handler.Requests, request => request.Path.Contains("/genre/28", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdStart_ProviderFailurePreservesPreviousPoolInsteadOfClaimingAnEmptyGeneration(bool malformedJson)
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "new-user" };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        Assert.True(_cache.Save([new DiscoveryResult { UserId = profile.UserId, Recommendations = [new DiscoveryRecommendation { TmdbId = 3401 }] }]));
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/discover/movies?page=1", malformedJson ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, malformedJson ? "{broken" : "");

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.Equal(3401, Assert.Single(Assert.Single(await _cache.LoadAsync(CancellationToken.None)).Recommendations).TmdbId);
        _feedbackStore.Verify(store => store.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()), Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_ExcludesUserDismissedAndRequestedItems()
    {
        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(601, 8.0), Candidate(602, 8.0)]);

        // The user dismissed candidate 601 -> it must be merged into the exclusion set for this user.
        _feedbackStore.Setup(f => f.GetDismissedItems(profile.UserId))
            .Returns(new HashSet<(int, string)> { (601, "movie") });

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.DoesNotContain(recorded!, r => r.TmdbId == 601);
        Assert.Contains(recorded!, r => r.TmdbId == 602);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_AllCandidatesFilteredOut_NoResultForUser()
    {
        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        // Below the 5.0 quality floor -> DeduplicateAndFilter yields zero -> null result for the user.
        RegisterGenreMovieResults(28, [Candidate(701, 4.0)]);

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedbackStore.Verify(
            f => f.RecordShown(profile.UserId, It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCertifications_PreserveThePriorPoolOnlyWhenTheProviderPassWasIncomplete(bool incomplete)
    {
        var profile = NewProfile();
        profile.MaxParentalRating = 6;
        profile.GenreDistribution = new Dictionary<string, int> { ["Family"] = 5 };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        var before = DateTime.UtcNow.AddHours(-1);
        Assert.True(_cache.Save([new DiscoveryResult { UserId = profile.UserId, GeneratedAt = before, Recommendations = [new DiscoveryRecommendation { TmdbId = 9600, CertificationScore = 6, CertificationPolicyCountry = "*" }] }]));
        RegisterMovieRoute("/api/v1/discover/movies/genre/10751?page=1", [Candidate(9601, 9), Candidate(9602, 9)]);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/9601", HttpStatusCode.OK, """{"id":9601,"genres":[{"id":10751}]}""");
        if (incomplete) _handler.RegisterFailure(HttpMethod.Get, "/api/v1/movie/9602", new TaskCanceledException("provider detail request timed out"));
        else _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/9602", HttpStatusCode.OK, """{"id":9602,"genres":[{"id":10751}]}""");
        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        var pools = await _cache.LoadAsync(TestContext.Current.CancellationToken);
        if (incomplete)
        {
            var retained = Assert.Single(pools);
            Assert.Equal(before, retained.GeneratedAt);
            Assert.Equal(9600, Assert.Single(retained.Recommendations).TmdbId);
        }
        else
        {
            Assert.Empty(pools);
        }
    }

    [Fact]
    public async Task StudioOnlyHistory_LabelsUnenrichedCardsAsMetadataOnlyInsteadOfPersonalMatches()
    {
        var profile = NewProfile();
        profile.GenreDistribution.Clear();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Studios = ["Studio A"] });
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        var candidates = Enumerable.Range(9800, 25).Select(id => Candidate(id, id < 9820 ? 9 : 8.5)).ToArray();
        RegisterMovieRoute("/api/v1/discover/movies?page=1", candidates);
        foreach (var id in Enumerable.Range(9800, 20))
        {
            _handler.RegisterResponse(HttpMethod.Get, $"/api/v1/movie/{id}", HttpStatusCode.OK, $$"""{"id":{{id}},"productionCompanies":[{"name":"Studio A"}]}""");
        }

        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        var result = Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken));
        var missingMetadata = result.Recommendations.Where(recommendation => recommendation.TmdbId >= 9820).ToList();
        Assert.Equal(5, missingMetadata.Count);
        Assert.All(missingMetadata, recommendation =>
        {
            Assert.False(recommendation.IsPersonalized);
            var features = recommendation.ScoringFeatures!;
            Assert.Equal(EngineConstants.ComputeColdStartPrior(features.CombinedCriticScore, features.RecencyScore, features.PopularityScore), recommendation.Score, 12);
            Assert.NotEqual("reasonStudioNamed", recommendation.ReasonKey);
        });
        Assert.Contains(result.Recommendations, recommendation => recommendation.IsPersonalized && recommendation.ReasonKey == "reasonStudioNamed");
    }

    [Theory]
    [InlineData("studio", "reasonStudioNamed")]
    [InlineData("franchise", "reasonFranchiseNamed")]
    [InlineData("person", "reasonPersonNamed")]
    public async Task PersonalMetadataWithoutGenres_RemainsSelectableInForYou(string signal, string reason)
    {
        var profile = NewProfile();
        profile.GenreDistribution.Clear();
        var row = new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true };
        profile.WatchedItems.Add(row);
        if (signal == "studio") row.Studios = ["Studio A"];
        if (signal == "franchise") row.TmdbCollectionId = 123;
        if (signal == "person") profile.PeopleProfile["Director A"] = 5;
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterMovieRoute("/api/v1/discover/movies?page=1", [Candidate(9700, 9)]);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/9700", HttpStatusCode.OK, """{"id":9700,"collection":{"id":123,"name":"Actual saga"},"productionCompanies":[{"name":"Studio A"}],"credits":{"crew":[{"name":"Director A","job":"Director"}]}}""");
        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        var result = Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken));
        var recommendation = Assert.Single(result.Recommendations);
        Assert.True(recommendation.IsPersonalized);
        Assert.True(recommendation.Score >= DiscoverySelection.MinimumPersonalScore);
        Assert.False(recommendation.ScoringFeatures!.HasGenreEvidence);
        Assert.Equal(0, recommendation.ScoringFeatures.GenreSimilarity);
        Assert.Equal(reason, recommendation.ReasonKey);
    }

    [Fact]
    public async Task ProviderOnlyGenre_UsesFallbackRetrievalAndLearnsItsVerifiedIdentity()
    {
        var profile = NewProfile();
        var alias = "Provider genre " + Guid.NewGuid().ToString("N");
        profile.GenreDistribution = new Dictionary<string, int> { [alias] = 5 };
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = [alias] });
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterMovieRoute("/api/v1/discover/movies?page=1", [Candidate(6800, 9)]);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/6800", HttpStatusCode.OK, $$"""{"id":6800,"genres":[{"id":28,"name":"{{alias}}"}]}""");
        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        var result = Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken));
        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal(6800, recommendation.TmdbId);
        Assert.Equal(1, recommendation.ScoringFeatures!.GenreSimilarity);
        Assert.True(recommendation.IsPersonalized);
        Assert.Equal(alias, recommendation.RelatedInfo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalDetails_AreBoundedAndFailedCallsAreNotRepeatedForCredits(bool people)
    {
        var profile = NewProfile();
        profile.GenreDistribution = new Dictionary<string, int> { ["Action"] = 10 };
        if (people) profile.PeopleProfile["Favorite Director"] = 5;
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, Enumerable.Range(8000, 60).Select(id => Candidate(id, 8)).ToArray());
        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        var details = _handler.Requests.Where(request => request.Path.StartsWith("/api/v1/movie/", StringComparison.Ordinal)).ToList();
        Assert.Equal(20, details.Count);
        Assert.Equal(details.Count, details.Select(request => request.Path).Distinct().Count());
        var pool = Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(20, pool.Recommendations.Count);
    }

    [Fact]
    public async Task SlowOptionalDetails_RespectTheSharedDeadlineAndStillProduceRecommendations()
    {
        var profile = NewProfile();
        profile.GenreDistribution = new Dictionary<string, int> { ["Action"] = 10 };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, Enumerable.Range(8200, 60).Select(id => Candidate(id, 8)).ToArray());
        _handler.DetailResponseDelay = TimeSpan.FromMinutes(1);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        timer.Stop();
        var pool = Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(20, pool.Recommendations.Count);
        Assert.All(pool.Recommendations, recommendation => Assert.False(recommendation.ScoringFeatures!.IsExternalFeatureKnown(FeatureIndex.FranchiseAffinity)));
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), "Optional details must use one eight-second deadline, not one minute or one timeout per batch.");
        Assert.InRange(_handler.MaxConcurrentDelayedDetails, 1, 3);
        Assert.InRange(_handler.Requests.Count(request => request.Path.StartsWith("/api/v1/movie/", StringComparison.Ordinal)), 1, 20);
    }

    [Fact]
    public async Task CollectionMemberships_AreSharedAcrossUsersWithoutSharingTheirCounts()
    {
        var first = NewProfile();
        var second = NewProfile();
        var titleA = Guid.NewGuid();
        var titleB = Guid.NewGuid();
        var titleC = Guid.NewGuid();
        first.WatchedItems.Add(new WatchedItemInfo { ItemId = titleA, ItemType = "Movie", Played = true, Genres = ["Action"] });
        second.WatchedItems.Add(new WatchedItemInfo { ItemId = titleB, ItemType = "Movie", Played = true, Genres = ["Action"] });
        second.WatchedItems.Add(new WatchedItemInfo { ItemId = titleC, ItemType = "Movie", Played = true, Genres = ["Action"] });
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        _libraryManager.Setup(library => library.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes != null && query.IncludeItemTypes.Contains(BaseItemKind.BoxSet)))).Returns([collection]);
        _linkedChildren.Setup(service => service.GetLinkedChildrenIds(collection.Id, (int)LinkedChildType.Manual)).Returns([titleA, titleB, titleC]);
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(first, second));
        RegisterGenreMovieResults(28, [Candidate(9800, 9)]);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/9800", HttpStatusCode.OK, """{"id":9800,"collection":{"id":123,"name":"Actual saga"}}""");

        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);

        var pools = await _cache.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, pools.Count);
        Assert.Equal(EngineConstants.ComputeCollectionProgressionBoost(1), Assert.Single(pools.Single(pool => pool.UserId == first.UserId).Recommendations).ScoringFeatures!.CollectionProgressionBoost);
        Assert.Equal(EngineConstants.ComputeCollectionProgressionBoost(2), Assert.Single(pools.Single(pool => pool.UserId == second.UserId).Recommendations).ScoringFeatures!.CollectionProgressionBoost);
        _linkedChildren.Verify(service => service.GetLinkedChildrenIds(collection.Id, (int)LinkedChildType.Manual), Times.Once);
        _libraryManager.Verify(library => library.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes != null && query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))), Times.Once);
    }

    [Fact]
    public async Task EmptyOrNonEngagedHistory_DoesNotReadCollectionMemberships()
    {
        var first = NewProfile();
        var second = NewProfile();
        second.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie" });
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(first, second));
        RegisterGenreMovieResults(28, [Candidate(9800, 9)]);

        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, (await _cache.LoadAsync(TestContext.Current.CancellationToken)).Count);
        _libraryManager.Verify(library => library.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes != null && query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))), Times.Never);
        _linkedChildren.Verify(service => service.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectionLookupFailure_KeepsProgressionUnknownRatherThanObservingZero(bool throws)
    {
        var profile = NewProfile();
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Action"] });
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        _libraryManager.Setup(library => library.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes != null && query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))))
            .Returns(() => throws ? throw new InvalidOperationException("offline") : null!);
        RegisterGenreMovieResults(28, [Candidate(9800, 9)]);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/9800", HttpStatusCode.OK, """{"id":9800,"collection":{"id":123,"name":"Actual saga"}}""");

        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);

        var recommendation = Assert.Single(Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken)).Recommendations);
        Assert.False(recommendation.ScoringFeatures!.IsExternalFeatureKnown(FeatureIndex.CollectionProgressionBoost));
        Assert.True(recommendation.AvailabilityVerified);
    }

    [Fact]
    public async Task WarmCachedAvailableTitles_DoNotConsumeTheOptionalEnrichmentSelectionLimit()
    {
        var profile = NewProfile();
        profile.GenreDistribution = new Dictionary<string, int> { ["Action"] = 10 };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, Enumerable.Range(8100, 20).Select(id => Candidate(id, 9)).ToArray());
        foreach (var id in Enumerable.Range(8100, 20))
        {
            _handler.RegisterResponse(HttpMethod.Get, $"/api/v1/movie/{id}", HttpStatusCode.OK, $$"""{"id":{{id}},"mediaInfo":{"status":5} }""");
        }
        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken)).Recommendations);
        var priorCalls = _handler.Requests.Count;
        RegisterGenreMovieResults(28, Enumerable.Range(8100, 40).Select(id => Candidate(id, id < 8120 ? 9 : 8)).ToArray());
        foreach (var id in Enumerable.Range(8120, 20))
        {
            _handler.RegisterResponse(HttpMethod.Get, $"/api/v1/movie/{id}", HttpStatusCode.OK, $$"""{"id":{{id}}}""");
        }

        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);

        var pool = Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(20, pool.Recommendations.Count);
        Assert.All(pool.Recommendations, recommendation =>
        {
            Assert.InRange(recommendation.TmdbId, 8120, 8139);
            Assert.True(recommendation.AvailabilityVerified);
        });
        var detailCalls = _handler.Requests.Skip(priorCalls).Where(request => request.Path.StartsWith("/api/v1/movie/", StringComparison.Ordinal)).ToList();
        Assert.Equal(20, detailCalls.Count);
        Assert.All(detailCalls, call => Assert.InRange(int.Parse(call.Path.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture), 8120, 8139));
    }

    [Fact]
    public async Task FinalScoring_RetainsCandidatesOutsideTheDetailEnrichmentBudget()
    {
        var profile = NewProfile();
        profile.GenreDistribution = new Dictionary<string, int> { ["Action"] = 10 };
        _history.Setup(history => history.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, Enumerable.Range(8100, 40).Select(id => Candidate(id, id < 8120 ? 9 : 8)).ToArray());
        foreach (var id in Enumerable.Range(8100, 20))
        {
            _handler.RegisterResponse(HttpMethod.Get, $"/api/v1/movie/{id}", HttpStatusCode.OK, $$"""{"id":{{id}},"mediaInfo":{"status":{{(int)SeerrMediaStatus.Available}} } }""");
        }

        await _sut.GenerateDiscoveryRecommendationsAsync(TestContext.Current.CancellationToken);
        var pool = Assert.Single(await _cache.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(20, pool.Recommendations.Count);
        Assert.All(pool.Recommendations, recommendation => Assert.InRange(recommendation.TmdbId, 8120, 8139));
        Assert.All(pool.Recommendations, recommendation => Assert.False(recommendation.AvailabilityVerified));
        Assert.Equal(20, _handler.Requests.Count(request => request.Path.StartsWith("/api/v1/movie/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_UserWithPeoplePreferences_EnrichesTopCandidates()
    {
        var profile = NewProfile();
        // People appearing in >= 2 items surface via TopPeople -> BuildPreferredPeopleSet non-empty.
        profile.PeopleProfile["Christopher Nolan"] = 5;
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        RegisterGenreMovieResults(28, [Candidate(801, 8.5)]);
        // Detail endpoint returns credits so EnrichTopCandidatesWithCreditsAsync populates KnownPeople.
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/801", HttpStatusCode.OK, """
        {
          "id": 801,
          "credits": {
            "crew": [ { "id": 1, "name": "Christopher Nolan", "job": "Director" } ],
            "cast": [ { "id": 2, "name": "Cillian Murphy", "order": 0 } ]
          }
        }
        """);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        var rec = Assert.Single(recorded!, r => r.TmdbId == 801);
        Assert.NotNull(rec.KnownPeople);
        Assert.Contains("Christopher Nolan", rec.KnownPeople!);
        Assert.Contains("Cillian Murphy", rec.KnownPeople!);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_DiscoverQueryReturnsResults_ThenSchedulesInterQueryDelay()
    {
        // A successful discover query parses results and sets delayAfter=true; the finally-block
        // delay then runs with a non-cancelled token. The candidate must flow through to persistence.
        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(901, 7.8)]);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Contains(recorded!, r => r.TmdbId == 901);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_DiscoverQueryReturnsHttpError_SkipsThoseCandidates()
    {
        // Two top genres: the first (Action -> 28) errors, the second (Comedy -> 35) succeeds.
        // The non-success branch must return empty instead of aborting the whole run.
        var profile = NewProfile();
        profile.GenreDistribution = new Dictionary<string, int> { ["Action"] = 10, ["Comedy"] = 8 };
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        // First genre (Action -> 28) fails with 500; its body would parse to candidate 1000 if the
        // non-success branch wrongly fell through. Second genre (Comedy -> 35) succeeds.
        _handler.RegisterResponse(
            HttpMethod.Get, "/genre/28?page=1", HttpStatusCode.InternalServerError,
            BuildDiscoverJson([28], (1000, 8.0)));
        RegisterGenreMovieResults(35, [Candidate(1001, 8.0)]);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Contains(recorded!, r => r.TmdbId == 1001);
        Assert.DoesNotContain(recorded!, r => r.TmdbId == 1000);
    }

    // Arr library exclusion set

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_ExcludesRadarrLibraryMovies()
    {
        Plugin.Instance!.Configuration.RadarrInstances =
        [
            new ArrInstanceConfig { Name = "R", Url = "http://radarr", ApiKey = "k" }
        ];
        _arr.Setup(a => a.GetRadarrMoviesAsync("http://radarr", "k", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ArrMovie { TmdbId = 1101 }]);

        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(1101, 8.0), Candidate(1102, 8.0)]);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.DoesNotContain(recorded!, r => r.TmdbId == 1101 && r.MediaType == "movie");
        Assert.Contains(recorded!, r => r.TmdbId == 1102);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_ExcludesSonarrLibrarySeries()
    {
        Plugin.Instance!.Configuration.SonarrInstances =
        [
            new ArrInstanceConfig { Name = "S", Url = "http://sonarr", ApiKey = "k" }
        ];
        _arr.Setup(a => a.GetSonarrSeriesAsync("http://sonarr", "k", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ArrSeries { TmdbId = 1201 }]);

        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        // TV Action maps to 10759; results are stamped "tv" by the service.
        RegisterTvRoute("/genre/10759?page=1", [Candidate(1201, 8.0), Candidate(1202, 8.0)]);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.DoesNotContain(recorded!, r => r.TmdbId == 1201 && r.MediaType == "tv");
        Assert.Contains(recorded!, r => r.TmdbId == 1202 && r.MediaType == "tv");
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_RadarrFetchThrows_ContinuesWithRemainingWork()
    {
        Plugin.Instance!.Configuration.RadarrInstances =
        [
            new ArrInstanceConfig { Name = "R", Url = "http://radarr", ApiKey = "k" }
        ];
        _arr.Setup(a => a.GetRadarrMoviesAsync("http://radarr", "k", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("radarr down"));

        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(1301, 8.0)]);

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        // The per-instance catch swallows the Arr failure; discovery still completes and persists.
        _feedbackStore.Verify(
            f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_LibraryQueryThrows_StillAppliesArrExclusions()
    {
        // The Jellyfin-library exclusion source runs first in BuildExclusionSetAsync. A non-fatal
        // failure there must not abort the run before Radarr/Sonarr exclusions apply.
        _libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Throws(new InvalidOperationException("library unavailable"));

        Plugin.Instance!.Configuration.RadarrInstances =
        [
            new ArrInstanceConfig { Name = "R", Url = "http://radarr", ApiKey = "k" }
        ];
        _arr.Setup(a => a.GetRadarrMoviesAsync("http://radarr", "k", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ArrMovie { TmdbId = 1401 }]);

        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(1401, 8.0), Candidate(1402, 8.0)]);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        // Generation still persisted, and the Radarr exclusion still fired despite the library scan failing.
        Assert.NotNull(recorded);
        Assert.DoesNotContain(recorded!, r => r.TmdbId == 1401 && r.MediaType == "movie");
        Assert.Contains(recorded!, r => r.TmdbId == 1402);
    }

    // Per-user error / best-effort branches inside the full pipeline

    [Fact]
    public async Task GenerateForUser_MalformedSeerrUrl_SkipsUserWithoutRecordingShown()
    {
        // Non-empty but unparseable URL passes GenerateDiscovery's whitespace guard, then the per-user ValidateSeerrConfig throws UriFormatException.
        Plugin.Instance!.Configuration.SeerrUrl = "not-a-url";
        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedbackStore.Verify(
            f => f.RecordShown(profile.UserId, It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateDiscovery_DismissedItemsLookupThrows_StillGeneratesForUser()
    {
        // The dismissed/requested lookup is best-effort: a non-fatal failure must be swallowed
        // and the exclusion set falls back to library-only, so generation still persists.
        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(1601, 8.0)]);

        _feedbackStore.Setup(f => f.GetDismissedItems(profile.UserId))
            .Throws(new InvalidOperationException("feedback store unavailable"));

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Contains(recorded!, r => r.TmdbId == 1601);
    }

    [Fact]
    public async Task GenerateDiscovery_DiscoverQueryTimesOut_SkipsThoseCandidatesAndContinues()
    {
        // Two top genres: the first query times out (TaskCanceledException with a NON-cancelled token = upstream timeout), the second succeeds.
        var profile = NewProfile();
        profile.GenreDistribution = new Dictionary<string, int> { ["Action"] = 10, ["Comedy"] = 8 };
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        // ThrowNext is one-shot: the first HTTP SendAsync (movie genre 28) throws a timeout.
        _handler.ThrowNext = new TaskCanceledException("upstream timeout");
        RegisterGenreMovieResults(35, [Candidate(1701, 8.0)]);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Contains(recorded!, r => r.TmdbId == 1701);
    }

    [Fact]
    public async Task GenerateDiscovery_DiscoverQueryReturnsMalformedJson_SkipsThoseCandidates()
    {
        // The first genre route returns HTTP 200 with invalid JSON (JsonException on Deserialize);
        // the second returns a valid candidate. Only the valid-route candidate is persisted.
        var profile = NewProfile();
        profile.GenreDistribution = new Dictionary<string, int> { ["Action"] = 10, ["Comedy"] = 8 };
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        _handler.RegisterResponse(HttpMethod.Get, "/genre/28?page=1", HttpStatusCode.OK, "{ not valid json ]");
        RegisterGenreMovieResults(35, [Candidate(1801, 8.0)]);

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Contains(recorded!, r => r.TmdbId == 1801);
    }

    [Fact]
    public async Task GenerateDiscovery_SonarrFetchThrows_ContinuesAndPersists()
    {
        // Mirror of the Radarr-throws test for the Sonarr exclusion branch: a per-instance
        // Sonarr failure is swallowed and discovery still completes and persists.
        Plugin.Instance!.Configuration.SonarrInstances =
        [
            new ArrInstanceConfig { Name = "S", Url = "http://sonarr", ApiKey = "k" }
        ];
        _arr.Setup(a => a.GetSonarrSeriesAsync("http://sonarr", "k", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("sonarr down"));

        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(1901, 8.0)]);

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedbackStore.Verify(
            f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateDiscovery_CreditsDetailReturnsError_LeavesKnownPeopleNull()
    {
        // Enrichment runs (user has a preferred person) but the detail endpoint returns 500.
        // A non-success detail response returns early, so KnownPeople stays unpopulated.
        var profile = NewProfile();
        profile.PeopleProfile["Christopher Nolan"] = 5;
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(2001, 8.5)]);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/2001", HttpStatusCode.InternalServerError, "");

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        var rec = Assert.Single(recorded!, r => r.TmdbId == 2001);
        Assert.True(rec.KnownPeople is null or { Count: 0 });
    }

    [Fact]
    public async Task GenerateDiscovery_CreditsDetailHasNoCredits_LeavesKnownPeopleNull()
    {
        // Detail returns valid JSON with no "credits" object -> detail.Credits == null early return
        // -> KnownPeople stays unpopulated.
        var profile = NewProfile();
        profile.PeopleProfile["Christopher Nolan"] = 5;
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(2101, 8.5)]);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/2101", HttpStatusCode.OK, """{ "id": 2101 }""");

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        var rec = Assert.Single(recorded!, r => r.TmdbId == 2101);
        Assert.True(rec.KnownPeople is null or { Count: 0 });
    }

    [Fact]
    public async Task GenerateDiscovery_CreditsDetailMalformedJson_LeavesKnownPeopleNullAndContinues()
    {
        // Detail returns HTTP 200 with invalid JSON -> JsonException inside the per-candidate enrichment task, which is caught.
        var profile = NewProfile();
        profile.PeopleProfile["Christopher Nolan"] = 5;
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(2201, 8.5)]);
        _handler.RegisterResponse(HttpMethod.Get, "/api/v1/movie/2201", HttpStatusCode.OK, "{ broken json ]");

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        var rec = Assert.Single(recorded!, r => r.TmdbId == 2201);
        Assert.True(rec.KnownPeople is null or { Count: 0 });
    }

    // Helpers

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

    private static string BuildDiscoverJson(IEnumerable<int> genreIds, params (int Id, double Vote)[] items)
    {
        var genres = string.Join(",", genreIds);
        var results = string.Join(",", items.Select(i =>
            $$"""
            { "id": {{i.Id}}, "title": "Title{{i.Id}}", "voteAverage": {{i.Vote.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "popularity": 50, "genreIds": [{{genres}}], "releaseDate": "2023-01-01" }
            """));
        return $$"""{ "results": [ {{results}} ] }""";
    }

    private static (int Id, double Vote) Candidate(int id, double vote) => (id, vote);

    private void RegisterGenreMovieResults(int movieGenreId, (int Id, double Vote)[] items)
    {
        // Movie discover route stamped with the Action genre so genre mapping behaves normally.
        RegisterMovieRoute($"/genre/{movieGenreId}?page=1", items);
    }

    // Movie discover route whose candidates carry the Action (28) genre id.
    private void RegisterMovieRoute(string suffix, (int Id, double Vote)[] items)
        => RegisterRoute(suffix, [28], items);

    // TV discover route whose candidates carry the TV Action & Adventure (10759) genre id.
    private void RegisterTvRoute(string suffix, (int Id, double Vote)[] items)
        => RegisterRoute(suffix, [10759], items);

    private void RegisterRoute(string suffix, int[] genreIds, (int Id, double Vote)[] items)
    {
        _handler.RegisterResponse(
            HttpMethod.Get, suffix, HttpStatusCode.OK, BuildDiscoverJson(genreIds, items));
    }
}
