namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Centralized default feature weights for recommendation scoring strategies.
///     Both <see cref="HeuristicScoringStrategy"/> (as fixed weights) and
///     <see cref="LearnedScoringStrategy"/> (as initial weights before training)
///     reference these values to ensure consistency and eliminate duplication.
/// </summary>
public static class DefaultWeights
{
    /// <summary>Weight for genre similarity signal (dominant).</summary>
    public const double GenreSimilarity = 0.25;

    /// <summary>Weight for collaborative filtering signal.</summary>
    public const double CollaborativeScore = 0.10;

    /// <summary>Weight for community rating signal.</summary>
    public const double RatingScore = 0.07;

    /// <summary>Weight for recency signal.</summary>
    public const double RecencyScore = 0.05;

    /// <summary>Weight for year proximity signal.</summary>
    public const double YearProximityScore = 0.05;

    /// <summary>Weight for normalized genre count signal.</summary>
    public const double GenreCountNormalized = 0.02;

    /// <summary>
    ///     Weight for series type signal. A positive weight provides a small boost when the
    ///     candidate is a series and the user's history contains series watches, enabling the
    ///     model to learn user preference for series vs. movies. The ML model can further adjust
    ///     this weight via training.
    /// </summary>
    public const double IsSeries = 0.04;

    /// <summary>Weight for genre × rating interaction signal.</summary>
    public const double GenreRatingInteraction = 0.05;

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
    public const double HasInteraction = 0.02;

    /// <summary>
    ///     Weight for people (cast/director) similarity signal.
    ///     Items featuring actors or directors from the user's watched content get a boost.
    ///     Cast/director overlap is a strong content-based signal for user preference.
    /// </summary>
    public const double PeopleSimilarity = 0.05;

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
    public const double PopularityScore = 0.03;

    /// <summary>
    ///     Weight for day-of-week affinity signal.
    ///     Captures temporal viewing patterns (e.g., comedies on weekends, dramas on weeknights).
    ///     A small weight so it acts as a tiebreaker rather than a dominant signal.
    /// </summary>
    public const double DayOfWeekAffinity = 0.02;

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
        weights[(int)FeatureIndex.IsAbandoned] = IsAbandoned;
        weights[(int)FeatureIndex.HasInteraction] = HasInteraction;
        weights[(int)FeatureIndex.PeopleSimilarity] = PeopleSimilarity;
        weights[(int)FeatureIndex.StudioMatch] = StudioMatch;
        weights[(int)FeatureIndex.SeriesProgressionBoost] = SeriesProgressionBoost;
        weights[(int)FeatureIndex.PopularityScore] = PopularityScore;
        weights[(int)FeatureIndex.DayOfWeekAffinity] = DayOfWeekAffinity;
        return weights;
    }
}