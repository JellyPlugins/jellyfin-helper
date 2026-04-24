namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Centralized default feature weights for recommendation scoring strategies.
///     Both <see cref="HeuristicScoringStrategy"/> (as fixed weights) and
///     <see cref="LearnedScoringStrategy"/> (as initial weights before training)
///     reference these values to ensure consistency and eliminate duplication.
/// </summary>
public static class DefaultWeights
{
    /// <summary>Weight for genre similarity signal (dominant, reduced from 0.23 to give more room to non-genre signals).</summary>
    public const double GenreSimilarity = 0.20;

    /// <summary>Weight for collaborative filtering signal.</summary>
    public const double CollaborativeScore = 0.11;

    /// <summary>Weight for community rating signal.</summary>
    public const double RatingScore = 0.07;

    /// <summary>Weight for recency signal (increased from 0.05 during genre rebalance).</summary>
    public const double RecencyScore = 0.06;

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
    public const double HasInteraction = 0.01;

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
    public const double PopularityScore = 0.01;

    /// <summary>
    ///     Weight for day-of-week affinity signal.
    ///     Captures temporal viewing patterns (e.g., comedies on weekends, dramas on weeknights).
    ///     A small weight so it acts as a tiebreaker rather than a dominant signal.
    /// </summary>
    public const double DayOfWeekAffinity = 0.02;

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
    public const double IsWeekend = 0.01;

    /// <summary>
    ///     Weight for tag-based content similarity signal.
    ///     Measures Jaccard overlap between the candidate's tags and the user's
    ///     preferred tags derived from watch history. Complements genre similarity
    ///     with more fine-grained content categorization.
    /// </summary>
    public const double TagSimilarity = 0.02;

    /// <summary>
    ///     Weight for people × genre interaction (actors you like in genres you prefer).
    /// </summary>
    public const double PeopleGenreInteraction = 0.03;

    /// <summary>
    ///     Weight for recency × rating interaction (trending: new + highly rated).
    /// </summary>
    public const double RecencyRatingInteraction = 0.03;

    /// <summary>
    ///     Negative weight for genre underexposure signal.
    ///     Items whose genres the user rarely watches receive a soft penalty.
    ///     Deliberately mild (-0.08) to avoid over-penalizing genres the user
    ///     simply hasn't explored yet — "rarely watched" ≠ "disliked".
    /// </summary>
    public const double GenreUnderexposure = -0.08;

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
    ///     Mild negative weight (-0.05) because a large gap might just mean
    ///     the user hasn't discovered the genre yet.
    /// </summary>
    public const double GenreAffinityGap = -0.05;

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
        var featureIndexCount = System.Enum.GetValues<FeatureIndex>().Length;
        if (featureIndexCount != CandidateFeatures.FeatureCount)
        {
            throw new System.InvalidOperationException(
                $"FeatureIndex count ({featureIndexCount}) must match CandidateFeatures.FeatureCount ({CandidateFeatures.FeatureCount}).");
        }

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
        weights[(int)FeatureIndex.HourOfDayAffinity] = HourOfDayAffinity;
        weights[(int)FeatureIndex.IsWeekend] = IsWeekend;
        weights[(int)FeatureIndex.TagSimilarity] = TagSimilarity;
        weights[(int)FeatureIndex.PeopleGenreInteraction] = PeopleGenreInteraction;
        weights[(int)FeatureIndex.RecencyRatingInteraction] = RecencyRatingInteraction;
        weights[(int)FeatureIndex.GenreUnderexposure] = GenreUnderexposure;
        weights[(int)FeatureIndex.GenreDominanceRatio] = GenreDominanceRatio;
        weights[(int)FeatureIndex.GenreAffinityGap] = GenreAffinityGap;
        return weights;
    }
}