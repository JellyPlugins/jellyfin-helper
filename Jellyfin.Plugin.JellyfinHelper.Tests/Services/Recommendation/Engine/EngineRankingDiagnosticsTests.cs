using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Ranking diagnostics are bounded, detached and never manufacture independent negative evidence.</summary>
public sealed class EngineRankingDiagnosticsTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(1001)]
    public void TraceCapturesSelectionPoolsAndClearlyMarksIncompletePopulations(int count)
    {
        var harness = EngineTestFactory.Create();
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Drama"] }] };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        var movies = Enumerable.Range(0, count).Select(index => (BaseItem)new Movie
        {
            Id = Guid.NewGuid(), Name = "Candidate " + index, Path = "/media/" + index + ".mkv", Genres = ["Drama"],
            CommunityRating = 7, ProductionYear = 2020, DateCreated = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }).ToArray();
        harness.LibraryManager.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? movies : []);
        var result = harness.Engine.GetRecommendations(user, 1, TestContext.Current.CancellationToken)!;
        var trace = Assert.IsType<CandidateRankingDiagnostics>(result.RankingDiagnostics);
        Assert.Equal(count, trace.ScoredCandidateCount);
        Assert.Equal(count <= EngineConstants.MaximumRankingTraceCandidates, trace.IsComplete);
        Assert.Equal(Math.Min(count, EngineConstants.MaximumRankingTraceCandidates), trace.Entries.Count);
        Assert.Equal(result.Recommendations.Select(item => item.ItemId), trace.Entries.Where(item => item.SelectionStage == "Selected").Select(item => item.ItemId));
        Assert.All(trace.Entries, entry =>
        {
            Assert.InRange(entry.InitialRank, 1, count);
            Assert.InRange(entry.InitialScore!.Value, 0, 1);
            Assert.Equal(entry.InitialScore, entry.PreContentScore);
            Assert.Null(entry.ContentFitSupport);
            Assert.Null(entry.AudienceMismatch);
            var expected = entry.InitialRank <= 5 ? "NotSelectedInMmrPool" : entry.InitialRank <= 20 ? "NotSelectedInExplorationPool" : "BelowSelectionPools";
            Assert.Equal(result.Recommendations.Any(item => item.ItemId == entry.ItemId) ? "Selected" : expected, entry.SelectionStage);
        });
        movies[0].Name = "Changed metadata";
        Assert.DoesNotContain(trace.Entries, entry => entry.Name == "Changed metadata");
    }

    [Fact]
    public void UnselectedRankingTraceEntriesCannotBecomeTrainingOrIndependentRejectionEvidence()
    {
        var user = Guid.NewGuid();
        var result = new RecommendationResult
        {
            UserId = user,
            RankingDiagnostics = new CandidateRankingDiagnostics
            {
                ScoredCandidateCount = 1, IsComplete = true,
                Entries = [new CandidateRankingEntry { ItemId = Guid.NewGuid(), Name = "Unseen story", InitialRank = 1, InitialScore = .9, SelectionStage = "NotSelectedInMmrPool" }]
            }
        };
        var built = TrainingDataBuilder.BuildExamples([result], [new UserWatchProfile { UserId = user }], TestContext.Current.CancellationToken);
        Assert.Empty(built.Examples);
    }
}
