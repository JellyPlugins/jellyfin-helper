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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests the full <c>GenerateDiscoveryRecommendationsAsync</c> pipeline of
///     <see cref="SeerrDiscoveryService"/>: the guard ladder (null config, not-configured,
///     task-mode gates, no active profiles), the per-user generate→dedup→pre-score→enrich→score
///     →persist path, the child-account genre routing, language queries, per-user exclusion
///     merging, credits enrichment, and the Radarr/Sonarr library exclusion set.
///     Belongs to <c>ConfigOverride</c> because it mutates <c>Plugin.Instance.Configuration</c>.
///     Reuses the <see cref="ScriptedHttpHandler"/> defined alongside
///     <see cref="SeerrDiscoveryServiceHttpTests"/>.
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
    private readonly DiscoveryCacheService _cache;
    private readonly string _cacheFilePath;
    private readonly SeerrDiscoveryService _sut;

    public SeerrDiscoveryServiceGenerationTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.SeerrUrl = "https://seerr.example.com";
        Plugin.Instance!.Configuration.SeerrApiKey = "test-api-key";
        Plugin.Instance!.Configuration.RecommendationsTaskMode = TaskMode.Activate;

        _handler = new ScriptedHttpHandler();

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

        _sut = new SeerrDiscoveryService(
            httpFactory.Object,
            _history.Object,
            _arr.Object,
            ensemble,
            _cache,
            _feedbackStore.Object,
            pluginLog.Object,
            new Mock<ILogger<SeerrDiscoveryService>>().Object);
    }

    public void Dispose()
    {
        _handler.Dispose();
        _cache.Dispose();
        if (File.Exists(_cacheFilePath))
        {
            File.Delete(_cacheFilePath);
        }

        ControllerTestFactory.ResetPluginConfiguration();
    }

    // ============================================================
    // Guard ladder
    // ============================================================

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
        // 0 watched + FavoriteCount < 3 → excluded from activeProfiles → no work.
        var idle = NewProfile();
        idle.WatchedMovieCount = 0;
        idle.WatchedEpisodeCount = 0;
        idle.FavoriteCount = 1;
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(idle));

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedbackStore.Verify(
            f => f.RecordShown(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
        Assert.False(File.Exists(_cacheFilePath));
    }

    // ============================================================
    // Full generate → persist path
    // ============================================================

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
        // MaxParentalRating <= 60 → child branch. Only the fixed Family/Animation/Kids routes are
        // registered; the user's own top-genre routes are intentionally left unregistered.
        var profile = NewProfile();
        profile.MaxParentalRating = 60;
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        // Child-safe candidates must carry a whitelisted genre (Family 10751 / Kids 10762) so
        // ParentalRatingHelper keeps them, and vote >= the child floor (5.5).
        RegisterRoute($"/genre/{TmdbGenreFamily}?page=1", [TmdbGenreFamily], [(9001, 6.0)]);
        RegisterRoute($"/genre/{TmdbGenreFamily}?page=2", [TmdbGenreFamily], []);
        RegisterRoute($"/genre/{TmdbGenreAnimation}?page=1", [TmdbGenreAnimation], []);
        RegisterRoute($"/genre/{TmdbGenreTvKids}?page=1", [TmdbGenreTvKids], [(9002, 6.0)]);
        RegisterRoute($"/genre/{TmdbGenreTvKids}?page=2", [TmdbGenreTvKids], []);
        // Family TV (page 1) shares the /genre/10751?page=1 suffix with Family movies above.

        List<DiscoveryRecommendation>? recorded = null;
        _feedbackStore
            .Setup(f => f.RecordShown(profile.UserId, profile.UserName, It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()))
            .Callback<Guid, string, IReadOnlyList<DiscoveryRecommendation>>((_, _, items) => recorded = items.ToList());

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.NotEmpty(recorded!);
        // Every persisted item came from a child route; the top-genre (28) route was never served.
        Assert.All(recorded!, r => Assert.True(r.TmdbId is 9001 or 9002));
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_UserWithLanguagePreference_IssuesLanguageQueries()
    {
        var profile = NewProfile();
        // ChosenCount >= 3 → GetPrimaryLanguageForDiscovery returns "de".
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
    public async Task GenerateForUser_ProfileYieldsNoGenrePreferences_ProducesNoResultForThatUser()
    {
        // Passes the active-filter via favorites but has no derivable genre signal
        // (empty GenreDistribution, no watched items) → BuildGenrePreferenceVector empty → null result.
        var profile = NewProfile();
        profile.GenreDistribution = new Dictionary<string, int>();
        profile.WatchedMovieCount = 0;
        profile.WatchedEpisodeCount = 0;
        profile.FavoriteCount = 5;
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedbackStore.Verify(
            f => f.RecordShown(profile.UserId, It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_ExcludesUserDismissedAndRequestedItems()
    {
        var profile = NewProfile();
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));
        RegisterGenreMovieResults(28, [Candidate(601, 8.0), Candidate(602, 8.0)]);

        // The user dismissed candidate 601 → it must be merged into the exclusion set for this user.
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
        // Below the 5.0 quality floor → DeduplicateAndFilter yields zero → null result for the user.
        RegisterGenreMovieResults(28, [Candidate(701, 4.0)]);

        await _sut.GenerateDiscoveryRecommendationsAsync(CancellationToken.None);

        _feedbackStore.Verify(
            f => f.RecordShown(profile.UserId, It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_UserWithPeoplePreferences_EnrichesTopCandidates()
    {
        var profile = NewProfile();
        // People appearing in >= 2 items surface via TopPeople → BuildPreferredPeopleSet non-empty.
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
        // Two top genres: the first (Action → 28) errors, the second (Comedy → 35) succeeds.
        // The non-success branch must return empty instead of aborting the whole run.
        var profile = NewProfile();
        profile.GenreDistribution = new Dictionary<string, int> { ["Action"] = 10, ["Comedy"] = 8 };
        _history.Setup(h => h.GetAllUserWatchProfiles()).Returns(Profiles(profile));

        // First genre (Action → 28) fails with 500; its body would parse to candidate 1000 if the
        // non-success branch wrongly fell through. Second genre (Comedy → 35) succeeds.
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

    // ============================================================
    // Arr library exclusion set
    // ============================================================

    [Fact]
    public async Task GenerateDiscoveryRecommendationsAsync_ExcludesRadarrLibraryMovies()
    {
        Plugin.Instance!.Configuration.RadarrInstances =
        [
            new ArrInstanceConfig { Name = "R", Url = "http://radarr", ApiKey = "k" }
        ];
        _arr.Setup(a => a.GetRadarrMoviesAsync("http://radarr", "k", It.IsAny<CancellationToken>()))
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
        _arr.Setup(a => a.GetSonarrSeriesAsync("http://sonarr", "k", It.IsAny<CancellationToken>()))
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
        _arr.Setup(a => a.GetRadarrMoviesAsync("http://radarr", "k", It.IsAny<CancellationToken>()))
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

    // ============================================================
    // Per-user error / best-effort branches inside the full pipeline
    // ============================================================

    [Fact]
    public async Task GenerateForUser_MalformedSeerrUrl_SkipsUserWithoutRecordingShown()
    {
        // Non-empty but unparseable URL passes GenerateDiscovery's whitespace guard, then the
        // per-user ValidateSeerrConfig throws UriFormatException. That user's generation returns
        // null (caught per-user) and the run completes without recording feedback for them.
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
        // Two top genres: the first query times out (TaskCanceledException with a NON-cancelled
        // token = upstream timeout), the second succeeds. The timeout catch returns [] for that
        // query instead of aborting the whole run.
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
        _arr.Setup(a => a.GetSonarrSeriesAsync("http://sonarr", "k", It.IsAny<CancellationToken>()))
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
        // Detail returns valid JSON with no "credits" object → detail.Credits == null early return
        // → KnownPeople stays unpopulated.
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
        // Detail returns HTTP 200 with invalid JSON → JsonException inside the per-candidate
        // enrichment task, which is caught. Generation still persists the candidate with
        // KnownPeople unpopulated.
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

    // ============================================================
    // Helpers
    // ============================================================

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
