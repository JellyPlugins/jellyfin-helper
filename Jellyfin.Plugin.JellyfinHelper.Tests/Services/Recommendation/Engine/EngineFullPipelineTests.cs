using System.Collections.ObjectModel;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     End-to-end pipeline tests that drive the FULL recommendation Engine through cold-start and warm code paths.
/// </summary>
public sealed class EngineFullPipelineTests
{
    [Fact]
    public void EngineScoringAndCapturedTrainingMetadataUseCachedCreditsWhenNativePeopleAreUnavailable()
    {
        var metadata = new TitleMetadataCache();
        var harness = EngineTestFactory.Create(titleMetadataCache: metadata);
        using var engine = harness.Engine;
        var item = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "77" }, Path = "/media/scoped.mkv", CommunityRating = 8, Genres = ["Drama"] };
        var acquired = DateTime.UtcNow.AddHours(-1);
        metadata.Store(harness.ConfigService.Object.GetConfiguration(), TitleMetadataSnapshot.FromDetail(new SeerrMediaDetailResponse
        {
            Id = 77, Credits = new SeerrCredits { Cast = [new SeerrCastMember { Id = 1, Name = "Actor", Order = 0 }], Crew = [] }
        }, "movie", acquired));
        harness.LibraryManager.Setup(manager => manager.GetPeople(item)).Throws(new IOException("native credit lookup unavailable"));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var captured = (CreditMetadataSnapshot)typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine).GetMethod("CaptureCreditMetadata", flags)!.Invoke(engine, [item])!;
        Assert.Equal("Actor", Assert.Single(captured.People()).Name);
        Assert.Equal(acquired, captured.Cast!.AcquiredAtUtc);
        Assert.Equal(item.Id, captured.SourceItemId);
        var content = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine).GetMethod("ResolveContentAffinity", flags)!.Invoke(engine, [item])!;
        var served = Assert.IsType<CreditMetadataSnapshot>(content.GetType().GetProperty("Credits")!.GetValue(content));
        Assert.Equal(captured.Cast, served.Cast);
        Assert.Empty(item.ProviderIds.Where(pair => pair.Key != "Tmdb"));
    }
    [Fact]
    public void BatchRosterRemovalExcludesTheStaleProfileFromBothResultsAndCommunityPopularity()
    {
        var harness = EngineTestFactory.Create();
        using var engine = harness.Engine;
        var active = new UserWatchProfile { UserId = Guid.NewGuid() };
        var first = new Movie { Id = Guid.NewGuid(), Path = "/media/first.mkv", CommunityRating = 8, DateCreated = DateTime.UtcNow.AddDays(-10) };
        var second = new Movie { Id = Guid.NewGuid(), Path = "/media/second.mkv", CommunityRating = 8, DateCreated = first.DateCreated };
        var removed = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [new() { ItemId = first.Id, ItemType = "Movie", Played = true }] };
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([active, removed]);
        harness.WatchHistory.Setup(history => history.GetAllUserIds()).Returns([active.UserId]);
        harness.LibraryManager.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? [first, second] : []);
        var result = Assert.Single(engine.GetAllRecommendations(2, TestContext.Current.CancellationToken));
        Assert.Equal(active.UserId, result.UserId);
        Assert.Equal(result.Recommendations[0].Score, result.Recommendations[1].Score, 10);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CohortWatchRatesExcludeRatingOnlyFeedbackButKeepPlaybackAndTheExplicitFavoriteProxy(bool played, bool favorite)
    {
        using var ensemble = new EnsembleScoringStrategy();
        var harness = EngineTestFactory.Create(ensemble);
        using var engine = harness.Engine;
        var control = Guid.NewGuid();
        var high = Guid.NewGuid();
        var controlIds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var highIds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = control },
            new() { UserId = high, WatchedItems = new Collection<WatchedItemInfo>(highIds.Select(id => new WatchedItemInfo { ItemId = id, ItemType = "Movie", UserRating = 2, Played = played, IsFavorite = favorite }).ToList()) }
        };
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns(profiles);
        harness.WatchHistory.Setup(history => history.GetAllUserIds()).Returns([control, high]);
        RecommendationResult[] previous =
        [
            new() { UserId = control, Cohort = "control", Recommendations = new Collection<RecommendedItem>(controlIds.Select(id => new RecommendedItem { ItemId = id }).ToList()) },
            new() { UserId = high, Cohort = "explore-high", Recommendations = new Collection<RecommendedItem>(highIds.Select(id => new RecommendedItem { ItemId = id }).ToList()) }
        ];
        typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine).GetMethod("ApplyGlobalCohortFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [previous]);
        Assert.Equal(played || favorite ? -EnsembleScoringStrategy.MidpointAdaptationStep : 0, ensemble.SigmoidMidpointOffset, 10);
    }

    private static Movie MakeMovie(
        string name,
        int? productionYear = 2020,
        string[]? genres = null,
        float? communityRating = 7.5f)
    {
        return new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = $"/media/movies/{Guid.NewGuid():N}.mkv",
            ProductionYear = productionYear,
            Genres = genres ?? ["Action", "Drama"],
            CommunityRating = communityRating,
            PremiereDate = productionYear.HasValue
                ? new DateTime(productionYear.Value, 6, 1, 0, 0, 0, DateTimeKind.Utc)
                : null,
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };
    }

    private static UserWatchProfile MakeWarmProfile(Guid userId, string userName, Guid watchedItemId)
    {
        return new UserWatchProfile
        {
            UserId = userId,
            UserName = userName,
            WatchedMovieCount = 1,
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = watchedItemId,
                    Name = "Already Watched",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 2,
                    Genres = new List<string> { "Action", "Drama" }
                }
            }
        };
    }

    private static void WireLibrary(EngineTestFactory.EngineHarness harness, List<BaseItem> movies)
    {
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(movies);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns([]);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns([]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoritativeMembership_CorroboratesSelfValuedIdInTheLiveSnapshot(bool coldStart)
    {
        var linked = new Mock<ILinkedChildrenService>();
        var harness = EngineTestFactory.Create(linkedChildren: linked.Object);
        var user = Guid.NewGuid();
        var watched = MakeMovie("Completed franchise entry", genres: ["Adventure"]);
        var unseen = MakeMovie("Next franchise entry", genres: ["Adventure"]);
        unseen.ProviderIds["Tmdb"] = "123";
        unseen.ProviderIds["TmdbCollection"] = "123";
        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
        linked.Setup(service => service.GetLinkedChildrenIds(collection.Id, (int)LinkedChildType.Manual)).Returns([unseen.Id]);
        var profile = MakeWarmProfile(user, "anonymous", watched.Id);
        profile.WatchedItems[0].TmdbCollectionId = 123;
        if (coldStart) profile.WatchedItems.Clear();
        WireLibrary(harness, [watched, unseen]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet)))).Returns([collection]);
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        var recommendation = Assert.Single(harness.Engine.GetRecommendations(user, cancellationToken: TestContext.Current.CancellationToken)!.Recommendations, item => item.ItemId == unseen.Id);
        Assert.Equal(123, recommendation.TmdbCollectionId);
        if (!coldStart) Assert.Equal(1, recommendation.ScoringFeatures!.FranchiseAffinity);
        Assert.Equal([collection.Id], recommendation.BoxSetIds);
        Assert.Empty(collection.LinkedChildren);
        linked.Verify(service => service.GetLinkedChildrenIds(collection.Id, (int)LinkedChildType.Manual), Times.Once);
    }

    [Fact]
    public void FranchiseIdentity_ReachesLibraryFeaturesWithoutNamesOrAnOptionalBoxsetLibrary()
    {
        var harness = EngineTestFactory.Create();
        var user = Guid.NewGuid();
        var watched = MakeMovie("Completed franchise entry", genres: ["Adventure"]);
        watched.ProviderIds["Tmdb"] = "45";
        watched.ProviderIds["TmdbCollection"] = "123";
        var unseen = MakeMovie("Next franchise entry", genres: ["Adventure"]);
        unseen.ProviderIds["Tmdb"] = "46";
        unseen.ProviderIds["TmdbCollection"] = "123";
        var profile = MakeWarmProfile(user, "anonymous", watched.Id);
        profile.WatchedItems[0].Genres = ["Adventure"];
        profile.WatchedItems[0].TmdbCollectionId = 123;
        WireLibrary(harness, [watched, unseen]);
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        var result = harness.Engine.GetRecommendations(user, cancellationToken: TestContext.Current.CancellationToken);
        var recommendation = Assert.Single(result!.Recommendations);
        Assert.Equal(123, recommendation.TmdbCollectionId);
        Assert.NotNull(recommendation.ScoringFeatures);
        Assert.Equal(1, recommendation.ScoringFeatures.FranchiseAffinity);
        Assert.Equal(0, recommendation.ScoringFeatures.CollectionProgressionBoost);
    }

    [Fact]
    public void FailedCollectionQuery_DoesNotEraseUnrelatedLibraryRecommendations()
    {
        var harness = EngineTestFactory.Create();
        var user = Guid.NewGuid();
        var watched = MakeMovie("Watched", genres: ["Adventure"]);
        var unseen = MakeMovie("Unseen", genres: ["Adventure"]);
        WireLibrary(harness, [watched, unseen]);
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(MakeWarmProfile(user, "anonymous", watched.Id));
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet)))).Throws(new InvalidOperationException("unavailable"));
        var result = harness.Engine.GetRecommendations(user, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(unseen.Id, Assert.Single(result!.Recommendations).ItemId);
    }

    [Fact]
    public void LinkedCollections_ReachLiveRecommendationsWithoutChangingFilesystemParents()
    {
        var harness = EngineTestFactory.Create();
        var user = Guid.NewGuid();
        var watched = MakeMovie("Completed entry", genres: ["Abenteuer"]);
        var unseen = MakeMovie("Next entry", genres: ["Adventure"]);
        var boxset = new BoxSet { Id = Guid.NewGuid(), LinkedChildren = [LinkedChild.Create(watched), LinkedChild.Create(unseen)] };
        WireLibrary(harness, [watched, unseen]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet)))).Returns([boxset]);
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(MakeWarmProfile(user, "anonymous", watched.Id));
        var result = harness.Engine.GetRecommendations(user, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal(unseen.Id, recommendation.ItemId);
        Assert.Contains(boxset.Id, recommendation.BoxSetIds);
    }

    [Theory]
    [InlineData(false, .3)]
    [InlineData(true, .5)]
    public void CanonicalCollectionProgressionUsesTitlesAndUnionsVerifiedBoxSetAliases(bool splitAliases, double expected)
    {
        var harness = EngineTestFactory.Create();
        using var engine = harness.Engine;
        var user = Guid.NewGuid();
        var first = MakeMovie("First physical copy");
        var copy = MakeMovie("Another physical copy");
        var target = MakeMovie("Unseen sibling");
        first.ProviderIds["Tmdb"] = "701";
        copy.ProviderIds["Tmdb"] = splitAliases ? "702" : "701";
        target.ProviderIds["Tmdb"] = "703";
        var firstBox = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "991" }, LinkedChildren = [LinkedChild.Create(first), LinkedChild.Create(target)] };
        var secondBox = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "991" }, LinkedChildren = [LinkedChild.Create(copy), LinkedChild.Create(target)] };
        var profile = new UserWatchProfile
        {
            UserId = user,
            WatchedItems =
            [
                new() { ItemId = first.Id, ItemType = "Movie", TmdbId = 701, Played = true, Genres = ["Action"] },
                new() { ItemId = copy.Id, ItemType = "Movie", TmdbId = splitAliases ? 702 : 701, Played = true, Genres = ["Action"] }
            ]
        };
        if (!splitAliases) firstBox.LinkedChildren = [LinkedChild.Create(first), LinkedChild.Create(copy), LinkedChild.Create(target)];
        WireLibrary(harness, [first, copy, target]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet)))).Returns(splitAliases ? [firstBox, secondBox] : [firstBox]);
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        var recommendation = Assert.Single(engine.GetRecommendations(user, cancellationToken: TestContext.Current.CancellationToken)!.Recommendations);
        Assert.Equal(target.Id, recommendation.ItemId);
        Assert.Equal(expected, recommendation.ScoringFeatures!.CollectionProgressionBoost, 12);
        Assert.Equal(41, recommendation.ScoringFeatures.ToVector().Length);
        Assert.Equal(991, recommendation.TmdbCollectionId);
    }

    [Fact]
    public void CanonicalCollectionCaptureSharesDetachedProviderMembershipWithNativeFallbackAndTraining()
    {
        var harness = EngineTestFactory.Create();
        using var engine = harness.Engine;
        var missing = MakeMovie("Missing collection metadata");
        missing.ProviderIds["Tmdb"] = "701";
        var direct = MakeMovie("Already verified metadata");
        direct.ProviderIds["TmdbCollection"] = "992";
        var box = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "991" }, LinkedChildren = [LinkedChild.Create(missing)] };
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet)))).Returns([box]);
        var metadata = engine.BuildTrainingLibraryMetadata([missing, direct], []);
        Assert.Equal(991, metadata.TmdbCollectionIds![missing.Id]);
        Assert.Equal(992, metadata.TmdbCollectionIds[direct.Id]);
        Assert.Equal([box.Id], metadata.BoxSetIds[missing.Id]);
        Assert.Equal(991, metadata.BoxSetProviderIds![box.Id]);
        Assert.True(metadata.BoxSetMembershipsComplete);
        var fallback = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine).GetMethod("LoadCollectionIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var ids = (Dictionary<Guid, int>)fallback.Invoke(engine, [new BaseItem[] { missing, direct }])!;
        Assert.Equal(991, ids[missing.Id]);
        Assert.Equal(992, ids[direct.Id]);
        box.ProviderIds["Tmdb"] = "993";
        box.LinkedChildren = [];
        Assert.Equal(991, metadata.TmdbCollectionIds[missing.Id]);
        Assert.Equal(991, metadata.BoxSetProviderIds[box.Id]);
        Assert.Equal([box.Id], metadata.BoxSetIds[missing.Id]);
    }

    [Fact]
    public void GetRecommendations_ColdStartUser_EmptyLibrary_ReturnsResultWithEmptyRecommendations()
    {
        // BUG GUARD: cold-start on an empty library must not throw and must produce a
        // valid - if empty - RecommendationResult. Entry test for GenerateColdStartRecommendations.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "newbie", WatchedItems = [] });

        var result = harness.Engine.GetRecommendations(userId, 5, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.Equal("newbie", result.UserName);
        Assert.Empty(result.Recommendations);
        Assert.Equal("strategyColdStart", result.ScoringStrategyKey);
    }

    [Fact]
    public void GetRecommendations_ColdStart_UnratedItem_TiesKnownMediocre_ButOutranksTrash()
    {
        // REGRESSION GUARD: the cold-start scalar formula must NOT let a fully-unrated candidate outrank a candidate the community explicitly rated poorly.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "newbie", WatchedItems = [] });

        var mediocre = MakeMovie("Known Mediocre", 2020, ["Action"], communityRating: 3.0f);
        var trash = MakeMovie("Known Trash", 2020, ["Action"], communityRating: 2.0f);
        var unrated = MakeMovie("Unknown Quality", 2020, ["Action"], communityRating: null);
        // Ensure the "unrated" item truly has NEITHER rating; rated items carry only their community score.
        unrated.CriticRating = null;
        mediocre.CriticRating = null;
        trash.CriticRating = null;

        WireLibrary(harness, [mediocre, trash, unrated]);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("strategyColdStart", result!.ScoringStrategyKey);
        Assert.NotEmpty(result.Recommendations);

        var mediocreRec = result.Recommendations.FirstOrDefault(r => r.ItemId == mediocre.Id);
        var trashRec = result.Recommendations.FirstOrDefault(r => r.ItemId == trash.Id);
        var unratedRec = result.Recommendations.FirstOrDefault(r => r.ItemId == unrated.Id);
        Assert.NotNull(mediocreRec);
        Assert.NotNull(trashRec);
        Assert.NotNull(unratedRec);

        // Upper boundary: unrated ties a 3/10 exactly (0.30 prior maps to the same rating term as
        // community 3.0/10). Asserting equality - not just "<=" - catches an over-penalizing regression.
        Assert.Equal(mediocreRec!.Score, unratedRec!.Score, 4);

        // Lower boundary: unrated strictly outranks genuinely-bad (2/10) content. Fails if the prior is pushed below the trash band (e.g.
        Assert.True(
            unratedRec.Score > trashRec!.Score,
            $"Unrated item (score={unratedRec.Score}) must strictly outrank the community-rated 2/10 item (score={trashRec.Score}).");
    }

    [Fact]
    public void GetRecommendations_ColdStartUser_WithMovies_ExecutesPipelineAndProducesValidResult()
    {
        // Drives cold-start scoring end-to-end: rating filter, combined-critic + recency, diversity reranking, RecommendedItem projection, and the graceful fallback in ResolveMediaLanguages / ResolveBoxSetIds (no streams on bare Movie() instances).
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "newbie", WatchedItems = [] });

        var movies = new List<BaseItem>
        {
            MakeMovie("Popular A", 2023, ["Action"], 8.5f),
            MakeMovie("Popular B", 2022, ["Drama"], 7.8f),
            MakeMovie("Popular C", 2021, ["Comedy"], 6.9f)
        };
        WireLibrary(harness, movies);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("strategyColdStart", result!.ScoringStrategyKey);
        // Guard against vacuous Assert.All on an empty collection: if all candidates are dropped by LoadCandidateItems the cold-start scoring pipeline (rating filter, popularity sort, RecommendedItem projection) is never exercised and the test provides false coverage.
        Assert.NotEmpty(result.Recommendations);
        Assert.All(result.Recommendations, r =>
        {
            Assert.NotEqual(Guid.Empty, r.ItemId);
            Assert.Equal("reasonPopular", r.ReasonKey);
            Assert.False(string.IsNullOrEmpty(r.Name));
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void TrainedPriorsReachColdAndWarmLiveAndBatchScoringWithoutBypassingChildSafety(bool batch, bool warm, bool restricted)
    {
        using var neural = new NeuralScoringStrategy();
        using var global = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), neural);
        Assert.True(global.TrainValidated(OperatingExamples(300, 1), OperatingExamples(100, 2), true, TestContext.Current.CancellationToken));
        Assert.True(global.GetDiagnosticsSnapshot().NeuralShare > 0);
        using var registry = new PerUserEnsembleRegistry(global, neural, null, new EnsembleBlendBounds(.3, .75, .1), Mock.Of<IPluginLogService>());
        var harness = EngineTestFactory.Create(global);
        harness.PerUserRegistry.Setup(service => service.GetScoringStrategyForUser(It.IsAny<Guid>())).Returns((Guid user) => registry.GetScoringStrategyForUser(user));
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        var profile = warm ? MakeWarmProfile(userId, "warm", watchedId) : new UserWatchProfile { UserId = userId, UserName = "cold", WatchedItems = [] };
        if (restricted) profile.MaxParentalRating = 12;
        harness.WatchHistory.Setup(service => service.GetUserWatchProfile(userId)).Returns(profile);
        harness.WatchHistory.Setup(service => service.GetAllUserWatchProfiles()).Returns(new Collection<UserWatchProfile> { profile });
        var candidates = new List<BaseItem> { MakeMovie("A"), MakeMovie("B"), MakeMovie("C") };
        WireLibrary(harness, candidates);
        var result = batch
            ? Assert.Single(harness.Engine.GetAllRecommendations(3, TestContext.Current.CancellationToken))
            : harness.Engine.GetRecommendations(userId, 3, TestContext.Current.CancellationToken)!;
        Assert.NotNull(result);
        Assert.Equal("strategyEnsemble", result.ScoringStrategyKey);
        var diagnostics = registry.GetDiagnostics(userId);
        Assert.Equal("GlobalPrior", diagnostics.ValidationLevel);
        Assert.Equal(.3, diagnostics.EffectiveAlpha, 12);
        Assert.True(diagnostics.NeuralShare > 0);
        if (restricted)
        {
            // Unknown certifications fail closed even with a trained operating prior.
            Assert.Empty(result.Recommendations);
            Assert.NotNull(result.TrainingContrasts);
            Assert.Empty(result.TrainingContrasts);
        }
        else
        {
            Assert.NotEmpty(result.Recommendations);
            Assert.All(result.Recommendations, item =>
            {
                Assert.NotNull(item.ScoringFeatures);
                // The public recommendation DTO intentionally rounds the immutable prediction to four decimals.
                Assert.Equal(Math.Round(registry.GetScoringStrategyForUser(userId).Score(item.ScoringFeatures!), 4), item.Score);
                Assert.InRange(item.Score, 0, 1);
            });
            Assert.Equal(result.Recommendations.Count, result.Recommendations.Select(item => item.ItemId).Distinct().Count());
            Assert.Equal(result.Recommendations.OrderByDescending(item => item.Score).Select(item => item.ItemId), result.Recommendations.Select(item => item.ItemId));
        }
    }

    private static TrainingExample[] OperatingExamples(int count, short partition)
        => Enumerable.Range(1, count).Select(index => new TrainingExample
        {
            Features = new CandidateFeatures { FeatureDerivationVersion = CandidateFeatures.CurrentFeatureDerivationVersion, GenreSimilarity = .8, CombinedCriticScore = .7, GenreCount = 2 },
            ObservationId = new Guid(index, partition, 0, new byte[8]),
            HasStableObservationId = true,
            Label = .85,
            GeneratedAtUtc = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }).ToArray();

    [Fact]
    public void GetRecommendations_WarmUser_WithCandidates_ReturnsScoredRecommendations()
    {
        // Drives the FULL warm path: GenerateForUser -> preference vectors -> ScoreCandidate -> DiversityReranker -> RecommendedItem projection.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();

        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(MakeWarmProfile(userId, "warm", watchedId));
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { MakeWarmProfile(userId, "warm", watchedId) });

        var candidates = new List<BaseItem>
        {
            MakeMovie("Cand 1", 2022, ["Action", "Thriller"], 8.0f),
            MakeMovie("Cand 2", 2020, ["Drama"], 7.0f),
            MakeMovie("Cand 3", 2019, ["Comedy"], 6.5f)
        };
        WireLibrary(harness, candidates);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.Equal("warm", result.UserName);
        // Cohort is populated from the mock strategy selector (default "control").
        Assert.Equal("control", result.Cohort);
        // Warm path uses the strategy passed via DI (HeuristicScoringStrategy from the
        // factory default). Its NameKey is stable across strategy-formula refactors.
        Assert.False(string.IsNullOrEmpty(result.ScoringStrategy));
        // At least one recommendation must be produced: if result.Recommendations is empty the warm scoring pipeline (GenerateForUser -> ScoreCandidate -> DiversityReranker) was never actually exercised and the test provides no coverage of those ~800 lines.
        Assert.NotEmpty(result.Recommendations);
    }

    [Fact]
    public void GetAllRecommendations_MultipleUsers_ProducesOneResultPerUser()
    {
        // Drives the parallel batch loop: LoadCandidateItems, PrecomputeUserWatchSets, PrecomputeCollaborativeContext, BuildCommunityPopularityMap, the two-user gate (activated here because we pass two users), and the Parallel.ForEach scoring branch.
        var harness = EngineTestFactory.Create();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var watched1 = Guid.NewGuid();
        var watched2 = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            MakeWarmProfile(user1, "alice", watched1),
            MakeWarmProfile(user2, "bob", watched2)
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);

        var candidates = new List<BaseItem>
        {
            MakeMovie("Batch Cand 1", 2022, ["Action"], 8.2f),
            MakeMovie("Batch Cand 2", 2021, ["Drama"], 7.4f)
        };
        WireLibrary(harness, candidates);

        var results = harness.Engine.GetAllRecommendations(5, CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void GetAllRecommendations_PlaceholderMovieAndEmptySeries_AreFilteredAndLogged()
    {
        // LoadCandidateItems must drop Arr placeholders: a Movie with no Path (file not yet downloaded) and a Series with zero indexed episodes cannot be played, so both are filtered out of the candidate pool and a single summary line is logged.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { MakeWarmProfile(userId, "user", watchedId) });

        var goodMovie = MakeMovie("Playable", 2022, ["Action"], 8.0f);
        var placeholder = MakeMovie("Not Downloaded Yet", 2022, ["Action"], 8.0f);
        placeholder.Path = null; // Arr placeholder - no media file on disk

        var emptySeries = new Series
        {
            Id = Guid.NewGuid(),
            Name = "No Episodes Yet",
            Path = "/media/series/empty",
            Genres = ["Drama"],
            ProductionYear = 2021,
            CommunityRating = 7.0f
        };

        // The Episode query returns an episode for an UNRELATED series id, so seriesEpisodeCounts
        // never contains emptySeries.Id and the series is skipped.
        var strayEpisode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesId = Guid.NewGuid(),
            Name = "Stray",
            Path = "/media/series/other/S01E01.mkv"
        };

        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(new List<BaseItem> { goodMovie, placeholder });
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(new List<BaseItem> { emptySeries });
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { strayEpisode });

        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        Assert.NotNull(results);
        // The filter summary is logged exactly once with the correct skipped counts.
        harness.PluginLog.Verify(
            p => p.LogInfo(
                It.IsAny<string>(),
                It.Is<string>(msg => msg.Contains("Filtered 1 empty movies and 1 empty series")),
                It.IsAny<Microsoft.Extensions.Logging.ILogger>()),
            Times.Once);

        // Neither the path-less movie nor the episode-less series may appear in any result.
        foreach (var r in results)
        {
            Assert.DoesNotContain(r.Recommendations, i => i.ItemId == placeholder.Id);
            Assert.DoesNotContain(r.Recommendations, i => i.ItemId == emptySeries.Id);
        }
    }

    [Fact]
    public void GetAllRecommendations_ColdStartUserWithCommunityHistory_UsesCommunityBlendedFormula()
    {
        // A cold-start user (no watch history) is scored with the enhanced 40/30/30 community formula when at least two OTHER users share watch history: the community-popularity map is non-empty, so a candidate watched by the crowd must outrank an identical candidate the crowd has never.
        var harness = EngineTestFactory.Create();

        var coldUser = Guid.NewGuid();
        var warmA = Guid.NewGuid();
        var warmB = Guid.NewGuid();

        // Two candidates with identical rating/genre/year so rating+recency terms tie; the only
        // differentiator is community popularity.
        var crowdFavorite = MakeMovie("Crowd Favorite", 2020, ["Action"], 7.0f);
        var unseenByCrowd = MakeMovie("Unseen", 2020, ["Action"], 7.0f);
        WireLibrary(harness, [crowdFavorite, unseenByCrowd]);

        UserWatchProfile MakeCrowdMember(Guid id, string name) => new()
        {
            UserId = id,
            UserName = name,
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = crowdFavorite.Id,
                    Name = "Crowd Favorite",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 1
                }
            }
        };

        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = coldUser, UserName = "cold", WatchedItems = [] },
            MakeCrowdMember(warmA, "warmA"),
            MakeCrowdMember(warmB, "warmB")
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);

        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        var coldResult = results.FirstOrDefault(r => r.UserId == coldUser);
        Assert.NotNull(coldResult);
        Assert.Equal("strategyColdStart", coldResult!.ScoringStrategyKey);

        var favoriteRec = coldResult.Recommendations.FirstOrDefault(i => i.ItemId == crowdFavorite.Id);
        var unseenRec = coldResult.Recommendations.FirstOrDefault(i => i.ItemId == unseenByCrowd.Id);
        Assert.NotNull(favoriteRec);
        Assert.NotNull(unseenRec);

        // The community-watched candidate must score strictly higher - the only signal that can
        // separate two otherwise-identical items is the 30% community-popularity term.
        Assert.True(
            favoriteRec!.Score > unseenRec!.Score,
            $"Community-watched candidate (score={favoriteRec.Score}) must outrank the crowd-unseen " +
            $"candidate (score={unseenRec.Score}) under the community-blended cold-start formula.");
    }

    [Fact]
    public void GetRecommendations_WarmUser_FavoriteSeriesAndManyCandidates_ProducesWellFormedResult()
    {
        // Warm path with a non-empty FavoriteSeriesIds set and a large candidate pool.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        var favoriteSeriesId = Guid.NewGuid();

        var profile = MakeWarmProfile(userId, "warm", watchedId);
        profile.FavoriteSeriesIds.Add(favoriteSeriesId);

        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        // A favorited series that IS a candidate must be excluded from the output.
        var favoriteSeries = new Series
        {
            Id = favoriteSeriesId,
            Name = "Favorited Show",
            Path = "/media/series/fav",
            Genres = ["Action", "Drama"],
            ProductionYear = 2020,
            CommunityRating = 8.0f
        };

        // > CancellationCheckBatchSize (200) movie candidates so the periodic ct check runs.
        var movies = new List<BaseItem>();
        for (var i = 0; i < 250; i++)
        {
            movies.Add(MakeMovie($"Cand {i}", 2015 + (i % 8), ["Action", "Drama"], 6.0f + (i % 4)));
        }

        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(movies);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(new List<BaseItem> { favoriteSeries });
        // The favorite series survives LoadCandidateItems only if an episode matches its id.
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem>
            {
                new Episode
                {
                    Id = Guid.NewGuid(),
                    SeriesId = favoriteSeriesId,
                    Name = "Ep1",
                    Path = "/media/series/fav/S01E01.mkv"
                }
            });

        var result = harness.Engine.GetRecommendations(userId, 20, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Recommendations);
        // A favorited series must never be recommended - the user already committed to it.
        Assert.DoesNotContain(result.Recommendations, i => i.ItemId == favoriteSeriesId);
    }

    [Fact]
    public void GetRecommendations_WarmUser_CalledTwice_ReusesCachedSnapshot_NoSecondLibraryScan()
    {
        // The first live request publishes a candidate snapshot; a second request within the CandidateSnapshotMaxAge TTL must take the still-valid fast-path in GetOrRefreshLiveSnapshot and reuse those candidates rather than re-scanning the library.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();

        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(MakeWarmProfile(userId, "warm", watchedId));
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { MakeWarmProfile(userId, "warm", watchedId) });

        var candidates = new List<BaseItem>
        {
            MakeMovie("Cand 1", 2022, ["Action", "Thriller"], 8.0f),
            MakeMovie("Cand 2", 2020, ["Drama"], 7.0f)
        };
        WireLibrary(harness, candidates);

        var first = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);
        var second = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(userId, first!.UserId);
        Assert.Equal(userId, second!.UserId);

        // The Movie candidate scan must have happened exactly once across BOTH calls: the second
        // call served from the still-valid TTL snapshot instead of re-scanning the library.
        harness.LibraryManager.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)),
            Times.Once);
    }

    [Fact]
    public void GetAllRecommendations_GetPeopleThrows_StillCachesMetadataAndCompletes()
    {
        // BuildCandidateContentAffinityLookup must swallow a non-fatal GetPeople failure (people=null) and still cache the five candidate metadata fields, so scoring proceeds with empty writer/billing signals.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { MakeWarmProfile(userId, "user", watchedId) });

        var candidates = new List<BaseItem>
        {
            MakeMovie("Cand 1", 2022, ["Action"], 8.0f),
            MakeMovie("Cand 2", 2021, ["Drama"], 7.0f)
        };
        WireLibrary(harness, candidates);

        // People index unreadable for every candidate: the catch-and-continue contract must hold.
        harness.LibraryManager
            .Setup(lm => lm.GetPeople(It.IsAny<BaseItem>()))
            .Throws(new InvalidOperationException("people index offline"));

        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        // Batch completes with one result for the single user - the guard absorbed the failure.
        Assert.NotNull(results);
        var resultItem = Assert.Single(results);
        Assert.Equal(userId, resultItem.UserId);
    }
}
