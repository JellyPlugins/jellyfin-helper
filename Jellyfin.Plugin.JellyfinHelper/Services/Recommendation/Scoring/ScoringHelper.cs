using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Shared scoring logic used by both HeuristicScoringStrategy and LearnedScoringStrategy to eliminate code duplication.
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

    /// <summary>Returns the canonical diagnostic name for a feature slot, excluding legacy aliases.</summary>
    /// <param name="index">The feature slot.</param>
    /// <returns>The canonical feature name.</returns>
    internal static string GetFeatureName(FeatureIndex index)
        => index == FeatureIndex.SeriesAffinity ? nameof(FeatureIndex.SeriesAffinity) : index.ToString();

    /// <summary>Compares copied configuration state exactly, not computed numerical approximations.</summary>
    /// <param name="left">The previously captured setting.</param>
    /// <param name="right">The current setting.</param>
    /// <returns>True only for identical bits; even an adjacent representable change invalidates evaluation.</returns>
    internal static bool SameConfigurationValue(double left, double right)
        => BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    /// <summary>Explains each named signal as an actual score-space removal effect, not a logit gradient.</summary>
    /// <param name="features">The observed raw features.</param>
    /// <param name="score">A scorer pinned to a coherent model state by its caller.</param>
    /// <param name="strategyName">The strategy display name.</param>
    /// <returns>Marginal effects; nonlinear and interacting effects are deliberately not presented as additive.</returns>
    internal static ScoreExplanation BuildMarginalExplanation(CandidateFeatures features, Func<CandidateFeatures, double> score, string strategyName)
    {
        var full = score(features);
        double Effect(FeatureIndex signal) => full - score(features.WithoutSignal(signal));
        var explanation = new ScoreExplanation
        {
            FinalScore = full,
            StrategyName = strategyName,
            ContributionsAreMarginalEffects = true,
            GenreContribution = Effect(FeatureIndex.GenreSimilarity),
            CollaborativeContribution = Effect(FeatureIndex.CollaborativeScore),
            RatingContribution = Effect(FeatureIndex.CombinedCriticScore),
            RecencyContribution = Effect(FeatureIndex.RecencyScore),
            YearProximityContribution = Effect(FeatureIndex.YearProximityScore),
            UserRatingContribution = Effect(FeatureIndex.UserRatingScore),
            PeopleContribution = Effect(FeatureIndex.PeopleSimilarity),
            StudioContribution = Effect(FeatureIndex.StudioMatch),
            FranchiseContribution = Effect(FeatureIndex.FranchiseAffinity),
            CollectionProgressionContribution = Effect(FeatureIndex.CollectionProgressionBoost),
            PopularityContribution = Effect(FeatureIndex.PopularityScore),
            InteractionContribution = Effect(FeatureIndex.GenreCountNormalized)
        };
        explanation.DominantSignal = ScoreExplanation.DetermineDominantSignal(
            explanation.GenreContribution,
            explanation.CollaborativeContribution,
            explanation.RatingContribution,
            explanation.UserRatingContribution,
            explanation.RecencyContribution,
            explanation.YearProximityContribution,
            explanation.InteractionContribution,
            explanation.PeopleContribution,
            explanation.StudioContribution,
            explanation.FranchiseContribution,
            explanation.CollectionProgressionContribution,
            explanation.PopularityContribution);
        return explanation;
    }

    /// <summary>
    ///     Computes a soft genre penalty multiplier that ramps linearly from floor (at genreSimilarity = 0) to 1.0 (at genreSimilarity >= threshold).
    /// </summary>
    /// <param name="genreSimilarity">The genre similarity value (0-1).</param>
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

    /// <summary>Applies genre mismatch penalties only when both sides supplied actual genre evidence.</summary>
    /// <param name="features">Candidate signals and their metadata availability.</param>
    /// <param name="floor">The minimum multiplier for an observed genre mismatch.</param>
    /// <returns>A neutral multiplier for missing evidence, otherwise the configured genre penalty.</returns>
    internal static double ComputeGenrePenalty(CandidateFeatures features, double floor)
        => features.HasGenreEvidence ? ComputeSoftGenrePenalty(features.GenreSimilarity, floor) : 1.0;

    /// <summary>
    ///     Computes the raw linear score from a feature vector, weights, and bias. Returns NaNFallbackScore if the result is NaN or Infinity, which can occur when weights are corrupted or features contain invalid values.
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
    ///     Guards a score value against NaN and Infinity. Returns NaNFallbackScore if the value is not a finite number.
    /// </summary>
    /// <param name="score">The score to guard.</param>
    /// <returns>The original score if finite, otherwise <see cref="NaNFallbackScore"/>.</returns>
    internal static double GuardScore(double score)
    {
        return double.IsFinite(score) ? score : NaNFallbackScore;
    }

    /// <summary>
    ///     Safely computes a single feature contribution (value × weight). Returns 0 when the feature index exceeds the vector or weight array length, which can happen when an older/corrupted model is loaded with fewer persisted weights.
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
    ///     Builds a complete ScoreExplanation from a feature vector and weights. Extracts per-feature contributions and determines the dominant signal.
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

        // Surfaced as named signals so the UI can explain "complete the collection" instead of a generic reason.
        // Pulled out of the interaction bucket below; the final-score sum is unchanged because they are added back.
        var franchiseContrib = GetContribution(vector, weights, FeatureIndex.FranchiseAffinity);
        var collectionContrib = GetContribution(vector, weights, FeatureIndex.CollectionProgressionBoost);
        var popularityContrib = GetContribution(vector, weights, FeatureIndex.PopularityScore);

        // Interaction + minor features (genreCount, isSeries, genre×rating, genre×collab, completionRatio, isAbandoned, hasInteraction, seriesProgression, popularity, dayOfWeek, hourOfDay, isWeekend, tagSimilarity, people×genre, recency×critic, genreExposure features, libraryAddedRecency,.
        var interactionSum =
            GetContribution(vector, weights, FeatureIndex.GenreCountNormalized) +
            GetContribution(vector, weights, FeatureIndex.IsSeries) +
            GetContribution(vector, weights, FeatureIndex.GenreCriticInteraction) +
            GetContribution(vector, weights, FeatureIndex.GenreCollabInteraction) +
            GetContribution(vector, weights, FeatureIndex.CompletionRatio) +
            GetContribution(vector, weights, FeatureIndex.IsAbandoned) +
            GetContribution(vector, weights, FeatureIndex.HasInteraction) +
            GetContribution(vector, weights, FeatureIndex.SeriesProgressionBoost) +
            GetContribution(vector, weights, FeatureIndex.DayOfWeekAffinity) +
            GetContribution(vector, weights, FeatureIndex.HourOfDayAffinity) +
            GetContribution(vector, weights, FeatureIndex.IsWeekend) +
            GetContribution(vector, weights, FeatureIndex.TagSimilarity) +
            GetContribution(vector, weights, FeatureIndex.PeopleGenreInteraction) +
            GetContribution(vector, weights, FeatureIndex.RecencyCriticInteraction) +
            GetContribution(vector, weights, FeatureIndex.GenreUnderexposure) +
            GetContribution(vector, weights, FeatureIndex.GenreDominanceRatio) +
            GetContribution(vector, weights, FeatureIndex.GenreAffinityGap) +
            GetContribution(vector, weights, FeatureIndex.LibraryAddedRecency) +
            GetContribution(vector, weights, FeatureIndex.ContentNearestNeighborScore) +
            GetContribution(vector, weights, FeatureIndex.LanguageAffinity) +
            GetContribution(vector, weights, FeatureIndex.SubtitleLanguageAffinity) +
            GetContribution(vector, weights, FeatureIndex.ProductionLocationAffinity) +
            GetContribution(vector, weights, FeatureIndex.InheritedTagSimilarity) +
            GetContribution(vector, weights, FeatureIndex.SeriesCompletability) +
            GetContribution(vector, weights, FeatureIndex.WriterAffinity) +
            GetContribution(vector, weights, FeatureIndex.BillingWeightedPeople) +
            GetContribution(vector, weights, FeatureIndex.GenreStudioIdfPrior);
        var interactionContrib = double.IsFinite(interactionSum) ? interactionSum : 0.0;

        // Franchise, collection and popularity are summed into the final score exactly as before, but kept out of
        // the interaction bucket so they can be the dominant signal and drive a concrete recommendation reason.
        var contributionSum = genreContrib + collabContrib + ratingContrib + recencyContrib +
            yearProxContrib + userRatingContrib + peopleContrib + studioContrib + interactionContrib +
            franchiseContrib + collectionContrib + popularityContrib;
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
            FranchiseContribution = franchiseContrib,
            CollectionProgressionContribution = collectionContrib,
            PopularityContribution = popularityContrib,
            GenrePenaltyMultiplier = 1.0, // No penalty in individual strategies - applied in Ensemble
            DominantSignal = ScoreExplanation.DetermineDominantSignal(
                genreContrib, collabContrib, ratingContrib, userRatingContrib, recencyContrib, yearProxContrib, interactionContrib, peopleContrib, studioContrib, franchiseContrib, collectionContrib, popularityContrib),
            StrategyName = strategyName
        };
    }
}
