using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>Generated candidates, viewport impressions and explicit actions retain separate, durable prediction-time evidence.</summary>
public sealed class DiscoveryImpressionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jfh-impressions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Generation_DoesNotCreateSeenNegatives()
    {
        var store = Store();
        var user = Guid.NewGuid();
        store.RecordGenerated(user, "user", [Item(1)]);
        var feedback = store.LoadForUser(user)!;
        Assert.Equal(DiscoveryInteractionStatus.Generated, feedback.Entries.Single().GetStatus());
        Assert.Null(feedback.Entries[0].ImpressionAtUtc);
        var examples = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([feedback], new Dictionary<Guid, UserWatchProfile>(), null, null, CancellationToken.None);
        Assert.Empty(examples.Examples);
    }

    [Fact]
    public void ConfirmedDisplay_FreezesActualPredictionFeaturesAndSurvivesReload()
    {
        var store = Store();
        var user = Guid.NewGuid();
        var generation = DateTime.UtcNow.AddDays(-1);
        store.RecordGenerated(user, "user", [Item(1), Item(2)], generation);
        var shown = Item(1);
        shown.ScoringFeatures!.GenreSimilarity = 0.7;
        store.RecordImpressions(user, [shown], generation);
        shown.ScoringFeatures.GenreSimilarity = 1.0;
        store.RecordGenerated(user, "user", [Item(1)], DateTime.UtcNow);
        var reloaded = Store().LoadForUser(user)!;
        var entry = reloaded.Entries.Single(e => e.TmdbId == 1);
        Assert.Equal(DiscoveryInteractionStatus.Shown, entry.GetStatus());
        Assert.Equal(0.7, entry.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(generation, entry.ShownAtUtc);
        Assert.Equal(DiscoveryInteractionStatus.Generated, reloaded.Entries.Single(e => e.TmdbId == 2).GetStatus());
        entry.ScoringFeatures.GenreSimilarity = 0.0;
        Assert.Equal(0.7, store.LoadForUser(user)!.Entries.Single(e => e.TmdbId == 1).ScoringFeatures!.GenreSimilarity);
    }

    [Fact]
    public void RecentImpression_IsCensoredUntilItsResponseWindowCloses()
    {
        var entry = new DiscoveryFeedbackEntry { TmdbId = 1, ImpressionAtUtc = DateTime.UtcNow, ShownAtUtc = DateTime.UtcNow.AddDays(-10), ScoringFeatures = new CandidateFeatures { IsExternalCandidate = true } };
        var feedback = new DiscoveryFeedbackResult { UserId = Guid.NewGuid(), Entries = [entry] };
        Assert.Empty(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([feedback], new Dictionary<Guid, UserWatchProfile>(), null, null, CancellationToken.None).Examples);
        entry.ImpressionAtUtc = DateTime.UtcNow.AddDays(-8);
        var examples = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([feedback], new Dictionary<Guid, UserWatchProfile>(), null, null, CancellationToken.None).Examples;
        var example = Assert.Single(examples);
        Assert.True(example.IsObserved);
        Assert.Equal(0.05, example.Label);
    }

    [Fact]
    public void OlderDeliveredSnapshot_WinsOverNewerGeneratedButUnseenFeatures()
    {
        var store = Store();
        var user = Guid.NewGuid();
        var firstGeneration = DateTime.UtcNow.AddDays(-2);
        var nextGeneration = firstGeneration.AddDays(1);
        var oldCard = Item(1);
        var newCard = Item(1);
        newCard.ScoringFeatures!.GenreSimilarity = 0.8;
        store.RecordGenerated(user, "user", [oldCard], firstGeneration);
        store.RecordGenerated(user, "user", [newCard], nextGeneration);
        store.RecordImpressions(user, [oldCard], firstGeneration);
        store.RecordImpressions(user, [newCard], nextGeneration);

        var persisted = Store().LoadForUser(user)!.Entries.Single();
        Assert.Equal(0.3, persisted.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(firstGeneration, persisted.ShownAtUtc);
        Assert.NotNull(persisted.ImpressionAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitFeedbackArrivingFirst_DoesNotPreventFreezingTheActualDisplay(bool requested)
    {
        var store = Store();
        var user = Guid.NewGuid();
        var firstGeneration = DateTime.UtcNow.AddDays(-2);
        var oldCard = Item(1);
        var newCard = Item(1);
        newCard.ScoringFeatures!.GenreSimilarity = 0.8;
        store.RecordGenerated(user, "user", [oldCard], firstGeneration);
        store.RecordGenerated(user, "user", [newCard], firstGeneration.AddDays(1));
        if (requested) store.RecordRequested(user, 1, "movie");
        else store.RecordDismissed(user, 1, "movie");
        var outcome = store.LoadForUser(user)!.Entries.Single();

        store.RecordImpressions(user, [oldCard], firstGeneration);
        store.RecordImpressions(user, [newCard], firstGeneration.AddDays(1));

        var persisted = Store().LoadForUser(user)!.Entries.Single();
        Assert.Equal(0.3, persisted.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(firstGeneration, persisted.ShownAtUtc);
        Assert.Equal(outcome.GetStatus(), persisted.GetStatus());
        Assert.Equal(outcome.RequestedAtUtc, persisted.RequestedAtUtc);
        Assert.Equal(outcome.DismissedAtUtc, persisted.DismissedAtUtc);
    }

    [Fact]
    public void FailedDurableWrite_ThrowsAndDoesNotLeaveAnAcknowledgedMemoryImpression()
    {
        var store = Store();
        var path = Assert.IsType<string>(typeof(DiscoveryFeedbackStore).GetField("_filePath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store));
        Directory.CreateDirectory(path);
        var user = Guid.NewGuid();
        Assert.Throws<IOException>(() => store.RecordImpressions(user, [Item(1)], DateTime.UtcNow));
        Assert.Empty(store.LoadAll());
    }

    private DiscoveryFeedbackStore Store() => new(Mock.Of<IPluginLogService>(), NullLogger<DiscoveryFeedbackStore>.Instance, _directory);

    private static DiscoveryRecommendation Item(int id) => new() { TmdbId = id, MediaType = "movie", Title = "title", Genres = ["Drama"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = 0.3, IsExternalCandidate = true } };
}
