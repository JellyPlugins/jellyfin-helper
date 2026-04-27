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
    ///     Default fallback score returned when a computed score is NaN or Infinity.
    ///     0.5 is neutral (neither positive nor negative recommendation signal).
    /// </summary>
    internal const double NaNFallbackScore = 0.5;

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
        if (threshold <= 0.0 || genreSimilarity >= threshold)
        {
            return 1.0;
        }

        return floor + ((genreSimilarity / threshold) * (1.0 - floor));
    }

    /// <summary>
    ///     Computes the raw linear score from a feature vector, weights, and bias.
    ///     Returns <see cref="NaNFallbackScore"/> if the result is NaN or Infinity,
    ///     which can occur when weights are corrupted or features contain invalid values.
    /// </summary>
    /// <param name="vector">The feature vector.</param>
    /// <param name="weights">The weight array (same length as vector).</param>
    /// <param name="bias">The bias term.</param>
    /// <returns>The raw (unclamped) score, or <see cref="NaNFallbackScore"/> if the result is not finite.</returns>
    internal static double ComputeRawScore(double[] vector, double[] weights, double bias)
    {
        var score = bias;
        var len = Math.Min(vector.Length, weights.Length);
        for (var i = 0; i < len; i++)
        {
            score += vector[i] * weights[i];
        }

        return double.IsFinite(score) ? score : NaNFallbackScore;
    }

    /// <summary>
    ///     Guards a score value against NaN and Infinity.
    ///     Returns <see cref="NaNFallbackScore"/> if the value is not a finite number.
    ///     This is the centralized guard used by all scoring strategies to prevent
    ///     NaN propagation through the recommendation pipeline.
    /// </summary>
    /// <param name="score">The score to guard.</param>
    /// <returns>The original score if finite, otherwise <see cref="NaNFallbackScore"/>.</returns>
    internal static double GuardScore(double score)
    {
        return double.IsFinite(score) ? score : NaNFallbackScore;
    }

    /// <summary>
    ///     Safely computes a single feature contribution (value × weight).
    ///     Returns 0 when the feature index exceeds the vector or weight array length,
    ///     which can happen when an older/corrupted model is loaded with fewer persisted weights.
    ///     This mirrors the truncation tolerance of <see cref="ComputeRawScore"/>.
    /// </summary>
    /// <param name="values">The feature vector.</param>
    /// <param name="coeffs">The weight/coefficient array.</param>
    /// <param name="index">The feature index to compute.</param>
    /// <returns>The guarded contribution, or 0 if the index is out of bounds.</returns>
    private static double GetContribution(double[] values, double[] coeffs, FeatureIndex index)
    {
        var i = (int)index;
        if (i >= values.Length || i >= coeffs.Length)
        {
            return 0.0;
        }

        var contribution = values[i] * coeffs[i];
        return double.IsFinite(contribution) ? contribution : 0.0;
    }

    /// <summary>
    ///     Builds a complete <see cref="ScoreExplanation"/> from a feature vector and weights.
    ///     Extracts per-feature contributions and determines the dominant signal.
    ///     All computed values are guarded against NaN/Infinity propagation.
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
        var genreContrib = GetContribution(vector, weights, FeatureIndex.GenreSimilarity);
        var collabContrib = GetContribution(vector, weights, FeatureIndex.CollaborativeScore);
        var ratingContrib = GetContribution(vector, weights, FeatureIndex.CombinedCriticScore);
        var recencyContrib = GetContribution(vector, weights, FeatureIndex.RecencyScore);
        var yearProxContrib = GetContribution(vector, weights, FeatureIndex.YearProximityScore);
        var userRatingContrib = GetContribution(vector, weights, FeatureIndex.UserRatingScore);

        // People and studio contributions tracked separately for dominant signal detection
        var peopleContrib = GetContribution(vector, weights, FeatureIndex.PeopleSimilarity);
        var studioContrib = GetContribution(vector, weights, FeatureIndex.StudioMatch);

        // Interaction + minor features (genreCount, isSeries, genre×rating, genre×collab, completionRatio, isAbandoned, hasInteraction, seriesProgression, popularity, dayOfWeek, hourOfDay, isWeekend, tagSimilarity)
        // Each GetContribution already returns 0.0 for non-finite products, so the sum should
        // always be finite. Use 0.0 (not NaNFallbackScore) as defensive fallback to avoid
        // injecting a neutral-looking 0.5 into an additive contribution term.
        var interactionSum =
            GetContribution(vector, weights, FeatureIndex.GenreCountNormalized) +
            GetContribution(vector, weights, FeatureIndex.IsSeries) +
            GetContribution(vector, weights, FeatureIndex.GenreCriticInteraction) +
            GetContribution(vector, weights, FeatureIndex.GenreCollabInteraction) +
            GetContribution(vector, weights, FeatureIndex.CompletionRatio) +
            GetContribution(vector, weights, FeatureIndex.IsAbandoned) +
            GetContribution(vector, weights, FeatureIndex.HasInteraction) +
            GetContribution(vector, weights, FeatureIndex.SeriesProgressionBoost) +
            GetContribution(vector, weights, FeatureIndex.PopularityScore) +
            GetContribution(vector, weights, FeatureIndex.DayOfWeekAffinity) +
            GetContribution(vector, weights, FeatureIndex.HourOfDayAffinity) +
            GetContribution(vector, weights, FeatureIndex.IsWeekend) +
            GetContribution(vector, weights, FeatureIndex.TagSimilarity) +
            GetContribution(vector, weights, FeatureIndex.PeopleGenreInteraction) +
            GetContribution(vector, weights, FeatureIndex.RecencyCriticInteraction) +
            GetContribution(vector, weights, FeatureIndex.ContentNearestNeighborScore) +
            GetContribution(vector, weights, FeatureIndex.LanguageAffinity);
        var interactionContrib = double.IsFinite(interactionSum) ? interactionSum : 0.0;

        // Compute FinalScore from the sum of contributions + bias instead of re-calling
        // ComputeRawScore. This ensures FinalScore is always consistent with the individual
        // contribution values, even if a corrupted weight produces a non-finite product
        // (GetContribution returns 0.0 for non-finite, while ComputeRawScore returns 0.5).
        var contributionSum = genreContrib + collabContrib + ratingContrib + recencyContrib +
            yearProxContrib + userRatingContrib + peopleContrib + studioContrib + interactionContrib;
        var rawScore = GuardScore(contributionSum + bias);
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
            PeopleContribution = peopleContrib,
            StudioContribution = studioContrib,
            GenrePenaltyMultiplier = 1.0, // No penalty in individual strategies — applied in Ensemble
            DominantSignal = ScoreExplanation.DetermineDominantSignal(
                genreContrib, collabContrib, ratingContrib, userRatingContrib, recencyContrib, yearProxContrib, interactionContrib, peopleContrib, studioContrib),
            StrategyName = strategyName
        };
    }
}