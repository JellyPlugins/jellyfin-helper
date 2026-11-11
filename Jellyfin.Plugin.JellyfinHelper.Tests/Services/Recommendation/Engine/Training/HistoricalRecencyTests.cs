using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>Historical age features are fixed at prediction/watch time; only training weights decay later.</summary>
public sealed class HistoricalRecencyTests
{
    private static readonly DateTime Prediction = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Release = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Added = Prediction.AddDays(-7);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyRecommendationAndCrossUserNegative_UsePredictionTimeNotLaterWatchOrTrainingTime(bool hasPremiere)
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var item = Guid.NewGuid();
        var recommendation = new RecommendedItem { ItemId = item, ItemType = "Movie", Genres = ["Drama"], PremiereDate = hasPremiere ? Release : null, DateCreated = Added };
        var result = new RecommendationResult { UserId = owner, GeneratedAt = Prediction, Recommendations = [recommendation] };
        var ownerProfile = new UserWatchProfile { UserId = owner, WatchedItems = [new WatchedItemInfo { ItemId = item, ItemType = "Movie", Played = true, LastPlayedDate = Prediction.AddDays(2) }] };
        var otherProfile = new UserWatchProfile { UserId = other };

        var built = TrainingDataBuilder.BuildExamples([result], [ownerProfile, otherProfile], TestContext.Current.CancellationToken);

        Assert.Equal(1, built.RandomNegativeCount);
        Assert.Equal(2, built.Examples.Count);
        var positive = Assert.Single(built.Examples, example => example.UserId == owner);
        var negative = Assert.Single(built.Examples, example => example.UserId == other);
        Assert.True(positive.IsObserved);
        Assert.True(negative.IsContrastSample);
        AssertRecency(positive, hasPremiere ? Release : Added, Added, Prediction);
        AssertRecency(negative, hasPremiere ? Release : Added, Added, Prediction);
        Assert.Equal(positive.Features.RecencyScore, negative.Features.RecencyScore);
    }

    [Theory]
    [InlineData("Movie", true)]
    [InlineData("Movie", false)]
    [InlineData("Series", true)]
    public void OrganicStandalone_UsesDatedWatchOrStableHistoricalFallback(string type, bool dated)
    {
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = type, Played = true, Year = 2026, DateCreated = Added, LastPlayedDate = dated ? Prediction : null }] };
        var fallback = new RecommendationResult { UserId = user, GeneratedAt = Prediction };

        var built = TrainingDataBuilder.BuildExamples(dated ? [] : [fallback], [profile], TestContext.Current.CancellationToken);

        Assert.Equal(1, built.OrganicCount);
        AssertRecency(Assert.Single(built.Examples), Release, Added, Prediction);
    }

    [Fact]
    public void OrganicEpisodes_UseLatestWatchAndEarliestLibraryAdditionWithoutCountingEveryEpisode()
    {
        var user = Guid.NewGuid();
        var series = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedItems =
        [
            new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = series, Played = true, Year = 2026, LastPlayedDate = Prediction.AddDays(-3), DateCreated = Added },
            new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = series, Played = true, Year = 2026, LastPlayedDate = Prediction, DateCreated = Added.AddDays(1) }
        ] };

        var built = TrainingDataBuilder.BuildExamples([], [profile], TestContext.Current.CancellationToken);

        Assert.Equal(1, built.OrganicCount);
        var example = Assert.Single(built.Examples);
        Assert.True(example.Features.IsSeries);
        AssertRecency(example, Release, Added, Prediction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedRecommendationAndContrast_RetainExactStoredAgeFeatures(bool contrast)
    {
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var stored = new CandidateFeatures { RecencyScore = 0.123, LibraryAddedRecency = 0.456, HasGenreEvidence = false };
        var recommendation = new RecommendedItem { ItemId = item, ItemType = "Movie", PremiereDate = Release, DateCreated = Added, ScoringFeatures = stored };
        var result = new RecommendationResult { UserId = user, GeneratedAt = Prediction, Recommendations = contrast ? [new RecommendedItem { ItemId = Guid.NewGuid(), ItemType = "Movie", ScoringFeatures = new CandidateFeatures() }] : [recommendation], TrainingContrasts = contrast ? [recommendation] : [] };
        var profile = new UserWatchProfile { UserId = user, WatchedItems = contrast ? [] : [new WatchedItemInfo { ItemId = item, ItemType = "Movie", Played = true, LastPlayedDate = Prediction.AddDays(2) }] };

        var example = Assert.Single(TrainingDataBuilder.BuildExamples([result], [profile], TestContext.Current.CancellationToken).Examples,
            candidate => candidate.ObservationId == TrainingExample.CreateLibraryObservationId(user, item));

        Assert.Equal(stored.ToVector(), example.Features.ToVector());
        Assert.False(example.Features.HasGenreEvidence);
        Assert.NotSame(stored, example.Features);
        Assert.Equal(Prediction, example.GeneratedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscoveryFeedback_UsesShownTimeUnlessConfirmedPredictionInputsAlreadyExist(bool recorded)
    {
        var user = Guid.NewGuid();
        var interaction = Prediction.AddDays(30);
        var stored = new CandidateFeatures { IsExternalCandidate = true, RecencyScore = 0.123, LibraryAddedRecency = 0.456 };
        var entry = new DiscoveryFeedbackEntry { TmdbId = 123, MediaType = "movie", Year = 2026, Popularity = 50, ShownAtUtc = Prediction, RequestedAtUtc = interaction, PredictionEvidenceAtUtc = recorded ? Prediction : null, ScoringFeatures = recorded ? stored : null };

        var built = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([new DiscoveryFeedbackResult { UserId = user, Entries = [entry] }], new Dictionary<Guid, UserWatchProfile>(), null, null, TestContext.Current.CancellationToken);

        var example = Assert.Single(built.Examples);
        Assert.Equal(interaction, example.GeneratedAtUtc); // Example weight follows the outcome; the feature does not.
        Assert.Equal(recorded ? stored.RecencyScore : ContentScoring.ComputeRecencyScore(Release, Prediction), example.Features.RecencyScore);
        Assert.Equal(recorded ? stored.LibraryAddedRecency : 0.5, example.Features.LibraryAddedRecency);
        if (recorded)
        {
            Assert.Equal(stored.ToVector(), example.Features.ToVector());
            Assert.NotSame(stored, example.Features);
        }
    }

    private static void AssertRecency(TrainingExample example, DateTime release, DateTime added, DateTime reference)
    {
        Assert.Equal(reference, example.GeneratedAtUtc);
        Assert.Equal(ContentScoring.ComputeRecencyScore(release, reference), example.Features.RecencyScore);
        Assert.Equal(ContentScoring.ComputeRecencyScore(added, reference), example.Features.LibraryAddedRecency);
        // A fixed positive control catches the old wall-clock implementation, even in a later pipeline run.
        Assert.NotEqual(ContentScoring.ComputeRecencyScore(release, reference.AddDays(90)), example.Features.RecencyScore);
        Assert.True(example.ComputeEffectiveWeight(reference.AddDays(90)) < example.ComputeEffectiveWeight(reference));
    }
}
