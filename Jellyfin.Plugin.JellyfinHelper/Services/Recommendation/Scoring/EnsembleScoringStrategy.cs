using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
    /// <summary>
    ///     Default minimum blending factor (heuristic dominates with no training data).
    ///     Set to 0.3 so that even without ML data, the learned strategy contributes 30%
    ///     (using its default genre-dominant weights) for a smoother cold-start experience.
    /// </summary>
    internal const double DefaultAlphaMin = 0.3;

    /// <summary>
    ///     Default maximum blending factor (learned dominates with abundant data).
    ///     Capped at 0.8 instead of 1.0 so that heuristic rules always contribute at least
    ///     20% — this guards against overfitting when the ML model has limited diversity.
    /// </summary>
    internal const double DefaultAlphaMax = 0.8;

    /// <summary>
    ///     Sigmoid steepness for alpha transition.
    ///     k=0.05 yields a gentle S-curve that transitions over ~80 examples (from ~10 to ~90).
    /// </summary>
    internal const double AlphaSigmoidK = 0.05;

    /// <summary>
    ///     Sigmoid midpoint (number of examples where alpha = (αMin + αMax) / 2).
    ///     50 examples is a reasonable threshold for a typical user's first few weeks of activity.
    /// </summary>
    internal const double AlphaSigmoidMidpoint = 50.0;

    /// <summary>
    ///     Genre similarity threshold below which the soft penalty ramps down.
    ///     Items above this threshold receive no penalty (multiplier = 1.0).
    ///     0.15 means items sharing at least ~15% of the user's preferred genres are unpenalized.
    /// </summary>
    internal const double GenrePenaltyThreshold = 0.15;

    /// <summary>
    ///     Default minimum penalty multiplier for items with zero genre overlap.
    ///     Items with GenreSimilarity = 0 get score × 0.10 (a 90% penalty).
    ///     This is aggressive enough to deprioritize completely unrelated items
    ///     while still allowing them to surface if other signals are very strong.
    /// </summary>
    internal const double DefaultGenrePenaltyFloor = 0.10;

    /// <summary>
    ///     Maximum acceptable validation loss (MSE) before alpha progression is frozen.
    ///     0.15 corresponds to an average prediction error of ~0.39 on a 0–1 scale,
    ///     which is the threshold above which the ML model's predictions are considered unreliable.
    /// </summary>
    internal const double ValidationLossThreshold = 0.15;

    /// <summary>Cached JSON serializer options for ensemble state persistence.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly HeuristicScoringStrategy _heuristic;
    private readonly LearnedScoringStrategy _learned;
    private readonly ILogger? _logger;
    private readonly object _syncRoot = new();
    private readonly double _alphaMax;
    private readonly double _alphaMin;
    private readonly double _genrePenaltyFloor;
    private readonly string? _statePath;
    private double _alpha;
    private bool _qualityGateFrozen;
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
    /// <param name="logger">Optional logger for training diagnostics.</param>
    public EnsembleScoringStrategy(
        LearnedScoringStrategy learned,
        HeuristicScoringStrategy heuristic,
        string? statePath = null,
        double alphaMin = DefaultAlphaMin,
        double alphaMax = DefaultAlphaMax,
        double genrePenaltyFloor = DefaultGenrePenaltyFloor,
        ILogger? logger = null)
    {
        _alphaMin = Math.Clamp(alphaMin, 0.0, 1.0);
        _alphaMax = Math.Clamp(alphaMax, _alphaMin, 1.0);
        _genrePenaltyFloor = Math.Clamp(genrePenaltyFloor, 0.0, 1.0);
        _alpha = _alphaMin;

        _learned = learned;
        // Disable penalty in sub-strategies since ensemble applies it centrally
        _heuristic = heuristic;
        _logger = logger;

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
    ///     Gets a value indicating whether alpha progression is currently frozen by the quality gate (for testing/debugging).
    /// </summary>
    internal bool IsQualityGateFrozen
    {
        get
        {
            lock (_syncRoot)
            {
                return _qualityGateFrozen;
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
                    _qualityGateFrozen = false;
                }
                else
                {
                    // Quality gate failed — freeze alpha at its current value.
                    // This prevents the learned model from gaining more influence when
                    // its predictions are unreliable. State is persisted so freeze
                    // survives server restarts.
                    _qualityGateFrozen = true;
                }
            }

            // Log training quality metrics for transparency using structured logging
            _logger?.LogInformation(
                "Training complete: examples={ExampleCount}, validationLoss={ValidationLoss:F6}, qualityGate={QualityGate}, alpha={Alpha:F4}",
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
    ///     Delegates to <see cref="ScoringHelper.ComputeSoftGenrePenalty"/> for consistency
    ///     with <see cref="HeuristicScoringStrategy"/>.
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
        return ScoringHelper.ComputeSoftGenrePenalty(genreSimilarity, penaltyFloor, GenrePenaltyThreshold);
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
    ///     Tries to load persisted ensemble state (alpha, training count, quality gate) from disk.
    ///     Restores the persisted alpha value directly instead of recomputing via sigmoid,
    ///     so that the quality-gate freeze state is preserved across server restarts.
    /// </summary>
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
                    _qualityGateFrozen = data.QualityGateFrozen;

                    // Restore persisted alpha directly instead of recomputing via sigmoid,
                    // so that the quality-gate freeze state is preserved across restarts.
                    // Only recompute if the persisted alpha is outside the valid range.
                    if (data.Alpha >= _alphaMin && data.Alpha <= _alphaMax)
                    {
                        _alpha = data.Alpha;
                    }
                    else
                    {
                        _alpha = ComputeSigmoidAlpha(_trainingExampleCount, _alphaMin, _alphaMax);
                    }
                }
            }
        }
        catch (IOException ex)
        {
            // Graceful fallback to defaults on I/O error — log for diagnostics
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to load state");
        }
        catch (JsonException ex)
        {
            // Graceful fallback to defaults on parse error — log for diagnostics
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to parse state");
        }
    }

    /// <summary>
    ///     Tries to persist current ensemble state to disk.
    /// </summary>
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
            bool frozen;
            lock (_syncRoot)
            {
                alpha = _alpha;
                exampleCount = _trainingExampleCount;
                frozen = _qualityGateFrozen;
            }

            var data = new EnsembleStateData
            {
                TrainingExampleCount = exampleCount,
                Alpha = alpha,
                QualityGateFrozen = frozen,
                UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            var json = JsonSerializer.Serialize(data, SerializerOptions);
            var tempPath = _statePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _statePath, overwrite: true);
        }
        catch (IOException ex)
        {
            // Non-critical — log for diagnostics but don't fail
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to save state");
        }
        catch (JsonException ex)
        {
            // Non-critical — log for diagnostics but don't fail
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to serialize state");
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

        /// <summary>Gets or sets a value indicating whether the quality gate has frozen alpha progression.</summary>
        public bool QualityGateFrozen { get; set; }

        /// <summary>Gets or sets the ISO 8601 timestamp of the last update.</summary>
        public string UpdatedAt { get; set; } = string.Empty;
    }
}