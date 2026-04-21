namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Centralized default feature weights for recommendation scoring strategies.
///     Both <see cref="HeuristicScoringStrategy"/> (as fixed weights) and
///     <see cref="LearnedScoringStrategy"/> (as initial weights before training)
///     reference these values to ensure consistency and eliminate duplication.
/// </summary>
public static class DefaultWeights
{
    /// <summary>Weight for genre similarity signal (dominant).</summary>
    public const double GenreSimilarity = 0.35;

    /// <summary>Weight for collaborative filtering signal.</summary>
    public const double CollaborativeScore = 0.12;

    /// <summary>Weight for community rating signal.</summary>
    public const double RatingScore = 0.08;

    /// <summary>Weight for recency signal.</summary>
    public const double RecencyScore = 0.05;

    /// <summary>Weight for year proximity signal.</summary>
    public const double YearProximityScore = 0.05;

    /// <summary>Weight for normalized genre count signal.</summary>
    public const double GenreCountNormalized = 0.05;

    /// <summary>Weight for series type signal (neutral — no inherent preference).</summary>
    public const double IsSeries = 0.00;

    /// <summary>Weight for genre × rating interaction signal.</summary>
    public const double GenreRatingInteraction = 0.08;

    /// <summary>Weight for genre × collaborative interaction signal.</summary>
    public const double GenreCollabInteraction = 0.08;

    /// <summary>Weight for user personal rating signal (stronger than community rating).</summary>
    public const double UserRatingScore = 0.10;

    /// <summary>Weight for watch completion ratio (penalizes abandoned items).</summary>
    // TODO: CompletionRatio acts as a positive signal but items ~80% completed get boosted
    // rather than penalized. Consider adding an explicit "abandoned" feature (< 25% completion)
    // with a negative weight, or changing CompletionRatio semantics to penalize partial views.
    public const double CompletionRatio = 0.04;

    /// <summary>Default bias term for the learned strategy.</summary>
    public const double Bias = 0.05;

    /// <summary>
    ///     Creates a weight array indexed by <see cref="FeatureIndex"/> with all default values.
    /// </summary>
    /// <returns>A new array with <see cref="CandidateFeatures.FeatureCount"/> elements.</returns>
    public static double[] CreateWeightArray()
    {
        var weights = new double[CandidateFeatures.FeatureCount];
        weights[(int)FeatureIndex.GenreSimilarity] = GenreSimilarity;
        weights[(int)FeatureIndex.CollaborativeScore] = CollaborativeScore;
        weights[(int)FeatureIndex.RatingScore] = RatingScore;
        weights[(int)FeatureIndex.RecencyScore] = RecencyScore;
        weights[(int)FeatureIndex.YearProximityScore] = YearProximityScore;
        weights[(int)FeatureIndex.GenreCountNormalized] = GenreCountNormalized;
        weights[(int)FeatureIndex.IsSeries] = IsSeries;
        weights[(int)FeatureIndex.GenreRatingInteraction] = GenreRatingInteraction;
        weights[(int)FeatureIndex.GenreCollabInteraction] = GenreCollabInteraction;
        weights[(int)FeatureIndex.UserRatingScore] = UserRatingScore;
        weights[(int)FeatureIndex.CompletionRatio] = CompletionRatio;
        return weights;
    }
}