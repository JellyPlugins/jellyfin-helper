using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>The diagnostic endpoint reads only existing cache data and cannot trigger training or generation.</summary>
public sealed class RecommendationRankingDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCacheOrLegacySnapshotNeverInvokesTheEngine(bool legacy)
    {
        var user = Guid.NewGuid();
        var cache = new Mock<IRecommendationCacheService>();
        cache.Setup(service => service.LoadResults()).Returns(legacy ? [new RecommendationResult { UserId = user }] : null);
        var engine = new Mock<IRecommendationEngine>(MockBehavior.Strict);
        var history = new Mock<IWatchHistoryService>(MockBehavior.Strict);
        var config = new Mock<IPluginConfigurationService>();
        config.Setup(service => service.GetConfiguration()).Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate });
        var controller = new RecommendationController(engine.Object, cache.Object, history.Object, config.Object);
        Assert.IsType<NotFoundObjectResult>(controller.GetRankingDiagnostics(user));
        engine.VerifyNoOtherCalls();
        history.VerifyNoOtherCalls();
        cache.Verify(service => service.SaveResults(It.IsAny<IReadOnlyList<RecommendationResult>>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedTraceRemainsUserScopedAndMakesTruncationExplicit(bool complete)
    {
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var other = Guid.NewGuid();
        var cache = new Mock<IRecommendationCacheService>();
        cache.Setup(service => service.LoadResults()).Returns(
        [
            new RecommendationResult { UserId = other, RankingDiagnostics = new CandidateRankingDiagnostics { Entries = [new CandidateRankingEntry { ItemId = item, Name = "Other user's private item" }] } },
            new RecommendationResult { UserId = user, RankingDiagnostics = new CandidateRankingDiagnostics { IsComplete = complete, ScoredCandidateCount = 2, Entries = [new CandidateRankingEntry { ItemId = item, Name = "Original item", InitialRank = 1 }, new CandidateRankingEntry { ItemId = Guid.NewGuid(), Name = "Another item" }] } }
        ]);
        var engine = new Mock<IRecommendationEngine>(MockBehavior.Strict);
        var history = new Mock<IWatchHistoryService>(MockBehavior.Strict);
        var config = new Mock<IPluginConfigurationService>();
        config.Setup(service => service.GetConfiguration()).Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate });
        var controller = new RecommendationController(engine.Object, cache.Object, history.Object, config.Object);
        var result = Assert.IsType<OkObjectResult>(controller.GetRankingDiagnostics(user, item));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(user, json.RootElement.GetProperty("UserId").GetGuid());
        Assert.Equal(complete, json.RootElement.GetProperty("IsComplete").GetBoolean());
        Assert.Equal("Original item", Assert.Single(json.RootElement.GetProperty("Entries").EnumerateArray()).GetProperty("Name").GetString());
        Assert.Equal(complete ? "NotScoredInSavedSnapshot" : "NotCapturedInBoundedTrace", json.RootElement.GetProperty("AbsenceMeaning").GetString());
        engine.VerifyNoOtherCalls();
        history.VerifyNoOtherCalls();
    }
}
