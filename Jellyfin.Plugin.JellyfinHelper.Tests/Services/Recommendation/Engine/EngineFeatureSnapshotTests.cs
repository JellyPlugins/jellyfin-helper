using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

public sealed class EngineFeatureSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedSnapshots_ExactlyMatchFinalScoringInputsWithoutRetainingTheCandidatePopulation(bool refreshMetadata)
    {
        var rankingInputs = new List<double[]>();
        var contrastInputs = new Dictionary<double, double[]>();
        var explanationInputs = new Dictionary<double, double[]>();
        BaseItem[] movies = [];
        var scored = 0;
        var strategy = new Mock<IScoringStrategy>();
        strategy.SetupGet(value => value.Name).Returns("capture");
        strategy.SetupGet(value => value.NameKey).Returns("capture");
        strategy.Setup(value => value.Score(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) =>
        {
            if (++scored <= 40)
            {
                rankingInputs.Add(features.ToVector());
            }
            else
            {
                contrastInputs.Add(features.CombinedCriticScore, features.ToVector());
            }
            if (scored == 40 && refreshMetadata)
            {
                movies[^1].CommunityRating = 9.9f;
            }
            return features.CombinedCriticScore;
        });
        strategy.Setup(value => value.ScoreWithExplanation(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) =>
        {
            explanationInputs.Add(features.CombinedCriticScore, features.ToVector());
            return new ScoreExplanation { FinalScore = features.CombinedCriticScore, StrategyName = "capture", DominantSignal = "rating" };
        });
        var harness = EngineTestFactory.Create(strategy.Object);
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-1), Genres = ["Drama"] }] };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        movies = Enumerable.Range(0, 40).Select(index => (BaseItem)new Movie { Id = Guid.NewGuid(), Name = $"Movie {index}", Path = $"/media/movie-{index}.mkv", Genres = ["Drama"], CommunityRating = 4.0f + index * 0.05f, DateCreated = DateTime.UtcNow.AddDays(-index - 1), ProductionYear = 2020 }).ToArray();
        harness.LibraryManager.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? movies : []);

        var result = Assert.IsType<RecommendationResult>(harness.Engine.GetRecommendations(user, 3, TestContext.Current.CancellationToken));

        strategy.Verify(value => value.Score(It.IsAny<CandidateFeatures>()), Times.Exactly(movies.Length + 5));
        strategy.Verify(value => value.ScoreWithExplanation(It.IsAny<CandidateFeatures>()), Times.Exactly(3));
        Assert.Equal(movies.Length, rankingInputs.Count);
        void AssertSnapshot(RecommendedItem recommendation, Dictionary<double, double[]> inputs)
        {
            var features = Assert.IsType<CandidateFeatures>(recommendation.ScoringFeatures);
            Assert.Equal(inputs[features.CombinedCriticScore], features.ToVector());
            Assert.Equal(Math.Round(features.CombinedCriticScore, 4), recommendation.Score);
            var dateCreated = Assert.IsType<DateTime>(recommendation.DateCreated);
            Assert.Equal(ContentScoring.ComputeRecencyScore(dateCreated, result.GeneratedAt), features.LibraryAddedRecency);
        }

        Assert.Equal(3, result.Recommendations.Count);
        Assert.All(result.Recommendations, item => AssertSnapshot(item, explanationInputs));
        var lastMovie = Assert.Single(result.Recommendations, item => item.ItemId == movies[^1].Id);
        Assert.Equal(ContentScoring.ComputeCombinedCriticScore(refreshMetadata ? 9.9f : 5.95f, null), lastMovie.ScoringFeatures!.CombinedCriticScore);
        Assert.Equal(!refreshMetadata, rankingInputs.Any(vector => vector.SequenceEqual(lastMovie.ScoringFeatures.ToVector())));
        Assert.NotNull(result.TrainingContrasts);
        Assert.Equal(5, result.TrainingContrasts.Count);
        Assert.All(result.TrainingContrasts, item =>
        {
            AssertSnapshot(item, contrastInputs);
            Assert.DoesNotContain(item.ItemId, result.Recommendations.Select(selected => selected.ItemId));
            Assert.DoesNotContain(item.ScoringFeatures!.CombinedCriticScore, explanationInputs.Keys);
        });
    }
}
