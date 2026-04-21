using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

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

    /// <summary>
    ///     Thread-local reusable buffer to avoid allocating a new vector per Score() call.
    /// </summary>
    [ThreadStatic]
    private static double[]? _vectorBuffer;

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

    /// <inheritdoc />
    public string Name => "Heuristic (Fixed Weights)";

    /// <inheritdoc />
    public string NameKey => "strategyHeuristic";

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        // Reuse thread-local buffer to avoid per-call allocation (Point 1)
        _vectorBuffer ??= new double[CandidateFeatures.FeatureCount];
        features.WriteToVector(_vectorBuffer);

        var raw = ScoringHelper.ComputeRawScore(_vectorBuffer, WeightsArray, bias: 0.0);
        var score = Math.Clamp(raw, 0.0, 1.0);

        // Apply genre penalty when used standalone
        if (_genrePenaltyFloor < 1.0)
        {
            var genreSim = features.GenreSimilarity;
            var penalty = _genrePenaltyFloor + ((1.0 - _genrePenaltyFloor) * genreSim);
            score *= penalty;
        }

        return score;
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        _vectorBuffer ??= new double[CandidateFeatures.FeatureCount];
        features.WriteToVector(_vectorBuffer);

        var explanation = ScoringHelper.BuildExplanation(_vectorBuffer, WeightsArray, bias: 0.0, Name);

        // Apply genre penalty when used standalone
        if (_genrePenaltyFloor < 1.0)
        {
            var genreSim = features.GenreSimilarity;
            var penalty = _genrePenaltyFloor + ((1.0 - _genrePenaltyFloor) * genreSim);
            explanation.FinalScore *= penalty;
            explanation.GenrePenaltyMultiplier = penalty;
        }

        return explanation;
    }
}