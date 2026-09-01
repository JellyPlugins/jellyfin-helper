using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for ExternalCandidateFeatureBuilder covering the discovery train/serve consistency fixes: Genre-exposure features (GenreUnderexposure / GenreDominanceRatio / GenreAffinityGap) are now computed at inference time to match the discovery training pipeline instead of staying at 0.0.
/// </summary>
public sealed class ExternalCandidateFeatureBuilderTests
{
    // TMDb movie genre id 28 maps to the Jellyfin genre "Action" (see TmdbGenreMap).
    private const int ActionTmdbGenreId = 28;
    private const string ActionGenre = "Action";

    [Theory]
    [InlineData(0.0, 0.0)]      // zero -> neutral zero
    [InlineData(-5.0, 0.0)]     // negative -> coerced to zero
    [InlineData(100.0, 0.5)]    // 100 / 200 cap = 0.5
    [InlineData(200.0, 1.0)]    // exactly at cap
    [InlineData(500.0, 1.0)]    // above cap -> clamped
    public void NormalizePopularity_MapsRawValueIntoUnitRange(double raw, double expected)
    {
        var result = ExternalCandidateFeatureBuilder.NormalizePopularity(raw);

        Assert.Equal(expected, result, 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NormalizePopularity_NonFinite_ReturnsZero(double raw)
    {
        Assert.Equal(0.0, ExternalCandidateFeatureBuilder.NormalizePopularity(raw), 6);
    }

    [Fact]
    public void Build_ValidGenreExposure_PopulatesDominanceForCoreGenre()
    {
        var profile = BuildActionHeavyProfile();
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);
        var avgYear = ContentScoring.ComputeAverageYear(profile);

        var candidate = new TmdbDiscoverItem
        {
            Id = 550,
            MediaType = "movie",
            Title = "Core Genre Match",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 7.5,
            Popularity = 120.0,
            ReleaseDate = new DateTime(2015, 1, 1)
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            genrePrefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            avgYear,
            genreExposure);

        // Previously this was hard-coded to 0.0 at inference (train/serve skew).
        // A candidate whose only genre is the user's dominant genre must now be boosted.
        Assert.True(
            features.GenreDominanceRatio > 0.0,
            $"GenreDominanceRatio should be positive for a core-genre candidate, got {features.GenreDominanceRatio}");
        Assert.Equal(0.0, features.GenreUnderexposure, 6);
    }

    [Fact]
    public void Build_InvalidGenreExposure_LeavesExposureFeaturesNeutral()
    {
        // A user with too little history yields an invalid analysis; behavior must be
        // identical to before the fix (all three exposure features stay at 0.0).
        var shortProfile = new UserWatchProfile { UserId = Guid.NewGuid() };
        shortProfile.WatchedItems.Add(new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(),
            Played = true,
            LastPlayedDate = DateTime.UtcNow.AddDays(-1),
            Genres = [ActionGenre],
            Year = 2015
        });

        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(shortProfile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, shortProfile);

        var candidate = new TmdbDiscoverItem
        {
            Id = 551,
            MediaType = "movie",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 7.0,
            Popularity = 50.0
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            genrePrefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            0.0,
            genreExposure);

        Assert.False(genreExposure.IsValid);
        Assert.Equal(0.0, features.GenreUnderexposure, 6);
        Assert.Equal(0.0, features.GenreDominanceRatio, 6);
        Assert.Equal(0.0, features.GenreAffinityGap, 6);
    }

    [Fact]
    public void Build_PopularityScore_UsesNormalizePopularityHelper()
    {
        var profile = BuildActionHeavyProfile();
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);

        var candidate = new TmdbDiscoverItem
        {
            Id = 552,
            MediaType = "movie",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 6.0,
            Popularity = 150.0
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            genrePrefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            2015.0,
            genreExposure);

        Assert.Equal(
            ExternalCandidateFeatureBuilder.NormalizePopularity(150.0),
            features.PopularityScore,
            6);
        Assert.Equal(0.75, features.PopularityScore, 6); // 150 / 200
    }

    [Fact]
    public void Build_And_TrainingBuilder_AgreeOnPopularityAndExposure()
    {
        var userId = Guid.NewGuid();
        var profile = BuildActionHeavyProfile(userId);
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);
        var avgYear = ContentScoring.ComputeAverageYear(profile);

        const double rawPopularity = 120.0;

        var candidate = new TmdbDiscoverItem
        {
            Id = 603,
            MediaType = "movie",
            Title = "Consistency",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 7.0,
            Popularity = rawPopularity,
            ReleaseDate = new DateTime(2015, 1, 1)
        };

        var inference = ExternalCandidateFeatureBuilder.Build(
            candidate,
            genrePrefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            avgYear,
            genreExposure);

        var feedback = new DiscoveryFeedbackResult { UserId = userId };
        feedback.Entries.Add(new DiscoveryFeedbackEntry
        {
            TmdbId = 603,
            MediaType = "movie",
            Title = "Consistency",
            Year = 2015,
            Genres = [ActionGenre],
            TmdbRating = 7.0,
            Popularity = rawPopularity,
            // Score is deliberately different from the normalized popularity: if training
            // regressed to using entry.Score as PopularityScore, this test would fail.
            Score = 0.9,
            ShownAtUtc = DateTime.UtcNow
        });

        var profileById = new Dictionary<Guid, UserWatchProfile> { [userId] = profile };
        var (examples, count) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(
            [feedback],
            profileById,
            seriesEpisodeCounts: null,
            CancellationToken.None);

        Assert.Equal(1, count);
        var training = examples[0].Features;

        // The four features that were previously skewed must now match exactly.
        Assert.Equal(inference.PopularityScore, training.PopularityScore, 6);
        Assert.Equal(inference.GenreUnderexposure, training.GenreUnderexposure, 6);
        Assert.Equal(inference.GenreDominanceRatio, training.GenreDominanceRatio, 6);
        Assert.Equal(inference.GenreAffinityGap, training.GenreAffinityGap, 6);

        // Popularity specifically resolves to the shared normalization, NOT the past score.
        Assert.Equal(ExternalCandidateFeatureBuilder.NormalizePopularity(rawPopularity), training.PopularityScore, 6);
        Assert.True(
            Math.Abs(0.9 - training.PopularityScore) > 0.01,
            $"PopularityScore must not fall back to the past ensemble score (0.9), got {training.PopularityScore}");
    }

    [Fact]
    public void TrainingBuilder_LegacyEntryWithoutPopularity_MatchesInferenceAndDownweighted()
    {
        // Entries persisted before the Popularity field existed have Popularity == 0.
        var userId = Guid.NewGuid();
        var profile = BuildActionHeavyProfile(userId);

        var feedback = new DiscoveryFeedbackResult { UserId = userId };
        feedback.Entries.Add(new DiscoveryFeedbackEntry
        {
            TmdbId = 604,
            MediaType = "movie",
            Title = "Legacy",
            Year = 2016,
            Genres = [ActionGenre],
            TmdbRating = 6.5,
            Popularity = 0.0, // legacy: not recorded
            Score = 0.42,
            ShownAtUtc = DateTime.UtcNow
        });

        var profileById = new Dictionary<Guid, UserWatchProfile> { [userId] = profile };
        var (examples, count) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(
            [feedback],
            profileById,
            seriesEpisodeCounts: null,
            CancellationToken.None);

        Assert.Equal(1, count);

        // Train/serve parity: training path must produce the exact same value inference
        //     would for a missing/zero popularity. NormalizePopularity(0) == 0.0.
        Assert.Equal(
            ExternalCandidateFeatureBuilder.NormalizePopularity(0.0),
            examples[0].Features.PopularityScore,
            6);

        Assert.True(
            Math.Abs(0.42 - examples[0].Features.PopularityScore) > 0.01,
            $"PopularityScore must not leak entry.Score (0.42), got {examples[0].Features.PopularityScore}");

        Assert.True(
            Math.Abs(0.5 - examples[0].Features.PopularityScore) > 0.01,
            $"PopularityScore must not diverge from inference by falling back to 0.5, got {examples[0].Features.PopularityScore}");

        // Provenance down-weighting stays intact so legacy rows still train the model
        //     but at reduced gradient contribution.
        Assert.Equal(
            EngineConstants.DiscoveryFeedbackSampleWeight * 0.5,
            examples[0].SampleWeight,
            6);
    }

    [Fact]
    public void TrainingBuilder_ModernEntryWithPopularity_UsesFullSampleWeight()
    {
        // Contract counterpart to the legacy-fallback test: entries WITH a persisted popularity
        // must retain the full DiscoveryFeedbackSampleWeight and use the normalized popularity.
        var userId = Guid.NewGuid();
        var profile = BuildActionHeavyProfile(userId);

        var feedback = new DiscoveryFeedbackResult { UserId = userId };
        feedback.Entries.Add(new DiscoveryFeedbackEntry
        {
            TmdbId = 605,
            MediaType = "movie",
            Title = "Modern",
            Year = 2020,
            Genres = [ActionGenre],
            TmdbRating = 7.5,
            Popularity = 120.0,
            Score = 0.9,
            ShownAtUtc = DateTime.UtcNow
        });

        var profileById = new Dictionary<Guid, UserWatchProfile> { [userId] = profile };
        var (examples, count) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(
            [feedback],
            profileById,
            seriesEpisodeCounts: null,
            CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(
            ExternalCandidateFeatureBuilder.NormalizePopularity(120.0),
            examples[0].Features.PopularityScore,
            6);
        Assert.Equal(EngineConstants.DiscoveryFeedbackSampleWeight, examples[0].SampleWeight, 6);
    }

    [Fact]
    public void Build_WithProfile_ComputesGenreEngagement_MatchingTrainingPath()
    {
        // Train/serve parity: discovery training (DiscoveryFeedbackExampleBuilder) sets the three
        // interaction features from ComputeGenreEngagement. Inference must produce the same values for
        // the same genres and profile, otherwise the model scores a signal it was never trained on.
        var profile = BuildActionHeavyProfile();
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);
        var avgYear = ContentScoring.ComputeAverageYear(profile);

        var candidate = new TmdbDiscoverItem
        {
            Id = 550,
            MediaType = "movie",
            Title = "Action Candidate",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 7.5,
            Popularity = 120.0,
            ReleaseDate = new DateTime(2015, 1, 1)
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            genrePrefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            avgYear,
            genreExposure,
            profile);

        var (familiarity, avgCompletion, abandonRate) =
            ContentScoring.ComputeGenreEngagement([ActionGenre], profile);

        Assert.Equal(familiarity > 0.0, features.HasUserInteraction);
        Assert.Equal(avgCompletion, features.CompletionRatio, 6);
        Assert.Equal(abandonRate, features.IsAbandoned, 6);

        // The profile is 35 fully-played Action items, so engagement is real, not neutral.
        Assert.True(features.HasUserInteraction);
        Assert.Equal(1.0, features.CompletionRatio, 6);
        Assert.Equal(0.0, features.IsAbandoned, 6);
    }

    [Fact]
    public void Build_WithoutProfile_LeavesGenreEngagementNeutral()
    {
        // Overload compatibility: callers that do not supply a profile keep the pre-fix neutral values.
        var profile = BuildActionHeavyProfile();
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);
        var avgYear = ContentScoring.ComputeAverageYear(profile);

        var candidate = new TmdbDiscoverItem
        {
            Id = 551,
            MediaType = "movie",
            Title = "No Profile",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 7.5,
            Popularity = 120.0,
            ReleaseDate = new DateTime(2015, 1, 1)
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate, genrePrefs, new HashSet<string>(StringComparer.OrdinalIgnoreCase), avgYear, genreExposure);

        Assert.False(features.HasUserInteraction);
        Assert.Equal(0.5, features.CompletionRatio, 6);
        Assert.Equal(0.0, features.IsAbandoned, 6);
    }

    /// <summary>
    ///     Builds a watch profile with enough Action history to yield a valid genre-exposure analysis (>= MinWatchCountForGenreExposure items) where Action is the dominant genre.
    /// </summary>
    private static UserWatchProfile BuildActionHeavyProfile(Guid? userId = null)
    {
        var profile = new UserWatchProfile { UserId = userId ?? Guid.NewGuid() };

        for (var i = 0; i < 35; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = DateTime.UtcNow.AddDays(-i),
                Genres = [ActionGenre],
                Year = 2015
            });
        }

        return profile;
    }
}
