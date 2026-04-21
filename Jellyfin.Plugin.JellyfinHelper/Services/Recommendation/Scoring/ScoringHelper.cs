using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Shared scoring logic used by both <see cref="HeuristicScoringStrategy"/> and
///     <see cref="LearnedScoringStrategy"/> to eliminate code duplication.
///     Computes per-feature contributions and builds a <see cref="ScoreExplanation"/>
///     from a feature vector and a weight array.
/// </summary>
internal static class ScoringHelper
{
    /// <summary>
    ///     Default threshold for genre penalty ramp: genre similarity values at or above
    ///     this threshold receive no penalty (multiplier = 1.0).
    /// </summary>
    internal const double DefaultGenrePenaltyThreshold = 0.3;

    /// <summary>
    ///     Computes a soft genre penalty multiplier that ramps linearly from <paramref name="floor"/>
    ///     (at genreSimilarity = 0) to 1.0 (at genreSimilarity ≥ <paramref name="threshold"/>).
    ///     Shared by <see cref="HeuristicScoringStrategy"/> and <see cref="EnsembleScoringStrategy"/>
    ///     to ensure consistent genre penalty behaviour across all strategies.
    /// </summary>
    /// <param name="genreSimilarity">The genre similarity value (0–1).</param>
    /// <param name="floor">The minimum penalty multiplier when genre similarity is 0.</param>
    /// <param name="threshold">Genre similarity value at which penalty reaches 1.0 (no penalty).</param>
    /// <returns>A penalty multiplier between <paramref name="floor"/> and 1.0.</returns>
    internal static double ComputeSoftGenrePenalty(double genreSimilarity, double floor, double threshold = DefaultGenrePenaltyThreshold)
    {
        if (genreSimilarity >= threshold)
        {
            return 1.0;
        }

        return floor + ((genreSimilarity / threshold) * (1.0 - floor));
    }

    /// <summary>
    ///     Computes the raw linear score from a feature vector, weights, and bias.
    /// </summary>
    /// <param name="vector">The feature vector.</param>
    /// <param name="weights">The weight array (same length as vector).</param>
    /// <param name="bias">The bias term.</param>
    /// <returns>The raw (unclamped) score.</returns>
    internal static double ComputeRawScore(double[] vector, double[] weights, double bias)
    {
        var score = bias;
        var len = Math.Min(vector.Length, weights.Length);
        for (var i = 0; i < len; i++)
        {
            score += vector[i] * weights[i];
        }

        return score;
    }

    /// <summary>
    ///     Builds a complete <see cref="ScoreExplanation"/> from a feature vector and weights.
    ///     Extracts per-feature contributions and determines the dominant signal.
    /// </summary>
    /// <param name="vector">The feature vector (length = <see cref="CandidateFeatures.FeatureCount"/>).</param>
    /// <param name="weights">The weight array.</param>
    /// <param name="bias">The bias term (0 for heuristic).</param>
    /// <param name="strategyName">The human-readable strategy name for the explanation.</param>
    /// <returns>A fully populated score explanation.</returns>
    internal static ScoreExplanation BuildExplanation(
        double[] vector,
        double[] weights,
        double bias,
        string strategyName)
    {
        var genreContrib = vector[(int)FeatureIndex.GenreSimilarity] * weights[(int)FeatureIndex.GenreSimilarity];
        var collabContrib = vector[(int)FeatureIndex.CollaborativeScore] * weights[(int)FeatureIndex.CollaborativeScore];
        var ratingContrib = vector[(int)FeatureIndex.RatingScore] * weights[(int)FeatureIndex.RatingScore];
        var recencyContrib = vector[(int)FeatureIndex.RecencyScore] * weights[(int)FeatureIndex.RecencyScore];
        var yearProxContrib = vector[(int)FeatureIndex.YearProximityScore] * weights[(int)FeatureIndex.YearProximityScore];
        var userRatingContrib = vector[(int)FeatureIndex.UserRatingScore] * weights[(int)FeatureIndex.UserRatingScore];

    // Interaction + minor features (genreCount, isSeries, genre×rating, genre×collab, completionRatio, isAbandoned, hasInteraction, peopleSimilarity, studioMatch)
        var interactionContrib =
            (vector[(int)FeatureIndex.GenreCountNormalized] * weights[(int)FeatureIndex.GenreCountNormalized]) +
            (vector[(int)FeatureIndex.IsSeries] * weights[(int)FeatureIndex.IsSeries]) +
            (vector[(int)FeatureIndex.GenreRatingInteraction] * weights[(int)FeatureIndex.GenreRatingInteraction]) +
            (vector[(int)FeatureIndex.GenreCollabInteraction] * weights[(int)FeatureIndex.GenreCollabInteraction]) +
            (vector[(int)FeatureIndex.CompletionRatio] * weights[(int)FeatureIndex.CompletionRatio]) +
            (vector[(int)FeatureIndex.IsAbandoned] * weights[(int)FeatureIndex.IsAbandoned]) +
            (vector[(int)FeatureIndex.HasInteraction] * weights[(int)FeatureIndex.HasInteraction]) +
            (vector[(int)FeatureIndex.PeopleSimilarity] * weights[(int)FeatureIndex.PeopleSimilarity]) +
            (vector[(int)FeatureIndex.StudioMatch] * weights[(int)FeatureIndex.StudioMatch]);

        var rawScore = ComputeRawScore(vector, weights, bias);
        var score = Math.Clamp(rawScore, 0.0, 1.0);

        return new ScoreExplanation
        {
            FinalScore = score,
            GenreContribution = genreContrib,
            CollaborativeContribution = collabContrib,
            RatingContribution = ratingContrib,
            RecencyContribution = recencyContrib,
            YearProximityContribution = yearProxContrib,
            UserRatingContribution = userRatingContrib,
            InteractionContribution = interactionContrib,
            GenrePenaltyMultiplier = 1.0, // No penalty in individual strategies — applied in Ensemble
            DominantSignal = ScoreExplanation.DetermineDominantSignal(
                genreContrib, collabContrib, ratingContrib, userRatingContrib, recencyContrib, yearProxContrib, interactionContrib),
            StrategyName = strategyName
        };
    }
}