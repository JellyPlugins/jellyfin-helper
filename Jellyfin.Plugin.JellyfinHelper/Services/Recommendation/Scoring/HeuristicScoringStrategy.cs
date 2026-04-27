using System;
using System.Buffers;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Fixed-weight heuristic scoring strategy.
///     Uses hand-tuned weights from <see cref="DefaultWeights"/> with genre similarity as the dominant signal.
///     When used standalone (not via Ensemble), a configurable genre-penalty floor is applied
///     so that items with zero genre overlap are penalized.
///     This strategy does not support learning — weights are constant.
/// </summary>
public sealed class HeuristicScoringStrategy : IScoringStrategy
{
    /// <summary>
    ///     Pre-allocated weight array for <see cref="ScoringHelper"/> which requires <c>double[]</c>.
    ///     Safe because this class never mutates the array after construction.
    /// </summary>
    private static readonly double[] WeightsArray = DefaultWeights.CreateWeightArray();

    private readonly double _genrePenaltyFloor;

    /// <summary>
    ///     Initializes a new instance of the <see cref="HeuristicScoringStrategy"/> class.
    /// </summary>
    /// <param name="genrePenaltyFloor">
    ///     Minimum multiplier for genre-mismatch penalty (0–1). Items with zero genre overlap
    ///     receive this multiplier. Default 0.10 (90% penalty). Set to 1.0 to disable penalty
    ///     (e.g. when used inside ensemble which applies its own penalty).
    /// </param>
    public HeuristicScoringStrategy(double genrePenaltyFloor = 0.10)
    {
        _genrePenaltyFloor = Math.Clamp(genrePenaltyFloor, 0.0, 1.0);
    }

    /// <summary>
    ///     Gets the configured genre penalty floor for this instance.
    ///     Used by <see cref="EnsembleScoringStrategy"/> to validate that the heuristic
    ///     was constructed with penalty disabled (floor = 1.0) to avoid double-penalization.
    /// </summary>
    internal double GenrePenaltyFloor => _genrePenaltyFloor;

    /// <inheritdoc />
    public string Name => "Heuristic (Fixed Weights)";

    /// <inheritdoc />
    public string NameKey => "strategyHeuristic";

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        // Rent from ArrayPool to avoid per-call allocation (same pattern as LearnedScoringStrategy).
        // The heuristic is called for every candidate in the Ensemble hot path.
        var vector = ArrayPool<double>.Shared.Rent(CandidateFeatures.FeatureCount);
        try
        {
            // Clear only the portion we use (Rent may return a larger array)
            Array.Clear(vector, 0, CandidateFeatures.FeatureCount);
            features.WriteToVector(vector);

            // Bias is intentionally 0.0 for the heuristic strategy — hand-tuned weights
            // already produce scores in the desired range without a bias offset.
            var raw = ScoringHelper.ComputeRawScore(vector, WeightsArray, bias: 0.0);
            var score = Math.Clamp(raw, 0.0, 1.0);

            // Apply genre penalty when used standalone (shared formula with EnsembleScoringStrategy)
            if (_genrePenaltyFloor < 1.0)
            {
                var penalty = ScoringHelper.ComputeSoftGenrePenalty(features.GenreSimilarity, _genrePenaltyFloor);
                score *= penalty;
            }

            return score;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(vector);
        }
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        // Rent from ArrayPool to avoid per-call allocation (same pattern as LearnedScoringStrategy).
        var vector = ArrayPool<double>.Shared.Rent(CandidateFeatures.FeatureCount);
        try
        {
            Array.Clear(vector, 0, CandidateFeatures.FeatureCount);
            features.WriteToVector(vector);

            var explanation = ScoringHelper.BuildExplanation(vector, WeightsArray, bias: 0.0, Name);

            // Apply genre penalty when used standalone (shared formula with EnsembleScoringStrategy).
            // Uses WithPenalty() to scale both FinalScore and all contributions consistently,
            // so that FinalScore = Σ(contributions) × GenrePenaltyMultiplier holds true.
            if (_genrePenaltyFloor < 1.0)
            {
                var penalty = ScoringHelper.ComputeSoftGenrePenalty(features.GenreSimilarity, _genrePenaltyFloor);
                explanation = explanation.WithPenalty(penalty);
            }

            return explanation;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(vector);
        }
    }
}