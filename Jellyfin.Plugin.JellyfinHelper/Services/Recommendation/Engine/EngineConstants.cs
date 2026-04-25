using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Central constants used across the recommendation engine components.
///     Extracted from the monolithic RecommendationEngine to support modular architecture.
/// </summary>
internal static class EngineConstants
{
    /// <summary>
    ///     Minimum number of shared watched items required between two users
    ///     before collaborative filtering considers them similar.
    /// </summary>
    internal const int MinCollaborativeOverlap = 3;

    /// <summary>
    ///     Minimum weighted contribution before a specific reason (genre, collaborative) is shown.
    ///     Must be low enough to work across strategies whose weights differ
    ///     (e.g. collaborative weight 0.12–0.15 → max contribution 0.12–0.15).
    /// </summary>
    internal const double ReasonScoreThreshold = 0.05;

    /// <summary>
    ///     Minimum weighted rating contribution before "Highly rated" reason is shown.
    ///     Rating weights are typically 0.08–0.10, so a threshold of 0.04 requires
    ///     the normalised community rating to be at least ~0.5 (i.e. ≥ 5.0/10).
    /// </summary>
    internal const double HighRatingThreshold = 0.04;

    /// <summary>
    ///     Boost factor applied to genres from favorited items when building preferences.
    ///     Favorites count this many times more than regular watched items.
    /// </summary>
    internal const double FavoriteGenreBoostFactor = 3.0;

    /// <summary>
    ///     Exponential decay constant for recency scoring (~365 day half-life).
    /// </summary>
    internal const double RecencyDecayConstant = 0.0019;

    /// <summary>
    ///     Gaussian denominator for year proximity (σ² × 2 = 200, so σ ≈ 10 years).
    /// </summary>
    internal const double YearProximityDenominator = 200.0;

    /// <summary>
    ///     Minimum number of watched items required before genre exposure features
    ///     (GenreUnderexposure, GenreDominanceRatio, GenreAffinityGap) are computed.
    ///     Below this threshold, all three features default to 0 (neutral) to avoid
    ///     drawing conclusions from insufficient data.
    /// </summary>
    internal const int MinWatchCountForGenreExposure = 30;

    /// <summary>
    ///     Genre watch share threshold below which a genre is considered "underexposed."
    ///     A genre representing less than 2% of the user's total watches is rarely watched.
    ///     This is deliberately low to avoid penalizing genres that the user watches
    ///     occasionally — only genres with very minimal or zero presence are flagged.
    /// </summary>
    internal const double GenreUnderexposureThreshold = 0.02;

    /// <summary>
    ///     Number of top genres to consider as the user's "dominant" genres.
    ///     The GenreDominanceRatio feature measures overlap with these top-N genres.
    /// </summary>
    internal const int GenreDominanceTopN = 3;

    /// <summary>
    ///     Maximum allowed recommendations per user (upper clamp for input validation).
    ///     Distinct from <c>PluginConfiguration.MaxRecommendationsPerUser</c> which is the
    ///     user-chosen value (default 20). This constant defines the hard upper bound.
    /// </summary>
    internal const int MaxRecommendationsPerUserLimit = 100;

    /// <summary>
    ///     MMR diversity trade-off parameter (0 = pure diversity, 1 = pure relevance).
    ///     A value of 0.7 gives strong relevance with meaningful diversity.
    /// </summary>
    internal const double MmrLambda = 0.7;

    /// <summary>
    ///     Minimum watch completion ratio below which an item is considered "abandoned".
    ///     Items abandoned by the user receive a penalty in scoring to avoid re-recommending
    ///     content the user already tried and didn't like.
    /// </summary>
    internal const double AbandonedCompletionThreshold = 0.25;

    /// <summary>
    ///     Soft label ceiling for watched items (not 1.0 to leave headroom and reduce label noise).
    /// </summary>
    internal const double WatchedLabel = 0.85;

    /// <summary>
    ///     Minimum label floor for items the user chose to watch, regardless of completion ratio.
    ///     Ensures that even low-completion watched items are treated as positive examples.
    /// </summary>
    internal const double WatchedLabelFloor = 0.5;

    /// <summary>
    ///     Label for items the user started but abandoned (strong negative signal).
    /// </summary>
    internal const double AbandonedLabel = 0.0;

    /// <summary>
    ///     Label for previously recommended but unwatched items (exposure bias mitigation —
    ///     user saw the recommendation but didn't engage). Kept very close to zero
    ///     to clearly separate "ignored" from "barely started" (WatchedLabelFloor = 0.5).
    /// </summary>
    internal const double ExposureLabel = 0.05;

    /// <summary>
    ///     Number of days after a recommendation within which a watch is considered
    ///     "recommendation-influenced". Items watched within this window receive a
    ///     higher training label (<see cref="RecommendationInfluencedLabel"/>) to
    ///     reward the model for successfully influencing user behavior.
    /// </summary>
    internal const double RecommendationInfluenceWindowDays = 7.0;

    /// <summary>
    ///     Training label for items that were recommended AND watched within the
    ///     <see cref="RecommendationInfluenceWindowDays"/> window. Higher than
    ///     <see cref="WatchedLabel"/> (0.85) to provide a stronger positive signal
    ///     for recommendation-influenced watches.
    /// </summary>
    internal const double RecommendationInfluencedLabel = 0.90;

    /// <summary>
    ///     Fraction of old training examples retained during incremental training.
    ///     New examples (since last training) are always included; this fraction
    ///     controls how many older examples are randomly sampled to prevent
    ///     catastrophic forgetting while reducing training time.
    /// </summary>
    internal const double IncrementalOldSampleRatio = 0.3;

    /// <summary>
    ///     Minimum number of total examples before incremental subsampling activates.
    ///     Below this threshold, all examples are used regardless of the incremental flag.
    /// </summary>
    internal const int IncrementalMinExamplesThreshold = 30;

    /// <summary>
    ///     Maximum candidate count before a performance warning is emitted.
    ///     Libraries with more items than this threshold may experience slower on-demand scoring.
    /// </summary>
    internal const int CandidateCountWarningThreshold = 5000;

    /// <summary>
    ///     Batch size for cancellation token checks inside the candidate scoring loop.
    ///     Checking every single iteration is wasteful; checking every N items balances
    ///     responsiveness with overhead.
    /// </summary>
    internal const int CancellationCheckBatchSize = 200;

    /// <summary>
    ///     Minimum number of genres a user must have in their preference vector
    ///     before genre pre-filtering is applied. Below this threshold, the user's
    ///     taste profile is too sparse for reliable filtering.
    /// </summary>
    internal const int GenrePreFilterMinPreferences = 3;

    /// <summary>
    ///     Number of recommendation slots reserved for exploration (random picks from
    ///     remaining candidates instead of MMR selection). Placed at the end of the
    ///     recommendation list so high-relevance items are unaffected. Guarantees the
    ///     model sees diverse feedback even when MMR converges on a narrow genre cluster.
    /// </summary>
    internal const int ExplorationSlotCount = 2;

    /// <summary>
    ///     Maximum number of random negative samples added per user during training.
    ///     These are items recommended to OTHER users that this user never interacted with,
    ///     providing the model with true "irrelevant" examples to sharpen the decision boundary.
    /// </summary>
    internal const int RandomNegativeSamplesPerUser = 5;

    /// <summary>
    ///     PersonKind types considered for people similarity scoring.
    ///     Only actors and directors are used — writers/producers are less predictive
    ///     of user preference and would add noise to the similarity signal.
    /// </summary>
    internal static readonly IReadOnlyList<PersonKind> RelevantPersonKinds = Array.AsReadOnly(new[] { PersonKind.Actor, PersonKind.Director });
}