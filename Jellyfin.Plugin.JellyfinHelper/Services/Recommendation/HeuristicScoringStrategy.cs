using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Fixed-weight heuristic scoring strategy.
///     Uses hand-tuned weights with genre similarity as the dominant signal.
///     This strategy does not apply genre-mismatch penalties — that responsibility
///     belongs to the ensemble layer to avoid double-penalization.
///     This strategy does not support learning — weights are constant.
/// </summary>
public sealed class HeuristicScoringStrategy : IScoringStrategy
{
    /// <summary>Weight for genre similarity signal (dominant).</summary>
    internal const double GenreWeight = 0.35;

    /// <summary>Weight for collaborative filtering signal.</summary>
    internal const double CollaborativeWeight = 0.12;

    /// <summary>Weight for community rating signal.</summary>
    internal const double RatingWeight = 0.08;

    /// <summary>Weight for recency signal.</summary>
    internal const double RecencyWeight = 0.05;

    /// <summary>Weight for year proximity signal.</summary>
    internal const double YearProximityWeight = 0.05;

    /// <summary>Weight for genre count signal (items with more genres = broader appeal).</summary>
    internal const double GenreCountWeight = 0.05;

    /// <summary>Weight for series type signal (neutral — no inherent preference for series or movies).</summary>
    internal const double IsSeriesWeight = 0.00;

    /// <summary>Weight for genre × rating interaction signal.</summary>
    internal const double GenreRatingInteractionWeight = 0.08;

    /// <summary>Weight for genre × collaborative interaction signal.</summary>
    internal const double GenreCollabInteractionWeight = 0.08;

    /// <summary>Weight for user personal rating signal (stronger than community rating).</summary>
    internal const double UserRatingWeight = 0.10;

    /// <summary>Weight for watch completion ratio (penalizes abandoned items).</summary>
    internal const double CompletionRatioWeight = 0.04;

    /// <inheritdoc />
    public string Name => "Heuristic (Fixed Weights)";

    /// <inheritdoc />
    public string NameKey => "strategyHeuristic";

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        var vector = features.ToVector();
        return Math.Clamp(ComputeRawScore(vector), 0.0, 1.0);
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        var vector = features.ToVector();

        var genreContrib = vector[(int)FeatureIndex.GenreSimilarity] * GenreWeight;
        var collabContrib = vector[(int)FeatureIndex.CollaborativeScore] * CollaborativeWeight;
        var ratingContrib = vector[(int)FeatureIndex.RatingScore] * RatingWeight;
        var recencyContrib = vector[(int)FeatureIndex.RecencyScore] * RecencyWeight;
        var yearProxContrib = vector[(int)FeatureIndex.YearProximityScore] * YearProximityWeight;
        var genreCountContrib = vector[(int)FeatureIndex.GenreCountNormalized] * GenreCountWeight;
        var isSeriesContrib = vector[(int)FeatureIndex.IsSeries] * IsSeriesWeight;
        var genreRatingInteraction = vector[(int)FeatureIndex.GenreRatingInteraction] * GenreRatingInteractionWeight;
        var genreCollabInteraction = vector[(int)FeatureIndex.GenreCollabInteraction] * GenreCollabInteractionWeight;
        var userRatingContrib = vector[(int)FeatureIndex.UserRatingScore] * UserRatingWeight;
        var completionContrib = vector[(int)FeatureIndex.CompletionRatio] * CompletionRatioWeight;

        var interactionTotal = genreRatingInteraction + genreCollabInteraction
            + genreCountContrib + isSeriesContrib + completionContrib;

        var score = genreContrib + collabContrib + ratingContrib + recencyContrib
            + yearProxContrib + interactionTotal + userRatingContrib;
        score = Math.Clamp(score, 0.0, 1.0);

        return new ScoreExplanation
        {
            FinalScore = score,
            GenreContribution = genreContrib,
            CollaborativeContribution = collabContrib,
            RatingContribution = ratingContrib,
            RecencyContribution = recencyContrib,
            YearProximityContribution = yearProxContrib,
            UserRatingContribution = userRatingContrib,
            InteractionContribution = interactionTotal,
            GenrePenaltyMultiplier = 1.0, // No penalty in heuristic — applied in Ensemble
            DominantSignal = ScoreExplanation.DetermineDominantSignal(
                genreContrib, collabContrib, ratingContrib, userRatingContrib, recencyContrib, yearProxContrib),
            StrategyName = Name
        };
    }

    /// <inheritdoc />
    public bool Train(IReadOnlyList<TrainingExample> examples)
    {
        // Heuristic strategy does not support learning
        return false;
    }

    /// <summary>
    ///     Computes the raw weighted score from a feature vector without allocating explanation objects.
    /// </summary>
    private static double ComputeRawScore(double[] vector)
    {
        return (vector[(int)FeatureIndex.GenreSimilarity] * GenreWeight)
            + (vector[(int)FeatureIndex.CollaborativeScore] * CollaborativeWeight)
            + (vector[(int)FeatureIndex.RatingScore] * RatingWeight)
            + (vector[(int)FeatureIndex.RecencyScore] * RecencyWeight)
            + (vector[(int)FeatureIndex.YearProximityScore] * YearProximityWeight)
            + (vector[(int)FeatureIndex.GenreCountNormalized] * GenreCountWeight)
            + (vector[(int)FeatureIndex.IsSeries] * IsSeriesWeight)
            + (vector[(int)FeatureIndex.GenreRatingInteraction] * GenreRatingInteractionWeight)
            + (vector[(int)FeatureIndex.GenreCollabInteraction] * GenreCollabInteractionWeight)
            + (vector[(int)FeatureIndex.UserRatingScore] * UserRatingWeight)
            + (vector[(int)FeatureIndex.CompletionRatio] * CompletionRatioWeight);
    }
}