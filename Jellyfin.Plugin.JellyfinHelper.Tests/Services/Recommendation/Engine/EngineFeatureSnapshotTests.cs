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
        var scoredInputs = new Dictionary<double, double[]>();
        BaseItem[] movies = [];
        var scored = 0;
        var strategy = new Mock<IScoringStrategy>();
        strategy.SetupGet(value => value.Name).Returns("capture");
        strategy.SetupGet(value => value.NameKey).Returns("capture");
        strategy.Setup(value => value.ScoreWithExplanation(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) =>
        {
            scoredInputs[features.CombinedCriticScore] = features.ToVector();
            if (++scored == 40 && refreshMetadata)
            {
                movies[^1].CommunityRating = 9.9f;
            }
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

        strategy.Verify(value => value.ScoreWithExplanation(It.IsAny<CandidateFeatures>()), Times.Exactly(movies.Length + 3 + 5));
        void AssertSnapshot(RecommendedItem recommendation)
        {
            var features = Assert.IsType<CandidateFeatures>(recommendation.ScoringFeatures);
            Assert.Equal(scoredInputs[features.CombinedCriticScore], features.ToVector());
            Assert.Equal(Math.Round(features.CombinedCriticScore, 4), recommendation.Score);
            Assert.NotNull(recommendation.DateCreated);
            Assert.Equal(ContentScoring.ComputeRecencyScore(recommendation.DateCreated.Value, result.GeneratedAt), features.LibraryAddedRecency);
        }

        Assert.Collection(result.Recommendations, AssertSnapshot, AssertSnapshot, AssertSnapshot);
        Assert.NotNull(result.TrainingContrasts);
        Assert.Collection(result.TrainingContrasts, AssertSnapshot, AssertSnapshot, AssertSnapshot, AssertSnapshot, AssertSnapshot);
    }
}
