using System.Reflection;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Real ranking-pipeline counterprobes with synthetic v6 predictors, never private production fixtures.</summary>
public sealed class EngineContentFitTests
{
    private static readonly string[] StoryTags = ["anime", "shounen", "pirate", "friendship", "super power"];
    private static readonly string[] Genres = ["Animation", "Action & Adventure"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentSnapshotKeepsOneDetachedGenreViewWhenLibraryMetadataChanges(bool batch)
    {
        var harness = EngineTestFactory.Create();
        using var lifetime = harness.Engine;
        var genres = new[] { "Drama" };
        var candidate = new Movie { Id = Guid.NewGuid(), Genres = genres };
        harness.LibraryManager.Setup(manager => manager.GetPeople(candidate)).Callback(() =>
        {
            genres[0] = "Comedy";
            candidate.Genres = ["Comedy", "Family"];
        }).Returns([]);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object content;
        if (batch)
        {
            var lookup = (System.Collections.IDictionary)harness.Engine.GetType().GetMethod("BuildCandidateContentAffinityLookup", flags)!
                .Invoke(harness.Engine, [new List<BaseItem> { candidate }, new Dictionary<Guid, int>()])!;
            content = lookup[candidate.Id]!;
        }
        else
        {
            content = harness.Engine.GetType().GetMethod("ResolveContentAffinity", flags)!.Invoke(harness.Engine, [candidate])!;
        }

        var effective = Assert.IsAssignableFrom<IReadOnlyList<string>>(content.GetType().GetProperty("EffectiveGenres")!.GetValue(content));
        var set = Assert.IsType<HashSet<string>>(content.GetType().GetProperty("GenreSet")!.GetValue(content));
        Assert.Equal(new[] { "Drama" }, effective);
        Assert.Equal(effective.Order(StringComparer.Ordinal), set.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Comedy", "Family" }, candidate.Genres);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void ColdStartUsesCapturedProviderGenresAndMetadataWithoutReplacingLocalGenres(bool batch, bool localGenres, bool nullGenres)
    {
        var cache = new TitleMetadataCache();
        var harness = EngineTestFactory.Create(titleMetadataCache: cache);
        using var lifetime = harness.Engine;
        cache.Store(harness.ConfigService.Object.GetConfiguration(), new TitleMetadataSnapshot { TmdbId = 101, GenreIds = [16], Keywords = ["anime"], FetchedAtUtc = DateTime.UtcNow });
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        var candidate = new Movie { Id = Guid.NewGuid(), Name = "Cold start title", Path = "/media/new.mkv", Genres = localGenres ? ["Drama"] : [], CommunityRating = 8, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "101" } };
        if (nullGenres) candidate.Genres = null!;
        var originalGenres = candidate.Genres;
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(profile.UserId)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? [candidate] : []);
        var result = batch ? Assert.Single(harness.Engine.GetAllRecommendations(1, TestContext.Current.CancellationToken)) : harness.Engine.GetRecommendations(profile.UserId, 1, TestContext.Current.CancellationToken)!;
        var selected = Assert.Single(result.Recommendations);
        Assert.Equal(localGenres ? ["Drama"] : new[] { "Animation" }, selected.Genres);
        Assert.Equal("anime", Assert.Single(selected.TitleMetadata!.Keywords!.Value));
        Assert.Equal(originalGenres, candidate.Genres);
    }

    [Fact]
    public void SeriesMetadataUsesTheTvNamespaceAndItsCachedGenresDuringLiveRanking()
    {
        var cache = new TitleMetadataCache();
        var harness = EngineTestFactory.Create(titleMetadataCache: cache);
        using var lifetime = harness.Engine;
        cache.Store(harness.ConfigService.Object.GetConfiguration(), new TitleMetadataSnapshot { TmdbId = 101, MediaType = "tv", GenreIds = [16], Keywords = ["anime"], FetchedAtUtc = DateTime.UtcNow });
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedMovieCount = 1, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Animation"] }] };
        var series = new Series { Id = Guid.NewGuid(), Name = "Unseen series", Path = "/media/new-series", CommunityRating = 8, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "101" } };
        var episode = Episode(series, 0);
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(profile.UserId)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            if (query.IncludeItemTypes?.Contains(BaseItemKind.Series) == true) return [series];
            if (query.IncludeItemTypes?.Contains(BaseItemKind.Episode) == true) return [episode];
            return [];
        });
        var selected = Assert.Single(harness.Engine.GetRecommendations(profile.UserId, 1, TestContext.Current.CancellationToken)!.Recommendations);
        Assert.Equal(series.Id, selected.ItemId);
        Assert.Contains("Animation", selected.Genres);
        Assert.Equal("tv", selected.TitleMetadata!.MediaType);
        Assert.Empty(series.Genres);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveCandidateMissingFromTheSnapshotLookupUsesTheSameNetworkFreeMetadataFallback(bool unreadablePeople)
    {
        var cache = new TitleMetadataCache();
        var harness = EngineTestFactory.Create(titleMetadataCache: cache);
        using var lifetime = harness.Engine;
        cache.Store(harness.ConfigService.Object.GetConfiguration(), new TitleMetadataSnapshot { TmdbId = 101, GenreIds = [16], Keywords = ["anime"], FetchedAtUtc = DateTime.UtcNow });
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedMovieCount = 1, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Animation"] }] };
        var candidate = new Movie { Id = Guid.NewGuid(), Path = "/media/new.mkv", Name = "Late title", CommunityRating = 8, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "101" } };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(profile.UserId)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? [candidate] : []);
        Assert.Single(harness.Engine.GetRecommendations(profile.UserId, 1, TestContext.Current.CancellationToken)!.Recommendations);
        var snapshot = harness.Engine.GetType().GetField("_cachedSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Engine)!;
        var lookup = (System.Collections.IDictionary)snapshot.GetType().GetProperty("ContentAffinityLookup")!.GetValue(snapshot)!;
        lookup.Remove(candidate.Id);
        if (unreadablePeople) harness.LibraryManager.Setup(manager => manager.GetPeople(candidate)).Throws(new IOException("people unavailable"));
        var selected = Assert.Single(harness.Engine.GetRecommendations(profile.UserId, 1, TestContext.Current.CancellationToken)!.Recommendations);
        Assert.Contains("Animation", selected.Genres);
        Assert.NotNull(selected.TitleMetadata);
        Assert.True(selected.ScoringFeatures!.HasGenreEvidence);
        Assert.Empty(candidate.Genres);
    }

    [Fact]
    public void ScheduledRunsKeepTheRelevanceCoreButRotateExplorationWithoutNewFeedback()
    {
        var harness = EngineTestFactory.Create(new HeuristicScoringStrategy());
        using var lifetime = harness.Engine;
        var profile = new UserWatchProfile { UserId = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var candidates = Enumerable.Range(1, 200).Select(id => (BaseItem)new Movie { Id = Guid.Parse($"00000000-0000-0000-0001-{id:000000000000}"), Path = $"/media/{id}.mkv", Name = $"Title {id}", CommunityRating = 10 - (id * .01f) }).ToArray();
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(profile.UserId)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? candidates : []);
        var runs = Enumerable.Range(0, 3).Select(_ => Assert.Single(harness.Engine.GetAllRecommendations(20, TestContext.Current.CancellationToken)).Recommendations.Select(item => item.ItemId).ToHashSet()).ToArray();
        Assert.All(runs.Skip(1), run => Assert.InRange(run.Intersect(runs[0]).Count(), 18, 20));
        Assert.True(runs.SelectMany(run => run).Distinct().Count() > 20);
        var live = harness.Engine.GetRecommendations(profile.UserId, 20, TestContext.Current.CancellationToken)!.Recommendations.Select(item => item.ItemId).ToArray();
        Assert.Equal(live, harness.Engine.GetRecommendations(profile.UserId, 20, TestContext.Current.CancellationToken)!.Recommendations.Select(item => item.ItemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderGenresAreUsedConsistentlyWhenTheLocalCandidateHasNoGenres(bool batch)
    {
        var cache = new TitleMetadataCache();
        var harness = EngineTestFactory.Create(titleMetadataCache: cache);
        using var lifetime = harness.Engine;
        cache.Store(harness.ConfigService.Object.GetConfiguration(), new TitleMetadataSnapshot { TmdbId = 101, GenreIds = [16], Keywords = ["anime"], FetchedAtUtc = DateTime.UtcNow });
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedMovieCount = 3, GenreDistribution = new Dictionary<string, int> { ["Animation"] = 3 } };
        for (var id = 1; id <= 3; id++) profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Animation"], Tags = ["anime"] });
        var candidate = new Movie { Id = Guid.NewGuid(), Name = "Unseen anime", Path = "/media/anime.mkv", CommunityRating = 8, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "101" } };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? [candidate] : []);
        var result = batch ? Assert.Single(harness.Engine.GetAllRecommendations(1, TestContext.Current.CancellationToken)) : harness.Engine.GetRecommendations(user, 1, TestContext.Current.CancellationToken)!;
        var selected = Assert.Single(result.Recommendations);
        Assert.True(selected.ScoringFeatures!.HasGenreEvidence);
        Assert.Equal(1, selected.ScoringFeatures.GenreSimilarity);
        Assert.Contains("Animation", selected.Genres);
        Assert.Empty(candidate.Genres);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedCachedMetadataImprovesPlaylistRankingWithoutExternalCallsOrChangingRecordedVectors(bool batch)
    {
        var cache = new TitleMetadataCache();
        var harness = EngineTestFactory.Create(new HeuristicScoringStrategy(genrePenaltyFloor: 1), titleMetadataCache: cache);
        using var lifetime = harness.Engine;
        var config = harness.ConfigService.Object.GetConfiguration();
        cache.Store(config, new TitleMetadataSnapshot { TmdbId = 101, Keywords = ["anime", "pirate", "friendship"], FetchedAtUtc = DateTime.UtcNow });
        cache.Store(config, new TitleMetadataSnapshot { TmdbId = 102, Keywords = ["live action"], FetchedAtUtc = DateTime.UtcNow });
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedMovieCount = 3, GenreDistribution = new Dictionary<string, int> { ["Animation"] = 3, ["Action"] = 3 } };
        for (var id = 1; id <= 3; id++) profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(), ItemType = "Movie", TmdbId = id, Played = true, Genres = ["Animation", "Action"],
            TitleMetadata = new TitleMetadataSnapshot { Keywords = ["anime", "pirate", "friendship"], FetchedAtUtc = DateTime.UtcNow }
        });
        var supported = new Movie { Id = Guid.NewGuid(), Name = "Related anime", Path = "/media/anime.mkv", Genres = ["Animation", "Action"], CommunityRating = 7, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "101" } };
        var other = new Movie { Id = Guid.NewGuid(), Name = "Popular live action", Path = "/media/action.mkv", Genres = ["Action"], CommunityRating = 10, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "102" } };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? [supported, other] : []);
        var result = batch ? Assert.Single(harness.Engine.GetAllRecommendations(2, TestContext.Current.CancellationToken)) : harness.Engine.GetRecommendations(user, 2, TestContext.Current.CancellationToken)!;
        Assert.Equal(supported.Id, Assert.Single(result.Recommendations).ItemId);
        Assert.Equal(1, result.Recommendations[0].ScoringFeatures!.ContentFitSupport);
        Assert.NotNull(result.Recommendations[0].TitleMetadata);
        var excluded = Assert.Single(result.RankingDiagnostics!.Entries, entry => entry.ItemId == other.Id);
        Assert.Equal(.5, excluded.ContentFamilyMismatch);
        Assert.Equal("NotSelectedContentFamilyMismatch", excluded.SelectionStage);
        var saved = result.Recommendations[0].ScoringFeatures!.Copy();
        cache.Store(config, new TitleMetadataSnapshot { TmdbId = 101, Keywords = ["changed"], FetchedAtUtc = DateTime.UtcNow });
        Assert.Equal(saved.ToVector(), result.Recommendations[0].ScoringFeatures!.ToVector());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeaningfulBattleStoryOutranksGenericAllAgesAnimationThroughTheFullPipeline(bool batch)
    {
        var predictor = Predictor();
        var harness = EngineTestFactory.Create(predictor.Object);
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var historySeries = Guid.NewGuid();
        var profile = Profile(user, historySeries);
        harness.WatchHistory.Setup(service => service.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(service => service.GetAllUserWatchProfiles()).Returns([profile]);
        var continuing = Series("Unseen battle adventure", 101, StoryTags, "FSK-12", 8.0f);
        var allAges = Series("Unseen generic cartoon", 102, ["JE Arr Tag: german"], "FSK-0", 9.0f);
        var prior = Series("Known narrative", 100, StoryTags, "FSK-12", 8.0f);
        prior.Id = historySeries;
        var episodes = Enumerable.Range(0, 12).Select(index => Episode(prior, index)).ToList();
        // Length must not silently exclude an unseen, continuing series with real episodes.
        episodes.AddRange(Enumerable.Range(0, 1179).Select(index => Episode(continuing, index)));
        episodes.Add(Episode(allAges, 0));
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            if (query.IncludeItemTypes?.Contains(BaseItemKind.Series) == true) return [prior, continuing, allAges];
            if (query.IncludeItemTypes?.Contains(BaseItemKind.Episode) == true) return episodes.Cast<BaseItem>().ToArray();
            return [];
        });

        var result = batch
            ? Assert.Single(harness.Engine.GetAllRecommendations(2, TestContext.Current.CancellationToken))
            : Assert.IsType<RecommendationResult>(harness.Engine.GetRecommendations(user, 2, TestContext.Current.CancellationToken));

        Assert.Equal(new[] { continuing.Id, allAges.Id }, result.Recommendations.Select(item => item.ItemId));
        Assert.True(result.Recommendations[0].Score > result.Recommendations[1].Score);
        var story = result.Recommendations[0].ScoringFeatures!;
        var generic = result.Recommendations[1].ScoringFeatures!;
        Assert.True(ContentFitProfile.Create(profile).ScoreTags(StoryTags) > .5);
        Assert.InRange(story.TagSimilarity, double.Epsilon, .01);
        Assert.Equal(0, generic.TagSimilarity);
        Assert.True(story.GenreSimilarity > generic.GenreSimilarity);
        Assert.All(result.Recommendations, item =>
        {
            Assert.Equal(41, item.ScoringFeatures!.ToVector().Length);
            Assert.Equal(Math.Round(Score(item.ScoringFeatures), 4), item.Score);
            var copied = JsonSerializer.Deserialize<CandidateFeatures>(JsonSerializer.Serialize(item.ScoringFeatures))!;
            Assert.Equal(Score(item.ScoringFeatures), Score(copied));
        });
    }

    [Fact]
    public void ReusedCandidateSnapshotKeepsBothRawAndSemanticTagInputsCoherentAfterLiveMetadataChanges()
    {
        var harness = EngineTestFactory.Create(Predictor().Object);
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var seen = new Movie { Id = Guid.NewGuid(), Name = "Known story", Path = "/media/seen.mkv", Genres = Genres, Tags = StoryTags };
        var unseen = new Movie { Id = Guid.NewGuid(), Name = "Unseen related story", Path = "/media/unseen.mkv", Genres = Genres, Tags = StoryTags, OfficialRating = "FSK-12", CommunityRating = 8 };
        var profile = new UserWatchProfile
        {
            UserId = user, WatchedMovieCount = 1, GenreDistribution = new Dictionary<string, int> { ["Crime"] = 100 },
            // No playback timestamp: exact snapshot comparisons must not include wall-clock genre decay.
            WatchedItems = [new WatchedItemInfo { ItemId = seen.Id, Played = true, PlayCount = 1, Genres = Genres, Tags = StoryTags }]
        };
        harness.WatchHistory.Setup(service => service.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(service => service.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? [seen, unseen] : []);
        var first = Assert.Single(harness.Engine.GetRecommendations(user, 1, TestContext.Current.CancellationToken)!.Recommendations).ScoringFeatures!;
        seen.Tags = ["wizard", "magic"];
        unseen.Tags = ["wizard", "magic"];
        var cached = Assert.Single(harness.Engine.GetRecommendations(user, 1, TestContext.Current.CancellationToken)!.Recommendations).ScoringFeatures!;
        Assert.Equal(first.GenreSimilarity, cached.GenreSimilarity);
        Assert.Equal(1, first.TagSimilarity);
        Assert.Equal(first.TagSimilarity, cached.TagSimilarity);
        Assert.Equal(first.ContentFitSupport, cached.ContentFitSupport);
        Assert.Equal(first.AudienceMismatch, cached.AudienceMismatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenAdverseSeriesPredictorCannotHideCoherentStoryEvidenceBehindBroadAllAgesMatches(bool batch)
    {
        var weights = new double[CandidateFeatures.FeatureCount];
        weights[(int)FeatureIndex.GenreSimilarity] = .03;
        weights[(int)FeatureIndex.IsSeries] = -.65;
        var learned = new LearnedScoringStrategy.PredictionSnapshot(new LearnedScoringStrategy.WeightsData { Version = 6, TrainingGeneration = 1, Weights = weights, Bias = .8 });
        Assert.True(LearnedScoringStrategy.IsValidParameters(learned.ExportParameters()));
        using var neural = new NeuralScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1);
        var ensemble = new EnsembleScoringStrategy(new LearnedScoringStrategy(), heuristic);
        typeof(EnsembleScoringStrategy).GetField("_acceptedPrediction", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ensemble, new AcceptedPrediction(heuristic, learned, neural.CapturePredictionSnapshot(), new AdmissionMixture(.66, .09), .3));
        var harness = EngineTestFactory.Create(ensemble);
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var prior = Series("Known battle story", 100, StoryTags, "FSK-12", 8);
        var profile = Profile(user, prior.Id);
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        var story = Series("Unseen pirate adventure", 101, StoryTags.Concat(["ocean", "sea", "ship"]).ToArray(), "FSK-12", 8);
        var cartoon = Series("Generic all-ages cartoon", 102, ["JE Arr Tag: german"], "FSK-0", 9);
        var family = new Movie { Id = Guid.NewGuid(), Name = "Generic family adventure", Path = "/media/family.mkv", Genres = ["Animation", "Action & Adventure"], Tags = ["JE Arr Tag: german"], OfficialRating = "FSK-6", CommunityRating = 8 };
        var episodes = Enumerable.Range(0, 12).Select(index => Episode(prior, index)).Concat(Enumerable.Range(0, 1179).Select(index => Episode(story, index))).Append(Episode(cartoon, 0)).Cast<BaseItem>().ToArray();
        harness.LibraryManager.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            if (query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true) return [family];
            if (query.IncludeItemTypes?.Contains(BaseItemKind.Series) == true) return [prior, story, cartoon];
            return episodes;
        });
        var result = batch
            ? Assert.Single(harness.Engine.GetAllRecommendations(3, TestContext.Current.CancellationToken))
            : harness.Engine.GetRecommendations(user, 3, TestContext.Current.CancellationToken)!;
        var chosen = Assert.Single(result.Recommendations, item => item.ItemId == story.Id);
        var broad = Assert.Single(result.Recommendations, item => item.ItemId == family.Id);
        Assert.True(learned.Score(chosen.ScoringFeatures!) < learned.Score(broad.ScoringFeatures!));
        Assert.True(chosen.Score > broad.Score);
        Assert.Equal(story.Id, result.Recommendations[0].ItemId);
        Assert.InRange(chosen.ScoringFeatures!.ContentFitSupport!.Value, .5, 1);
        Assert.Equal(.25, broad.ScoringFeatures!.AudienceMismatch);
        Assert.All(result.Recommendations, item => Assert.Equal(Math.Round(ensemble.Score(item.ScoringFeatures!), 4), item.Score));
        var trace = Assert.IsType<CandidateRankingDiagnostics>(result.RankingDiagnostics);
        Assert.True(trace.IsComplete);
        Assert.DoesNotContain(trace.Entries, entry => entry.ItemId == prior.Id);
        Assert.Equal("Selected", Assert.Single(trace.Entries, entry => entry.ItemId == story.Id).SelectionStage);
    }

    private static Mock<IScoringStrategy> Predictor()
    {
        var strategy = new Mock<IScoringStrategy>();
        strategy.SetupGet(value => value.Name).Returns("synthetic v6 ensemble");
        strategy.SetupGet(value => value.NameKey).Returns("strategyEnsemble");
        strategy.Setup(value => value.Score(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) => Score(features));
        strategy.Setup(value => value.ScoreWithExplanation(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) => new ScoreExplanation
        {
            FinalScore = Score(features), StrategyName = "synthetic v6 ensemble", DominantSignal = "genre"
        });
        return strategy;
    }

    private static double Score(CandidateFeatures features)
    {
        var weights = new double[CandidateFeatures.FeatureCount];
        weights[(int)FeatureIndex.GenreSimilarity] = .1;
        weights[(int)FeatureIndex.CombinedCriticScore] = .1;
        weights[(int)FeatureIndex.TagSimilarity] = .2;
        var learned = new LearnedScoringStrategy.PredictionSnapshot(new LearnedScoringStrategy.WeightsData { Version = 6, TrainingGeneration = 1, Weights = weights, Bias = .5 });
        var h = HeuristicScoringStrategy.ScoreRaw(features);
        return ScoringHelper.ComputeContextualScore((.25 * h) + (.66 * learned.Score(features)) + (.09 * .75), features, .1);
    }

    private static UserWatchProfile Profile(Guid user, Guid parent)
    {
        var profile = new UserWatchProfile { UserId = user, WatchedEpisodeCount = 12 };
        for (var i = 0; i < 12; i++) profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(), SeriesId = parent, SeriesTmdbId = 100, ItemType = "Episode", Played = true, PlayCount = 1,
            LastPlayedDate = DateTime.UtcNow.AddDays(-2), Genres = Genres, Tags = StoryTags, SeriesTags = StoryTags, OwnTags = []
        });
        for (var i = 0; i < 100; i++) profile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow.AddDays(-3),
            Genres = Genres, Tags = Enumerable.Range(0, 10).Select(index => $"unrelated {i} {index}").ToArray()
        });
        profile.WatchedMovieCount = 100;
        return profile;
    }

    private static Series Series(string name, int provider, string[] tags, string rating, float critic)
        => new() { Id = Guid.NewGuid(), Name = name, Path = "/media/" + provider, Genres = Genres, Tags = tags, OfficialRating = rating,
            CommunityRating = critic, ProductionYear = 1999, DateCreated = DateTime.UtcNow.AddMonths(-1), ProviderIds = new(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = provider.ToString(System.Globalization.CultureInfo.InvariantCulture) } };

    private static Episode Episode(Series parent, int index)
        => new() { Id = Guid.NewGuid(), SeriesId = parent.Id, Path = parent.Path + "/episode-" + index + ".mkv", ParentId = parent.Id };
}
