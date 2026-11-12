using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests the external-candidate scoring seam used by the Trakt source: config/profile guards, mapping of
///     public candidates, parental filtering, source-rank ordering, relaxed quality floors, the own-recommendation
///     exclusion, and feature-reason preservation. Scoring internals are covered by the existing discovery suite;
///     here we assert the seam's observable contract.
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
    private readonly object _detailDelayLock = new();
    private readonly List<int> _detailRequestedIds = [];
    private int _detailCalls;
    private HttpStatusCode _detailStatus = HttpStatusCode.OK;
    private Exception? _detailFailure;
    private Action? _onDetailFetch;
    private TimeSpan _detailDelay;
    private int _activeDelayedDetails;
    private int _maxConcurrentDelayedDetails;

    // The DiscoveryCacheService created by the most recent CreateService call, so a test can seed the user's
    // own cached recommendations (the source the own-recommendation exclusion reads).
    private DiscoveryCacheService? _lastCache;

    // The plugin-log mock from the most recent CreateService call, so a test can assert the diagnostic
    // stage-count line emitted when every candidate is filtered out.
    private Mock<IPluginLogService>? _lastPluginLog;

    public SeerrDiscoveryExternalScoringTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "seerr-key";
        _history.Setup(h => h.GetUserParentalPolicy(It.IsAny<Guid>())).Returns((Available: true, MaximumRating: (int?)null));
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

    private SeerrDiscoveryService CreateService(Action<Mock<IDiscoveryFeedbackStore>>? configureFeedback = null, IServerConfigurationManager? serverConfiguration = null, Action<Mock<ILocalizationManager>>? configureLocalization = null)
    {
        var factory = new Mock<System.Net.Http.IHttpClientFactory>();
        // Valid minimal details are the default. Failure scenarios explicitly select their HTTP status
        // or transport error, so ordinary scoring tests still verify availability without real network I/O.
        var handler = new Mock<System.Net.Http.HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<System.Net.Http.HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<System.Net.Http.HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (System.Net.Http.HttpRequestMessage request, CancellationToken token) =>
            {
                Interlocked.Increment(ref _detailCalls);
                if (_detailDelay > TimeSpan.Zero)
                {
                    lock (_detailDelayLock)
                    {
                        _activeDelayedDetails++;
                        _maxConcurrentDelayedDetails = Math.Max(_maxConcurrentDelayedDetails, _activeDelayedDetails);
                    }

                    try
                    {
                        await Task.Delay(_detailDelay, token);
                    }
                    finally
                    {
                        lock (_detailDelayLock) { _activeDelayedDetails--; }
                    }
                }
                _onDetailFetch?.Invoke();
                if (_detailFailure is not null) throw _detailFailure;
                if (_detailStatus != HttpStatusCode.OK) return new System.Net.Http.HttpResponseMessage(_detailStatus);
                var segments = request.RequestUri?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    ?? [];
                var last = segments.Length > 0 ? segments[^1] : string.Empty;
                lock (_detailDelayLock)
                {
                    if (int.TryParse(last, out var requestedId)) _detailRequestedIds.Add(requestedId);
                }
                if (int.TryParse(last, out var id) && _detailById.TryGetValue(id, out var json))
                {
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent(json),
                    };
                }

                if (id <= 0) return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent($$"""{"id":{{id}}}""")
                };
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
        _lastPluginLog = pluginLog;
        var cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, filePath: Path.GetTempFileName());
        _lastCache = cache;
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
        var localization = new Mock<ILocalizationManager>();
        localization.Setup(mapper => mapper.GetRatingScore("6", "DE")).Returns(new ParentalRatingScore(6, null));
        localization.Setup(mapper => mapper.GetRatingScore("18", "DE")).Returns(new ParentalRatingScore(18, null));
        localization.Setup(mapper => mapper.GetRatingScore("XXX", "US")).Returns(new ParentalRatingScore(1000, null));
        configureLocalization?.Invoke(localization);
        return new SeerrDiscoveryService(
            factory.Object, _history.Object, arr.Object, _libraryManager.Object,
            perUserRegistry, cache, feedbackStore.Object, pluginLog.Object,
            TestMockFactory.CreateSecretProtector(),
            TestMockFactory.CreateVirtualItemDetector(), new Mock<ILogger<SeerrDiscoveryService>>().Object, localization.Object, serverConfiguration);
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
        _history.Setup(h => h.GetUserParentalPolicy(userId)).Returns((Available: true, MaximumRating: maxParentalRating));
        _history.Setup(h => h.GetSeriesEpisodeCounts()).Returns(new Dictionary<Guid, int>());
    }

    private void ReturnsDetail(int tmdbId, string genresJson, string? posterPath = "/enriched.jpg", bool adult = false, int? mediaStatus = null, string? overview = null)
    {
        var mediaInfo = mediaStatus.HasValue ? $"\"mediaInfo\":{{\"status\":{mediaStatus.Value}}}," : string.Empty;
        var overviewField = overview is null ? string.Empty : $"\"overview\":\"{overview}\",";
        _detailById[tmdbId] =
            $"{{\"id\":{tmdbId},\"genres\":{genresJson},\"posterPath\":\"{posterPath}\",\"voteAverage\":7,\"popularity\":10,\"adult\":{(adult ? "true" : "false")},{overviewField}{mediaInfo}\"credits\":null}}";
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

    private static string CertifiedDetail(int id, int age) => $$"""{"id":{{id}},"genres":[{"id":28}],"releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"{{age}}"}]}] } }""";

    [Fact]
    public async Task RequestCertification_ForcesProviderRefreshAndDoesNotTrustAnEarlierSafeDetail()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, 6);
        _detailById[101] = CertifiedDetail(101, 6);
        var service = CreateService();
        Assert.Equal((true, true), await service.VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
        _detailById[101] = CertifiedDetail(101, 18);

        Assert.Equal((false, true), await service.VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
        Assert.Equal(2, _detailCalls);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(18, false)]
    public async Task RequestCertification_UsesVerifiedNativeProviderScores(int age, bool allowed)
    {
        var user = Guid.NewGuid();
        SetupProfile(user, 6);
        _detailById[101] = CertifiedDetail(101, age);

        Assert.Equal((allowed, true), await CreateService().VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":101}")]
    [InlineData("{broken")]
    [InlineData("{\"id\":102,\"releases\":{\"results\":[{\"iso_3166_1\":\"DE\",\"release_dates\":[{\"certification\":\"6\"}]}]}}")]
    public async Task RequestCertification_UnknownMalformedOrWrongIdentity_IsUnverified(string detail)
    {
        var user = Guid.NewGuid();
        SetupProfile(user, 6);
        _detailById[101] = detail;
        Assert.Equal((false, false), await CreateService().VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestCertification_ProviderFailureDoesNotAuthorizeRestrictedRequest(bool transportFailure)
    {
        var user = Guid.NewGuid();
        SetupProfile(user, 6);
        if (transportFailure) _detailFailure = new System.Net.Http.HttpRequestException("offline");
        else _detailStatus = HttpStatusCode.ServiceUnavailable;
        Assert.Equal((false, false), await CreateService().VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
    }

    [Fact]
    public async Task CurrentUnavailablePolicy_CannotBeOverriddenByAStaleUnrestrictedProfile()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        _history.Setup(history => history.GetUserParentalPolicy(user)).Returns((false, (int?)null));
        var service = CreateService();
        Assert.Empty(service.FilterConsumedItems(user, new DiscoveryResult { UserId = user, Recommendations = [new DiscoveryRecommendation { TmdbId = 101 }] }).Recommendations);
        Assert.Equal((false, true), await service.VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
        Assert.Equal(0, _detailCalls);
        _history.Verify(history => history.GetUserWatchProfile(user), Times.Never);
    }

    [Fact]
    public async Task PolicyTightenedDuringProviderFetch_IsRecheckedBeforeAuthorizing()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, 18);
        var maximum = 18;
        _history.Setup(history => history.GetUserParentalPolicy(user)).Returns(() => (true, (int?)maximum));
        _detailById[101] = CertifiedDetail(101, 18);
        _onDetailFetch = () => maximum = 6;
        Assert.Equal((false, true), await CreateService().VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
    }

    [Fact]
    public void CachedResults_AreFilteredAgainstCurrentPolicyOnEveryRead()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var maximum = (int?)null;
        _history.Setup(history => history.GetUserParentalPolicy(user)).Returns(() => (true, maximum));
        var cached = new DiscoveryResult { UserId = user, Recommendations = [new DiscoveryRecommendation { TmdbId = 1, CertificationScore = 6, CertificationPolicyCountry = "*" }, new DiscoveryRecommendation { TmdbId = 2, CertificationScore = 18, CertificationPolicyCountry = "*" }, new DiscoveryRecommendation { TmdbId = 3 }] };
        var service = CreateService();
        Assert.Equal(3, service.FilterConsumedItems(user, cached).Recommendations.Count);
        maximum = 6;
        Assert.Equal(1, service.FilterConsumedItems(user, cached).Recommendations.Single().TmdbId);
        Assert.Equal(3, cached.Recommendations.Count);
    }

    [Fact]
    public async Task CachedRating_LosesAuthorityWhenMetadataCountryChanges()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, maxParentalRating: 6);
        var configuration = new ServerConfiguration { MetadataCountryCode = "DE" };
        var server = new Mock<IServerConfigurationManager>();
        server.SetupGet(manager => manager.Configuration).Returns(configuration);
        var service = CreateService(serverConfiguration: server.Object);
        var cached = new DiscoveryResult { UserId = user, Recommendations = [new DiscoveryRecommendation { TmdbId = 1, CertificationScore = 0, CertificationPolicyCountry = "DE" }] };
        var cache = Assert.IsType<DiscoveryCacheService>(_lastCache);
        Assert.True(cache.Save([cached]));
        var loaded = Assert.Single(await cache.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Single(service.FilterConsumedItems(user, loaded).Recommendations);
        configuration.MetadataCountryCode = "US";
        Assert.Empty(service.FilterConsumedItems(user, loaded).Recommendations);
        var recommendation = Assert.Single(loaded.Recommendations);
        Assert.Equal("DE", recommendation.CertificationPolicyCountry);
        Assert.Equal("DE", recommendation.Clone().CertificationPolicyCountry);
    }

    [Fact]
    public void RatingAndCountryStamp_UseOneSnapshotWhenTheMapperChangesConfiguration()
    {
        var configuration = new ServerConfiguration { MetadataCountryCode = "DE" };
        var server = new Mock<IServerConfigurationManager>();
        server.SetupGet(manager => manager.Configuration).Returns(configuration);
        var service = CreateService(serverConfiguration: server.Object, configureLocalization: mapper =>
            mapper.Setup(value => value.GetRatingScore("0", "DE")).Callback(() => configuration.MetadataCountryCode = "US").Returns(new ParentalRatingScore(0, null)));
        var detail = System.Text.Json.JsonSerializer.Deserialize<SeerrMediaDetailResponse>("""{"id":101,"releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"0"}]},{"iso_3166_1":"US","release_dates":[{"certification":"TV-Y7"}]}]}}""")!;
        var candidate = new TmdbDiscoverItem { Id = 101 };
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyFetchedDetail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)method.Invoke(service, [candidate, detail])!);
        Assert.Equal(0, candidate.CertificationScore);
        Assert.Equal("DE", candidate.CertificationPolicyCountry);
        Assert.Equal("US", configuration.MetadataCountryCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestVerification_RejectsAChangedCountryDuringMappingOrFinalPolicyRead(bool changeDuringMapping)
    {
        var user = Guid.NewGuid();
        SetupProfile(user, maxParentalRating: 6);
        var configuration = new ServerConfiguration { MetadataCountryCode = "DE" };
        var server = new Mock<IServerConfigurationManager>();
        server.SetupGet(manager => manager.Configuration).Returns(configuration);
        var policyReads = 0;
        _history.Setup(history => history.GetUserParentalPolicy(user)).Returns(() =>
        {
            if (++policyReads > 1 && !changeDuringMapping) configuration.MetadataCountryCode = "US";
            return (true, (int?)6);
        });
        _detailById[101] = """{"id":101,"releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"0"}]}]}}""";
        var service = CreateService(serverConfiguration: server.Object, configureLocalization: mapper =>
            mapper.Setup(value => value.GetRatingScore("0", "DE")).Callback(() =>
            {
                if (changeDuringMapping) configuration.MetadataCountryCode = "US";
            }).Returns(new ParentalRatingScore(0, null)));
        Assert.Equal((false, false), await service.VerifyRequestForUserAsync(user, 101, "movie", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LegacyCachedRatingWithoutCountryEvidence_FailsClosedForRestrictedUsers()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, maxParentalRating: 6);
        var cached = new DiscoveryResult { UserId = user, Recommendations = [new DiscoveryRecommendation { TmdbId = 1, CertificationScore = 0 }] };
        Assert.Empty(CreateService().FilterConsumedItems(user, cached).Recommendations);
    }

    [Fact]
    public void CountryPolicyChangeDuringCachedFiltering_FailsClosedForRestrictedUsers()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, maxParentalRating: 6);
        var server = new Mock<IServerConfigurationManager>();
        server.SetupSequence(manager => manager.Configuration).Returns(new ServerConfiguration { MetadataCountryCode = "DE" }).Returns(new ServerConfiguration { MetadataCountryCode = "US" });
        var service = CreateService(serverConfiguration: server.Object);
        var result = service.FilterConsumedItems(user, new DiscoveryResult
        {
            UserId = user,
            Recommendations = [new DiscoveryRecommendation { TmdbId = 101, CertificationScore = 6, CertificationPolicyCountry = "DE" }]
        });
        Assert.Empty(result.Recommendations);
    }

    [Theory]
    [InlineData("franchise", "reasonFranchiseNamed")]
    [InlineData("studio", "reasonStudioNamed")]
    [InlineData("tag", null)]
    [InlineData("person", "reasonPersonNamed")]
    public async Task PersonalMetadataWithoutGenres_UsesTheEnsembleRatherThanAColdStartPrior(string signal, string? reason)
    {
        var user = Guid.NewGuid();
        var row = new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true };
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [row] };
        if (signal == "franchise") { row.TmdbCollectionId = 123; }
        if (signal == "studio") { row.Studios = ["Studio A"]; }
        if (signal == "tag") { row.Tags = ["own tag"]; }
        if (signal == "person") { profile.PeopleProfile["Director A"] = 5; }
        _history.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        _detailById[101] = """{"id":101,"collection":{"id":123,"name":"Actual saga"},"keywords":[{"name":"own tag"}],"productionCompanies":[{"name":"Studio A"}],"credits":{"crew":[{"name":"Director A","job":"Director"}]}}""";
        var result = await CreateService().ScoreExternalCandidatesAsync(user, [Candidate(101)], TestContext.Current.CancellationToken);
        var recommendation = Assert.Single(result!.Recommendations);
        Assert.True(recommendation.IsPersonalized);
        var features = recommendation.ScoringFeatures!;
        Assert.NotEqual(EngineConstants.ComputeColdStartPrior(features.CombinedCriticScore, features.RecencyScore, features.PopularityScore), recommendation.Score);
        if (reason is not null) Assert.Equal(reason, recommendation.ReasonKey);
        if (signal == "tag") Assert.Equal(1, features.TagSimilarity);
    }

    [Fact]
    public async Task ProviderLearnedAlias_RebuildsExposureAndEngagementBeforeFinalScoring()
    {
        var user = Guid.NewGuid();
        var alias = "Provider-only genre " + Guid.NewGuid().ToString("N");
        var profile = new UserWatchProfile { UserId = user, GenreDistribution = new Dictionary<string, int> { [alias] = 1, ["Science Fiction"] = 100 } };
        for (var i = 0; i < 101; i++) profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = [i == 0 ? alias : "Science Fiction"] });
        _history.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        _detailById[101] = $$"""{"id":101,"genres":[{"id":878,"name":"{{alias}}"}]}""";
        var before = PreferenceBuilder.BuildGenreExposureAnalysis(PreferenceBuilder.BuildGenrePreferenceVector(profile), profile);
        Assert.Contains(alias, before.UnderexposedGenres);
        var result = await CreateService().ScoreExternalCandidatesAsync(user, [Candidate(101)], TestContext.Current.CancellationToken);
        var features = Assert.Single(result!.Recommendations).ScoringFeatures!;
        Assert.Equal(0, features.GenreUnderexposure);
        Assert.Equal(1, features.GenreDominanceRatio);
        Assert.Equal(0, features.GenreAffinityGap);
        Assert.Equal(1, features.GenreSimilarity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AvailableReplacementBeyondTheInitialBudget_IsNeverSelectedUnchecked(bool warmCache)
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var candidates = Enumerable.Range(1000, 21).Select((id, rank) => RankedCandidate(id, rank)).ToList();
        foreach (var candidate in candidates) _detailById[candidate.TmdbId] = $$"""{"id":{{candidate.TmdbId}},"mediaInfo":{"status":5} }""";
        var service = CreateService();
        if (warmCache) Assert.Null(await service.ScoreExternalCandidatesAsync(user, candidates.Take(20).ToList(), TestContext.Current.CancellationToken));
        var calls = _detailCalls;
        Assert.Null(await service.ScoreExternalCandidatesAsync(user, candidates, TestContext.Current.CancellationToken));
        Assert.Equal(warmCache ? 1 : 21, _detailCalls - calls);
        Assert.Contains(1020, _detailRequestedIds);
    }

    [Fact]
    public async Task CachedRankedPool_DoesNotFetchUnneededLowerRankedTitles()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var candidates = Enumerable.Range(1000, 40).Select((id, rank) => RankedCandidate(id, rank)).ToList();
        var service = CreateService();
        Assert.Equal(20, (await service.ScoreExternalCandidatesAsync(user, candidates.Take(20).ToList(), TestContext.Current.CancellationToken))!.Recommendations.Count);
        var priorCalls = _detailCalls;

        var result = await service.ScoreExternalCandidatesAsync(user, candidates, TestContext.Current.CancellationToken);

        Assert.Equal(20, _detailCalls - priorCalls); // Fresh status for the selected pool, never lower-ranked extras.
        Assert.All(_detailRequestedIds, id => Assert.InRange(id, 1000, 1019));
        Assert.Equal(Enumerable.Range(1000, 20), result!.Recommendations.Select(item => item.TmdbId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CachedLowerRankedPool_InspectsTheHigherRankBeforeStoppingWithoutFetchingLaterExtras(bool higherRankSucceeds)
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var candidates = Enumerable.Range(1000, 41).Select((id, rank) => RankedCandidate(id, rank)).ToList();
        var service = CreateService();
        var warm = await service.ScoreExternalCandidatesAsync(user, candidates.Skip(1).Take(20).ToList(), TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(1001, 20), warm!.Recommendations.Select(item => item.TmdbId));
        var priorCalls = _detailCalls;
        if (!higherRankSucceeds) _detailById[1000] = "{}";

        var result = await service.ScoreExternalCandidatesAsync(user, candidates, TestContext.Current.CancellationToken);

        Assert.Equal(higherRankSucceeds ? 21 : 22, _detailCalls - priorCalls);
        Assert.All(_detailRequestedIds, id => Assert.InRange(id, 1000, 1020));
        Assert.Equal(Enumerable.Range(higherRankSucceeds ? 1000 : 1001, 20), result!.Recommendations.Select(item => item.TmdbId));
        Assert.All(result.Recommendations, item => Assert.True(item.AvailabilityVerified));
    }

    [Fact]
    public async Task FailedAvailabilityVerification_DoesNotOfferAnUncheckedTraktTitle()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        _detailStatus = HttpStatusCode.NotFound;
        Assert.Null(await CreateService().ScoreExternalCandidatesAsync(user, [Candidate(101)], TestContext.Current.CancellationToken));
        Assert.Equal(2, _detailCalls); // Optional metadata failure does not stand in for the independent status check.
    }

    [Fact]
    public async Task SlowUnrestrictedTraktDetails_UseOnlyTheSharedTop20Deadline()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        _detailDelay = TimeSpan.FromMinutes(1);
        var candidates = Enumerable.Range(1000, 40).Select((id, rank) => RankedCandidate(id, rank)).ToList();
        for (var i = 20; i < candidates.Count; i++) candidates[i].MediaType = "tv";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await CreateService().ScoreExternalCandidatesAsync(user, candidates, TestContext.Current.CancellationToken);
        timer.Stop();
        Assert.Null(result); // An expired optional budget cannot verify the availability of these titles.
        Assert.InRange(_maxConcurrentDelayedDetails, 1, 3);
        Assert.InRange(_detailCalls, 1, 20);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), "Unrestricted Trakt details must not run a forty-candidate pre-enrichment pass.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StatusBudgetGate_StopsExpiredOrCancelledReadsBeforeEnteringHttp(bool expired)
    {
        var service = CreateService();
        using var budget = new CancellationTokenSource();
        budget.CancelAfter(TimeSpan.FromMinutes(1));
        if (!expired) budget.Cancel();
        var deadline = Environment.TickCount64 + (expired ? -1 : 60_000);
        var gate = typeof(SeerrDiscoveryService).GetMethod("ThrowIfStatusBudgetExpired", BindingFlags.NonPublic | BindingFlags.Static)!;
        // Model a delayed CancelAfter callback deterministically, without sleeping or relaxing a gate.
        var rejected = Assert.Throws<TargetInvocationException>(() => gate.Invoke(null, [budget, deadline]));
        Assert.IsType<OperationCanceledException>(rejected.InnerException, exactMatch: false);
        Assert.True(budget.IsCancellationRequested);

        var handler = TestMockFactory.CreateHttpMessageHandler(HttpStatusCode.OK, """{"id":101,"mediaInfo":null}""");
        using var client = new System.Net.Http.HttpClient(handler.Object);
        var fetch = typeof(SeerrDiscoveryService).GetMethod("FetchTitleStatusAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var read = Assert.IsType<Task<SeerrTitleStatus>>(fetch.Invoke(service, [client, new Uri("https://seerr.example.com/"), "test-key", 101, "movie", budget.Token]), exactMatch: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        handler.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<System.Net.Http.HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SlowRestrictedTraktDetails_UseASeparateBoundedBudgetAndFailClosed()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, maxParentalRating: 6);
        _detailDelay = TimeSpan.FromMinutes(1);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await CreateService().ScoreExternalCandidatesAsync(user, Enumerable.Range(1000, 40).Select((id, rank) => RankedCandidate(id, rank)).ToList(), TestContext.Current.CancellationToken);
        timer.Stop();
        Assert.Null(result);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(45), "Mandatory certifications need one bounded deadline, not forty individual timeouts.");
        Assert.InRange(_detailCalls, 1, 15);
    }

    [Fact]
    public async Task CompleteStandaloneMovie_WithOmittedCollection_PreservesKnownZeroFeatures()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        _detailById[101] = """{"id":101,"title":"Standalone movie","releaseDate":"2024-01-01","originalTitle":"Standalone movie","originalLanguage":"en","adult":false,"budget":0,"revenue":0,"runtime":90,"genres":[{"id":28,"name":"Action"}],"productionCompanies":[],"productionCountries":[],"spokenLanguages":[],"status":"Released","video":false,"voteCount":10,"voteAverage":8,"popularity":50,"credits":{"cast":[],"crew":[]},"releases":{"results":[]},"keywords":[],"externalIds":{}}""";
        var result = await CreateService().ScoreExternalCandidatesAsync(user, [Candidate(101)], TestContext.Current.CancellationToken);
        var features = Assert.Single(result!.Recommendations).ScoringFeatures!;
        Assert.True(features.IsExternalFeatureKnown(FeatureIndex.FranchiseAffinity));
        Assert.True(features.IsExternalFeatureKnown(FeatureIndex.CollectionProgressionBoost));
        var means = Enumerable.Repeat(0.37, CandidateFeatures.FeatureCount).ToArray();
        Assert.Equal(0, features.ToModelVector(means)[(int)FeatureIndex.FranchiseAffinity]);
        Assert.Equal(0, features.ToModelVector(means)[(int)FeatureIndex.CollectionProgressionBoost]);
    }

    [Fact]
    public async Task CompleteProviderMetadata_ReachesSnapshotsWithoutDependingOnBoxsets()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow.AddDays(-1), Genres = ["Action"], TmdbCollectionId = 123, Tags = ["based on manga"], Studios = ["Studio A"] }] };
        _history.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        _detailById[101] = """{"id":101,"genres":[{"id":28,"name":"Action"}],"collection":{"id":123,"name":"Localized Collection"},"keywords":[{"id":210024,"name":"based on manga"}],"productionCompanies":[{"id":1,"name":"Studio A"}]}""";
        var configuration = new ServerConfiguration { MetadataCountryCode = "DE", PreferredMetadataLanguage = "de" };
        var server = new Mock<IServerConfigurationManager>();
        server.SetupGet(manager => manager.Configuration).Returns(configuration);
        var result = await CreateService(serverConfiguration: server.Object).ScoreExternalCandidatesAsync(user, [Candidate(101)], TestContext.Current.CancellationToken);
        var recommendation = Assert.Single(result!.Recommendations);
        Assert.Equal(123, recommendation.TmdbCollectionId);
        Assert.Equal(1, recommendation.ScoringFeatures!.FranchiseAffinity);
        Assert.Equal(1, recommendation.ScoringFeatures.TagSimilarity);
        Assert.True(recommendation.ScoringFeatures.StudioMatch);
        Assert.Equal(0, recommendation.ScoringFeatures.CollectionProgressionBoost);
        Assert.Equal("reasonFranchiseNamed", recommendation.ReasonKey);
        Assert.Equal("Localized Collection", recommendation.RelatedInfo);
        Assert.Equal(2, _detailCalls); // One scoring enrichment, one independent selected-title status read.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCollectionRoster_PreservesUnknownProgressInsteadOfAnObservedZero(bool absent)
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        _history.Object.GetUserWatchProfile(user)!.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Action"] });
        _detailById[101] = """{"id":101,"collection":{"id":123,"name":"Collection"}}""";
        var service = CreateService();
        var roster = _libraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.BoxSet))));
        if (absent) roster.Returns((IReadOnlyList<BaseItem>)null!);
        else roster.Throws(new InvalidOperationException("roster unavailable"));
        var result = await service.ScoreExternalCandidatesAsync(user, [Candidate(101)], TestContext.Current.CancellationToken);
        var features = Assert.Single(result!.Recommendations).ScoringFeatures!;
        Assert.False(features.IsExternalFeatureKnown(FeatureIndex.CollectionProgressionBoost));
        _libraryManager.Verify(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.BoxSet))), Times.Once);
    }

    [Fact]
    public async Task ProviderDetails_AreReusedButExpiredEntriesAndCredentialsAreIsolated()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        _detailById[101] = CertifiedDetail(101, 6);
        var service = CreateService();
        Assert.NotNull(await service.ScoreExternalCandidatesAsync(user, [Candidate(101)], CancellationToken.None));
        var cache = (System.Collections.IDictionary)typeof(SeerrDiscoveryService).GetField("_metadataCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var entry = cache.Values.Cast<object>().Single();
        Assert.NotNull(await service.ScoreExternalCandidatesAsync(user, [Candidate(101)], CancellationToken.None));
        Assert.Equal(3, _detailCalls); // Metadata was reused; status was read freshly in both generations.
        Assert.Same(entry, cache.Values.Cast<object>().Single());
        entry.GetType().GetProperty("ExpiresAtUtc")!.SetValue(entry, DateTime.UtcNow.AddMinutes(-1));
        Assert.NotNull(await service.ScoreExternalCandidatesAsync(user, [Candidate(101)], CancellationToken.None));
        Assert.Equal(5, _detailCalls);
        Assert.NotSame(entry, cache.Values.Cast<object>().Single());
        Plugin.Instance!.Configuration.SeerrApiKey = "another-key";
        Assert.NotNull(await service.ScoreExternalCandidatesAsync(user, [Candidate(101)], CancellationToken.None));
        Assert.Equal(7, _detailCalls);
        Plugin.Instance.Configuration.SeerrUrl = "https://another-seerr.example.com";
        Assert.NotNull(await service.ScoreExternalCandidatesAsync(user, [Candidate(101)], CancellationToken.None));
        Assert.Equal(9, _detailCalls);
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public async Task RequestVerification_HonorsCancellationEvenForUnrestrictedAccounts()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateService().VerifyRequestForUserAsync(user, 101, "movie", cancelled.Token));
        Assert.Equal(0, _detailCalls);
    }

    [Theory]
    [InlineData(0, "movie")]
    [InlineData(101, "music")]
    public async Task RequestVerification_InvalidIdentifiersNeverReachProvider(int id, string mediaType)
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        Assert.Equal((false, false), await CreateService().VerifyRequestForUserAsync(user, id, mediaType, CancellationToken.None));
        Assert.Equal(0, _detailCalls);
    }

    [Fact]
    public async Task RequestVerification_DisabledDuringFetchIsDeniedAndPolicyRelaxationIsRespected()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, 6);
        var available = true;
        var maximum = (int?)6;
        _history.Setup(history => history.GetUserParentalPolicy(user)).Returns(() => (available, maximum));
        _detailById[101] = CertifiedDetail(101, 6);
        var service = CreateService();
        _onDetailFetch = () => available = false;
        Assert.Equal((false, true), await service.VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
        available = true;
        _onDetailFetch = () => maximum = null;
        Assert.Equal((true, true), await service.VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
    }

    [Fact]
    public async Task RequestVerification_InvalidConfigurationAndUnavailablePolicyFailClosed()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, 6);
        var service = CreateService();
        Plugin.Instance!.Configuration.SeerrUrl = "not-an-http-url";
        Assert.Equal((false, false), await service.VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
        _history.Setup(history => history.GetUserParentalPolicy(user)).Throws(new InvalidOperationException("policy unavailable"));
        Assert.Equal((false, true), await service.VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
        Assert.Equal(0, _detailCalls);
    }

    [Fact]
    public async Task RequestVerification_MissingPluginConfigurationIsUnverified()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, 6);
        var service = CreateService();
        ControllerTestFactory.TeardownPluginInstance();
        Assert.Equal((false, false), await service.VerifyRequestForUserAsync(user, 101, "movie", CancellationToken.None));
        Assert.Equal(0, _detailCalls);
    }

    [Fact]
    public async Task ProviderDetails_CacheRemainsBoundedAndRefreshingExistingKeyDoesNotEvictAnother()
    {
        var user = Guid.NewGuid();
        SetupProfile(user, maxParentalRating: 6);
        var candidates = Enumerable.Range(1, 520).Select(Candidate).ToArray();
        foreach (var candidate in candidates) _detailById[candidate.TmdbId] = CertifiedDetail(candidate.TmdbId, 6);
        var service = CreateService();
        Assert.NotNull(await service.ScoreExternalCandidatesAsync(user, candidates, CancellationToken.None));
        var cache = (System.Collections.IDictionary)typeof(SeerrDiscoveryService).GetField("_metadataCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.Equal(512, cache.Count);
        SetupProfile(user, 6);
        Assert.Equal((true, true), await service.VerifyRequestForUserAsync(user, 520, "movie", CancellationToken.None));
        Assert.Equal(512, cache.Count);
    }

    [Fact]
    public async Task ReturnsNull_WhenCandidatesEmpty()
    {
        var result = await CreateService().ScoreExternalCandidatesAsync(Guid.NewGuid(), [], CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenSeerrNotConfigured()
    {
        Plugin.Instance!.Configuration.SeerrApiKey = string.Empty;
        var result = await CreateService().ScoreExternalCandidatesAsync(Guid.NewGuid(), [Candidate(1)], CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenUserHasNoProfile()
    {
        var userId = Guid.NewGuid();
        _history.Setup(h => h.GetUserWatchProfile(userId)).Returns((UserWatchProfile?)null);
        var result = await CreateService().ScoreExternalCandidatesAsync(userId, [Candidate(1)], CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ScoresAndKeepsFeatureReason_ForScorableCandidates()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [Candidate(101), Candidate(102), Candidate(103)],
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.NotEmpty(result.Recommendations);

        // The external path no longer overwrites the reason with a generic source key: each card keeps the
        // feature reason DetermineReason computed (a real "reason*" key), so the UI can explain the fit.
        Assert.All(result.Recommendations, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.ReasonKey));
            Assert.StartsWith("reason", r.ReasonKey, StringComparison.Ordinal);
            Assert.NotEqual("reasonTrakt", r.ReasonKey);
        });
    }

    [Fact]
    public async Task SurvivingCandidate_AppearsInResult()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [Candidate(101)],
            CancellationToken.None);

        // With the default empty feedback store the single scorable candidate survives filtering.
        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.TmdbId == 101);
    }

    [Fact]
    public async Task UnknownCertification_DroppedForRestrictedProfileEvenWhenGenresAreKnown()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId, maxParentalRating: 90);
        ReturnsDetail(101, "[{\"id\":27,\"name\":\"Horror\"}]");

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [Candidate(101)],
            CancellationToken.None);

        // A genre label cannot prove age suitability without a recognized certification.
        Assert.Null(result);
    }

    [Fact]
    public async Task EmptyAfterFilter_LogsStageCountsForDiagnosis()
    {
        // When every candidate is filtered out, a stage-count line records how many were mapped and how large the
        // exclusion set was, so an ownership collapse (the virtual-stub-library bug) is distinguishable from an
        // empty input or a parental drop.
        var userId = Guid.NewGuid();
        SetupProfile(userId, maxParentalRating: 90);
        ReturnsDetail(101, "[{\"id\":27,\"name\":\"Horror\"}]");

        var service = CreateService();
        var result = await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], CancellationToken.None);

        Assert.Null(result);
        _lastPluginLog!.Verify(
            l => l.LogDebug(
                It.IsAny<string>(),
                It.Is<string>(m => m.Contains("after filter", StringComparison.Ordinal) && m.Contains("exclusions=", StringComparison.Ordinal)),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public async Task VirtualStubInLibrary_DoesNotExcludeCandidate()
    {
        // The regression at the heart of the fix: a virtual stub library entry carries a real TMDb id but no
        // owned file, so it must NOT exclude the matching discovery candidate. Candidate 101 survives.
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        ReturnsDetail(101, "[{\"id\":28,\"name\":\"Action\"}]");
        var service = CreateService();
        // Set the library AFTER CreateService, whose own GetItemList setup returns an empty list.
        _libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([LibraryMovie(tmdbId: 101, path: null)]); // null path => virtual stub

        var result = await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.TmdbId == 101);
    }

    [Fact]
    public async Task RealItemInLibrary_ExcludesCandidate()
    {
        // Counter-proof that the filter is not a blanket pass-through: a real owned file with the same TMDb id
        // must still exclude the candidate, so discovery never offers a title the user already has.
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        ReturnsDetail(101, "[{\"id\":28,\"name\":\"Action\"}]");
        var service = CreateService();
        _libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([LibraryMovie(tmdbId: 101, path: OperatingSystem.IsWindows() ? @"D:\media\real.mkv" : "/media/real.mkv")]);

        var result = await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], CancellationToken.None);

        // The only candidate is owned, so nothing survives the exclusion.
        Assert.Null(result);
    }

    [Fact]
    public async Task PhysicalStubUnderPluginsPath_DoesNotExcludeCandidate()
    {
        // JellyNext writes a real file with a TMDb id under <data>/plugins/..., not a path-less virtual entry.
        // Such a stub must not exclude the matching candidate, while a real library under <data> still would.
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        ReturnsDetail(101, "[{\"id\":28,\"name\":\"Action\"}]");
        var service = CreateService();
        // Build the stub path from the same plugins root the detector uses (Path.Join keeps it OS-correct).
        var stubPath = Path.Join("/data", "plugins", "Jellyfin.Plugin.JellyNext", "jellynext-virtual", "abc", "4875-B-.mp4");
        _libraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([LibraryMovie(tmdbId: 101, path: stubPath)]);

        var result = await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.TmdbId == 101);
    }

    [Fact]
    public async Task NoGenrePreferences_UsesMetadataOnlyColdStartWithoutInventingPersonalAffinity()
    {
        // Trakt recommendations/trending already supply the candidates; a new user still receives them.
        var userId = Guid.NewGuid();
        _history.Setup(h => h.GetUserWatchProfile(userId)).Returns(new UserWatchProfile
        {
            UserId = userId,
            UserName = "nogenres",
            GenreDistribution = new Dictionary<string, int>(),
        });
        _history.Setup(h => h.GetSeriesEpisodeCounts()).Returns(new Dictionary<Guid, int>());

        var service = CreateService();
        var result = await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], CancellationToken.None);

        Assert.NotNull(result);
        var recommendation = Assert.Single(result.Recommendations);
        var features = Assert.IsType<CandidateFeatures>(recommendation.ScoringFeatures);
        Assert.Equal(0.0, features.GenreSimilarity);
        Assert.False(features.HasUserInteraction);
        Assert.Equal(EngineConstants.ComputeColdStartPrior(features.CombinedCriticScore, features.RecencyScore, features.PopularityScore), recommendation.Score);
        Assert.DoesNotContain(recommendation.ReasonKey, new[] { "reasonGenre", "reasonPersonNamed" });
        _lastPluginLog!.Verify(
            l => l.LogDebug(
                It.IsAny<string>(),
                It.Is<string>(m => m.Contains("cold-start", StringComparison.Ordinal)),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    private static Movie LibraryMovie(int tmdbId, string? path) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Lib " + tmdbId,
        Path = path,
        ProviderIds = new Dictionary<string, string>
        {
            ["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
    };

    [Fact]
    public async Task UnenrichedCandidate_DroppedForRestricted_KeptForUnrestricted()
    {
        // No detail fixture registered: the enrichment 404s for both users.
        var teenId = Guid.NewGuid();
        SetupProfile(teenId, maxParentalRating: 90);
        var teenResult = await CreateService().ScoreExternalCandidatesAsync(
            teenId,
            [Candidate(101)],
            CancellationToken.None);

        // Fail closed: unavailable provider details cannot establish a safe certification.
        Assert.Null(teenResult);

        var openId = Guid.NewGuid();
        SetupProfile(openId);
        var openResult = await CreateService().ScoreExternalCandidatesAsync(
            openId,
            [Candidate(101)],
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
            [
                new ExternalDiscoveryCandidate
                {
                    TmdbId = 101,
                    MediaType = "movie",
                    Title = "Movie 101",
                    Year = 2020,
                    VoteAverage = 8.5,
                    PosterPath = null,
                }
            ],
            CancellationToken.None);

        Assert.NotNull(result);
        var rec = Assert.Single(result!.Recommendations);
        Assert.Equal("/enriched.jpg", rec.PosterPath);
        Assert.Equal(8.5, rec.TmdbRating);
    }

    [Fact]
    public async Task Enrichment_ReplacesSourceOverviewWithSeerrOverview()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        // Seerr returns the synopsis in its configured locale; the candidate carries the source's own-language text.
        ReturnsDetail(101, "[{\"id\":28,\"name\":\"Action\"}]", overview: "Deutsche Beschreibung");

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [
                new ExternalDiscoveryCandidate
                {
                    TmdbId = 101,
                    MediaType = "movie",
                    Title = "Movie 101",
                    Year = 2020,
                    VoteAverage = 8.5,
                    GenreIds = [28],
                    Overview = "English overview from Trakt",
                }
            ],
            CancellationToken.None);

        Assert.NotNull(result);
        var rec = Assert.Single(result!.Recommendations);
        Assert.Equal("Deutsche Beschreibung", rec.Overview);
    }

    [Fact]
    public async Task Enrichment_KeepsSourceOverview_WhenSeerrOverviewMissing()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        // Detail carries genres (so the item is enrichable and survives) but no overview field at all.
        ReturnsDetail(101, "[{\"id\":28,\"name\":\"Action\"}]");

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [
                new ExternalDiscoveryCandidate
                {
                    TmdbId = 101,
                    MediaType = "movie",
                    Title = "Movie 101",
                    Year = 2020,
                    VoteAverage = 8.5,
                    GenreIds = [28],
                    Overview = "English overview from Trakt",
                }
            ],
            CancellationToken.None);

        Assert.NotNull(result);
        var rec = Assert.Single(result!.Recommendations);
        // A blank Seerr overview must never wipe a card: the source text is the fallback.
        Assert.Equal("English overview from Trakt", rec.Overview);
    }

    [Fact]
    public async Task Enrichment_KeepsSourceOverview_WhenSeerrOverviewBlank()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        // An all-whitespace Seerr overview is treated as absent, same as a missing field.
        ReturnsDetail(101, "[{\"id\":28,\"name\":\"Action\"}]", overview: "   ");

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [
                new ExternalDiscoveryCandidate
                {
                    TmdbId = 101,
                    MediaType = "movie",
                    Title = "Movie 101",
                    Year = 2020,
                    VoteAverage = 8.5,
                    GenreIds = [28],
                    Overview = "English overview from Trakt",
                }
            ],
            CancellationToken.None);

        Assert.NotNull(result);
        var rec = Assert.Single(result!.Recommendations);
        Assert.Equal("English overview from Trakt", rec.Overview);
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
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SecondScoring_ReusesCachedExclusionSet()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        var service = CreateService();

        await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], CancellationToken.None);
        await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], CancellationToken.None);

        // The library scan backing the shared exclusion set runs once; the second scoring reuses the cache.
        _libraryManager.Verify(lm => lm.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Movie) && query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Series))), Times.Once);
    }

    // A candidate carrying an explicit source rank, for the rank-ordering tests.
    private static ExternalDiscoveryCandidate RankedCandidate(int tmdbId, int sourceRank, double voteAverage = 7.5, int year = 2020) => new()
    {
        TmdbId = tmdbId,
        MediaType = "movie",
        Title = "Movie " + tmdbId,
        Year = year,
        VoteAverage = voteAverage,
        Popularity = 50,
        GenreIds = [28],
        PosterPath = "/p" + tmdbId + ".jpg",
        SourceRank = sourceRank,
    };

    [Fact]
    public async Task PreservesSourceRankOrder_RegardlessOfScore()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);

        // Feed candidates whose source rank is the REVERSE of their natural TMDb-id order, so a plain
        // insertion order cannot accidentally satisfy the assertion. The result must follow SourceRank.
        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [RankedCandidate(301, sourceRank: 2), RankedCandidate(302, sourceRank: 0), RankedCandidate(303, sourceRank: 1)],
            CancellationToken.None);

        Assert.NotNull(result);
        var ids = result!.Recommendations.Select(r => r.TmdbId).ToList();
        Assert.Equal([302, 303, 301], ids);
        // The rank is carried onto the recommendation so the ordering is auditable, not incidental.
        Assert.Equal([0, 1, 2], result.Recommendations.Select(r => r.SourceRank).ToList());
    }

    [Fact]
    public async Task KeepsLowScoreHighRankItem_NotTruncatedByScore()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);

        // The top-ranked item deliberately mismatches the user's Action/Drama taste (a genre the profile does
        // not favor) so it scores low. Rank-based ordering must still surface it first, proving the score-based
        // Take() does not drop it.
        var lowScoreTopRank = new ExternalDiscoveryCandidate
        {
            TmdbId = 401,
            MediaType = "movie",
            Title = "Niche 401",
            Year = 2019,
            VoteAverage = 6.1,
            GenreIds = [99], // Documentary - not in the user's genre profile
            SourceRank = 0,
        };

        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [RankedCandidate(402, sourceRank: 1), lowScoreTopRank],
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(401, result!.Recommendations[0].TmdbId);
    }

    [Fact]
    public async Task RelaxesQualityFloors_KeepsLowRatedAndOldTitles()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);

        // A 3.0-rated 1975 title would be dropped on the local path (min vote 5.0 and the year floor). The
        // external path relaxes both, so Trakt's deliberate pick survives.
        var result = await CreateService().ScoreExternalCandidatesAsync(
            userId,
            [RankedCandidate(501, sourceRank: 0, voteAverage: 3.0, year: 1975)],
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Recommendations, r => r.TmdbId == 501);
    }

    [Fact]
    public async Task ExcludesTitlesAlreadyInOwnRecommendations()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);

        var service = CreateService();

        // Seed the user's own cached local discovery with tmdb 601, then feed Trakt both 601 and 602.
        // 601 must be dropped as a duplicate of what the "For you" tab already shows; 602 survives.
        _lastCache!.Save([new DiscoveryResult
        {
            UserId = userId,
            UserName = "tester",
            Recommendations = [Rec(601)],
        }]);

        var result = await service.ScoreExternalCandidatesAsync(
            userId,
            [RankedCandidate(601, sourceRank: 0), RankedCandidate(602, sourceRank: 1)],
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Recommendations, r => r.TmdbId == 601);
        Assert.Contains(result.Recommendations, r => r.TmdbId == 602);
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
    public void FilterConsumedItems_PreservesIdentity()
    {
        var userId = Guid.NewGuid();
        var generatedAt = new DateTime(2030, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var input = new DiscoveryResult
        {
            UserId = userId,
            UserName = "alice",
            Recommendations = [Rec(1)],
            GeneratedAt = generatedAt,
        };
        var service = CreateService(f =>
        {
            f.Setup(s => s.GetDismissedItems(It.IsAny<Guid>())).Returns(new HashSet<(int, string)>());
            f.Setup(s => s.GetRequestedItems(It.IsAny<Guid>())).Returns(new HashSet<(int, string)>());
        });

        var result = service.FilterConsumedItems(userId, input);

        Assert.Equal(userId, result.UserId);
        Assert.Equal("alice", result.UserName);
        Assert.Equal(generatedAt, result.GeneratedAt);
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
