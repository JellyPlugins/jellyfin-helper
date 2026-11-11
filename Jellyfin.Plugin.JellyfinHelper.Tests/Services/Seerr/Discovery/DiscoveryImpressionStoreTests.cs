using System.Reflection;
using System.Text.Json;
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
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-impressions-" + Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliveryBoundActionWithoutImpression_FreezesItsExactPredictionAcrossGenerationAndReload(bool requested)
    {
        var store = Store();
        var user = Guid.NewGuid();
        var generation = DateTime.UtcNow.AddDays(-2);
        var displayed = Item(1);
        var unseen = Item(1);
        unseen.ScoringFeatures!.GenreSimilarity = 0.8;
        store.RecordGenerated(user, "user", [displayed], generation);
        store.RecordGenerated(user, "user", [unseen], generation.AddDays(1));
        if (requested) store.RecordRequested(user, displayed, generation);
        else store.RecordDismissed(user, displayed, generation);
        var first = Store().LoadForUser(user)!;
        var entry = Assert.Single(first.Entries);
        Assert.Null(entry.ImpressionAtUtc);
        Assert.NotNull(entry.PredictionEvidenceAtUtc);
        Assert.Equal(generation, entry.ShownAtUtc);
        Assert.Equal(0.3, Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([first], new Dictionary<Guid, UserWatchProfile>(), null, null, CancellationToken.None).Examples).Features.GenreSimilarity);
        displayed.ScoringFeatures!.GenreSimilarity = 1;
        store.RecordGenerated(user, "user", [unseen], DateTime.UtcNow);
        store.RecordImpressions(user, [unseen], generation.AddDays(1));
        if (requested) store.RecordRequested(user, 1, "movie");
        else store.RecordDismissed(user, 1, "movie");
        var persisted = Store().LoadForUser(user)!.Entries.Single();
        Assert.NotNull(persisted.ImpressionAtUtc);
        Assert.Equal(0.3, persisted.ScoringFeatures!.GenreSimilarity);
        Assert.Equal(generation, persisted.ShownAtUtc);
        Assert.Equal(entry.PredictionEvidenceAtUtc, persisted.PredictionEvidenceAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyActionWithoutDelivery_DiscardsUnconfirmedGeneratedFeatures(bool requested)
    {
        var user = Guid.NewGuid();
        var store = Store();
        store.RecordGenerated(user, "user", [Item(1)]);
        if (requested) store.RecordRequested(user, 1, "movie");
        else store.RecordDismissed(user, 1, "movie");
        var feedback = Store().LoadForUser(user)!;
        var entry = Assert.Single(feedback.Entries);
        Assert.Null(entry.ScoringFeatures);
        Assert.Null(entry.PredictionEvidenceAtUtc);
        // Old persisted versions may still contain generated snapshots; the builder also fails closed on provenance.
        entry.ScoringFeatures = new CandidateFeatures { IsExternalCandidate = true, GenreSimilarity = 0.8 };
        var example = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([feedback], new Dictionary<Guid, UserWatchProfile>(), null, null, CancellationToken.None).Examples);
        Assert.Equal(0.0, example.Features.GenreSimilarity);
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

    [Fact]
    public void OversizedValidStore_IsRecoveredWithinUtf8BudgetWithoutLosingExplicitOutcomes()
    {
        Directory.CreateDirectory(_directory);
        var requestedUser = Guid.NewGuid();
        var dismissedUser = Guid.NewGuid();
        var file = Path.Join(_directory, "jellyfin-helper-discovery-feedback.json");
        var generated = new DiscoveryFeedbackEntry { TmdbId = 2, Title = new string('x', 20_000), ShownAtUtc = DateTime.UtcNow };
        var feedback = new[]
        {
            new DiscoveryFeedbackResult { UserId = requestedUser, Entries = [new DiscoveryFeedbackEntry { TmdbId = 1, Title = new string('ö', 20_000), RequestedAtUtc = DateTime.UtcNow, ShownAtUtc = DateTime.UtcNow, ScoringFeatures = Item(1).ScoringFeatures }, generated] },
            new DiscoveryFeedbackResult { UserId = dismissedUser, Entries = [new DiscoveryFeedbackEntry { TmdbId = 3, Title = new string('x', 20_000), DismissedAtUtc = DateTime.UtcNow, ShownAtUtc = DateTime.UtcNow }] }
        };
        File.WriteAllText(file, JsonSerializer.Serialize(feedback));
        var store = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), NullLogger<DiscoveryFeedbackStore>.Instance, _directory, sizeLimit: 2000);
        Assert.Equal(new[] { requestedUser, dismissedUser }, store.LoadAll().Select(result => result.UserId));
        Assert.True(new FileInfo(file).Length <= 2000);
        Assert.Contains((1, "movie"), store.GetRequestedItems(requestedUser));
        Assert.Contains((3, "movie"), store.GetDismissedItems(dismissedUser));
        Assert.DoesNotContain(store.LoadForUser(requestedUser)!.Entries, entry => entry.TmdbId == 2);
        Assert.Contains((1, "movie"), Store().GetRequestedItems(requestedUser));
        Assert.Contains((3, "movie"), Store().GetDismissedItems(dismissedUser));
        var compacted = Store().LoadAll();
        Assert.All(compacted.SelectMany(result => result.Entries), entry => Assert.True(entry.PredictionInputsCompacted));
        Assert.Empty(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(compacted, new Dictionary<Guid, UserWatchProfile>(), null, null, CancellationToken.None).Examples);
    }

    [Fact]
    public void GeneratedFeatures_AreNotDuplicatedIntoUnobservedPersistentRows()
    {
        var store = Store();
        var user = Guid.NewGuid();
        store.RecordGenerated(user, "user", [Item(1)]);
        store.RecordGenerated(user, "user", [Item(1)]);
        Assert.Null(Store().LoadForUser(user)!.Entries.Single().ScoringFeatures);
        store.RecordRequested(user, Item(1), DateTime.UtcNow);
        Assert.NotNull(Store().LoadForUser(user)!.Entries.Single().ScoringFeatures);
    }

    [Fact]
    public void UnrecoverablyLargeFile_IsPreservedAndCannotBeOverwrittenByNewFeedback()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Join(_directory, "jellyfin-helper-discovery-feedback.json");
        using (var stream = File.Create(file)) stream.SetLength(121L * 1024 * 1024);
        var store = Store();
        Assert.Empty(store.LoadAll());
        store.RecordRequested(Guid.NewGuid(), 1, "movie");
        Assert.True(File.Exists(file));
        Assert.Equal(121L * 1024 * 1024, new FileInfo(file).Length);
    }

    [Fact]
    public void ImpossibleExplicitFeedbackBudget_DoesNotOverwriteTheExistingFile()
    {
        var user = Guid.NewGuid();
        var store = Store();
        store.RecordRequested(user, 1, "movie");
        var file = Path.Join(_directory, "jellyfin-helper-discovery-feedback.json");
        var before = File.ReadAllBytes(file);
        var bounded = new DiscoveryFeedbackStore(Mock.Of<IPluginLogService>(), NullLogger<DiscoveryFeedbackStore>.Instance, _directory, sizeLimit: 1);
        bounded.RecordDismissed(user, 2, "movie");
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Contains((1, "movie"), Store().GetRequestedItems(user));
    }

    private DiscoveryFeedbackStore Store() => new(Mock.Of<IPluginLogService>(), NullLogger<DiscoveryFeedbackStore>.Instance, _directory);

    private static DiscoveryRecommendation Item(int id) => new() { TmdbId = id, MediaType = "movie", Title = "title", Genres = ["Drama"], ScoringFeatures = new CandidateFeatures { GenreSimilarity = 0.3, IsExternalCandidate = true } };
}
