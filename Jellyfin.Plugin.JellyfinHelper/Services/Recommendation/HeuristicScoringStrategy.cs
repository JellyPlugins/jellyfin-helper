using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Fixed-weight heuristic scoring strategy.
///     Uses the original hand-tuned weights for genre, collaborative, rating, recency, and year proximity.
///     This strategy does not support learning — weights are constant.
/// </summary>
public sealed class HeuristicScoringStrategy : IScoringStrategy
{
    /// <summary>Weight for genre similarity signal.</summary>
    internal const double GenreWeight = 0.40;

    /// <summary>Weight for collaborative filtering signal.</summary>
    internal const double CollaborativeWeight = 0.25;

    /// <summary>Weight for community rating signal.</summary>
    internal const double RatingWeight = 0.15;

    /// <summary>Weight for recency signal.</summary>
    internal const double RecencyWeight = 0.10;

    /// <summary>Weight for year proximity signal.</summary>
    internal const double YearProximityWeight = 0.10;

    /// <inheritdoc />
    public string Name => "Heuristic (Fixed Weights)";

    /// <inheritdoc />
    public string NameKey => "strategyHeuristic";

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        return
            (features.GenreSimilarity * GenreWeight) +
            (features.CollaborativeScore * CollaborativeWeight) +
            (features.RatingScore * RatingWeight) +
            (features.RecencyScore * RecencyWeight) +
            (features.YearProximityScore * YearProximityWeight);
    }

    /// <inheritdoc />
    public bool Train(IReadOnlyList<TrainingExample> examples)
    {
        // Heuristic strategy does not support learning
        return false;
    }
}