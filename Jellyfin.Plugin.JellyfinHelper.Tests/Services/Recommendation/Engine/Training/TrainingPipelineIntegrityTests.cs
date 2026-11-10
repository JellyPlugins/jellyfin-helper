using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>End-to-end data invariants across historical features, immutable snapshots, quality evidence and holdout ownership.</summary>
public sealed class TrainingPipelineIntegrityTests
{
    private static readonly DateTime Prediction = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HistoricalProfile_DoesNotBorrowTargetFutureOrUndatedPreferences()
    {
        var past = Watch(Guid.NewGuid(), Prediction.AddDays(-1), ["Drama"]);
        var target = Watch(Guid.NewGuid(), Prediction.AddDays(1), ["Action"]);
        var undated = Watch(Guid.NewGuid(), null, ["Comedy"]);
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [past, target, undated], GenreDistribution = new Dictionary<string, int> { ["Action"] = 99 }, LanguageProfile = new Dictionary<string, LanguageProfileEntry> { ["de"] = new() { ChosenCount = 100 } } };

        var historical = TrainingHistory.Before(profile, Prediction);

        var historicalItem = Assert.Single(historical.WatchedItems);
        Assert.Same(past, historicalItem);
        Assert.Empty(historical.GenreDistribution);
        Assert.Empty(historical.LanguageProfile);
        Assert.Equal(3, profile.WatchedItems.Count);
        Assert.Equal(99, profile.GenreDistribution["Action"]);
    }

    [Fact]
    public void RecordedFeatures_RoundTripAndIgnoreAllLaterProfileChanges()
    {
        var id = Guid.NewGuid();
        var user = Guid.NewGuid();
        var features = new CandidateFeatures { GenreSimilarity = 0.2, CollaborativeScore = 0.7, StudioMatch = false, ContentNearestNeighborScore = 0.15 };
        var recommendation = new RecommendedItem { ItemId = id, ItemType = "Movie", Genres = ["Action"], Studios = ["Target studio"], ScoringFeatures = features.Copy() };
        var cached = JsonSerializer.Deserialize<RecommendationResult>(JsonSerializer.Serialize(new RecommendationResult { UserId = user, GeneratedAt = Prediction, Recommendations = [recommendation] }))!;
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [Watch(id, Prediction.AddDays(1), ["Action"])] };
        features.GenreSimilarity = 1.0;
        var example = TrainingDataBuilder.BuildExamples([cached], new Collection<UserWatchProfile> { profile }, CancellationToken.None).Examples.Single();

        Assert.Equal(0.2, example.Features.GenreSimilarity);
        Assert.Equal(0.7, example.Features.CollaborativeScore);
        Assert.False(example.Features.StudioMatch);
        Assert.Equal(0.15, example.Features.ContentNearestNeighborScore);
        Assert.NotSame(cached.Recommendations[0].ScoringFeatures, example.Features);
        Assert.True(example.HasStableObservationId);
    }

    [Fact]
    public void LegacyFeatures_UseCollaborativeEvidenceAvailableBeforeTargetWatch()
    {
        var user = Guid.NewGuid();
        var neighbor = Guid.NewGuid();
        var target = Guid.NewGuid();
        var shared = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var a = new UserWatchProfile { UserId = user };
        var b = new UserWatchProfile { UserId = neighbor };
        foreach (var id in shared)
        {
            a.WatchedItems.Add(Watch(id, Prediction.AddDays(-3), ["Drama"]));
            b.WatchedItems.Add(Watch(id, Prediction.AddDays(-3), ["Drama"]));
        }
        a.WatchedItems.Add(Watch(target, Prediction.AddDays(1), ["Action"]));
        b.WatchedItems.Add(Watch(target, Prediction.AddDays(-2), ["Action"]));
        var result = new RecommendationResult { UserId = user, GeneratedAt = Prediction, Recommendations = [new RecommendedItem { ItemId = target, ItemType = "Movie", Genres = ["Action"], Studios = ["Unique studio"], PeopleNames = ["Unique actor"] }] };
        var built = TrainingDataBuilder.BuildExamples([result], new Collection<UserWatchProfile> { a, b }, CancellationToken.None);
        var example = built.Examples.Single(e => e.UserId == user && e.GeneratedAtUtc == Prediction && e.Label > 0.5);

        Assert.True(example.Features.CollaborativeScore > 0.0);
        Assert.False(example.Features.StudioMatch);
        Assert.Equal(0.0, example.Features.ContentNearestNeighborScore);
    }

    [Theory]
    [InlineData(1.0, 0.1)]
    [InlineData(10.0, 1.0)]
    public void ExplicitRating_IsNotOverwrittenByRecommendationInfluence(double rating, double expected)
    {
        var user = Guid.NewGuid();
        var id = Guid.NewGuid();
        var watch = Watch(id, Prediction.AddDays(1), ["Action"]);
        watch.UserRating = rating;
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [watch] };
        var cached = new RecommendationResult { UserId = user, GeneratedAt = Prediction, Recommendations = [new RecommendedItem { ItemId = id, ItemType = "Movie", ScoringFeatures = new CandidateFeatures() }] };
        var example = TrainingDataBuilder.BuildExamples([cached], new Collection<UserWatchProfile> { profile }, CancellationToken.None).Examples.Single();

        Assert.Equal(expected, example.Label);
        Assert.True(example.IsObserved);
    }

    [Fact]
    public void StartedPlayback_IsNotAnObservedAbandonmentOutcome()
    {
        var user = Guid.NewGuid();
        var id = Guid.NewGuid();
        var watch = Watch(id, Prediction.AddDays(1), ["Action"]);
        watch.Played = false;
        watch.PlayCount = 0;
        watch.PlaybackPositionTicks = watch.RuntimeTicks / 10;
        var cached = new RecommendationResult { UserId = user, GeneratedAt = Prediction, Recommendations = [new RecommendedItem { ItemId = id, ItemType = "Movie", ScoringFeatures = new CandidateFeatures() }] };
        var example = TrainingDataBuilder.BuildExamples([cached], new Collection<UserWatchProfile> { new() { UserId = user, WatchedItems = [watch] } }, CancellationToken.None).Examples.Single();

        Assert.False(example.IsObserved);
        Assert.Empty(TrainingMath.FilterEffectiveExamples([example], Prediction.AddDays(1), 0.01));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecommendedSeries_FinishedEarlierEpisodesRemainObservedWhenTheLatestEpisodeIsPartial(bool recorded)
    {
        var user = Guid.NewGuid();
        var series = Guid.NewGuid();
        var completed = Watch(Guid.NewGuid(), Prediction.AddDays(1), ["Drama"]);
        completed.ItemType = "Episode";
        completed.SeriesId = series;
        var partial = Watch(Guid.NewGuid(), Prediction.AddDays(2), ["Drama"]);
        partial.ItemType = "Episode";
        partial.SeriesId = series;
        partial.Played = false;
        partial.PlayCount = 0;
        partial.PlaybackPositionTicks = partial.RuntimeTicks / 5;
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [completed, partial] };
        var cached = new RecommendationResult { UserId = user, GeneratedAt = Prediction, Recommendations = [new RecommendedItem { ItemId = series, ItemType = "Series", Genres = ["Drama"], ScoringFeatures = recorded ? new CandidateFeatures { IsSeries = true, GenreSimilarity = 0.4 } : null }] };

        var example = Assert.Single(TrainingDataBuilder.BuildExamples([cached], [profile], CancellationToken.None).Examples);
        Assert.True(example.IsObserved);
        Assert.Single(TrainingMath.FilterEffectiveExamples([example], Prediction.AddDays(3), 0.01));
    }

    [Fact]
    public void Contrasts_UseOnlyDatedPreferencesBeforeTheirPredictionTime()
    {
        var user = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var prior = Watch(Guid.NewGuid(), Prediction.AddDays(-1), ["Drama"]);
        var later = Watch(Guid.NewGuid(), Prediction.AddDays(1), ["Action"]);
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [prior, later], LanguageProfile = new Dictionary<string, LanguageProfileEntry> { ["de"] = new() { ChosenCount = 100 } } };
        var negative = new RecommendedItem { ItemId = Guid.NewGuid(), ItemType = "Movie", Genres = ["Action"], AudioLanguages = ["de"], SubtitleLanguages = ["de"] };
        var cached = new RecommendationResult { UserId = owner, GeneratedAt = Prediction, Recommendations = [negative] };
        var built = TrainingDataBuilder.BuildExamples([cached], [profile, new UserWatchProfile { UserId = owner }], CancellationToken.None);
        var contrast = Assert.Single(built.Examples, example => example.UserId == user && example.IsContrastSample);

        Assert.Equal(Prediction, contrast.GeneratedAtUtc);
        Assert.Equal(0.0, contrast.Features.GenreSimilarity);
        Assert.False(contrast.Features.HasUserInteraction);
        Assert.Equal(0.5, contrast.Features.LanguageAffinity);
        Assert.Equal(0.5, contrast.Features.SubtitleLanguageAffinity);
        Assert.Equal(Prediction.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, contrast.Features.IsWeekend);
    }

    [Fact]
    public void DiscoverySnapshot_PreservesPersonalInputsInsteadOfRebuildingWithGlobalMeans()
    {
        var user = Guid.NewGuid();
        var entry = new DiscoveryFeedbackEntry { TmdbId = 123, RequestedAtUtc = Prediction.AddDays(1), ShownAtUtc = Prediction, ScoringFeatures = new CandidateFeatures { IsExternalCandidate = true, CollaborativeScore = 0.6, GenreSimilarity = 0.3 } };
        var globalMeans = Enumerable.Repeat(0.2, CandidateFeatures.FeatureCount).ToArray();
        var built = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([new DiscoveryFeedbackResult { UserId = user, Entries = [entry] }], new Dictionary<Guid, UserWatchProfile> { [user] = new() { UserId = user } }, null, globalMeans, CancellationToken.None);

        Assert.Equal(0.6, built.Examples[0].Features.CollaborativeScore);
        Assert.Equal(0.3, built.Examples[0].Features.GenreSimilarity);
        Assert.NotSame(entry.ScoringFeatures, built.Examples[0].Features);
    }

    [Fact]
    public void EachModel_ImputesMissingExternalSignalsInItsOwnFixedCoordinates()
    {
        var raw = new CandidateFeatures { IsExternalCandidate = true, CollaborativeScore = 0.9, GenreSimilarity = 0.8, CombinedCriticScore = 0.7 };
        var globalMeans = Enumerable.Repeat(0.2, CandidateFeatures.FeatureCount).ToArray();
        var personalMeans = Enumerable.Repeat(0.6, CandidateFeatures.FeatureCount).ToArray();
        var globalVector = raw.ToModelVector(globalMeans);
        var personalVector = raw.ToModelVector(personalMeans);

        Assert.Equal(0.2, globalVector[(int)FeatureIndex.CollaborativeScore]);
        Assert.Equal(0.6, personalVector[(int)FeatureIndex.CollaborativeScore]);
        Assert.Equal(0.8 * 0.2, globalVector[(int)FeatureIndex.GenreCollabInteraction]);
        Assert.Equal(0.8 * 0.6, personalVector[(int)FeatureIndex.GenreCollabInteraction]);
        Assert.Equal(0.8, globalVector[(int)FeatureIndex.GenreSimilarity]);
        Assert.Equal(0.9, raw.CollaborativeScore);
        var library = raw.Copy();
        library.IsExternalCandidate = false;
        Assert.Equal(library.ToVector(), library.ToModelVector(personalMeans));
    }

    [Fact]
    public void PermanentHoldouts_AreSharedByGlobalAndPersonalSplitsAndDoNotDrift()
    {
        var examples = Enumerable.Range(0, 300).Select(i => new TrainingExample
        {
            Features = new CandidateFeatures(), Label = i % 2, UserId = new Guid(i % 3 + 1, 0, 0, new byte[8]),
            GeneratedAtUtc = Prediction.AddMinutes(i), HasStableObservationId = true,
            ObservationId = TrainingExample.CreateObservationId(new Guid(i % 3 + 1, 0, 0, new byte[8]), "title:" + i)
        }).ToList();
        var global = Split(examples);
        Assert.NotEmpty(global.Holdout);
        foreach (var personal in examples.GroupBy(e => e.UserId).Select(group => Split(group.ToList())))
        {
            Assert.All(personal.Holdout, example => Assert.Contains(example, global.Holdout));
            Assert.DoesNotContain(personal.Holdout, example => global.Fit.Contains(example));
        }
        foreach (var example in examples) example.GeneratedAtUtc = example.GeneratedAtUtc.AddDays(90);
        var repeated = Split(examples);
        Assert.Equal(global.Holdout.Select(e => e.ObservationId), repeated.Holdout.Select(e => e.ObservationId));
    }

    [Fact]
    public void ConstantBaseline_CannotEarnModelTrustAndUninformativeLabelsAreUnavailable()
    {
        Assert.Equal(0.0, TrainingMath.QualityFactor(0.25, 0.25));
        Assert.Equal(0.0, TrainingMath.QualityFactor(0.3, 0.25));
        Assert.Equal(0.5, TrainingMath.QualityFactor(0.125, 0.25));
        Assert.Equal(0.0, TrainingMath.QualityFactor(double.NaN, 0.25));
        var uniform = Enumerable.Range(0, 20).Select(_ => new TrainingExample { Features = new CandidateFeatures(), Label = 0.05, GeneratedAtUtc = Prediction }).ToArray();
        Assert.False(TrainingMath.HasRankingEvidence(uniform, Prediction, 0.01));
        Assert.True(double.IsNaN(TrainingMath.BaselineValidationLoss(uniform, uniform, Prediction, 0.01)));
    }

    [Fact]
    public void ReplayingTheSameObservations_DoesNotIncreaseInformationCount()
    {
        using var ensemble = new EnsembleScoringStrategy();
        var examples = Balanced(20);
        var heldout = Balanced(20);
        for (var i = 0; i < 10; i++) Assert.True(ensemble.Train(examples, heldout));
        Assert.Equal(20, ensemble.TrainingExampleCount);
        Assert.Equal(0.0, ensemble.CurrentNeuralBeta);
    }

    [Fact]
    public void WeekendContext_FollowsPredictionTimeNotLastActivity()
    {
        var profile = new UserWatchProfile { LastActivityDate = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc) };
        Assert.False(TemporalFeatures.ResolveIsWeekend(profile, new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc)));
        Assert.True(TemporalFeatures.ResolveIsWeekend(profile, new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc)));
    }

    private static List<TrainingExample> Balanced(int count) => Enumerable.Range(0, count).Select(i => new TrainingExample
    {
        Features = new CandidateFeatures { GenreSimilarity = i % 2 == 0 ? 0.9 : 0.1 }, Label = i % 2 == 0 ? 0.9 : 0.1
    }).ToList();

    private static (List<TrainingExample> Fit, List<TrainingExample> Holdout) Split(List<TrainingExample> examples)
    {
        object?[] arguments = [examples, null, null];
        typeof(TrainingService).GetMethod("SplitHeldOut", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);
        return ((List<TrainingExample>)arguments[1]!, (List<TrainingExample>)arguments[2]!);
    }

    private static WatchedItemInfo Watch(Guid id, DateTime? time, IReadOnlyList<string> genres) => new()
    {
        ItemId = id, ItemType = "Movie", Genres = genres, LastPlayedDate = time, Played = true,
        PlayCount = 1, RuntimeTicks = TimeSpan.FromMinutes(100).Ticks
    };
}
