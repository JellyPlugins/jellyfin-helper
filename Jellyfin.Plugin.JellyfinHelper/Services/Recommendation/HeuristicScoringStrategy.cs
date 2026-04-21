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

        // vector: [genre, collab, rating, recency, yearProx, genreCount_norm, isSeries,
        //          genre×rating, genre×collab, userRating, completionRatio]
        var score =
            (vector[0] * GenreWeight) +
            (vector[1] * CollaborativeWeight) +
            (vector[2] * RatingWeight) +
            (vector[3] * RecencyWeight) +
            (vector[4] * YearProximityWeight) +
            (vector[5] * GenreCountWeight) +
            (vector[6] * IsSeriesWeight) +
            (vector[7] * GenreRatingInteractionWeight) +
            (vector[8] * GenreCollabInteractionWeight) +
            (vector[9] * UserRatingWeight) +
            (vector[10] * CompletionRatioWeight);

        // No genre-mismatch penalty here — applied centrally in the Ensemble strategy
        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <inheritdoc />
    public bool Train(IReadOnlyList<TrainingExample> examples)
    {
        // Heuristic strategy does not support learning
        return false;
    }
}