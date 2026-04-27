using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Centralized default feature weights for recommendation scoring strategies.
///     Both <see cref="HeuristicScoringStrategy"/> (as fixed weights) and
///     <see cref="LearnedScoringStrategy"/> (as initial weights before training)
///     reference these values to ensure consistency and eliminate duplication.
/// </summary>
public static class DefaultWeights
{
    /// <summary>Weight for genre similarity signal.</summary>
    public const double GenreSimilarity = 0.20;

    /// <summary>Weight for collaborative filtering signal.</summary>
    public const double CollaborativeScore = 0.09;

    /// <summary>Weight for combined critic score signal (TMDb 55% + Tomatometer 45%).</summary>
    public const double CombinedCriticScore = 0.07;

    /// <summary>Weight for recency signal.</summary>
    public const double RecencyScore = 0.06;

    /// <summary>Weight for year proximity signal.</summary>
    public const double YearProximityScore = 0.05;

    /// <summary>Weight for normalized genre count signal. Near-neutral — ML can learn if it matters.</summary>
    public const double GenreCountNormalized = 0.005;

    /// <summary>
    ///     Weight for series type signal. Near-neutral initial weight — does not blindly prefer
    ///     series over movies. The ML model learns whether the user prefers series based on
    ///     their actual watch patterns.
    /// </summary>
    public const double IsSeries = 0.01;

    /// <summary>Weight for genre × combined critic interaction signal.</summary>
    public const double GenreCriticInteraction = 0.05;

    /// <summary>Weight for genre × collaborative interaction signal.</summary>
    public const double GenreCollabInteraction = 0.05;

    /// <summary>Weight for user personal rating signal (stronger than community rating).</summary>
    public const double UserRatingScore = 0.09;

    /// <summary>
    ///     Weight for watch completion ratio (positive signal — rewards fully watched items).
    ///     Works together with the companion <see cref="IsAbandoned"/> feature which penalizes
    ///     abandoned ones.
    /// </summary>
    public const double CompletionRatio = 0.07;

    /// <summary>
    ///     Negative weight for abandoned items (CompletionRatio &lt; 25%).
    ///     Penalizes items the user started but stopped watching early, which is a strong
    ///     signal that the user didn't enjoy the content. The negative weight ensures these
    ///     items are deprioritized in recommendations.
    /// </summary>
    public const double IsAbandoned = -0.04;

    /// <summary>
    ///     Weight for has-interaction signal (1 if user interacted, 0 for new candidates).
    ///     A small positive weight that gives a slight boost to items the user has some
    ///     history with, allowing the model to distinguish "not yet seen" from "abandoned".
    /// </summary>
    public const double HasInteraction = 0.005;

    /// <summary>
    ///     Weight for people (cast/director) similarity signal (increased from 0.05 during genre rebalance).
    ///     Items featuring actors or directors from the user's watched content get a boost.
    ///     Cast/director overlap is a strong content-based signal for user preference.
    /// </summary>
    public const double PeopleSimilarity = 0.06;

    /// <summary>
    ///     Weight for studio match signal.
    ///     Items from studios the user has watched before get a small positive boost.
    /// </summary>
    public const double StudioMatch = 0.02;

    /// <summary>
    ///     Weight for series progression boost signal.
    ///     Rewards follow-up seasons when the user has watched earlier seasons of the same series,
    ///     encouraging "continue watching" style recommendations.
    /// </summary>
    public const double SeriesProgressionBoost = 0.06;

    /// <summary>
    ///     Weight for popularity score signal.
    ///     Provides a baseline signal from global watch counts, particularly useful for cold-start
    ///     users who have little personal history. The ML model can adjust this weight down as
    ///     personalized signals strengthen.
    /// </summary>
    public const double PopularityScore = 0.005;

    /// <summary>
    ///     Weight for day-of-week affinity signal.
    ///     Captures temporal viewing patterns (e.g., comedies on weekends, dramas on weeknights).
    ///     A small weight so it acts as a tiebreaker rather than a dominant signal.
    /// </summary>
    public const double DayOfWeekAffinity = 0.015;

    /// <summary>
    ///     Weight for hour-of-day affinity signal.
    ///     Captures intra-day viewing patterns (e.g., lighter content in the morning,
    ///     thrillers in the evening). Acts as a contextual tiebreaker.
    /// </summary>
    public const double HourOfDayAffinity = 0.02;

    /// <summary>
    ///     Weight for weekend flag signal.
    ///     Captures weekend vs. weekday viewing preference differences.
    ///     A small contextual weight that allows the model to learn that users
    ///     may prefer different content types on weekends.
    /// </summary>
    /// <remarks>Micro-trimmed from 0.01 for LanguageAffinity budget.</remarks>
    public const double IsWeekend = 0.005;

    /// <summary>
    ///     Weight for tag-based content similarity signal.
    ///     Measures Jaccard overlap between the candidate's tags and the user's
    ///     preferred tags derived from watch history. Complements genre similarity
    ///     with more fine-grained content categorization.
    /// </summary>
    public const double TagSimilarity = 0.015;

    /// <summary>
    ///     Weight for people × genre interaction (actors you like in genres you prefer).
    /// </summary>
    public const double PeopleGenreInteraction = 0.03;

    /// <summary>
    ///     Weight for recency × combined critic interaction (trending: new + highly rated).
    /// </summary>
    public const double RecencyCriticInteraction = 0.03;

    /// <summary>
    ///     Negative weight for genre underexposure signal.
    ///     Items whose genres the user rarely watches receive a soft penalty.
    ///     Moderate (-0.12) to effectively counterbalance collaborative signals from
    ///     similar users who watch different genres. "Rarely watched" ≠ "disliked"
    ///     but should noticeably reduce ranking vs. familiar-genre items.
    /// </summary>
    public const double GenreUnderexposure = -0.12;

    /// <summary>
    ///     Positive weight for genre dominance ratio signal.
    ///     Items matching the user's core genres (top-3) get a boost.
    ///     Complements GenreSimilarity with a "strength of preference" signal
    ///     rather than just "breadth of match".
    /// </summary>
    public const double GenreDominanceRatio = 0.10;

    /// <summary>
    ///     Negative weight for genre affinity gap signal.
    ///     Items whose genres are well below the user's average preference weight
    ///     receive a soft penalty. Measures "distance from comfort zone."
    ///     Moderate negative weight (-0.08) to meaningfully deprioritize items
    ///     far from the user's genre comfort zone while still allowing discovery.
    /// </summary>
    public const double GenreAffinityGap = -0.08;

    /// <summary>
    ///     Weight for library-added recency signal.
    ///     Captures new additions to the user's collection separately from content release date.
    ///     A small weight so it acts as a supplementary freshness signal.
    /// </summary>
    public const double LibraryAddedRecency = 0.03;

    /// <summary>
    ///     Weight for content-based nearest-neighbor signal.
    ///     Composite item-to-item similarity (genre 50%, people 30%, studio 20%) between
    ///     the candidate and the user's most similar watched item. Near-neutral initial
    ///     weight (0.02) because it partially overlaps with GenreSimilarity and PeopleSimilarity
    ///     at the profile level — this feature adds the item-to-item perspective. The ML model
    ///     can learn the optimal weight through training.
    /// </summary>
    public const double ContentNearestNeighborScore = 0.02;

    /// <summary>
    ///     Weight for audio language affinity signal.
    ///     Items available in the user's preferred audio language get a moderate boost.
    ///     For monolingual libraries (all items same language), this feature is constant
    ///     and the weight effectively becomes zero (no ranking impact).
    ///     Budget sourced via proportional micro-trim from 6 near-neutral features
    ///     (GenreCountNormalized, HasInteraction, PopularityScore, IsWeekend,
    ///     DayOfWeekAffinity, TagSimilarity) — each trimmed by 0.005, totaling 0.03.
    /// </summary>
    public const double LanguageAffinity = 0.03;

    /// <summary>Default bias term for the learned strategy.</summary>
    public const double Bias = 0.05;

    /// <summary>
    ///     Creates a weight array indexed by <see cref="FeatureIndex"/> with all default values.
    /// </summary>
    /// <returns>A new array with <see cref="CandidateFeatures.FeatureCount"/> elements.</returns>
    public static double[] CreateWeightArray()
    {
        // Guard: every FeatureIndex enum value must map to a valid slot.
        // If a new FeatureIndex is added without updating FeatureCount, this fires immediately.
        var featureIndexCount = Enum.GetValues<FeatureIndex>().Length;
        if (featureIndexCount != CandidateFeatures.FeatureCount)
        {
            throw new InvalidOperationException(
                $"FeatureIndex count ({featureIndexCount}) must match CandidateFeatures.FeatureCount ({CandidateFeatures.FeatureCount}).");
        }

        var weights = new double[CandidateFeatures.FeatureCount];
        var assigned = new bool[CandidateFeatures.FeatureCount];

        void Set(FeatureIndex idx, double value)
        {
            var i = (int)idx;
            weights[i] = value;
            assigned[i] = true;
        }

        Set(FeatureIndex.GenreSimilarity, GenreSimilarity);
        Set(FeatureIndex.CollaborativeScore, CollaborativeScore);
        Set(FeatureIndex.CombinedCriticScore, CombinedCriticScore);
        Set(FeatureIndex.RecencyScore, RecencyScore);
        Set(FeatureIndex.YearProximityScore, YearProximityScore);
        Set(FeatureIndex.GenreCountNormalized, GenreCountNormalized);
        Set(FeatureIndex.IsSeries, IsSeries);
        Set(FeatureIndex.GenreCriticInteraction, GenreCriticInteraction);
        Set(FeatureIndex.GenreCollabInteraction, GenreCollabInteraction);
        Set(FeatureIndex.UserRatingScore, UserRatingScore);
        Set(FeatureIndex.CompletionRatio, CompletionRatio);
        Set(FeatureIndex.IsAbandoned, IsAbandoned);
        Set(FeatureIndex.HasInteraction, HasInteraction);
        Set(FeatureIndex.PeopleSimilarity, PeopleSimilarity);
        Set(FeatureIndex.StudioMatch, StudioMatch);
        Set(FeatureIndex.SeriesProgressionBoost, SeriesProgressionBoost);
        Set(FeatureIndex.PopularityScore, PopularityScore);
        Set(FeatureIndex.DayOfWeekAffinity, DayOfWeekAffinity);
        Set(FeatureIndex.HourOfDayAffinity, HourOfDayAffinity);
        Set(FeatureIndex.IsWeekend, IsWeekend);
        Set(FeatureIndex.TagSimilarity, TagSimilarity);
        Set(FeatureIndex.PeopleGenreInteraction, PeopleGenreInteraction);
        Set(FeatureIndex.RecencyCriticInteraction, RecencyCriticInteraction);
        Set(FeatureIndex.GenreUnderexposure, GenreUnderexposure);
        Set(FeatureIndex.GenreDominanceRatio, GenreDominanceRatio);
        Set(FeatureIndex.GenreAffinityGap, GenreAffinityGap);
        Set(FeatureIndex.LibraryAddedRecency, LibraryAddedRecency);
        Set(FeatureIndex.ContentNearestNeighborScore, ContentNearestNeighborScore);
        Set(FeatureIndex.LanguageAffinity, LanguageAffinity);

        // Guard: detect missing per-index assignments. The count check above catches
        // new enum values without FeatureCount bump, but this catches the more likely
        // failure mode of adding a new enum+count without adding a Set() call.
        for (var i = 0; i < assigned.Length; i++)
        {
            if (!assigned[i])
            {
                throw new InvalidOperationException(
                    $"DefaultWeights.CreateWeightArray is missing an assignment for FeatureIndex slot {i} ({(FeatureIndex)i}).");
            }
        }

        return weights;
    }
}