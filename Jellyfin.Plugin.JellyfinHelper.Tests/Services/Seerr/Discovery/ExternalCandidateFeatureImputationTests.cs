using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for the discovery mean-imputation: features that cannot be computed for an external
///     (TMDb) candidate are imputed to the model's persisted training-set means instead of arbitrary
///     0/0.5 constants, so a discovery candidate scores against the trained distribution.
/// </summary>
public sealed class ExternalCandidateFeatureImputationTests
{
    private const int ActionTmdbGenreId = 28;
    private const string ActionGenre = "Action";

    // A recognizable, distinct mean per feature index so the index→property mapping can be asserted exactly.
    private static double[] DistinctMeans()
    {
        var means = new double[CandidateFeatures.FeatureCount];
        for (var i = 0; i < means.Length; i++)
        {
            // Keep values inside [0,1] so the CandidateFeatures setters (which clamp) do not distort them.
            means[i] = (i + 1) / 100.0;
        }

        return means;
    }

    [Fact]
    public void ApplyMeanImputation_SuppliedMeans_OverwritesContinuousPlaceholders()
    {
        var means = DistinctMeans();
        var features = new CandidateFeatures();

        ExternalCandidateFeatureBuilder.ApplyMeanImputation(features, means);

        Assert.Equal(means[(int)FeatureIndex.CollaborativeScore], features.CollaborativeScore, 9);
        Assert.Equal(means[(int)FeatureIndex.TagSimilarity], features.TagSimilarity, 9);
        Assert.Equal(means[(int)FeatureIndex.ContentNearestNeighborScore], features.ContentNearestNeighborScore, 9);
        Assert.Equal(means[(int)FeatureIndex.LanguageAffinity], features.LanguageAffinity, 9);
        Assert.Equal(means[(int)FeatureIndex.FranchiseAffinity], features.FranchiseAffinity, 9);
        Assert.Equal(means[(int)FeatureIndex.BillingWeightedPeople], features.BillingWeightedPeople, 9);
        Assert.Equal(means[(int)FeatureIndex.GenreStudioIdfPrior], features.GenreStudioIdfPrior, 9);
        Assert.Equal(means[(int)FeatureIndex.SeriesCompletability], features.SeriesCompletability, 9);
    }

    [Fact]
    public void ApplyMeanImputation_BoolPlaceholders_StayFalse()
    {
        // StudioMatch / IsWeekend are bool-typed; a fractional mean cannot flow through them and false is the
        // correct value for an unknown external candidate, so imputation must leave them false.
        var features = new CandidateFeatures { StudioMatch = false, IsWeekend = false };

        ExternalCandidateFeatureBuilder.ApplyMeanImputation(features, DistinctMeans());

        Assert.False(features.StudioMatch);
        Assert.False(features.IsWeekend);
    }

    [Fact]
    public void ApplyMeanImputation_NullMeans_LeavesFeaturesUnchanged()
    {
        var features = new CandidateFeatures { CollaborativeScore = 0.5, TagSimilarity = 0.0 };

        ExternalCandidateFeatureBuilder.ApplyMeanImputation(features, featureMeans: null);

        Assert.Equal(0.5, features.CollaborativeScore, 9);
        Assert.Equal(0.0, features.TagSimilarity, 9);
    }

    [Fact]
    public void ApplyMeanImputation_WrongLengthMeans_LeavesFeaturesUnchanged()
    {
        var features = new CandidateFeatures { CollaborativeScore = 0.5 };

        ExternalCandidateFeatureBuilder.ApplyMeanImputation(features, [0.1, 0.2, 0.3]);

        Assert.Equal(0.5, features.CollaborativeScore, 9);
    }

    [Fact]
    public void Build_WithMeans_ImputesPlaceholdersButKeepsComputedFeatures()
    {
        var profile = BuildActionHeavyProfile();
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);
        var means = DistinctMeans();

        var candidate = new TmdbDiscoverItem
        {
            Id = 700,
            MediaType = "movie",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 8.0,
            Popularity = 100.0
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            genrePrefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            avgYear: 2015,
            genreExposure,
            profile,
            means);

        // Placeholder features are imputed to their means.
        Assert.Equal(means[(int)FeatureIndex.CollaborativeScore], features.CollaborativeScore, 9);
        Assert.Equal(means[(int)FeatureIndex.LanguageAffinity], features.LanguageAffinity, 9);

        // Computed features are untouched: GenreSimilarity is high (candidate genre matches the profile) and
        // PopularityScore follows the 100/200 normalization, not a mean.
        Assert.True(features.GenreSimilarity > 0.0);
        Assert.Equal(0.5, features.PopularityScore, 9);
        Assert.Equal(0.8, features.CombinedCriticScore, 9);
    }

    [Fact]
    public void Build_WithoutMeans_KeepsLegacyNeutralConstants()
    {
        var profile = BuildActionHeavyProfile();
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);

        var candidate = new TmdbDiscoverItem
        {
            Id = 701,
            MediaType = "movie",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 8.0,
            Popularity = 100.0
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            genrePrefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            avgYear: 2015,
            genreExposure,
            profile);

        // Legacy neutral constants remain when no means are supplied.
        Assert.Equal(0.5, features.CollaborativeScore, 9);
        Assert.Equal(0.0, features.TagSimilarity, 9);
        Assert.Equal(0.0, features.FranchiseAffinity, 9);
    }

    private static UserWatchProfile BuildActionHeavyProfile()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 40; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = DateTime.UtcNow.AddDays(-i),
                Genres = [ActionGenre]
            });
        }

        return profile;
    }
}
