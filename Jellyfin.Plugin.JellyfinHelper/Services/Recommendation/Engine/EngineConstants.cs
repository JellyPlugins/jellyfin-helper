using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Central constants used across the recommendation engine components. Extracted from the monolithic RecommendationEngine to support modular architecture.
/// </summary>
internal static class EngineConstants
{
    /// <summary>
    ///     Minimum number of shared watched items required between two users
    ///     before collaborative filtering considers them similar.
    /// </summary>
    internal const int MinCollaborativeOverlap = 3;

    /// <summary>
    ///     Watch-count threshold above which a neighbour is treated as fully trusted in collaborative filtering.
    /// </summary>
    internal const int CollaborativeTrustWatchCeiling = 20;

    /// <summary>
    ///     Scale for the saturating exponential trust curve 1 - exp(-otherCount / scale). A scale of 10 yields ~0.63 trust at 10 watches and ~0.86 at 20 watches, so partially active neighbours are damped gently instead of the sharp linear cliff of the previous formula.
    /// </summary>
    internal const double CollaborativeTrustScale = 10.0;

    /// <summary>
    ///     Minimum weighted contribution before a specific reason (genre, collaborative) is shown.
    /// </summary>
    internal const double ReasonScoreThreshold = 0.05;

    /// <summary>
    ///     Minimum weighted rating contribution before "Highly rated" reason is shown. Rating weights are typically 0.08-0.10, so a threshold of 0.04 requires the normalised community rating to be at least ~0.5 (i.e.
    /// </summary>
    internal const double HighRatingThreshold = 0.04;

    /// <summary>
    ///     Boost factor applied to genres from favorited items when building preferences.
    ///     Favorites count this many times more than regular watched items.
    /// </summary>
    internal const double FavoriteGenreBoostFactor = 3.0;

    /// <summary>
    ///     Half-life (in days) for recency scoring exponential decay.
    /// </summary>
    internal const double RecencyHalfLifeDays = 365.0;

    /// <summary>
    ///     Half-life (in days) for genre preference temporal decay (~180 days). Genres watched recently contribute more than genres watched months ago.
    /// </summary>
    internal const double GenreDecayHalfLifeDays = 180.0;

    /// <summary>
    ///     Gaussian denominator for year proximity (σ² × 2 = 200, so σ ≈ 10 years).
    /// </summary>
    internal const double YearProximityDenominator = 200.0;

    /// <summary>
    ///     Minimum number of watched items required before genre exposure features (GenreUnderexposure, GenreDominanceRatio, GenreAffinityGap) are computed.
    /// </summary>
    internal const int MinWatchCountForGenreExposure = 30;

    /// <summary>
    ///     Genre watch share threshold below which a genre is considered "underexposed." A genre representing less than 2% of the user's total watches is rarely watched.
    /// </summary>
    internal const double GenreUnderexposureThreshold = 0.02;

    /// <summary>
    ///     Number of top genres to consider as the user's "dominant" genres.
    ///     The GenreDominanceRatio feature measures overlap with these top-N genres.
    /// </summary>
    internal const int GenreDominanceTopN = 3;

    /// <summary>
    ///     Maximum allowed recommendations per user (upper clamp for input validation). Distinct from PluginConfiguration.MaxRecommendationsPerUser which is the user-chosen value (default 20).
    /// </summary>
    internal const int MaxRecommendationsPerUserLimit = 100;

    /// <summary>
    ///     MMR diversity trade-off parameter (0 = pure diversity, 1 = pure relevance).
    ///     A value of 0.7 gives strong relevance with meaningful diversity.
    /// </summary>
    internal const double MmrLambda = 0.7;

    /// <summary>
    ///     Rating prior substituted for a FULLY unrated candidate (no community and no critic rating) in the cold-start scalar ranking formula only.
    /// </summary>
    internal const double ColdStartUnratedRatingPrior = 0.30;

    /// <summary>
    ///     Minimum watch completion ratio below which an item is considered "abandoned". Items abandoned by the user receive a penalty in scoring to avoid re-recommending content the user already tried and didn't like.
    /// </summary>
    internal const double AbandonedCompletionThreshold = 0.25;

    /// <summary>
    ///     Soft label ceiling for watched items (not 1.0 to leave headroom and reduce label noise).
    /// </summary>
    internal const double WatchedLabel = 0.85;

    /// <summary>
    ///     Minimum label floor for items the user chose to watch, regardless of completion ratio.
    /// </summary>
    internal const double WatchedLabelFloor = 0.5;

    /// <summary>
    ///     Label for items the user started but abandoned (strong negative signal).
    /// </summary>
    internal const double AbandonedLabel = 0.0;

    /// <summary>
    ///     Label for previously recommended but unwatched items (exposure bias mitigation - user saw the recommendation but didn't engage).
    /// </summary>
    internal const double ExposureLabel = 0.05;

    /// <summary>
    ///     Number of days after a recommendation within which a watch is considered "recommendation-influenced".
    /// </summary>
    internal const double RecommendationInfluenceWindowDays = 7.0;

    /// <summary>
    ///     Training label for items that were recommended AND watched within the RecommendationInfluenceWindowDays window.
    /// </summary>
    internal const double RecommendationInfluencedLabel = 0.90;

    /// <summary>
    ///     Fraction of old training examples retained during incremental training. New examples (since last training) are always included; this fraction controls how many older examples are randomly sampled to prevent catastrophic forgetting while reducing training time.
    /// </summary>
    internal const double IncrementalOldSampleRatio = 0.3;

    /// <summary>
    ///     Minimum number of total examples before incremental subsampling activates. Below this threshold, all examples are used regardless of the incremental flag.
    /// </summary>
    internal const int IncrementalMinExamplesThreshold = 30;

    /// <summary>
    ///     Maximum candidate count before a performance warning is emitted. Libraries with more items than this threshold may experience slower on-demand scoring.
    /// </summary>
    internal const int CandidateCountWarningThreshold = 5000;

    /// <summary>
    ///     Batch size for cancellation token checks inside the candidate scoring loop. Checking every single iteration is wasteful; checking every N items balances responsiveness with overhead.
    /// </summary>
    internal const int CancellationCheckBatchSize = 200;

    /// <summary>
    ///     Minimum number of genres a user must have in their preference vector before genre pre-filtering is applied.
    /// </summary>
    internal const int GenrePreFilterMinPreferences = 3;

    /// <summary>
    ///     Number of recommendation slots reserved for exploration (random picks from remaining candidates instead of MMR selection).
    /// </summary>
    internal const int ExplorationSlotCount = 2;

    /// <summary>
    ///     Divisor for the proportional exploration-slot allocation.
    /// </summary>
    internal const int ExplorationSlotDivisor = 10;

    /// <summary>
    ///     Maximum number of random negative samples added per user during training. These are items recommended to OTHER users that this user never interacted with, providing the model with true "irrelevant" examples to sharpen the decision boundary.
    /// </summary>
    internal const int RandomNegativeSamplesPerUser = 5;

    // Used by TrainingDataBuilder Phase 4 to assign labels to discovery interactions.

    /// <summary>
    ///     Training label for discovery items that were shown but the user took no action.
    ///     Identical to <see cref="ExposureLabel"/> - passive non-engagement.
    /// </summary>
    internal const double DiscoveryShownLabel = ExposureLabel;

    /// <summary>
    ///     Training label for discovery items that the user explicitly dismissed.
    ///     Stronger negative signal than mere exposure - active rejection.
    /// </summary>
    internal const double DiscoveryDismissedLabel = 0.0;

    /// <summary>
    ///     Training label for discovery items that the user requested via Seerr.
    ///     Strong explicit positive signal - the user actively wants this content.
    /// </summary>
    internal const double DiscoveryRequestedLabel = 0.75;

    /// <summary>
    ///     Training label for discovery items that were requested AND subsequently watched.
    /// </summary>
    internal const double DiscoveryRequestedAndWatchedLabel = 0.90;

    /// <summary>
    ///     Sample weight for discovery feedback training examples. Slightly lower than recommendation feedback (1.0) because discovery features lack some signals available for library items (CollaborativeScore, ContentNearestNeighbor).
    /// </summary>
    internal const double DiscoveryFeedbackSampleWeight = 0.6;

    /// <summary>
    ///     Base contribution for a single watched sibling in a BoxSet: 0.3. Used by the diminishing-returns progression formula 0.3 + (n-1) × 0.2, clamped [0,1].
    /// </summary>
    internal const double CollectionProgressionBaseBoost = 0.3;

    /// <summary>
    ///     Per-additional-sibling increment for the diminishing-returns progression formula.
    /// </summary>
    internal const double CollectionProgressionIncrement = 0.2;

    /// <summary>
    ///     Neutral value returned by ComputeSeriesCompletability when the signal does not apply (movies, unknown status).
    /// </summary>
    internal const double SeriesCompletabilityNeutral = 0.5;

    /// <summary>
    ///     Completability value for a finished ("Ended") series - a fully watchable, bounded arc.
    /// </summary>
    internal const double SeriesCompletabilityEnded = 1.0;

    /// <summary>
    ///     Completability value for an ongoing ("Continuing") series - watchable but open-ended.
    /// </summary>
    internal const double SeriesCompletabilityContinuing = 0.5;

    /// <summary>
    ///     Completability value for an unreleased series - nothing to watch yet.
    /// </summary>
    internal const double SeriesCompletabilityUnreleased = 0.0;

    /// <summary>
    ///     Decay scale for ComputeBillingWeight. With scale 4, top-billed (order 0) -> 1.0, 4th-billed -> 0.5, and the weight tails off smoothly for deep-cast/bit-part entries.
    /// </summary>
    internal const double BillingWeightDecayScale = 4.0;

    /// <summary>
    ///     Number of heavy-hitter preferred people used to compute the average weight that drives the weighted-people-similarity denominator.
    /// </summary>
    internal const int WeightedPeopleSimilarityTopK = 20;

    /// <summary>
    ///     Weighted-people-similarity denominator floor.
    /// </summary>
    internal const double WeightedPeopleSimilarityMinDenominator = 5.0;

    /// <summary>
    ///     Exponential decay constant for recency scoring, derived from RecencyHalfLifeDays.
    /// </summary>
    internal static readonly double RecencyDecayConstant = Math.Log(2.0) / RecencyHalfLifeDays;

    /// <summary>
    ///     PersonKind types considered for people similarity scoring. Only actors and directors are used - writers/producers are less predictive of user preference and would add noise to the similarity signal.
    /// </summary>
    internal static readonly IReadOnlyList<PersonKind> RelevantPersonKinds = Array.AsReadOnly(
    [
        PersonKind.Actor, PersonKind.Director
    ]);

    /// <summary>
    ///     Shared collection-progression boost formula used by BOTH the live inference path (Engine.ComputeCollectionProgressionBoostLive) and the training path (TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts) so a copy-drift between the two call sites.
    /// </summary>
    /// <param name="watchedSiblingCount">
    ///     Number of siblings from the same BoxSet the user has watched. Non-positive values
    ///     (including zero) return <c>0.0</c>.
    /// </param>
    /// <returns>A progression boost in <c>[0.0, 1.0]</c>.</returns>
    internal static double ComputeCollectionProgressionBoost(int watchedSiblingCount)
    {
        if (watchedSiblingCount <= 0)
        {
            return 0.0;
        }

        return Math.Clamp(
            CollectionProgressionBaseBoost + ((watchedSiblingCount - 1) * CollectionProgressionIncrement),
            0.0,
            1.0);
    }

    /// <summary>
    ///     Shared series-completability formula used by BOTH the live inference path (Engine.ScoreCandidate) and the training path so the two call sites cannot drift.
    /// </summary>
    /// <param name="isSeries">Whether the candidate is a series.</param>
    /// <param name="seriesStatus">The <c>SeriesStatus</c> name (e.g. "Continuing", "Ended", "Unreleased"), or null.</param>
    /// <param name="hasEndDate">Whether the series has a known end date.</param>
    /// <returns>A completability signal in <c>[0.0, 1.0]</c>; <see cref="SeriesCompletabilityNeutral"/> when N/A.</returns>
    internal static double ComputeSeriesCompletability(bool isSeries, string? seriesStatus, bool hasEndDate)
    {
        if (!isSeries)
        {
            return SeriesCompletabilityNeutral;
        }

        if (string.Equals(seriesStatus, "Ended", StringComparison.OrdinalIgnoreCase))
        {
            return SeriesCompletabilityEnded;
        }

        if (string.Equals(seriesStatus, "Unreleased", StringComparison.OrdinalIgnoreCase))
        {
            return SeriesCompletabilityUnreleased;
        }

        if (string.Equals(seriesStatus, "Continuing", StringComparison.OrdinalIgnoreCase))
        {
            // A known end date on a still-"Continuing" series signals it has effectively wrapped.
            return hasEndDate
                ? (SeriesCompletabilityContinuing + SeriesCompletabilityEnded) / 2.0
                : SeriesCompletabilityContinuing;
        }

        // Unknown / absent status -> neutral (do not bias ranking).
        return SeriesCompletabilityNeutral;
    }

    /// <summary>
    ///     Shared billing-weight formula used by BOTH the live inference path (Engine.ResolveBillingWeightMap) and the training path so the two call sites cannot drift.
    /// </summary>
    /// <param name="sortOrder">The ascending billing position (0 = top-billed).</param>
    /// <returns>A billing weight in <c>(0.0, 1.0]</c>.</returns>
    internal static double ComputeBillingWeight(int sortOrder)
    {
        var order = sortOrder < 0 ? 0 : sortOrder;
        return BillingWeightDecayScale / (BillingWeightDecayScale + order);
    }
}
