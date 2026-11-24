using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    [Theory]
    [InlineData(.2, 1, 3)]
    [InlineData(.8, 7, 9)]
    public void ActualCachedDeliveryKeepsTheVisibleMediaMixAfterRequestsAndPreservesTheFullBackfillPool(double share, int minimumSeries, int maximumSeries)
    {
        var user = Guid.NewGuid();
        var requested = new HashSet<(int, string)>();
        var service = CreateService(configureFeedback: store => store.Setup(value => value.GetRequestedItems(user)).Returns(requested));
        _history.Setup(history => history.GetUserWatchProfile(user)).Returns(MediaProfile(user, series: share > .5));
        var input = new DiscoveryResult
        {
            UserId = user, GeneratedAt = DateTime.UtcNow.AddDays(-1), TargetSeriesShare = share,
            Recommendations = Enumerable.Range(1, 40).Select(id => new DiscoveryRecommendation
            {
                TmdbId = id, MediaType = id <= 20 ? "tv" : "movie", Score = id <= 20 ? .81 : .8,
                Genres = ["Drama"], IsPersonalized = true, ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7 }
            }).ToList()
        };
        Assert.True(_lastCache!.Save([input]));
        var cached = Assert.Single(_lastCache.Load());
        Assert.Equal(share, cached.TargetSeriesShare);
        var first = service.FilterConsumedItems(user, cached);
        var visible = first.Recommendations.Take(SeerrDiscoveryService.MaxVisiblePerUser).ToArray();
        Assert.InRange(visible.Count(item => item.MediaType == "tv"), minimumSeries, maximumSeries);
        foreach (var item in visible.Take(4)) requested.Add((item.TmdbId, item.MediaType));
        var next = service.FilterConsumedItems(user, cached);
        var backfilled = next.Recommendations.Take(SeerrDiscoveryService.MaxVisiblePerUser).ToArray();
        Assert.Equal(36, next.Recommendations.Count);
        Assert.Equal(10, backfilled.Length);
        Assert.DoesNotContain(backfilled, item => requested.Contains((item.TmdbId, item.MediaType)));
        Assert.InRange(backfilled.Count(item => item.MediaType == "tv"), minimumSeries, maximumSeries);
        Assert.Equal(backfilled.OrderByDescending(item => item.Score), backfilled);
        Assert.Equal(40, cached.Recommendations.Count);
        Assert.Equal(input.GeneratedAt, next.GeneratedAt);
        Assert.All(next.Recommendations, item => Assert.Equal(.7, item.ScoringFeatures!.GenreSimilarity));
        Assert.Equal(0, _detailCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingRequestRetirementBetweenStoreCallsCannotReofferTheConfirmedTitle(bool generation)
    {
        var user = Guid.NewGuid();
        var durable = new HashSet<(int, string)>();
        SetupProfile(user);
        var service = CreateService(configureFeedback: store =>
        {
            store.Setup(value => value.GetRequestedItems(user)).Returns(() => durable.ToHashSet());
            store.Setup(value => value.GetPendingRequestedItems(user)).Returns(() => { durable.Add((77, "movie")); return new HashSet<(int, string)>(); });
        });
        if (generation)
        {
            var excluded = (HashSet<(int, string)>)typeof(SeerrDiscoveryService).GetMethod("BuildUserExclusionSet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [new UserWatchProfile { UserId = user }, new HashSet<(int, string)>()])!;
            Assert.Contains((77, "movie"), excluded);
            Assert.DoesNotContain((77, "tv"), excluded);
        }
        else
        {
            var saved = new DiscoveryResult { UserId = user, Recommendations = [new() { TmdbId = 77, MediaType = "movie" }, new() { TmdbId = 77, MediaType = "tv" }] };
            Assert.Equal("tv", Assert.Single(service.FilterConsumedItems(user, saved).Recommendations).MediaType);
            Assert.Equal(2, saved.Recommendations.Count);
        }

        Assert.Equal(0, _detailCalls);
    }

    [Fact]
    public async Task UnknownInitialFeedbackReadWithholdsDeliveryAndGenerationButPreservesTheCachedPoolForRecovery()
    {
        var user = Guid.NewGuid();
        var directory = Path.Join(Path.GetTempPath(), "jfh-unknown-feedback-" + Guid.NewGuid().ToString("N"));
        using (var original = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory)) original.RecordDismissed(user, 700, "tv");
        var clock = new ControlledWaitTimeProvider();
        var fail = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory)
        {
            RetryTimeProvider = clock,
            OpenFeedbackRead = path => { if (fail) throw new IOException("unavailable initial read"); return File.OpenRead(path); }
        };
        Assert.Throws<IOException>(() => store.RecordRequested(user, 77, "movie"));
        Assert.Contains((77, "movie"), store.GetPendingRequestedItems(user));
        SetupProfile(user);
        var service = CreateService(feedbackStoreOverride: store);
        var cached = new DiscoveryResult { UserId = user, GeneratedAt = DateTime.UtcNow.AddDays(-1), Recommendations = [new() { TmdbId = 77, MediaType = "movie" }, new() { TmdbId = 700, MediaType = "tv" }, new() { TmdbId = 78, MediaType = "movie" }] };
        var withheld = service.FilterConsumedItems(user, cached);
        Assert.Empty(withheld.Recommendations);
        Assert.Equal(cached.GeneratedAt, withheld.GeneratedAt);
        Assert.Equal(3, cached.Recommendations.Count);
        Assert.Null(await service.ScoreExternalCandidatesAsync(user, [Candidate(78)], TestContext.Current.CancellationToken));
        Assert.Equal(0, _detailCalls);
        fail = false;
        clock.ExpireDeadlines();
        await FeedbackWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(78, Assert.Single(service.FilterConsumedItems(user, cached).Recommendations).TmdbId);
        Assert.Equal(3, cached.Recommendations.Count);
    }

    [Fact]
    public void AFeedbackFileBeyondRecoveryBudgetCannotReofferItsPermanentlyDismissedCachedTitle()
    {
        var user = Guid.NewGuid();
        var directory = Path.Join(Path.GetTempPath(), "jfh-oversized-evidence-" + Guid.NewGuid().ToString("N"));
        var file = Path.Join(directory, "jellyfin-helper-discovery-feedback.json");
        using (var original = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory)) original.RecordDismissed(user, 700, "tv");
        var originalLength = new FileInfo(file).Length;
        using (var stream = File.OpenWrite(file)) stream.SetLength(121L * 1024 * 1024);
        using var unavailable = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory);
        try
        {
            SetupProfile(user);
            var service = CreateService(feedbackStoreOverride: unavailable);
            var cached = new DiscoveryResult { UserId = user, GeneratedAt = DateTime.UtcNow.AddDays(-1), Recommendations = [new() { TmdbId = 700, MediaType = "tv" }, new() { TmdbId = 78, MediaType = "movie" }] };
            Assert.Throws<IOException>(() => unavailable.GetDismissedItems(user));
            var withheld = service.FilterConsumedItems(user, cached);
            Assert.Empty(withheld.Recommendations);
            Assert.Equal(cached.GeneratedAt, withheld.GeneratedAt);
            Assert.Equal(2, cached.Recommendations.Count);
            Assert.Equal(121L * 1024 * 1024, new FileInfo(file).Length);
            using (var stream = File.OpenWrite(file)) stream.SetLength(originalLength);
            using var recovered = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory);
            Assert.Contains((700, "tv"), recovered.GetDismissedItems(user));
        }
        finally
        {
            unavailable.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FeedbackCommittedDuringTheLibraryLookupIsRecheckedBeforeReturningATraktCard()
    {
        var user = Guid.NewGuid();
        var dismissed = new HashSet<(int, string)>();
        var service = CreateService(configureFeedback: store => store.Setup(value => value.GetDismissedItems(user)).Returns(() => dismissed.ToHashSet()));
        _libraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => { dismissed.Add((77, "tv")); return []; });
        var saved = new DiscoveryResult { UserId = user, Recommendations = [new() { TmdbId = 77, MediaType = "tv" }, new() { TmdbId = 78, MediaType = "movie" }] };
        var result = service.FilterConsumedItems(user, saved);
        Assert.Equal(78, Assert.Single(result.Recommendations).TmdbId);
        Assert.Equal(2, saved.Recommendations.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalFeedbackWaitCannotRetainAnEarlierUnrestrictedParentalPolicy(bool restricted)
    {
        var user = Guid.NewGuid();
        int? rating = null;
        var reads = 0;
        var service = CreateService(configureFeedback: store => store.Setup(value => value.GetDismissedItems(user)).Returns(() =>
        {
            if (++reads == 2 && restricted) rating = 6;
            return new HashSet<(int, string)>();
        }));
        _history.Setup(history => history.GetUserParentalPolicy(user)).Returns(() => (true, rating));
        var pool = new DiscoveryResult { Recommendations = [new() { TmdbId = 77, MediaType = "movie", CertificationScore = 18, CertificationPolicyCountry = "*" }, new() { TmdbId = 78, MediaType = "movie", CertificationScore = 6, CertificationPolicyCountry = "*" }] };
        var result = service.FilterConsumedItems(user, pool);
        Assert.Equal(restricted ? new[] { 78 } : [77, 78], result.Recommendations.Select(item => item.TmdbId));
        Assert.Equal(2, pool.Recommendations.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeedbackChangedDuringTheLastPolicyLookupDefersTheViewInsteadOfReturningAPreviouslyAdmittedCard(bool changed)
    {
        var directory = Path.Join(Path.GetTempPath(), "jfh-coherent-view-" + Guid.NewGuid().ToString("N"));
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory);
        var user = Guid.NewGuid();
        var service = CreateService(feedbackStoreOverride: store);
        var policyReads = 0;
        _history.Setup(history => history.GetUserParentalPolicy(user)).Returns(() =>
        {
            if (++policyReads == 2 && changed) store.RecordDismissed(user, 77, "movie");
            return (true, (int?)null);
        });
        try
        {
            var pool = new DiscoveryResult { Recommendations = [new() { TmdbId = 77, MediaType = "movie" }, new() { TmdbId = 78, MediaType = "movie" }] };
            var view = service.FilterConsumedItems(user, pool);
            Assert.Equal(changed ? 0 : 2, view.Recommendations.Count);
            if (changed) Assert.Equal(78, Assert.Single(service.FilterConsumedItems(user, pool).Recommendations).TmdbId);
            Assert.Equal(2, pool.Recommendations.Count);
        }
        finally { store.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LibraryOwnershipChangedDuringFinalFeedbackReadDefersTheNativeViewWithoutRescanningOrPruningItsPool(bool changed)
    {
        var directory = Path.Join(Path.GetTempPath(), "jfh-library-handoff-" + Guid.NewGuid().ToString("N"));
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var users = new Mock<MediaBrowser.Controller.Library.IUserManager>();
        users.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var configuration = new Mock<Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess.IPluginConfigurationService>();
        configuration.Setup(service => service.GetConfiguration()).Returns(() => Plugin.Instance!.Configuration);
        using var history = new WatchHistoryService(_libraryManager.Object, configuration.Object, users.Object, Mock.Of<MediaBrowser.Controller.Library.IUserDataManager>(), Mock.Of<IPluginLogService>(), Mock.Of<ILogger<WatchHistoryService>>());
        var owned = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = "/media/newly-owned.mkv", ProviderIds = new() { ["Tmdb"] = "77" } };
        var nowOwned = false;
        var reads = 0;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory)
        {
            BeforeDismissalSnapshotSelection = () =>
            {
                if (++reads == 2 && changed)
                {
                    nowOwned = true;
                    _libraryManager.Raise(library => library.ItemAdded += null, _libraryManager.Object, new MediaBrowser.Controller.Library.ItemChangeEventArgs { Item = owned });
                }
            }
        };
        CreateService();
        _libraryManager.Setup(library => library.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>())).Returns(() => nowOwned ? [owned] : []);
        var service = new SeerrDiscoveryService(Mock.Of<IHttpClientFactory>(), history, Mock.Of<IArrIntegrationService>(), _libraryManager.Object, Mock.Of<IPerUserEnsembleRegistry>(), _lastCache!, store,
            Mock.Of<IPluginLogService>(), TestMockFactory.CreateSecretProtector(), TestMockFactory.CreateVirtualItemDetector(), Mock.Of<ILogger<SeerrDiscoveryService>>());
        try
        {
            var pool = new DiscoveryResult { UserId = user.Id, Recommendations = [new() { TmdbId = 77, MediaType = "movie" }, new() { TmdbId = 78, MediaType = "movie" }] };
            var view = service.FilterConsumedItems(user.Id, pool);
            Assert.Equal(changed ? 0 : 2, view.Recommendations.Count);
            _libraryManager.Verify(library => library.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once);
            if (changed) Assert.Equal(78, Assert.Single(service.FilterConsumedItems(user.Id, pool).Recommendations).TmdbId);
            Assert.Equal(2, pool.Recommendations.Count);
            Assert.Equal(changed ? 1 : 0, history.LibraryEpoch);
        }
        finally
        {
            store.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectingWrongTitleMetadataRemovesOnlyProjectedGenresNotOverlappingIndependentLabels(bool independent)
    {
        var service = CreateService(titleMetadataCache: new TitleMetadataCache());
        var candidate = new TmdbDiscoverItem
        {
            Id = 77, MediaType = "movie", SourceGenres = independent ? ["Drama"] : null,
            TitleMetadata = new TitleMetadataSnapshot { TmdbId = 78, MediaType = "movie", SourceGenres = ["Drama", "Kids"], FetchedAtUtc = DateTime.UtcNow }
        };
        candidate.ProjectSourceGenres(["Drama", "Kids"]);
        typeof(SeerrDiscoveryService).GetMethod("ApplySharedCandidateMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [candidate, Plugin.Instance!.Configuration, null]);
        Assert.Null(candidate.TitleMetadata);
        Assert.Equal(independent, candidate.SourceGenres?.Contains("Drama") == true);
        Assert.DoesNotContain("Kids", candidate.SourceGenres ?? []);
    }

    [Fact]
    public void FailedCandidateOwnershipLookupRetainsKnownDurableFeedbackExclusions()
    {
        var user = Guid.NewGuid();
        var service = CreateService(configureFeedback: store => store.Setup(value => value.GetDismissedItems(user)).Returns(new HashSet<(int, string)> { (77, "tv") }));
        _libraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Throws(new IOException("library unavailable"));
        var saved = new DiscoveryResult { UserId = user, Recommendations = [new() { TmdbId = 77, MediaType = "tv" }, new() { TmdbId = 78, MediaType = "movie" }] };
        Assert.Equal(78, Assert.Single(service.FilterConsumedItems(user, saved).Recommendations).TmdbId);
        Assert.Equal(2, saved.Recommendations.Count);
    }

    [Fact]
    public async Task ExternalOwnershipChecksUseOnlyCandidateIdsAndStillExcludeHiddenOwnedCopies()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var service = CreateService();
        var queries = new List<InternalItemsQuery>();
        var owned = new Movie { Id = Guid.NewGuid(), Path = "/hidden-library/owned.mkv", ProviderIds = new() { ["Tmdb"] = "77" } };
        _libraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => { queries.Add(query); return [owned]; });
        Assert.Null(await service.ScoreExternalCandidatesAsync(user, [Candidate(77)], TestContext.Current.CancellationToken));
        Assert.Null(await service.ScoreExternalCandidatesAsync(user, [Candidate(77)], TestContext.Current.CancellationToken));
        Assert.Equal(2, queries.Count);
        Assert.All(queries, query =>
        {
            Assert.Null(query.User);
            Assert.NotNull(query.HasAnyProviderIds);
            Assert.True(query.HasAnyProviderIds.TryGetValue("Tmdb", out var ids));
            Assert.Equal(new[] { "77" }, ids);
        });
    }

    [Theory]
    [InlineData("compatible", true)]
    [InlineData("conflicting", false)]
    [InlineData("movie", false)]
    [InlineData("virtual", false)]
    public void CandidateScopedOwnershipIncludesOnlyCompatibleTypedPhysicalAliases(string copy, bool owned)
    {
        var cache = new TitleMetadataCache();
        cache.Store(Plugin.Instance!.Configuration, new TitleMetadataSnapshot { TmdbId = 77, MediaType = "tv", TvdbId = 22, ImdbId = "tt1234", FetchedAtUtc = DateTime.UtcNow });
        var service = CreateService(titleMetadataCache: cache);
        BaseItem item = copy == "movie" ? new Movie() : new MediaBrowser.Controller.Entities.TV.Series();
        item.Id = Guid.NewGuid();
        item.Path = copy == "virtual" ? null : "/hidden/title";
        item.IsVirtualItem = copy == "virtual";
        item.ProviderIds = new() { ["Tvdb"] = "22", ["Imdb"] = copy == "conflicting" ? "tt9999" : "tt1234" };
        InternalItemsQuery? captured = null;
        _libraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => { captured = query; return [item]; });
        var card = new DiscoveryRecommendation { TmdbId = 77, MediaType = "tv" };
        var result = service.FilterConsumedItems(Guid.NewGuid(), new DiscoveryResult { Recommendations = [card] });
        Assert.Equal(!owned, result.Recommendations.Count == 1);
        Assert.NotNull(captured);
        Assert.Null(captured.User);
        Assert.NotNull(captured.HasAnyProviderIds);
        Assert.Equal(new[] { "22" }, captured.HasAnyProviderIds["Tvdb"]);
        Assert.Equal(new[] { "tt1234" }, captured.HasAnyProviderIds["Imdb"]);
        Assert.Equal(0, _detailCalls);
    }

    [Fact]
    public async Task CandidateOwnershipCancellationIsNotSwallowedAsAnUnavailableLibrary()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var service = CreateService();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _libraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => { cancellation.Cancel(); return [new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "77" } }]; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScoreExternalCandidatesAsync(user, [Candidate(77)], cancellation.Token));
    }

    [Fact]
    public void DurableFeedbackFailureAfterTheOwnershipLookupStillWithholdsTheDelivery()
    {
        var directory = Path.Join(Path.GetTempPath(), "jfh-final-feedback-" + Guid.NewGuid().ToString("N"));
        var user = Guid.NewGuid();
        using (var initial = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory)) initial.RecordDismissed(user, 700, "tv");
        var unavailable = false;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory)
        {
            BeforeDismissalSnapshotSelection = () => { if (unavailable) throw new IOException("durable snapshot unavailable"); }
        };
        var service = CreateService(feedbackStoreOverride: store);
        _libraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => { unavailable = true; return []; });
        try
        {
            var saved = new DiscoveryResult { Recommendations = [new() { TmdbId = 78, MediaType = "movie" }] };
            Assert.Empty(service.FilterConsumedItems(user, saved).Recommendations);
            Assert.Single(saved.Recommendations);
        }
        finally { store.Dispose(); Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulTraktFetchWithLocalFeedbackFailureRetriesImmediatelyButActualProviderFailureBacksOff(bool providerFailure)
    {
        var user = Guid.NewGuid();
        var directory = Path.Join(Path.GetTempPath(), "jfh-trakt-feedback-" + Guid.NewGuid().ToString("N"));
        using (var durable = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory)) durable.RecordDismissed(user, 77, "movie");
        var failedRead = true;
        using var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory)
        {
            OpenFeedbackRead = path => failedRead ? throw new IOException("local evidence unavailable") : File.OpenRead(path)
        };
        SetupProfile(user);
        var discovery = CreateService(feedbackStoreOverride: store);
        using var handler = new CountingHttpHandler();
        const string movies = """[{"title":"Dismissed","rating":8,"ids":{"tmdb":77}},{"title":"Eligible","rating":8,"ids":{"tmdb":78}}]""";
        handler.EnqueueResponse("/recommendations/movies", providerFailure ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, movies);
        if (!providerFailure) handler.EnqueueResponse("/recommendations/shows", HttpStatusCode.OK, "[]");
        using var client = new System.Net.Http.HttpClient(handler, disposeHandler: false);
        var factory = new Mock<System.Net.Http.IHttpClientFactory>();
        factory.Setup(value => value.CreateClient("Trakt")).Returns(client);
        var official = new Mock<Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.IOfficialTraktPluginReader>();
        official.Setup(value => value.IsPresent()).Returns(true);
        official.Setup(value => value.TryGetToken(user, It.IsAny<DateTimeOffset>())).Returns(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.OfficialTraktToken("test-token", DateTimeOffset.UtcNow.AddDays(1)));
        Plugin.Instance!.Configuration.TraktSourcingEnabled = true;
        var now = DateTime.UtcNow;
        var cache = new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktCacheService(() => now);
        var trakt = new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktDiscoveryService(factory.Object,
            new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktPersonalSourceService(official.Object), discovery, cache, Mock.Of<IPluginLogService>(), Mock.Of<IUserManager>(), Mock.Of<ILogger<Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktDiscoveryService>>());
        try
        {
            Assert.Null(await trakt.GetPersonalAsync(user, TestContext.Current.CancellationToken));
            Assert.Equal(providerFailure, cache.IsPersonalBackoffActive(user));
            Assert.Null(cache.GetPersonal(user, TimeSpan.FromHours(1)));
            Assert.Equal(0, _detailCalls);
            failedRead = false;
            if (providerFailure)
            {
                Assert.Null(await trakt.GetPersonalAsync(user, TestContext.Current.CancellationToken));
                Assert.Equal(1, handler.RequestCount("/recommendations/movies"));
                now = now.AddHours(1);
            }

            handler.EnqueueResponse("/recommendations/movies", HttpStatusCode.OK, movies);
            handler.EnqueueResponse("/recommendations/shows", HttpStatusCode.OK, "[]");
            var recovered = await trakt.GetPersonalAsync(user, TestContext.Current.CancellationToken);
            Assert.Equal(78, Assert.Single(recovered!.Recommendations).TmdbId);
            Assert.False(cache.IsPersonalBackoffActive(user));
            Assert.Equal(2, handler.RequestCount("/recommendations/movies"));
            Assert.Equal(providerFailure ? 1 : 2, handler.RequestCount("/recommendations/shows"));
        }
        finally
        {
            store.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecoveryPublishingBeforeAnInitialReadFailureReturnsCannotAuthorizeAnUnfilteredDelivery()
    {
        var user = Guid.NewGuid();
        var directory = Path.Join(Path.GetTempPath(), "jfh-feedback-read-handoff-" + Guid.NewGuid().ToString("N"));
        using var durable = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory);
        durable.RecordDismissed(user, 700, "tv");
        var recoveredSnapshot = durable.LoadAll().ToList();
        DiscoveryFeedbackStore? store = null;
        using var recovering = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), Mock.Of<ILogger<DiscoveryFeedbackStore>>(), directory)
        {
            OpenFeedbackRead = _ =>
            {
                // Model another reader publishing the actual durable snapshot in the
                // handoff before this already-failed read reaches the serving caller.
                typeof(DiscoveryFeedbackStore).GetField("_memoryCache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, recoveredSnapshot);
                throw new IOException("initial read failed before concurrent recovery");
            }
        };
        store = recovering;
        SetupProfile(user);
        var service = CreateService(feedbackStoreOverride: recovering);
        var cached = new DiscoveryResult { UserId = user, Recommendations = [new() { TmdbId = 700, MediaType = "tv" }, new() { TmdbId = 78, MediaType = "movie" }] };
        Assert.Empty(service.FilterConsumedItems(user, cached).Recommendations);
        Assert.Equal(78, Assert.Single(service.FilterConsumedItems(user, cached).Recommendations).TmdbId);
        Assert.Equal(2, cached.Recommendations.Count);
    }

    [Fact]
    public void CurrentEligibleHistoryOverridesTheStoredMediaTargetWithoutChangingTheSavedGeneration()
    {
        var user = Guid.NewGuid();
        var service = CreateService();
        _history.Setup(history => history.GetUserWatchProfile(user)).Returns(MediaProfile(user, series: false));
        var saved = MediaPool(user, .8);
        var delivered = service.FilterConsumedItems(user, saved);
        Assert.InRange(delivered.Recommendations.Take(10).Count(item => item.MediaType == "tv"), 1, 3);
        Assert.Equal(.2, delivered.TargetSeriesShare!.Value, precision: 10);
        Assert.Equal(.8, saved.TargetSeriesShare);
        Assert.Equal(saved.GeneratedAt, delivered.GeneratedAt);
        Assert.Equal(40, saved.Recommendations.Count);
        Assert.Equal(0, _detailCalls);
    }

    [Fact]
    public void WarmOwnPoolDeliveryUsesTheLightweightAggregateAndNeverBuildsAFullWatchProfile()
    {
        var user = Guid.NewGuid();
        var service = CreateService();
        _history.Setup(history => history.GetUserParentalPolicy(user)).Returns((true, (int?)null));
        _history.Setup(history => history.GetCurrentMediaTarget(user)).Returns(.8);
        _history.Setup(history => history.GetUserWatchProfile(user)).Throws(new InvalidOperationException("full profile must not be rebuilt on cached delivery"));
        var cached = MediaPool(user, .2);
        var delivered = service.FilterConsumedItems(user, cached);
        Assert.Equal(.8, delivered.TargetSeriesShare);
        Assert.NotEmpty(delivered.Recommendations);
        _history.Verify(history => history.GetUserWatchProfile(user), Times.Never);
        _history.Verify(history => history.GetCurrentMediaTarget(user), Times.Once);
    }

    [Fact]
    public void ParentPolicyLoweredDuringOwnershipLookupIsAuthoritativeForTheReturnedCards()
    {
        var user = Guid.NewGuid();
        var service = CreateService();
        var maximum = 18;
        _history.Setup(history => history.GetUserParentalPolicy(user)).Returns(() => (true, (int?)maximum));
        _libraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => { maximum = 6; return []; });
        var country = (string)typeof(SeerrDiscoveryService).GetMethod("CurrentCertificationCountry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [])!;
        var card = new DiscoveryRecommendation { TmdbId = 700, MediaType = "movie", CertificationPolicyCountry = country, CertificationScore = 12 };
        var cached = new DiscoveryResult { UserId = user, Recommendations = [card] };
        Assert.Empty(service.FilterConsumedItems(user, cached).Recommendations);
        Assert.Single(cached.Recommendations);
        _history.Verify(history => history.GetUserParentalPolicy(user), Times.Exactly(2));
    }

    [Fact]
    public void AnAccountUnavailableAfterOwnershipLookupCannotReceiveTheRetainedPool()
    {
        var user = Guid.NewGuid();
        var service = CreateService();
        _history.SetupSequence(history => history.GetUserParentalPolicy(user)).Returns((true, (int?)null)).Returns((false, (int?)null));
        var saved = new DiscoveryResult { UserId = user, UserName = "owner", GeneratedAt = DateTime.UtcNow.AddDays(-1), Recommendations = [new() { TmdbId = 700, MediaType = "movie" }] };
        var withheld = service.FilterConsumedItems(user, saved);
        Assert.Empty(withheld.Recommendations);
        Assert.Equal(saved.GeneratedAt, withheld.GeneratedAt);
        Assert.Equal(saved.UserName, withheld.UserName);
        Assert.Single(saved.Recommendations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableCurrentHistoryCannotReuseAStalePersonalMediaTarget(bool throws)
    {
        var user = Guid.NewGuid();
        var service = CreateService();
        if (throws) _history.Setup(history => history.GetUserWatchProfile(user)).Throws(new IOException("test unavailable profile"));
        var saved = MediaPool(user, .8);
        var delivered = service.FilterConsumedItems(user, saved);
        Assert.Null(delivered.TargetSeriesShare);
        Assert.Equal(.8, saved.TargetSeriesShare);
        Assert.Equal(saved.Recommendations, delivered.Recommendations);
        Assert.Equal(0, _detailCalls);
    }

    private static UserWatchProfile MediaProfile(Guid user, bool series)
        => new()
        {
            UserId = user, WatchedItems = new Collection<WatchedItemInfo>(Enumerable.Range(0, 40).Select(_ => new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(), ItemType = series ? "Episode" : "Movie", SeriesId = series ? Guid.NewGuid() : null,
                Played = true, RuntimeTicks = TimeSpan.TicksPerHour, LastPlayedDate = DateTime.UtcNow.AddDays(-1), Genres = ["Drama"]
            }).ToList())
        };

    private static DiscoveryResult MediaPool(Guid user, double share)
        => new()
        {
            UserId = user, GeneratedAt = DateTime.UtcNow.AddDays(-1), TargetSeriesShare = share,
            Recommendations = Enumerable.Range(1, 40).Select(id => new DiscoveryRecommendation
            {
                TmdbId = id, MediaType = id <= 20 ? "tv" : "movie", Score = id <= 20 ? .81 : .8, Genres = ["Drama"], IsPersonalized = true
            }).ToList()
        };

    private SeerrDiscoveryService CreateService(Action<Mock<IDiscoveryFeedbackStore>>? configureFeedback = null, IServerConfigurationManager? serverConfiguration = null, Action<Mock<ILocalizationManager>>? configureLocalization = null, TitleMetadataCache? titleMetadataCache = null, IDiscoveryFeedbackStore? feedbackStoreOverride = null, TimeProvider? cacheWaitClock = null, Action<Mock<IArrIntegrationService>>? configureArr = null)
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
        _history.Setup(history => history.GetCurrentMediaTarget(It.IsAny<Guid>())).Returns((Guid owner) =>
        {
            var current = _history.Object.GetUserWatchProfile(owner);
            return current?.UserId == owner ? MediaTypeBalance.TargetSeriesShare(current) : null;
        });
        configureArr?.Invoke(arr);
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
        var cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, filePath: Path.GetTempFileName())
        {
            WaitTimeProvider = cacheWaitClock ?? TimeProvider.System
        };
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
            perUserRegistry, cache, feedbackStoreOverride ?? feedbackStore.Object, pluginLog.Object,
            TestMockFactory.CreateSecretProtector(),
            TestMockFactory.CreateVirtualItemDetector(), new Mock<ILogger<SeerrDiscoveryService>>().Object, localization.Object, serverConfiguration, titleMetadataCache: titleMetadataCache);
    }

    [Fact]
    public async Task ExternalScoringCancelsItsCacheReaderAfterAWriterCallerHasAbandonedPersistence()
    {
        var user = Guid.NewGuid();
        _history.Setup(history => history.GetUserWatchProfile(user)).Returns(new UserWatchProfile { UserId = user });
        var service = CreateService();
        var cache = _lastCache!;
        var gate = (SemaphoreSlim)typeof(DiscoveryCacheService).GetField("_fileLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Task<bool>? writer = null;
        try
        {
            Assert.False(await cache.SaveAsync([], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            writer = (Task<bool>)typeof(DiscoveryCacheService).GetField("_pendingSave", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var scoring = service.ScoreExternalCandidatesAsync(user, [new ExternalDiscoveryCandidate { TmdbId = 500, MediaType = "movie" }], cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scoring.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(writer.IsCompleted);
        }
        finally
        {
            gate.Release();
            if (writer is not null) await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedEmptyGenresRespectTheWinningSeerrFieldsAcquisitionTime(bool current)
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var scope = TitleMetadataCache.SeerrScope(Plugin.Instance!.Configuration, "en");
        var metadata = new TitleMetadataSnapshot
        {
            TmdbId = 101, GenreIds = [28], Source = "seerr", ObservationScope = scope, FetchedAtUtc = now.AddSeconds(-30),
            FieldFetchedAtUtc = System.Collections.Immutable.ImmutableDictionary<string, DateTime>.Empty.Add(nameof(TitleMetadataSnapshot.GenreIds), now.AddSeconds(-5))
        };
        var candidate = new TmdbDiscoverItem { Id = 101, GenreIds = [16], TitleMetadata = metadata };
        var ttl = (TimeSpan)typeof(SeerrDiscoveryService).GetField("SeerrUserCacheTtl", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var acquired = now.AddSeconds(current ? -1 : -15);
        var type = typeof(SeerrDiscoveryService).GetNestedType("CachedMediaDetail", BindingFlags.NonPublic)!;
        var cached = Activator.CreateInstance(type, new SeerrMediaDetailResponse { Id = 101, Genres = [] }, acquired.Add(ttl))!;
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyCachedDetail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)method.Invoke(service, [candidate, cached, scope])!);
        Assert.Equal(current ? [] : new[] { 28 }, candidate.GenreIds);
        Assert.Equal(current ? [] : new[] { 28 }, candidate.TitleMetadata!.GenreIds!.Value);
        Assert.Equal(current ? acquired : now.AddSeconds(-5), candidate.TitleMetadata.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.GenreIds)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedKeywordsRespectTheirOwnAcquisitionTimeWithoutPreventingCurrentGenreUpdates(bool current)
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var scope = TitleMetadataCache.SeerrScope(Plugin.Instance!.Configuration, "en");
        var metadata = new TitleMetadataSnapshot
        {
            TmdbId = 101, GenreIds = [16], Keywords = ["anime", "pirate"], Source = "seerr", ObservationScope = scope, FetchedAtUtc = now.AddSeconds(-30),
            FieldFetchedAtUtc = System.Collections.Immutable.ImmutableDictionary<string, DateTime>.Empty
                .Add(nameof(TitleMetadataSnapshot.GenreIds), now.AddSeconds(-20)).Add(nameof(TitleMetadataSnapshot.Keywords), now.AddSeconds(-5))
        };
        var candidate = new TmdbDiscoverItem { Id = 101, GenreIds = [16], KnownKeywords = ["anime", "pirate"], TitleMetadata = metadata };
        var ttl = (TimeSpan)typeof(SeerrDiscoveryService).GetField("SeerrUserCacheTtl", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var acquired = now.AddSeconds(current ? -1 : -15);
        var type = typeof(SeerrDiscoveryService).GetNestedType("CachedMediaDetail", BindingFlags.NonPublic)!;
        var detail = new SeerrMediaDetailResponse
        {
            Id = 101, Genres = [], Keywords = current ? [] : [new SeerrMediaDetailResponse.GenreRef { Id = 42, Name = "preschool" }]
        };
        var cached = Activator.CreateInstance(type, detail, acquired.Add(ttl))!;
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyCachedDetail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)method.Invoke(service, [candidate, cached, scope])!);
        Assert.Empty(candidate.GenreIds);
        Assert.Equal(current ? [] : new[] { "anime", "pirate" }, candidate.KnownKeywords);
        Assert.Equal(current ? [] : new[] { "anime", "pirate" }, candidate.TitleMetadata!.Keywords!.Value);
        Assert.Equal(acquired, candidate.TitleMetadata.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.GenreIds)]);
        Assert.Equal(current ? acquired : now.AddSeconds(-5), candidate.TitleMetadata.FieldFetchedAtUtc[nameof(TitleMetadataSnapshot.Keywords)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewerScopedTraktMetadataClearsOlderCandidateGenresAndLanguageWithoutInventingIndependentEvidence(bool independentLabel)
    {
        var config = Plugin.Instance!.Configuration;
        config.TraktSourcingEnabled = true;
        var acquired = DateTime.UtcNow.AddHours(-2);
        var older = Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktMapper.Metadata(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktMediaItem
        {
            Ids = new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktIds { Tmdb = 101 }, Genres = ["Animation"], OriginalLanguage = "ja"
        }, "movie", acquired);
        var newer = Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktMapper.Metadata(new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktMediaItem
        {
            Ids = new Jellyfin.Plugin.JellyfinHelper.Services.Trakt.TraktIds { Tmdb = 101 }, Genres = [], OriginalLanguage = "en"
        }, "movie", acquired.AddHours(1));
        var cache = new TitleMetadataCache();
        cache.StoreSupplemental(config, newer, new("trakt", "https://api.trakt.tv", "public-metadata"), null);
        var candidate = new TmdbDiscoverItem { Id = 101, SourceGenres = independentLabel ? ["custom interest"] : null, TitleMetadata = older };
        candidate.ProjectSourceGenres(["Animation"]);
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyCachedSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
        method.Invoke(null, [candidate, cache.Get(config, 101, "movie")]);
        Assert.Equal(independentLabel ? new[] { "custom interest" } : [], candidate.SourceGenres);
        var metadata = Assert.IsType<TitleMetadataSnapshot>(candidate.TitleMetadata);
        Assert.Equal("en", metadata.OriginalLanguage);
        Assert.Empty(metadata.SourceGenres!.Value);
        Assert.False(metadata.ConflictingValues?.ContainsKey(nameof(TitleMetadataSnapshot.OriginalLanguage)) == true);
        Assert.Equal(acquired.AddHours(1), metadata.FetchedAtUtc);
        Assert.Equal(acquired, older.FetchedAtUtc);
        Assert.Equal("Animation", Assert.Single(older.SourceGenres!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitEmptySeerrGenresClearCandidateIdsButRetainIndependentSourceLabels(bool partial)
    {
        var service = CreateService();
        var acquired = DateTime.UtcNow.AddHours(-1);
        var scope = TitleMetadataCache.SeerrScope(Plugin.Instance!.Configuration, "en");
        var seerr = new TitleMetadataSnapshot { TmdbId = 101, GenreIds = [16], Source = "seerr", ObservationScope = scope, FetchedAtUtc = acquired.AddHours(-1) };
        var independent = new TitleMetadataSnapshot { TmdbId = 101, Source = "trakt", SourceGenres = ["Family"], FetchedAtUtc = acquired.AddHours(-1) };
        var candidate = new TmdbDiscoverItem { Id = 101, GenreIds = [16], SourceGenres = ["Family"], TitleMetadata = TitleMetadataMerger.Combine([seerr, independent]) };
        var detail = new SeerrMediaDetailResponse { Id = 101, Genres = partial ? null : [] };
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyDetail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)method.Invoke(service, [candidate, detail, acquired, scope])!);
        Assert.Equal(partial ? new[] { 16 } : [], candidate.GenreIds);
        Assert.Equal("Family", Assert.Single(candidate.SourceGenres!));
        Assert.Equal(partial ? new[] { 16 } : [], candidate.TitleMetadata!.GenreIds!.Value);
        Assert.Equal("Family", Assert.Single(candidate.TitleMetadata.SourceGenres!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryCompletionUsesBoundedFeedbackIoAndItsActualGenerationTime(bool cancel)
    {
        var directory = Path.Join(Path.GetTempPath(), "jfh-discovery-feedback-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var stall = false;
        var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, directory)
        {
            WaitTimeProvider = cancel ? new ControlledWaitTimeProvider() : TimeProvider.System,
            WriteFeedback = (path, contents) =>
            {
                if (stall)
                {
                    started.TrySetResult();
                    release.Wait(TestContext.Current.CancellationToken);
                }

                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        store.RecordRequested(user, 1, "movie");
        stall = true;
        var service = CreateService(feedbackStoreOverride: store, cacheWaitClock: cancel ? new ControlledWaitTimeProvider() : TimeProvider.System);
        var result = new DiscoveryResult { UserId = user, UserName = "user", GeneratedAt = DateTime.UtcNow.AddDays(-3), Recommendations = [new DiscoveryRecommendation { TmdbId = 500, Title = "generated" }] };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var method = typeof(SeerrDiscoveryService).GetMethod("PersistValidatedResultsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var persistence = (Task)method.Invoke(service, [new List<DiscoveryResult> { result }, new HashSet<Guid>(), null, cancellation.Token])!;
        Task<bool>? writer = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            writer = (Task<bool>)typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => persistence.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            else
            {
                await persistence.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }

            Assert.False(writer.IsCompleted);
            Assert.Contains((1, "movie"), store.GetRequestedItems(user));
            Assert.Single(store.LoadForUser(user)!.Entries);
            Assert.Equal(result.GeneratedAt, Assert.Single(_lastCache!.Load()).GeneratedAt);
        }
        finally
        {
            release.Set();
            if (writer is not null) Assert.True(await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            await CacheWriter(_lastCache!).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        var entry = Assert.Single(store.LoadForUser(user)!.Entries, row => row.TmdbId == 500);
        Assert.Equal(result.GeneratedAt, entry.ShownAtUtc);
        Assert.False(entry.HasConfirmedPrediction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryMetadataPersistenceBoundsItsWaitAndPropagatesScheduledCancellation(bool cancel)
    {
        var fs = new Mock<System.IO.Abstractions.IFileSystem>();
        var file = new Mock<System.IO.Abstractions.IFile>();
        fs.SetupGet(value => value.File).Returns(file.Object);
        fs.SetupGet(value => value.Directory).Returns(new Mock<System.IO.Abstractions.IDirectory>().Object);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        file.Setup(value => value.WriteAllText(It.IsAny<string>(), It.IsAny<string>())).Callback(() =>
        {
            started.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
        });
        var metadata = new TitleMetadataCache(fileSystem: fs.Object, cachePath: Path.GetFullPath("discovery-stalled-metadata.json"))
        {
            WaitTimeProvider = cancel ? new ControlledWaitTimeProvider() : TimeProvider.System
        };
        metadata.Store(Plugin.Instance!.Configuration, new TitleMetadataSnapshot { TmdbId = 101, FetchedAtUtc = DateTime.UtcNow });
        Mock<IDiscoveryFeedbackStore>? feedback = null;
        var service = CreateService(configureFeedback: value => feedback = value, titleMetadataCache: metadata, cacheWaitClock: cancel ? new ControlledWaitTimeProvider() : TimeProvider.System);
        var result = new DiscoveryResult { UserId = Guid.NewGuid(), UserName = "test user" };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var method = typeof(SeerrDiscoveryService).GetMethod("PersistValidatedResultsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var persistence = (Task)method.Invoke(service, [new List<DiscoveryResult> { result }, new HashSet<Guid>(), null, cancellation.Token])!;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => persistence.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            else
            {
                await persistence.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                await CacheWriter(_lastCache!).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }

            Assert.Equal(result.UserId, Assert.Single(_lastCache!.Load()).UserId);
            feedback!.Verify(store => store.RecordGeneratedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), cancel ? Times.Never() : Times.Once());
            file.Verify(value => value.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            file.Verify(value => value.Move(It.IsAny<string>(), It.IsAny<string>(), true), Times.Never);
        }
        finally
        {
            release.Set();
            Assert.True(await metadata.SaveAsync(TestContext.Current.CancellationToken));
            await CacheWriter(_lastCache!).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        feedback!.Verify(store => store.RecordGeneratedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoveryRecommendation>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    private static Task<bool> CacheWriter(DiscoveryCacheService cache)
        => (Task<bool>)typeof(DiscoveryCacheService).GetField("_pendingSave", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    [Fact]
    public async Task LateCacheCommitRecordsOnlyItsFreshDetachedGenerationsBeforeAnyLaterCarriedPoolCanSkipThem()
    {
        var directory = Path.Join(Path.GetTempPath(), "jfh-late-discovery-" + Guid.NewGuid().ToString("N"));
        var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, directory);
        var clock = new ControlledWaitTimeProvider();
        var service = CreateService(feedbackStoreOverride: store, cacheWaitClock: clock);
        var cache = _lastCache!;
        var gate = (SemaphoreSlim)typeof(DiscoveryCacheService).GetField("_fileLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        var fresh = new DiscoveryResult { UserId = Guid.NewGuid(), UserName = "fresh", GeneratedAt = DateTime.UtcNow.AddDays(-3), Recommendations = [new DiscoveryRecommendation { TmdbId = 500, Title = "original" }] };
        var held = true;
        var carried = new DiscoveryResult { UserId = Guid.NewGuid(), UserName = "carried", GeneratedAt = fresh.GeneratedAt, Recommendations = [new DiscoveryRecommendation { TmdbId = 600 }] };
        var method = typeof(SeerrDiscoveryService).GetMethod("PersistValidatedResultsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var persistence = (Task)method.Invoke(service, [new List<DiscoveryResult> { fresh, carried }, new HashSet<Guid> { carried.UserId }, null, TestContext.Current.CancellationToken])!;
        try
        {
            clock.ExpireDeadlines();
            await persistence.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(store.LoadForUser(fresh.UserId));
            Assert.False(CacheWriter(cache).IsCompleted);
            fresh.Recommendations[0].Title = "mutated after admission";
            gate.Release();
            held = false;
            Assert.True(await CacheWriter(cache).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            var saved = Assert.Single(store.LoadForUser(fresh.UserId)!.Entries);
            Assert.Equal("original", saved.Title);
            Assert.Equal(fresh.GeneratedAt, saved.ShownAtUtc);
            Assert.Null(saved.ImpressionAtUtc);
            Assert.Null(store.LoadForUser(carried.UserId));
            var before = System.Text.Json.JsonSerializer.Serialize(store.LoadAll());
            await ((Task)method.Invoke(service, [cache.Load().ToList(), new HashSet<Guid> { fresh.UserId, carried.UserId }, null, TestContext.Current.CancellationToken])!).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(store.LoadAll()));
        }
        finally
        {
            if (held) gate.Release();
            await CacheWriter(cache).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReconciliationRetriesRejectedConfirmedRequestsOutsideTheCurrentPoolWithoutCrossingUsersOrMediaTypes()
    {
        var directory = Path.Join(Path.GetTempPath(), "jfh-rejected-discovery-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, directory)
        {
            WaitTimeProvider = new ControlledWaitTimeProvider(),
            WriteFeedback = (path, contents) =>
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            }
        };
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var service = CreateService(feedbackStoreOverride: store);
        var cache = _lastCache!;
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyReconciliationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task<int> Reconcile(Guid owner, params (int TmdbId, string MediaType)[] confirmed)
            => (Task<int>)method.Invoke(service, [owner, confirmed.ToHashSet(), typeof(SeerrDiscoveryService).GetMethod("UserSourceScope", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Plugin.Instance?.Configuration]), TestContext.Current.CancellationToken])!;
        var first = store.RecordRequestedAsync(user, 1, "movie", TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var queued = Enumerable.Range(2, 64).Select(id => store.RecordRequestedAsync(user, id, "movie", TestContext.Current.CancellationToken)).ToArray();
            Assert.True(cache.Save([new DiscoveryResult { UserId = user, Recommendations = [new DiscoveryRecommendation { TmdbId = 9000, MediaType = "movie" }] }]));
            var acquiredBefore = DateTime.UtcNow;
            Assert.Equal(0, await Reconcile(user, (9000, "movie")));
            var acquiredAfter = DateTime.UtcNow;
            Assert.Contains((9000, "movie"), store.GetPendingRequestedItems(user));
            Assert.False(Assert.Single(Assert.Single(cache.Load()).Recommendations).AlreadyRequested);
            // A fresh status check/generation can remove the requested card while the
            // feedback queue still has no capacity. Recovery must not need that card.
            Assert.True(cache.Save([new DiscoveryResult { UserId = user, Recommendations = [new DiscoveryRecommendation { TmdbId = 500 }] }]));
            release.Set();
            Assert.All(await Task.WhenAll(queued.Append(first)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), saved => Assert.True(saved));
            await FeedbackWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(0, await Reconcile(other, (9000, "movie")));
            Assert.Equal(0, await Reconcile(user, (9000, "tv")));
            Assert.Throws<IOException>(() => store.LoadAll());
            Assert.Equal(1, await Reconcile(user, (9000, "movie")));
            await FeedbackWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var entry = Assert.Single(Assert.Single(store.LoadAll()).Entries, entry => entry.TmdbId == 9000);
            Assert.InRange(entry.RequestedAtUtc!.Value, acquiredBefore, acquiredAfter);
            Assert.Equal("movie", entry.MediaType);
            Assert.Null(entry.ScoringFeatures);
            Assert.Empty(store.GetPendingRequestedItems(user));
            Assert.DoesNotContain(Assert.Single(cache.Load()).Recommendations, item => item.TmdbId == 9000);
        }
        finally
        {
            release.Set();
            await FeedbackWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReconciliationRetriesARetainedCardUpdateEvenWhenTheOlderRequestIsDurableAndTheCardHasLeftThePool()
    {
        var directory = Path.Join(Path.GetTempPath(), "jfh-retained-card-" + Guid.NewGuid().ToString("N"));
        var fail = false;
        var store = new DiscoveryFeedbackStore(new Mock<IPluginLogService>().Object, new Mock<ILogger<DiscoveryFeedbackStore>>().Object, directory)
        {
            WriteFeedback = (path, contents) =>
            {
                if (fail) throw new IOException("test card-update persistence failure");
                Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            },
            RetryTimeProvider = new ControlledWaitTimeProvider()
        };
        var user = Guid.NewGuid();
        store.RecordRequested(user, 9000, "movie");
        var older = Assert.Single(store.LoadAll()).Entries.Single();
        var olderRequestedAt = older.RequestedAtUtc;
        Assert.Null(older.ScoringFeatures);
        var service = CreateService(feedbackStoreOverride: store);
        var cache = _lastCache!;
        var generated = DateTime.UtcNow.AddDays(-2);
        var card = new DiscoveryRecommendation { TmdbId = 9000, MediaType = "movie", Title = "confirmed card", ScoringFeatures = new CandidateFeatures { GenreSimilarity = .8 } };
        var before = DateTime.UtcNow;
        fail = true;
        store.RecordRequested(user, card, generated);
        var after = DateTime.UtcNow;
        card.Title = "mutated caller card";
        fail = false;
        Assert.Contains((9000, "movie"), store.GetRequestedItems(user));
        Assert.Contains((9000, "movie"), store.GetPendingRequestedItems(user));
        Assert.Throws<IOException>(() => store.LoadAll());
        Assert.True(cache.Save([new DiscoveryResult { UserId = user, Recommendations = [new DiscoveryRecommendation { TmdbId = 500 }] }]));
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyReconciliationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            var retry = (Task<int>)method.Invoke(service, [user, new HashSet<(int, string)> { (9000, "movie") }, typeof(SeerrDiscoveryService).GetMethod("UserSourceScope", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Plugin.Instance?.Configuration]), TestContext.Current.CancellationToken])!;
            Assert.Equal(1, await retry.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            await FeedbackWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var saved = Assert.Single(Assert.Single(store.LoadAll()).Entries);
            Assert.Equal("confirmed card", saved.Title);
            Assert.Equal(.8, saved.ScoringFeatures!.GenreSimilarity);
            Assert.Equal(generated, saved.ShownAtUtc);
            Assert.InRange(saved.RequestedAtUtc!.Value, before, after);
            Assert.Empty(store.GetPendingRequestedItems(user));
            Assert.Equal(olderRequestedAt, older.RequestedAtUtc);
            Assert.Null(older.ScoringFeatures);
            Assert.DoesNotContain(Assert.Single(cache.Load()).Recommendations, item => item.TmdbId == 9000);
        }
        finally
        {
            fail = false;
            await FeedbackWriter(store).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<bool> FeedbackWriter(DiscoveryFeedbackStore store)
        => (Task<bool>)typeof(DiscoveryFeedbackStore).GetField("_pendingGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

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

    [Fact]
    public async Task ExternalSourceGenresRemainVisibleAndObservedWhenProviderGenresAreAbsent()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var candidate = Candidate(101);
        candidate.GenreIds = [];
        candidate.SourceGenres = ["action", "drama"];
        _detailById[101] = """{"id":101,"keywords":[],"mediaInfo":{"status":1}}""";
        var result = await CreateService().ScoreExternalCandidatesAsync(user, [candidate], TestContext.Current.CancellationToken);
        var selected = Assert.Single(result!.Recommendations);
        Assert.Equal(new[] { "Action", "Drama" }, selected.Genres.Order(StringComparer.Ordinal));
        Assert.True(selected.ScoringFeatures!.HasGenreEvidence);
        Assert.True(selected.ScoringFeatures.GenreSimilarity > 0);
    }

    [Theory]
    [InlineData("6", true)]
    [InlineData("18", false)]
    [InlineData(null, false)]
    public async Task RequiredCertificationEnrichmentStillNormalizesRetainedSharedGenreAndKeywordEvidence(string? certification, bool allowed)
    {
        var user = Guid.NewGuid();
        SetupProfile(user, maxParentalRating: 6);
        var profile = _history.Object.GetUserWatchProfile(user)!;
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Action"], Tags = ["pirate", "friendship"] });
        var cache = new TitleMetadataCache();
        cache.Store(Plugin.Instance!.Configuration, new TitleMetadataSnapshot { TmdbId = 101, GenreIds = [28], Keywords = ["pirate", "friendship"], FetchedAtUtc = DateTime.UtcNow.AddDays(-10) });
        var candidate = Candidate(101);
        candidate.GenreIds = [];
        var release = certification is null ? string.Empty : $$""", "releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"{{certification}}"}]}]}""";
        _detailById[101] = $$"""{"id":101,"mediaInfo":{"status":1}{{release}}}""";
        var result = await CreateService(titleMetadataCache: cache).ScoreExternalCandidatesAsync(user, [candidate], TestContext.Current.CancellationToken);
        if (!allowed)
        {
            Assert.Null(result);
            return;
        }

        var selected = Assert.Single(result!.Recommendations);
        Assert.Equal(new[] { "Action" }, selected.Genres);
        Assert.Equal(6, selected.CertificationScore);
        Assert.True(selected.ScoringFeatures!.HasGenreEvidence);
        Assert.Equal(1, selected.ScoringFeatures.TagSimilarity, 10);
        Assert.Equal(new[] { "pirate", "friendship" }, selected.TitleMetadata!.Keywords!.Value);
    }

    [Fact]
    public async Task WarmupWithoutAnOptionalSharedCacheMakesNoProviderRequests()
    {
        await CreateService().WarmTitleMetadataAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _detailCalls);
        _history.Verify(history => history.GetAllUserWatchProfiles(), Times.Never);
    }

    [Fact]
    public async Task MissingOptionalServerSettingsDoNotPreventExternalScoring()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var server = new Mock<IServerConfigurationManager>();
        server.SetupGet(manager => manager.Configuration).Returns((ServerConfiguration)null!);
        var result = await CreateService(serverConfiguration: server.Object).ScoreExternalCandidatesAsync(user, [Candidate(101)], TestContext.Current.CancellationToken);
        var selected = Assert.Single(result!.Recommendations);
        Assert.Equal(101, selected.TmdbId);
        Assert.True(selected.AvailabilityVerified);
    }

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
        var acquired = DateTime.UtcNow;
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyDetail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)method.Invoke(service, [candidate, detail, acquired, null])!);
        Assert.Equal(acquired, candidate.SeerrTitleMetadata!.FetchedAtUtc);
        Assert.Equal(0, candidate.CertificationScore);
        Assert.Equal("DE", candidate.CertificationPolicyCountry);
        Assert.Equal("US", configuration.MetadataCountryCode);
    }

    [Theory]
    [InlineData(101, true)]
    [InlineData(999, false)]
    public void CachedDetailPreservesSupplementalFieldsAndTheirOriginalAcquisitionTimes(int detailId, bool accepted)
    {
        var service = CreateService();
        var acquired = DateTime.UtcNow.AddHours(-1);
        var supplemental = new TitleMetadataSnapshot
        {
            TmdbId = 101, OriginalLanguage = "ja", Overview = "supplemental synopsis", Source = "trakt", FetchedAtUtc = acquired.AddHours(-1)
        };
        var candidate = new TmdbDiscoverItem { Id = 101, TitleMetadata = supplemental };
        var detail = new SeerrMediaDetailResponse { Id = detailId, Keywords = [new SeerrMediaDetailResponse.GenreRef { Id = 42, Name = "pirate" }] };
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var ttl = (TimeSpan)typeof(SeerrDiscoveryService).GetField("SeerrUserCacheTtl", flags)!.GetValue(null)!;
        var cacheType = typeof(SeerrDiscoveryService).GetNestedType("CachedMediaDetail", BindingFlags.NonPublic)!;
        var cached = Activator.CreateInstance(cacheType, detail, acquired.Add(ttl))!;
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyCachedDetail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(accepted, (bool)method.Invoke(service, [candidate, cached, null])!);
        if (accepted)
        {
            Assert.Equal(acquired, candidate.SeerrTitleMetadata!.FetchedAtUtc);
            Assert.Equal("supplemental synopsis", candidate.TitleMetadata!.Overview);
            Assert.Equal("ja", candidate.TitleMetadata.OriginalLanguage);
            Assert.Equal(acquired.AddHours(-1), candidate.TitleMetadata.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.Overview)]);
            Assert.Equal(acquired, candidate.TitleMetadata.FieldFetchedAtUtc[nameof(TitleMetadataSnapshot.Keywords)]);
            Assert.Equal("pirate", Assert.Single(candidate.TitleMetadata.Keywords!.Value));
        }
        else
        {
            Assert.Same(supplemental, candidate.TitleMetadata);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedPartialDetailsPreserveMatchingScopedSeerrFieldsWhileExplicitEmptyStillClears(bool empty)
    {
        var service = CreateService();
        var acquired = DateTime.UtcNow.AddHours(-1);
        var scope = TitleMetadataCache.SeerrScope(Plugin.Instance!.Configuration, "en");
        var previous = new TitleMetadataSnapshot
        {
            TmdbId = 101, Keywords = ["friendship"], Overview = "retained", Source = "seerr", ObservationScope = scope, FetchedAtUtc = acquired.AddHours(-1)
        };
        var candidate = new TmdbDiscoverItem { Id = 101, TitleMetadata = previous };
        var detail = new SeerrMediaDetailResponse { Id = 101, Keywords = empty ? [] : null };
        var ttl = (TimeSpan)typeof(SeerrDiscoveryService).GetField("SeerrUserCacheTtl", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var cacheType = typeof(SeerrDiscoveryService).GetNestedType("CachedMediaDetail", BindingFlags.NonPublic)!;
        var cached = Activator.CreateInstance(cacheType, detail, acquired.Add(ttl))!;
        var method = typeof(SeerrDiscoveryService).GetMethod("ApplyCachedDetail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)method.Invoke(service, [candidate, cached, scope])!);
        var metadata = Assert.IsType<TitleMetadataSnapshot>(candidate.TitleMetadata);
        var keywords = Assert.IsType<System.Collections.Immutable.ImmutableArray<string>>(metadata.Keywords);
        Assert.Equal(empty ? Array.Empty<string>() : ["friendship"], keywords);
        Assert.Equal("retained", metadata.Overview);
        Assert.Equal(acquired.AddHours(-1), metadata.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.Overview)]);
        Assert.Equal(scope, candidate.SeerrTitleMetadata!.ObservationScope);
        Assert.Equal(empty ? acquired : acquired.AddHours(-1), metadata.FieldFetchedAtUtc[nameof(TitleMetadataSnapshot.Keywords)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASeerrKeywordClearReplacesOnlySeerrEvidenceEvenAfterProjectionSerialization(bool independentAnime)
    {
        var service = CreateService();
        var old = DateTime.UtcNow.AddDays(-1);
        var seerr = new TitleMetadataSnapshot { TmdbId = 101, Keywords = ["anime"], Overview = "retained", Source = "seerr", FetchedAtUtc = old };
        var independent = new TitleMetadataSnapshot { TmdbId = 101, Keywords = independentAnime ? ["anime", "friendship"] : ["friendship"], Source = "trakt", FetchedAtUtc = old };
        var projected = TitleMetadataMerger.Combine([seerr, independent]);
        var restored = System.Text.Json.JsonSerializer.Deserialize<TitleMetadataSnapshot>(System.Text.Json.JsonSerializer.Serialize(projected))!;
        var candidate = new TmdbDiscoverItem { Id = 101, TitleMetadata = restored };
        var detail = new SeerrMediaDetailResponse { Id = 101, Keywords = [] };
        var acquired = DateTime.UtcNow;
        Assert.True((bool)typeof(SeerrDiscoveryService).GetMethod("ApplyDetail", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [candidate, detail, acquired, null])!);
        Assert.Equal(acquired, candidate.SeerrTitleMetadata!.FetchedAtUtc);
        Assert.Empty(candidate.KnownKeywords!);
        var metadata = Assert.IsType<TitleMetadataSnapshot>(candidate.TitleMetadata);
        var keywords = Assert.IsType<System.Collections.Immutable.ImmutableArray<string>>(metadata.Keywords);
        Assert.Equal(independentAnime, keywords.Contains("anime"));
        Assert.Contains("friendship", keywords);
        Assert.Equal("retained", metadata.Overview);
        Assert.Equal(old, metadata.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.Overview)]);
        Assert.All(metadata.SourceObservations!.Value, observation => Assert.Null(observation.SourceObservations));
    }

    [Fact]
    public void ApplyingSharedMetadataCannotReintroduceExpiredCandidateObservationFields()
    {
        var now = DateTime.UtcNow;
        var scope = TitleMetadataCache.SeerrScope(Plugin.Instance!.Configuration, null);
        var stale = new TitleMetadataSnapshot
        {
            TmdbId = 101, Keywords = ["anime"], GenreIds = [16], FetchedAtUtc = now.AddDays(-1), ObservationScope = scope,
            FieldFetchedAtUtc = System.Collections.Immutable.ImmutableDictionary<string, DateTime>.Empty.Add(nameof(TitleMetadataSnapshot.Keywords), now.AddDays(-31))
        };
        var fresh = stale with { Keywords = null };
        var candidate = new TmdbDiscoverItem { Id = 101, TitleMetadata = stale, KnownKeywords = ["anime"] };
        typeof(SeerrDiscoveryService).GetMethod("ApplyCachedSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [candidate, fresh]);
        Assert.Null(candidate.TitleMetadata!.Keywords);
        Assert.Equal(new[] { 16 }, candidate.GenreIds);
        Assert.Null(candidate.KnownKeywords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactTypedTmdbMetadataSurvivesAConflictingCandidateAlias(bool conflicts)
    {
        var cache = new TitleMetadataCache();
        var service = CreateService(titleMetadataCache: cache);
        var config = Plugin.Instance!.Configuration;
        cache.Store(config, new TitleMetadataSnapshot { TmdbId = 77, MediaType = "tv", TvdbId = 22, GenreIds = [16], Keywords = ["pirate"], FetchedAtUtc = DateTime.UtcNow.AddMinutes(-2) });
        var candidate = new TmdbDiscoverItem
        {
            Id = 77, MediaType = "tv", SourceGenres = ["Drama"], TitleMetadata = new TitleMetadataSnapshot { TmdbId = 77, MediaType = "tv", TvdbId = conflicts ? 23 : 22, Keywords = ["friendship"], SourceGenres = ["Kids"], Source = "trakt", FetchedAtUtc = DateTime.UtcNow.AddMinutes(-1) }
        };
        candidate.ProjectSourceGenres(["Kids"]);
        typeof(SeerrDiscoveryService).GetMethod("ApplySharedCandidateMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [candidate, config, null]);
        Assert.Equal(22, candidate.TitleMetadata!.TvdbId);
        Assert.Contains("pirate", candidate.TitleMetadata.Keywords!.Value);
        Assert.Equal(!conflicts, candidate.TitleMetadata.Keywords.Value.Contains("friendship"));
        Assert.Contains("Drama", candidate.SourceGenres!);
        Assert.Equal(!conflicts, candidate.SourceGenres!.Contains("Kids"));
        Assert.Contains(16, candidate.GenreIds);
    }

    [Fact]
    public void ACompatibleAliasStillSuppliesMetadataWithoutAnExactTmdbObservation()
    {
        var cache = new TitleMetadataCache();
        var service = CreateService(titleMetadataCache: cache);
        var config = Plugin.Instance!.Configuration;
        config.SonarrInstances = [new Jellyfin.Plugin.JellyfinHelper.Configuration.ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "test-key" }];
        Assert.NotNull(cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 0, MediaType = "tv", TvdbId = 22, GenreIds = [16], Keywords = ["pirate"], FetchedAtUtc = DateTime.UtcNow.AddMinutes(-1) }, new MetadataSourceScope("sonarr", "https://sonarr.example.test", "test-key"), null));
        Assert.Null(cache.GetForLanguage(config, 77, "tv", 0, null, null));
        var candidate = new TmdbDiscoverItem { Id = 77, MediaType = "tv", TitleMetadata = new TitleMetadataSnapshot { TmdbId = 77, MediaType = "tv", TvdbId = 22, FetchedAtUtc = DateTime.UtcNow } };
        typeof(SeerrDiscoveryService).GetMethod("ApplySharedCandidateMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [candidate, config, null]);
        Assert.Contains("pirate", candidate.TitleMetadata!.Keywords!.Value);
        Assert.Contains(16, candidate.GenreIds);
    }

    [Theory]
    [InlineData("omitted", false)]
    [InlineData("clear", false)]
    [InlineData("expired", false)]
    [InlineData("omitted", true)]
    [InlineData("clear", true)]
    [InlineData("expired", true)]
    public void OmittedSeerrKeywordsUseCurrentIndependentEvidenceButAnExplicitClearRemainsAuthoritative(string field, bool detailPath)
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var scope = TitleMetadataCache.SeerrScope(Plugin.Instance!.Configuration, null);
        var independent = new TitleMetadataSnapshot { TmdbId = 101, Keywords = ["pirate"], Source = "trakt", FetchedAtUtc = now.AddMinutes(-1) };
        var seerr = new TitleMetadataSnapshot
        {
            TmdbId = 101, GenreIds = [16], Source = "seerr", ObservationScope = scope, FetchedAtUtc = now.AddMinutes(-1),
            Keywords = field == "clear" ? [] : field == "expired" ? ["expired Seerr term"] : null,
            FieldFetchedAtUtc = field == "expired" ? System.Collections.Immutable.ImmutableDictionary<string, DateTime>.Empty.Add(nameof(TitleMetadataSnapshot.Keywords), now.AddDays(-31)) : null
        };
        var combined = TitleMetadataMerger.Combine([independent, seerr]);
        var candidate = new TmdbDiscoverItem { Id = 101, TitleMetadata = combined, KnownKeywords = ["expired Seerr term"] };
        if (detailPath)
        {
            var detail = new SeerrMediaDetailResponse { Id = 101, Genres = [new() { Id = 16, Name = "Animation" }], Keywords = field == "clear" ? [] : null };
            Assert.True((bool)typeof(SeerrDiscoveryService).GetMethod("ApplyDetail", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [candidate, detail, now, scope])!);
        }
        else
        {
            typeof(SeerrDiscoveryService).GetMethod("ApplyCachedSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [candidate, combined]);
        }

        Assert.Equal(field == "clear" ? [] : new[] { "pirate" }, candidate.KnownKeywords);
        Assert.Contains("pirate", candidate.TitleMetadata!.Keywords!.Value);
        Assert.DoesNotContain("expired Seerr term", candidate.KnownKeywords!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASharedKeywordClearReprojectsAnAlreadyEnrichedCandidateEvenWithoutIncomingGenres(bool keywordsOnly)
    {
        var acquired = DateTime.UtcNow.AddHours(-1);
        var scope = TitleMetadataCache.SeerrScope(Plugin.Instance!.Configuration, null);
        var previous = new TitleMetadataSnapshot { TmdbId = 101, GenreIds = [16], Keywords = ["anime", "pirate"], Source = "seerr", ObservationScope = scope, FetchedAtUtc = acquired };
        var candidate = new TmdbDiscoverItem { Id = 101, GenreIds = [16], KnownKeywords = ["anime", "pirate"], TitleMetadata = previous, MetadataFetchAttempted = true };
        var current = previous with { GenreIds = keywordsOnly ? null : [18], Keywords = [], FetchedAtUtc = acquired.AddMinutes(30) };
        typeof(SeerrDiscoveryService).GetMethod("ApplyCachedSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [candidate, current]);
        Assert.Empty(candidate.KnownKeywords!);
        Assert.Empty(candidate.TitleMetadata!.Keywords!.Value);
        Assert.Equal(keywordsOnly ? new[] { 16 } : [18], candidate.GenreIds);
    }

    [Fact]
    public void ANewerSharedStoreWinnerProjectsItsGenresAndKeywordsBeforeFinalScoring()
    {
        var cache = new TitleMetadataCache();
        var service = CreateService(titleMetadataCache: cache);
        var config = Plugin.Instance!.Configuration;
        var acquired = DateTime.UtcNow.AddHours(-1);
        var scope = TitleMetadataCache.SeerrScope(config, null);
        var old = new TitleMetadataSnapshot { TmdbId = 101, GenreIds = [16], Keywords = ["anime"], Source = "seerr", ObservationScope = scope, FetchedAtUtc = acquired };
        cache.Store(config, old with { GenreIds = [28], Keywords = [], FetchedAtUtc = acquired.AddMinutes(30) });
        var candidate = new TmdbDiscoverItem { Id = 101, GenreIds = [16], KnownKeywords = ["anime"], TitleMetadata = old, SeerrTitleMetadata = old };
        typeof(SeerrDiscoveryService).GetMethod("CacheSharedTitleMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [new Uri("https://seerr.example.com/"), "seerr-key", null, candidate]);
        Assert.Equal(new[] { 28 }, candidate.GenreIds);
        Assert.Empty(candidate.KnownKeywords!);
        Assert.Empty(candidate.TitleMetadata!.Keywords!.Value);
        Assert.Equal(acquired.AddMinutes(30), candidate.SeerrTitleMetadata!.FetchedAtUtc);
    }

    [Fact]
    public void PublishingAnIsolatedSeerrObservationPreservesCandidateLocalMetadataWithoutCrossScopeLeakage()
    {
        var cache = new TitleMetadataCache();
        var service = CreateService(titleMetadataCache: cache);
        var acquired = DateTime.UtcNow.AddDays(-2);
        var candidate = new TmdbDiscoverItem
        {
            Id = 101, TitleMetadata = new TitleMetadataSnapshot { TmdbId = 101, OriginalLanguage = "ja", Overview = "external synopsis", Source = "trakt", FetchedAtUtc = acquired }
        };
        var detail = new SeerrMediaDetailResponse { Id = 101, Keywords = [new SeerrMediaDetailResponse.GenreRef { Id = 42, Name = "pirate" }] };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var fetched = DateTime.UtcNow;
        Assert.True((bool)typeof(SeerrDiscoveryService).GetMethod("ApplyDetail", flags)!.Invoke(service, [candidate, detail, fetched, null])!);
        Assert.Equal(fetched, candidate.SeerrTitleMetadata!.FetchedAtUtc);
        typeof(SeerrDiscoveryService).GetMethod("CacheSharedTitleMetadata", flags)!.Invoke(service, [new Uri("https://seerr.example.com/"), "seerr-key", null, candidate]);
        Assert.Equal("ja", candidate.TitleMetadata!.OriginalLanguage);
        Assert.Equal("external synopsis", candidate.TitleMetadata.Overview);
        Assert.Equal(acquired, candidate.TitleMetadata.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.Overview)]);
        Assert.Equal("pirate", Assert.Single(candidate.TitleMetadata.Keywords!.Value));
        Assert.Null(candidate.SeerrTitleMetadata!.Overview);
        Assert.Null(candidate.SeerrTitleMetadata.OriginalLanguage);
        var stored = cache.Get(Plugin.Instance!.Configuration, 101, "movie")!;
        Assert.Null(stored.Overview);
        Assert.Null(stored.OriginalLanguage);
        Assert.Equal("seerr", stored.Source);
        Assert.Equal("pirate", Assert.Single(stored.Keywords!.Value));
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
    public async Task ProviderGenreAliasRefreshRebuildsSemanticAudienceEvidenceBeforeFinalScoring()
    {
        var user = Guid.NewGuid();
        var alias = "animation alias " + Guid.NewGuid().ToString("N");
        var profile = new UserWatchProfile
        {
            UserId = user,
            WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = [alias], Tags = ["shounen", "pirate"] }]
        };
        _history.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        var before = GenreNormalizer.CatalogVersion;
        _detailById[101] = $$"""{"id":101,"genres":[{"id":16,"name":"{{alias}}"}],"keywords":[{"name":"preschool"},{"name":"children's television"}]}""";
        var result = await CreateService().ScoreExternalCandidatesAsync(user, [Candidate(101)], TestContext.Current.CancellationToken);
        var delivered = Assert.Single(result!.Recommendations).ScoringFeatures!;
        Assert.True(GenreNormalizer.CatalogVersion > before);
        var genres = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var baseline = SimilarityComputer.ComputeGenreSimilarity(["Animation"], genres);
        var expected = ContentFitProfile.Create(profile).AdjustGenreSimilarity(baseline, ["Animation"], ["preschool", "children's television"], null, genres);
        Assert.Equal(expected, delivered.GenreSimilarity, 12);
        Assert.Equal(baseline * .3, expected, 12);
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
        if (signal == "studio")
        {
            row.Studios = ["Studio A"];
            row.TitleMetadata = new TitleMetadataSnapshot
            {
                MediaType = "movie", FetchedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                ProductionCompanies = [new("tmdb", "production-company", 1, "Studio A")]
            };
        }
        if (signal == "tag") { row.Tags = ["own tag"]; }
        if (signal == "person")
        {
            profile.PeopleProfile["Director A"] = 5;
            row.Credits = new CreditMetadataSnapshot
            {
                SourceItemId = row.ItemId,
                Cast = new CreditField { Complete = true, AcquiredAtUtc = DateTime.UtcNow.AddMinutes(-1), People = [] },
                Crew = new CreditField { Complete = true, AcquiredAtUtc = DateTime.UtcNow.AddMinutes(-1), People = [new() { Name = "Director A", Kind = Jellyfin.Data.Enums.PersonKind.Director }] }
            };
            for (var index = 0; index < 4; index++)
            {
                var id = Guid.NewGuid();
                profile.WatchedItems.Add(new WatchedItemInfo { ItemId = id, ItemType = "Movie", Played = true, Credits = row.Credits with { SourceItemId = id } });
            }
        }
        _history.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        _detailById[101] = """{"id":101,"collection":{"id":123,"name":"Actual saga"},"keywords":[{"name":"own tag"}],"productionCompanies":[{"id":1,"name":"Studio A"}],"credits":{"crew":[{"name":"Director A","job":"Director"}]}}""";
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
        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(14), "Trakt's optional pass must share the new fifteen-second per-user deadline.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(26), "The fifteen-second optional pass and separate eight-second status check must not become a forty-candidate pre-enrichment pass.");
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
        profile.WatchedItems[0].TitleMetadata = new TitleMetadataSnapshot
        {
            MediaType = "movie", FetchedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ProductionCompanies = [new("tmdb", "production-company", 1, "Studio A")]
        };
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
    public async Task SecondScoring_ReusesArrOwnershipButRechecksCurrentLibraryIdentities()
    {
        var userId = Guid.NewGuid();
        SetupProfile(userId);
        Plugin.Instance!.Configuration.RadarrInstances = [new Jellyfin.Plugin.JellyfinHelper.Configuration.ArrInstanceConfig { Url = "https://radarr.example.test", ApiKey = "test" }];
        Mock<IArrIntegrationService>? arr = null;
        var service = CreateService(configureArr: mock =>
        {
            arr = mock;
            mock.Setup(value => value.GetRadarrMoviesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        });

        await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], CancellationToken.None);
        await service.ScoreExternalCandidatesAsync(userId, [Candidate(101)], CancellationToken.None);

        // Only ARR ownership is cached. Each pass resolves current physical aliases.
        arr!.Verify(value => value.GetRadarrMoviesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _libraryManager.Verify(lm => lm.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Movie) && query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Series))), Times.Exactly(2));
    }

    [Fact]
    public async Task SourceOnlyGenreIsSharedByActualScoringDisplayAndReasonSelection()
    {
        var user = Guid.NewGuid();
        _history.Setup(history => history.GetUserWatchProfile(user)).Returns(new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Animation"] }] });
        var service = CreateService();
        var result = await service.ScoreExternalCandidatesAsync(user, [new ExternalDiscoveryCandidate { TmdbId = 101, MediaType = "movie", SourceGenres = ["Animation"], VoteAverage = 7.5, Title = "Source only" }], TestContext.Current.CancellationToken);
        var item = Assert.Single(result!.Recommendations);
        Assert.Equal("reasonGenre", item.ReasonKey);
        Assert.Equal("Animation", item.RelatedInfo);
        Assert.Contains("Animation", item.Genres);
        Assert.True(item.ScoringFeatures!.GenreSimilarity > .7);
    }

    [Fact]
    public async Task SupplementalAliasMetadataReachesAnAnchoredTraktCandidateEvenWhenSeerrOmitsTheAlias()
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var config = Plugin.Instance!.Configuration;
        config.SonarrInstances = [new() { Url = "https://sonarr.example.test", ApiKey = "key" }];
        var metadata = new TitleMetadataCache();
        metadata.StoreSupplemental(config, new TitleMetadataSnapshot { TvdbId = 456, MediaType = "tv", AnimeHint = true, FetchedAtUtc = DateTime.UtcNow }, new("sonarr", "https://sonarr.example.test", "key"), null);
        var candidate = new ExternalDiscoveryCandidate { TmdbId = 123, MediaType = "tv", Title = "Anchored", VoteAverage = 7.5, SourceGenres = ["Animation"], TitleMetadata = new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", FetchedAtUtc = DateTime.UtcNow, Source = "trakt" } };
        var result = await CreateService(titleMetadataCache: metadata).ScoreExternalCandidatesAsync(user, [candidate], TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(result!.Recommendations).TitleMetadata!.AnimeHint);
    }

    [Fact]
    public void LateOlderDetailResponseCannotOverwriteANewerCachedSynopsis()
    {
        var service = CreateService();
        var cache = typeof(SeerrDiscoveryService).GetMethod("CacheDetail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var read = typeof(SeerrDiscoveryService).GetMethod("CachedDetail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var now = DateTime.UtcNow;
        cache.Invoke(service, ["same-key", new SeerrMediaDetailResponse { Id = 100, Overview = "new" }, now]);
        cache.Invoke(service, ["same-key", new SeerrMediaDetailResponse { Id = 100, Overview = "old" }, now.AddMinutes(-1)]);
        var entry = read.Invoke(service, ["same-key"])!;
        var detail = (SeerrMediaDetailResponse)entry.GetType().GetProperty("Detail")!.GetValue(entry)!;
        Assert.Equal("new", detail.Overview);
        cache.Invoke(service, ["same-key", new SeerrMediaDetailResponse { Id = 100, Overview = "newer" }, now.AddSeconds(1)]);
        entry = read.Invoke(service, ["same-key"])!;
        Assert.Equal("newer", ((SeerrMediaDetailResponse)entry!.GetType().GetProperty("Detail")!.GetValue(entry)!).Overview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedDeliveryDropsNewlyOwnedMediaUsingCurrentTypedAliasesAndNeverTreatsStubsAsOwned(bool aliasOnly)
    {
        var user = Guid.NewGuid();
        SetupProfile(user);
        var titleCache = new TitleMetadataCache();
        titleCache.Store(Plugin.Instance!.Configuration, new TitleMetadataSnapshot { TmdbId = 700, TvdbId = 800, MediaType = "tv", FetchedAtUtc = DateTime.UtcNow });
        var owned = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Path = "/media/admin-excluded/owned", ProviderIds = aliasOnly ? new() { ["Tvdb"] = "800" } : new() { ["Tmdb"] = "700" } };
        var stub = new Movie { Id = Guid.NewGuid(), Path = "/data/plugins/JellyNext/stub.mkv", IsVirtualItem = true, ProviderIds = new() { ["Tmdb"] = "700" } };
        var acquired = false;
        var service = CreateService(titleMetadataCache: titleCache);
        _libraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => acquired ? [owned, stub] : []);
        var saved = new DiscoveryResult { UserId = user, GeneratedAt = DateTime.UtcNow.AddDays(-1), Recommendations = [new() { TmdbId = 700, MediaType = "tv" }, new() { TmdbId = 700, MediaType = "movie" }, new() { TmdbId = 701, MediaType = "tv" }] };
        Assert.Equal(3, service.FilterConsumedItems(user, saved).Recommendations.Count);
        acquired = true;
        var delivered = service.FilterConsumedItems(user, saved);
        Assert.DoesNotContain(delivered.Recommendations, row => row.TmdbId == 700 && row.MediaType == "tv");
        Assert.Contains(delivered.Recommendations, row => row.TmdbId == 700 && row.MediaType == "movie");
        Assert.Contains(delivered.Recommendations, row => row.TmdbId == 701);
        Assert.Equal(3, saved.Recommendations.Count);
        Assert.Equal(saved.GeneratedAt, delivered.GeneratedAt);
        Assert.Equal(0, _detailCalls);
        _libraryManager.Verify(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.HasAnyProviderIds != null && query.HasAnyProviderIds.ContainsKey("Tmdb")
            && (!aliasOnly || (query.HasAnyProviderIds.ContainsKey("Tvdb") && query.HasAnyProviderIds["Tvdb"].Contains("800"))))), Times.AtLeastOnce);
    }

    [Fact]
    public void AConfirmedRequestLeavesCachedDiscoveryWhileItsFeedbackReceiptIsStillPending()
    {
        var user = Guid.NewGuid();
        var service = CreateService(configureFeedback: store => store.Setup(value => value.GetPendingRequestedItems(user)).Returns(new HashSet<(int, string)> { (700, "tv") }));
        var saved = new DiscoveryResult { UserId = user, Recommendations = [new() { TmdbId = 700, MediaType = "tv" }, new() { TmdbId = 700, MediaType = "movie" }] };
        var delivered = service.FilterConsumedItems(user, saved);
        Assert.Equal("movie", Assert.Single(delivered.Recommendations).MediaType);
        Assert.Equal(2, saved.Recommendations.Count);
        Assert.Equal(0, _detailCalls);
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
