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
    public void LegacyRecommendation_UsesPredictionTimeWithoutInventingAnOtherUsersNegative(bool hasPremiere)
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var item = Guid.NewGuid();
        var recommendation = new RecommendedItem { ItemId = item, ItemType = "Movie", Genres = ["Drama"], PremiereDate = hasPremiere ? Release : null, DateCreated = Added };
        var result = new RecommendationResult { UserId = owner, GeneratedAt = Prediction, Recommendations = [recommendation] };
        var ownerProfile = new UserWatchProfile { UserId = owner, WatchedItems = [new WatchedItemInfo { ItemId = item, ItemType = "Movie", Played = true, LastPlayedDate = Prediction.AddDays(2) }] };
        var otherProfile = new UserWatchProfile { UserId = other };

        var built = TrainingDataBuilder.BuildExamples([result], [ownerProfile, otherProfile], TestContext.Current.CancellationToken);

        Assert.Equal(0, built.RandomNegativeCount);
        Assert.Single(built.Examples);
        var positive = Assert.Single(built.Examples, example => example.UserId == owner);
        Assert.True(positive.IsObserved);
        Assert.DoesNotContain(built.Examples, example => example.UserId == other);
        AssertRecency(positive, hasPremiere ? Release : Added, Added, Prediction);
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
        Guid chosen;
        do { chosen = Guid.NewGuid(); } while (new TrainingExample { Features = new CandidateFeatures(), ObservationId = TrainingExample.CreateLibraryObservationId(user, chosen), HasStableObservationId = true }.IsPermanentHoldout);
        var result = new RecommendationResult { UserId = user, GeneratedAt = Prediction, Recommendations = contrast ? [new RecommendedItem { ItemId = chosen, ItemType = "Movie", DateCreated = Added, ScoringFeatures = new CandidateFeatures() }, recommendation] : [recommendation], TrainingContrasts = [] };
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = contrast ? chosen : item, ItemType = "Movie", Played = true, DateCreated = Added, LastPlayedDate = Prediction.AddDays(2) }] };

        var example = Assert.Single(TrainingDataBuilder.BuildExamples([result], [profile], Prediction.AddDays(3), TestContext.Current.CancellationToken).Examples,
            candidate => candidate.ObservationId == TrainingExample.CreateLibraryObservationId(user, item));

        Assert.Equal(stored.ToVector(), example.Features.ToVector());
        Assert.False(example.Features.HasGenreEvidence);
        Assert.NotSame(stored, example.Features);
        Assert.Equal(Prediction, example.GeneratedAtUtc);
        Assert.Equal(contrast, example.IsContrastSample);
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

    [Theory]
    [InlineData("tv")]
    [InlineData("TV")]
    [InlineData(" TV ")]
    public void LegacyTvTypeEngagementSuppressesPassiveShownSamplesInsteadOfTreatingTheEpisodeAsAMovie(string type)
    {
        var user = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var entry = new DiscoveryFeedbackEntry { TmdbId = 700, MediaType = "tv", ShownAtUtc = now.AddDays(-40), ImpressionAtUtc = now.AddDays(-30), Genres = ["Drama"], ScoringFeatures = new CandidateFeatures { IsExternalCandidate = true, IsSeries = true, HasGenreEvidence = true, GenreSimilarity = .8 } };
        // A dormant legacy row can predate setter normalization; physical TV
        // engagement must still match its typed title rather than an episode ID.
        typeof(DiscoveryFeedbackEntry).GetField("_mediaType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(entry, type);
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new() { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = Guid.NewGuid(), SeriesTmdbId = 700, TmdbId = 9000, Played = true, LastPlayedDate = now.AddDays(-20) }] };
        var examples = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([new() { UserId = user, Entries = [entry] }], new Dictionary<Guid, UserWatchProfile> { [user] = profile }, null, null, TestContext.Current.CancellationToken);
        Assert.Empty(examples.Examples);
        var control = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([new() { UserId = user, Entries = [entry] }], new Dictionary<Guid, UserWatchProfile>(), null, null, TestContext.Current.CancellationToken);
        Assert.NotEmpty(control.Examples);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void WriterSemanticsHistoricalFeaturesUsePredictionNotFittingClock(int earlierRows)
    {
        var user = Guid.NewGuid();
        var target = Guid.NewGuid();
        var rows = Enumerable.Range(1, earlierRows).Select(i => new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, LastPlayedDate = Prediction.AddDays(-i), Genres = ["Drama"], WriterNames = ["Writer A"], IsFavorite = true, UserRating = 9 }).ToList();
        rows.Add(new() { ItemId = target, ItemType = "Movie", Played = true, LastPlayedDate = Prediction, Genres = ["Drama"], WriterNames = ["Writer A"] });
        foreach (var row in rows)
        {
            // This fixture explicitly observed these credits at the historical
            // boundary; undated current credits must not repair an older sample.
            row.Credits = new CreditMetadataSnapshot
            {
                SourceItemId = row.ItemId,
                Crew = new CreditField { Complete = true, AcquiredAtUtc = row.LastPlayedDate, People = [new() { Name = "Writer A", Kind = Jellyfin.Data.Enums.PersonKind.Writer }] }
            };
        }
        var profile = new UserWatchProfile { UserId = user, WatchedItems = new System.Collections.ObjectModel.Collection<WatchedItemInfo>(rows) };
        var expectedWeight = Enumerable.Range(1, earlierRows).Sum(i => Math.Exp(-Math.Log(2) / 180 * i));
        var expected = SimilarityComputer.ComputeWriterAffinity(["Writer A"], new Dictionary<string, double> { ["Writer A"] = expectedWeight });
        var key = TrainingExample.CreateLibraryObservationId(user, target);
        foreach (var fitting in new[] { Prediction.AddDays(2), Prediction.AddDays(182) })
        {
            var example = Assert.Single(TrainingDataBuilder.BuildExamples([], [profile], fitting, TestContext.Current.CancellationToken).Examples, value => value.ObservationId == key);
            Assert.Equal(Prediction, example.GeneratedAtUtc);
            Assert.Equal(expected, example.Features.WriterAffinity, 10);
            Assert.Equal(41, example.Features.ToVector().Length);
        }
        Assert.All(rows.Take(earlierRows), row => Assert.True(row.IsFavorite));
    }

    [Fact]
    public void WriterSemanticsDirectWeightsUseOneProvidedUtcClockAndRecordedVectorsStayImmutable()
    {
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new() { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, LastPlayedDate = Prediction.AddDays(-1).ToLocalTime(), WriterNames = ["Writer A"] }] };
        var initial = PreferenceBuilder.BuildWriterPreferenceWeights(profile, Prediction)["Writer A"];
        var later = PreferenceBuilder.BuildWriterPreferenceWeights(profile, Prediction.AddDays(180))["Writer A"];
        Assert.Equal(Math.Exp(-Math.Log(2) / 180), initial, 10);
        Assert.Equal(initial * .5, later, 10);
        var item = Guid.NewGuid();
        var stored = new CandidateFeatures { WriterAffinity = .72, DayOfWeekAffinity = .31, HourOfDayAffinity = .41, CombinedCriticScore = .123, PopularityScore = .456 };
        var raw = stored.ToVector();
        var result = new RecommendationResult { UserId = user, GeneratedAt = Prediction, Recommendations = [new() { ItemId = item, ItemType = "Movie", ScoringFeatures = stored }] };
        profile.WatchedItems.Add(new() { ItemId = item, ItemType = "Movie", Played = true, LastPlayedDate = Prediction.AddDays(1) });
        foreach (var fitting in new[] { Prediction.AddDays(2), Prediction.AddDays(182) })
        {
            var example = Assert.Single(TrainingDataBuilder.BuildExamples([result], [profile], fitting, TestContext.Current.CancellationToken).Examples, value => value.ObservationId == TrainingExample.CreateLibraryObservationId(user, item));
            Assert.Equal(raw, example.Features.ToVector());
            Assert.Equal(Prediction, example.GeneratedAtUtc);
        }
        Assert.Equal(raw, stored.ToVector());
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
