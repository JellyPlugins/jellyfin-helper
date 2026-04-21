using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Ensemble scoring strategy that combines the learned (adaptive ML) strategy
///     with the heuristic (rule-based) strategy for more robust recommendations.
///     Uses a dynamic blending factor (α) that smoothly shifts weight toward the learned
///     model as more training data becomes available via a sigmoid function.
///     Alpha progression is gated by validation loss quality — if the learned model
///     generalizes poorly, alpha will not advance further.
///     Applies the genre-mismatch penalty centrally (once) after blending to avoid
///     double-penalization that would occur if each sub-strategy applied it independently.
/// </summary>
/// <remarks>
///     Architecture: score = (α × Learned.Score + (1 - α) × Heuristic.Score) × softPenalty(genreSimilarity)
///     where α is computed via sigmoid: α = αMin + (αMax - αMin) / (1 + e^(-k × (n - midpoint)))
///     but capped if the learned model's validation loss exceeds <see cref="ValidationLossThreshold"/>.
///     Training delegates to the learned strategy; the heuristic strategy is static.
///     Genre penalty is applied to both the final score AND per-feature contributions
///     for consistency (sum of contributions ≈ finalScore).
/// </remarks>
public sealed class EnsembleScoringStrategy : IScoringStrategy, ITrainableStrategy
{
    /// <summary>Default minimum blending factor (heuristic dominates with no training data).</summary>
    internal const double DefaultAlphaMin = 0.3;

    /// <summary>Default maximum blending factor (learned dominates with abundant data).</summary>
    internal const double DefaultAlphaMax = 0.8;

    /// <summary>Sigmoid steepness for alpha transition.</summary>
    internal const double AlphaSigmoidK = 0.05;

    /// <summary>Sigmoid midpoint (number of examples where alpha = (αMin + αMax) / 2).</summary>
    internal const double AlphaSigmoidMidpoint = 50.0;

    /// <summary>
    ///     Genre similarity threshold below which the soft penalty ramps down.
    ///     Items above this threshold receive no penalty (multiplier = 1.0).
    /// </summary>
    internal const double GenrePenaltyThreshold = 0.15;

    /// <summary>
    ///     Default minimum penalty multiplier for items with zero genre overlap.
    ///     Items with GenreSimilarity = 0 get score × this value.
    /// </summary>
    internal const double DefaultGenrePenaltyFloor = 0.10;

    /// <summary>
    ///     Maximum acceptable validation loss before alpha progression is frozen.
    ///     If the learned model's validation loss exceeds this threshold, alpha
    ///     will not increase beyond its current value (quality gate).
    /// </summary>
    internal const double ValidationLossThreshold = 0.15;

    /// <summary>Cached JSON serializer options for ensemble state persistence.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly HeuristicScoringStrategy _heuristic;
    private readonly LearnedScoringStrategy _learned;
    private readonly object _syncRoot = new();
    private readonly double _alphaMax;
    private readonly double _alphaMin;
    private readonly double _genrePenaltyFloor;
    private readonly string? _statePath;
    private double _alpha;
    private int _trainingExampleCount;

    /// <summary>
    ///     Initializes a new instance of the <see cref="EnsembleScoringStrategy" /> class
    ///     with injected sub-strategies for testability and flexibility.
    /// </summary>
    /// <param name="learned">The learned (adaptive ML) sub-strategy.</param>
    /// <param name="heuristic">The heuristic (rule-based) sub-strategy.</param>
    /// <param name="statePath">Optional file path for persisting ensemble state.</param>
    /// <param name="alphaMin">Minimum blending factor.</param>
    /// <param name="alphaMax">Maximum blending factor.</param>
    /// <param name="genrePenaltyFloor">Minimum genre penalty multiplier.</param>
    public EnsembleScoringStrategy(
        LearnedScoringStrategy learned,
        HeuristicScoringStrategy heuristic,
        string? statePath = null,
        double alphaMin = DefaultAlphaMin,
        double alphaMax = DefaultAlphaMax,
        double genrePenaltyFloor = DefaultGenrePenaltyFloor)
    {
        _alphaMin = Math.Clamp(alphaMin, 0.0, 1.0);
        _alphaMax = Math.Clamp(alphaMax, _alphaMin, 1.0);
        _genrePenaltyFloor = Math.Clamp(genrePenaltyFloor, 0.0, 1.0);
        _alpha = _alphaMin;

        _learned = learned;
        // Disable penalty in sub-strategies since ensemble applies it centrally
        _heuristic = heuristic;

        _statePath = statePath;
        TryLoadState();
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="EnsembleScoringStrategy"/> class.
    ///     Convenience constructor that creates sub-strategies internally.
    ///     Kept for backward compatibility and simple usage scenarios.
    /// </summary>
    /// <param name="weightsPath">Optional file path for persisting learned weights.</param>
    /// <param name="alphaMin">Minimum blending factor.</param>
    /// <param name="alphaMax">Maximum blending factor.</param>
    /// <param name="genrePenaltyFloor">Minimum genre penalty multiplier.</param>
    public EnsembleScoringStrategy(
        string? weightsPath = null,
        double alphaMin = DefaultAlphaMin,
        double alphaMax = DefaultAlphaMax,
        double genrePenaltyFloor = DefaultGenrePenaltyFloor)
        : this(
            new LearnedScoringStrategy(weightsPath),
            new HeuristicScoringStrategy(genrePenaltyFloor: 1.0), // disable penalty in sub-strategy
            DeriveStatePath(weightsPath),
            alphaMin,
            alphaMax,
            genrePenaltyFloor)
    {
    }

    /// <inheritdoc />
    public string Name => "Ensemble (Adaptive ML + Rules)";

    /// <inheritdoc />
    public string NameKey => "strategyEnsemble";

    /// <summary>
    ///     Gets the current blending factor α (for testing/debugging).
    ///     α = weight of the learned strategy; (1 - α) = weight of the heuristic strategy.
    /// </summary>
    internal double CurrentAlpha
    {
        get
        {
            lock (_syncRoot)
            {
                return _alpha;
            }
        }
    }

    /// <summary>
    ///     Gets the total number of training examples seen so far (for testing/debugging).
    /// </summary>
    internal int TrainingExampleCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _trainingExampleCount;
            }
        }
    }

    /// <summary>
    ///     Gets the underlying learned strategy (for testing/debugging).
    /// </summary>
    internal LearnedScoringStrategy LearnedStrategy => _learned;

    /// <summary>
    ///     Gets the underlying heuristic strategy (for testing/debugging).
    /// </summary>
    internal HeuristicScoringStrategy HeuristicStrategy => _heuristic;

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        var learnedScore = _learned.Score(features);
        var heuristicScore = _heuristic.Score(features);

        double alpha;
        lock (_syncRoot)
        {
            alpha = _alpha;
        }

        var blendedScore = (alpha * learnedScore) + ((1.0 - alpha) * heuristicScore);
        var penalty = ComputeSoftGenrePenalty(features.GenreSimilarity, _genrePenaltyFloor);
        return blendedScore * penalty;
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        var learnedExplanation = _learned.ScoreWithExplanation(features);
        var heuristicExplanation = _heuristic.ScoreWithExplanation(features);

        double alpha;
        lock (_syncRoot)
        {
            alpha = _alpha;
        }

        // Blend sub-strategy explanations using the Blend helper, then apply centralized penalty
        var blended = heuristicExplanation.Blend(learnedExplanation, alpha);
        var penalty = ComputeSoftGenrePenalty(features.GenreSimilarity, _genrePenaltyFloor);
        var result = blended.WithPenalty(penalty);

        result.StrategyName = Name;
        result.DominantSignal = ScoreExplanation.DetermineDominantSignal(
            result.GenreContribution,
            result.CollaborativeContribution,
            result.RatingContribution,
            result.UserRatingContribution,
            result.RecencyContribution,
            result.YearProximityContribution,
            result.InteractionContribution);

        return result;
    }

    /// <summary>
    ///     Delegates training to the learned strategy and updates the blending factor.
    /// </summary>
    /// <param name="examples">Training examples with features and labels.</param>
    /// <returns>True if training was performed, false if insufficient data.</returns>
    public bool Train(IReadOnlyList<TrainingExample> examples)
    {
        var result = _learned.Train(examples);

        if (result)
        {
            // Check validation loss quality gate
            var validationLoss = _learned.LastValidationLoss;
            var qualityGatePassed = !double.IsNaN(validationLoss) && validationLoss <= ValidationLossThreshold;

            lock (_syncRoot)
            {
                // Always track cumulative examples
                _trainingExampleCount += examples.Count;

                if (qualityGatePassed)
                {
                    // Good generalization — let alpha progress normally
                    _alpha = ComputeSigmoidAlpha(_trainingExampleCount, _alphaMin, _alphaMax);
                }

                // If quality gate failed, alpha stays at its current value (frozen)
                // This prevents the learned model from gaining more influence when
                // its predictions are unreliable.
            }

            // Log training quality metrics for transparency (Point 12)
            System.Diagnostics.Trace.TraceInformation(
                "[EnsembleScoringStrategy] Training complete: examples={0}, validationLoss={1:F6}, qualityGate={2}, alpha={3:F4}",
                examples.Count,
                validationLoss,
                qualityGatePassed ? "passed" : "frozen",
                _alpha);

            TrySaveState();
        }

        return result;
    }

    /// <summary>
    ///     Computes a soft genre-mismatch penalty that ramps linearly from
    ///     <paramref name="penaltyFloor"/> (at GenreSimilarity = 0) to 1.0
    ///     (at GenreSimilarity ≥ <see cref="GenrePenaltyThreshold"/>).
    ///     This avoids the hard cutoff of the previous implementation.
    /// </summary>
    /// <param name="genreSimilarity">The candidate's genre similarity score (0–1).</param>
    /// <param name="penaltyFloor">
    ///     Minimum penalty multiplier (default: <see cref="DefaultGenrePenaltyFloor"/>).
    /// </param>
    /// <returns>A penalty multiplier between <paramref name="penaltyFloor"/> and 1.0.</returns>
    internal static double ComputeSoftGenrePenalty(
        double genreSimilarity,
        double penaltyFloor = DefaultGenrePenaltyFloor)
    {
        if (genreSimilarity >= GenrePenaltyThreshold)
        {
            return 1.0;
        }

        // Linear ramp from penaltyFloor to 1.0 as genreSimilarity goes from 0 to GenrePenaltyThreshold
        var t = genreSimilarity / GenrePenaltyThreshold;
        return penaltyFloor + (t * (1.0 - penaltyFloor));
    }

    /// <summary>
    ///     Computes the blending factor α using a sigmoid function for smooth transitions.
    ///     Formula: α = αMin + (αMax - αMin) / (1 + e^(-k × (n - midpoint))).
    /// </summary>
    /// <param name="trainingExampleCount">The cumulative number of training examples.</param>
    /// <param name="alphaMin">
    ///     Minimum alpha value (default: <see cref="DefaultAlphaMin"/>).
    /// </param>
    /// <param name="alphaMax">
    ///     Maximum alpha value (default: <see cref="DefaultAlphaMax"/>).
    /// </param>
    /// <returns>A blending factor between <paramref name="alphaMin"/> and <paramref name="alphaMax"/>.</returns>
    internal static double ComputeSigmoidAlpha(
        int trainingExampleCount,
        double alphaMin = DefaultAlphaMin,
        double alphaMax = DefaultAlphaMax)
    {
        var exponent = -AlphaSigmoidK * (trainingExampleCount - AlphaSigmoidMidpoint);
        return alphaMin + ((alphaMax - alphaMin) / (1.0 + Math.Exp(exponent)));
    }

    /// <summary>
    ///     Derives the ensemble state file path from the learned weights path.
    /// </summary>
    private static string? DeriveStatePath(string? weightsPath)
    {
        if (string.IsNullOrEmpty(weightsPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(weightsPath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        return Path.Combine(directory, "ensemble_state.json");
    }

    /// <summary>
    ///     Tries to load persisted ensemble state (alpha, training count) from disk.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Graceful fallback to defaults on any I/O or parse error")]
    private void TryLoadState()
    {
        if (string.IsNullOrEmpty(_statePath) || !File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            var data = JsonSerializer.Deserialize<EnsembleStateData>(json);
            if (data is not null && data.TrainingExampleCount > 0)
            {
                lock (_syncRoot)
                {
                    _trainingExampleCount = data.TrainingExampleCount;
                    _alpha = ComputeSigmoidAlpha(_trainingExampleCount, _alphaMin, _alphaMax);
                }
            }
        }
        catch (Exception)
        {
            // Silently fall back to defaults
        }
    }

    /// <summary>
    ///     Tries to persist current ensemble state to disk.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Non-critical persistence — silently ignore write failures")]
    private void TrySaveState()
    {
        if (string.IsNullOrEmpty(_statePath))
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir) && dir != ".")
            {
                Directory.CreateDirectory(dir);
            }

            double alpha;
            int exampleCount;
            lock (_syncRoot)
            {
                alpha = _alpha;
                exampleCount = _trainingExampleCount;
            }

            var data = new EnsembleStateData
            {
                TrainingExampleCount = exampleCount,
                Alpha = alpha,
                UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            var json = JsonSerializer.Serialize(data, SerializerOptions);
            var tempPath = _statePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _statePath, overwrite: true);
        }
        catch (Exception)
        {
            // Non-critical — silently ignore
        }
    }

    /// <summary>
    ///     Serializable container for persisted ensemble state.
    /// </summary>
    internal sealed class EnsembleStateData
    {
        /// <summary>Gets or sets the cumulative number of training examples seen.</summary>
        public int TrainingExampleCount { get; set; }

        /// <summary>Gets or sets the current blending factor alpha.</summary>
        public double Alpha { get; set; }

        /// <summary>Gets or sets the ISO 8601 timestamp of the last update.</summary>
        public string UpdatedAt { get; set; } = string.Empty;
    }
}