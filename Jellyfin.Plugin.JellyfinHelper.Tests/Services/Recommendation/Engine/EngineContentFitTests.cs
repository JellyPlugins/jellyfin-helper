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

/// <summary>Real ranking-pipeline counterprobes with synthetic v5 predictors, never private production fixtures.</summary>
public sealed class EngineContentFitTests
{
    private static readonly string[] StoryTags = ["anime", "shounen", "pirate", "friendship", "super power"];
    private static readonly string[] Genres = ["Animation", "Action & Adventure"];

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
            Assert.Equal(38, item.ScoringFeatures!.ToVector().Length);
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
        var weights = new double[38];
        weights[(int)FeatureIndex.GenreSimilarity] = .03;
        weights[(int)FeatureIndex.IsSeries] = -.65;
        var learned = new LearnedScoringStrategy.PredictionSnapshot(new LearnedScoringStrategy.WeightsData { Version = 5, TrainingGeneration = 1, Weights = weights, Bias = .8 });
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
        strategy.SetupGet(value => value.Name).Returns("synthetic v5 ensemble");
        strategy.SetupGet(value => value.NameKey).Returns("strategyEnsemble");
        strategy.Setup(value => value.Score(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) => Score(features));
        strategy.Setup(value => value.ScoreWithExplanation(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) => new ScoreExplanation
        {
            FinalScore = Score(features), StrategyName = "synthetic v5 ensemble", DominantSignal = "genre"
        });
        return strategy;
    }

    private static double Score(CandidateFeatures features)
    {
        var weights = new double[CandidateFeatures.FeatureCount];
        weights[(int)FeatureIndex.GenreSimilarity] = .1;
        weights[(int)FeatureIndex.CombinedCriticScore] = .1;
        weights[(int)FeatureIndex.TagSimilarity] = .2;
        var learned = new LearnedScoringStrategy.PredictionSnapshot(new LearnedScoringStrategy.WeightsData { Version = 5, TrainingGeneration = 1, Weights = weights, Bias = .5 });
        var h = new HeuristicScoringStrategy(genrePenaltyFloor: 1).ScoreRaw(features);
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
