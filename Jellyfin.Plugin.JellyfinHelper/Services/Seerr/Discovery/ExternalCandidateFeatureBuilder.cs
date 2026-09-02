using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Builds a CandidateFeatures vector from external TMDb metadata.
/// </summary>
internal static class ExternalCandidateFeatureBuilder
{
    private const int MinPeopleForFullScore = 5;

    private const double PopularityNormalizationCap = 200.0;

    /// <summary>
    ///     Builds a feature vector from a TMDb discover item and user preferences.
    /// </summary>
    /// <param name="candidate">The TMDb discover item.</param>
    /// <param name="genrePreferences">Genre preference vector.</param>
    /// <param name="preferredPeople">Preferred people set.</param>
    /// <param name="avgYear">Average watched year.</param>
    /// <param name="genreExposure">Prebuilt genre exposure analysis.</param>
    /// <param name="profile">The user's watch profile. When supplied, the genre-engagement features (familiarity, average completion, abandon rate) are computed so discovery inference matches DiscoveryFeedbackExampleBuilder training. When null they stay neutral.</param>
    /// <param name="featureMeans">
    ///     Optional per-feature training-set means. When supplied, the features that cannot be computed for an
    ///     external (TMDb) candidate are imputed to their training-set mean instead of an arbitrary 0/0.5
    ///     constant, so under standardization they land at ~0 ("no information") rather than biasing the
    ///     score. Null (no trained model yet) keeps the legacy neutral constants.
    /// </param>
    /// <returns>A populated feature vector.</returns>
    internal static CandidateFeatures Build(
        TmdbDiscoverItem candidate,
        Dictionary<string, double> genrePreferences,
        HashSet<string> preferredPeople,
        double avgYear,
        PreferenceBuilder.GenreExposureAnalysis genreExposure,
        UserWatchProfile? profile = null,
        IReadOnlyList<double>? featureMeans = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(genrePreferences);
        ArgumentNullException.ThrowIfNull(preferredPeople);
        ArgumentNullException.ThrowIfNull(genreExposure);

        // Ensure case insensitive matching.
        if (preferredPeople.Count > 0 && preferredPeople.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            preferredPeople = new HashSet<string>(preferredPeople, StringComparer.OrdinalIgnoreCase);
        }

        var genres = TmdbGenreMap.ToJellyfinGenres(candidate.GenreIds);

        // Genre-engagement package, computed on the same basis as discovery training so the model
        // scores the signal it was trained on. External candidates are never in the watch set, so no
        // self-exclusion is needed (mirrors a genuine unseen candidate at inference).
        var (familiarity, genreAvgCompletion, genreAbandonRate) = profile is not null
            ? ContentScoring.ComputeGenreEngagement(genres, profile)
            : (0.0, 0.5, 0.0);
        var userRatingScore = profile is not null
            ? ContentScoring.ComputeGenreRatingScore(genres, profile)
            : 0.5;

        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(genres, genrePreferences),
            CombinedCriticScore = Math.Clamp(candidate.VoteAverage / 10.0, 0.0, 1.0),
            RecencyScore = candidate.EffectiveReleaseDate is { } releaseDate
                                 && releaseDate.Year is >= 1 and <= 9999
                ? ContentScoring.ComputeRecencyScore(new DateTime(releaseDate.Year, 7, 1, 0, 0, 0, DateTimeKind.Utc))
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(
                candidate.EffectiveReleaseDate?.Year, avgYear),
            GenreCount = genres.Count,
            IsSeries = string.Equals(candidate.MediaType, "tv", StringComparison.OrdinalIgnoreCase),
            PopularityScore = NormalizePopularity(candidate.Popularity),
            PeopleSimilarity = ComputePeopleSimilarity(candidate, preferredPeople),
            CollaborativeScore = 0.5,
            UserRatingScore = userRatingScore,
            HasUserInteraction = familiarity > 0.0,
            CompletionRatio = genreAvgCompletion,
            IsAbandoned = genreAbandonRate,
            StudioMatch = false,
            SeriesProgressionBoost = 0.0,
            DayOfWeekAffinity = 0.5,
            HourOfDayAffinity = 0.5,
            IsWeekend = false,
            TagSimilarity = 0.0,
            LibraryAddedRecency = 0.5,
            ContentNearestNeighborScore = 0.0,
            LanguageAffinity = 0.5,
            SubtitleLanguageAffinity = 0.5,
            CollectionProgressionBoost = 0.0,

            FranchiseAffinity = 0.0,
            ProductionLocationAffinity = 0.0,
            InheritedTagSimilarity = 0.0,
            SeriesCompletability = 0.5,
            WriterAffinity = 0.0,
            BillingWeightedPeople = 0.0,
            GenreStudioIdfPrior = 0.0
        };
        var (underexposure, dominanceRatio, affinityGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(genres, genreExposure);
        features.GenreUnderexposure = underexposure;
        features.GenreDominanceRatio = dominanceRatio;
        features.GenreAffinityGap = affinityGap;

        ApplyMeanImputation(features, featureMeans);

        return features;
    }

    /// <summary>
    ///     Overwrites the features that cannot be computed for an external (TMDb) candidate with their
    ///     training-set means, so a discovery candidate is scored against the distribution the model was
    ///     trained on rather than an arbitrary 0/0.5 constant. Only the continuous placeholder features are
    ///     imputed; the two boolean placeholders (StudioMatch, IsWeekend) stay false because they cannot
    ///     carry a fractional mean through their bool property and false is the genuinely-correct value for
    ///     an unknown candidate (no studio-match signal; IsWeekend is request-time context, not a candidate
    ///     property). Computed features (genre/people/popularity/critic/recency/exposure/engagement) are
    ///     never touched. A null <paramref name="featureMeans" /> (no trained model yet) leaves the legacy
    ///     neutral constants in place.
    /// </summary>
    /// <param name="features">The feature vector to impute in place.</param>
    /// <param name="featureMeans">Per-feature training-set means, or null to skip imputation.</param>
    internal static void ApplyMeanImputation(
        CandidateFeatures features,
        IReadOnlyList<double>? featureMeans)
    {
        ArgumentNullException.ThrowIfNull(features);

        if (featureMeans is null || featureMeans.Count != CandidateFeatures.FeatureCount)
        {
            return;
        }

        features.CollaborativeScore = featureMeans[(int)FeatureIndex.CollaborativeScore];
        features.SeriesProgressionBoost = featureMeans[(int)FeatureIndex.SeriesProgressionBoost];
        features.DayOfWeekAffinity = featureMeans[(int)FeatureIndex.DayOfWeekAffinity];
        features.HourOfDayAffinity = featureMeans[(int)FeatureIndex.HourOfDayAffinity];
        features.TagSimilarity = featureMeans[(int)FeatureIndex.TagSimilarity];
        features.LibraryAddedRecency = featureMeans[(int)FeatureIndex.LibraryAddedRecency];
        features.ContentNearestNeighborScore = featureMeans[(int)FeatureIndex.ContentNearestNeighborScore];
        features.LanguageAffinity = featureMeans[(int)FeatureIndex.LanguageAffinity];
        features.SubtitleLanguageAffinity = featureMeans[(int)FeatureIndex.SubtitleLanguageAffinity];
        features.CollectionProgressionBoost = featureMeans[(int)FeatureIndex.CollectionProgressionBoost];
        features.FranchiseAffinity = featureMeans[(int)FeatureIndex.FranchiseAffinity];
        features.ProductionLocationAffinity = featureMeans[(int)FeatureIndex.ProductionLocationAffinity];
        features.InheritedTagSimilarity = featureMeans[(int)FeatureIndex.InheritedTagSimilarity];
        features.SeriesCompletability = featureMeans[(int)FeatureIndex.SeriesCompletability];
        features.WriterAffinity = featureMeans[(int)FeatureIndex.WriterAffinity];
        features.BillingWeightedPeople = featureMeans[(int)FeatureIndex.BillingWeightedPeople];
        features.GenreStudioIdfPrior = featureMeans[(int)FeatureIndex.GenreStudioIdfPrior];
    }

    /// <summary>
    ///     Normalizes a raw TMDb popularity value into the [0, 1] range used by the PopularityScore feature.
    /// </summary>
    /// <param name="rawPopularity">The raw TMDb popularity value (typically 0-200+).</param>
    /// <returns>A normalized popularity score in [0, 1].</returns>
    internal static double NormalizePopularity(double rawPopularity)
    {
        if (!double.IsFinite(rawPopularity) || rawPopularity <= 0)
        {
            return 0.0;
        }

        return Math.Clamp(rawPopularity / PopularityNormalizationCap, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes people similarity from a list of known people names against preferred people.
    /// </summary>
    /// <param name="knownPeople">The candidate's known people names.</param>
    /// <param name="preferredPeople">The user's preferred people set (case-insensitive).</param>
    /// <returns>A similarity score between 0.0 and 1.0.</returns>
    internal static double ComputePeopleSimilarityFromNames(
        IEnumerable<string>? knownPeople,
        HashSet<string> preferredPeople)
    {
        if (knownPeople == null || preferredPeople.Count == 0)
        {
            return 0.0;
        }

        var overlap = knownPeople
            .Where(p => !string.IsNullOrWhiteSpace(p) && preferredPeople.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return Math.Clamp((double)overlap / Math.Min(preferredPeople.Count, MinPeopleForFullScore), 0.0, 1.0);
    }

    /// <summary>
    ///     Computes people similarity from TMDb discover data.
    /// </summary>
    private static double ComputePeopleSimilarity(
        TmdbDiscoverItem candidate,
        HashSet<string> preferredPeople)
    {
        if (preferredPeople.Count == 0 || candidate.KnownPeople is not { Count: > 0 })
        {
            return 0.0;
        }

        return ComputePeopleSimilarityFromNames(candidate.KnownPeople, preferredPeople);
    }
}