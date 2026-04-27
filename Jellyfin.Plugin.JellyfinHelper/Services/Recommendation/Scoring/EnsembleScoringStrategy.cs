using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

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
///     but soft-dampened when the learned model's validation loss exceeds <see cref="ValidationLossThreshold"/>.
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
    ///     Capped at 0.75 instead of 1.0 so that heuristic rules always contribute at least
    ///     25% — this guards against overfitting when the ML model has limited diversity.
    /// </summary>
    internal const double DefaultAlphaMax = 0.75;

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
    /// <remarks>
    ///     References the shared constant to stay consistent with <see cref="HeuristicScoringStrategy"/>.
    /// </remarks>
    internal const double GenrePenaltyThreshold = ScoringHelper.DefaultGenrePenaltyThreshold;

    /// <summary>
    ///     Default minimum penalty multiplier for items with zero genre overlap.
    ///     Items with GenreSimilarity = 0 get score × 0.10 (a 90% penalty).
    ///     This is aggressive enough to deprioritize completely unrelated items
    ///     while still allowing them to surface if other signals are very strong.
    /// </summary>
    internal const double DefaultGenrePenaltyFloor = 0.10;

    /// <summary>
    ///     Validation loss (MSE) threshold for full alpha progression.
    ///     Below this threshold, alpha advances at full sigmoid rate.
    ///     Above it, alpha is soft-dampened proportionally (not hard-frozen).
    ///     0.30 corresponds to an average prediction error of ~0.55 on a 0–1 scale,
    ///     which allows the ML model to contribute even with noisy small-sample training data.
    /// </summary>
    internal const double ValidationLossThreshold = 0.30;

    /// <summary>
    ///     Upper bound for soft damping. When validation loss reaches this value (2× threshold),
    ///     alpha is fully dampened back to <see cref="DefaultAlphaMin"/>.
    ///     Between <see cref="ValidationLossThreshold"/> and this value, alpha is linearly interpolated.
    /// </summary>
    internal const double ValidationLossCeiling = ValidationLossThreshold * 2.0;

    /// <summary>
    ///     Minimum cumulative training examples before the neural strategy is blended in.
    ///     Below this threshold, the neural strategy is not used (beta = 0).
    ///     Set to 75 (between linear's 5 and a full dataset) so the MLP has enough
    ///     examples for Z-score standardization and meaningful gradient updates.
    /// </summary>
    internal const int NeuralActivationThreshold = 75;

    /// <summary>
    ///     Maximum fraction of the learned weight (α) that can be re-allocated to the neural strategy.
    ///     At full progression, the neural strategy receives up to 40% of the ML budget,
    ///     with the remaining 60% staying with the linear learned strategy.
    /// </summary>
    internal const double NeuralMaxBetaFraction = 0.4;

    /// <summary>
    ///     Minimum neural beta below which the neural strategy is deactivated.
    ///     Prevents infinitesimal floating-point ghost values from keeping the neural
    ///     path active with no meaningful contribution.
    /// </summary>
    internal const double NeuralBetaMinFloor = 0.01;

    /// <summary>
    ///     Minimum number of metrics snapshots required before trend analysis activates.
    ///     Below this count, <see cref="AnalyzeTrend"/> returns <see cref="MetricsTrend.InsufficientData"/>.
    /// </summary>
    internal const int TrendMinSnapshots = 5;

    /// <summary>
    ///     Alpha damping factor applied per training round when a degrading trend is detected.
    ///     Multiplicative: new_alpha = alphaMin + (alpha - alphaMin) * factor.
    ///     0.90 means 10% rollback toward heuristic per degrading round.
    /// </summary>
    internal const double TrendDegradationDamping = 0.90;

    /// <summary>
    ///     Alpha boost factor applied when an improving trend is detected.
    ///     The quality factor is multiplied by this value (capped at 1.0) to allow
    ///     faster alpha progression when the model is consistently improving.
    /// </summary>
    internal const double TrendImprovementBoost = 1.15;

    /// <summary>Cached JSON serializer options for ensemble state persistence.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly HeuristicScoringStrategy _heuristic;
    private readonly LearnedScoringStrategy _learned;
    private readonly NeuralScoringStrategy? _neural;
    private readonly ILogger? _logger;
    private readonly object _syncRoot = new();
    private readonly double _alphaMax;
    private readonly double _alphaMin;
    private readonly double _genrePenaltyFloor;
    private readonly string? _statePath;
    private double _alpha;
    private double _neuralBeta;
    private bool _qualityGateFrozen;
    private int _trainingExampleCount;
    private List<MetricsSnapshot> _metricsHistory = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="EnsembleScoringStrategy" /> class
    ///     with injected sub-strategies for testability and flexibility.
    /// </summary>
    /// <param name="learned">The learned (adaptive ML) sub-strategy.</param>
    /// <param name="heuristic">
    ///     The heuristic (rule-based) sub-strategy. Must be constructed with
    ///     <c>genrePenaltyFloor: 1.0</c> (penalty disabled) because the ensemble applies
    ///     the genre penalty centrally via <see cref="ComputeSoftGenrePenalty"/> after blending.
    ///     Passing a default-configured heuristic (floor 0.10) would cause double-penalization.
    /// </param>
    /// <param name="neural">Optional neural (MLP) sub-strategy. When provided, it is blended in after sufficient training data is available.</param>
    /// <param name="statePath">Optional file path for persisting ensemble state.</param>
    /// <param name="alphaMin">Minimum blending factor.</param>
    /// <param name="alphaMax">Maximum blending factor.</param>
    /// <param name="genrePenaltyFloor">Minimum genre penalty multiplier.</param>
    /// <param name="logger">Optional logger for training diagnostics.</param>
    public EnsembleScoringStrategy(
        LearnedScoringStrategy learned,
        HeuristicScoringStrategy heuristic,
        NeuralScoringStrategy? neural = null,
        string? statePath = null,
        double alphaMin = DefaultAlphaMin,
        double alphaMax = DefaultAlphaMax,
        double genrePenaltyFloor = DefaultGenrePenaltyFloor,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(learned);
        ArgumentNullException.ThrowIfNull(heuristic);

        // Guard: the heuristic must have its genre penalty disabled (floor = 1.0) because
        // the ensemble applies the penalty centrally via ComputeSoftGenrePenalty after blending.
        // A default-configured heuristic (floor 0.10) would cause double-penalization.
        if (Math.Abs(heuristic.GenrePenaltyFloor - 1.0) > 0.001)
        {
            throw new ArgumentException(
                $"Heuristic sub-strategy must have genrePenaltyFloor=1.0 (penalty disabled) to avoid " +
                $"double-penalization. Got {heuristic.GenrePenaltyFloor:F3}.",
                nameof(heuristic));
        }

        _alphaMin = Math.Clamp(alphaMin, 0.0, 1.0);
        _alphaMax = Math.Clamp(alphaMax, _alphaMin, 1.0);
        _genrePenaltyFloor = Math.Clamp(genrePenaltyFloor, 0.0, 1.0);
        _alpha = _alphaMin;

        _learned = learned;
        _neural = neural;
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
            neural: null,
            statePath: DeriveStatePath(weightsPath),
            alphaMin: alphaMin,
            alphaMax: alphaMax,
            genrePenaltyFloor: genrePenaltyFloor)
    {
    }

    /// <summary>
    ///     Detected trend direction from metrics history analysis.
    /// </summary>
    internal enum MetricsTrend
    {
        /// <summary>Not enough snapshots for reliable trend detection.</summary>
        InsufficientData,

        /// <summary>Validation loss is decreasing and/or ranking metrics are improving.</summary>
        Improving,

        /// <summary>Metrics are fluctuating within a narrow band.</summary>
        Stable,

        /// <summary>Validation loss is increasing and/or ranking metrics are declining.</summary>
        Degrading
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

    /// <summary>
    ///     Gets the underlying neural strategy, if any (for testing/debugging).
    /// </summary>
    internal NeuralScoringStrategy? NeuralStrategy => _neural;

    /// <summary>
    ///     Gets the current neural blending factor β (for testing/debugging).
    ///     β is the fraction of the ML budget allocated to the neural strategy.
    /// </summary>
    internal double CurrentNeuralBeta
    {
        get
        {
            lock (_syncRoot)
            {
                return _neuralBeta;
            }
        }
    }

    /// <summary>
    ///     Gets the trend detected from the current metrics history (for testing/debugging).
    ///     Returns <see cref="MetricsTrend.InsufficientData"/> before enough training runs.
    /// </summary>
    internal MetricsTrend LastTrend
    {
        get
        {
            lock (_syncRoot)
            {
                return AnalyzeTrend(_metricsHistory);
            }
        }
    }

    /// <summary>
    ///     Gets the current metrics history count (for testing/debugging).
    /// </summary>
    internal int MetricsHistoryCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _metricsHistory.Count;
            }
        }
    }

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        // Snapshot blending factors atomically — sub-strategies handle their own thread safety.
        double alpha;
        double beta;
        lock (_syncRoot)
        {
            alpha = _alpha;
            beta = _neuralBeta;
        }

        // Score calls are outside the lock to avoid nested locking (each sub-strategy
        // has its own internal lock) and to allow parallel scoring across threads.
        var learnedScore = _learned.Score(features);
        var heuristicScore = _heuristic.Score(features);

        double mlScore;
        if (_neural is not null && beta > 0)
        {
            var neuralScore = _neural.Score(features);
            // Split ML budget between learned and neural: β goes to neural, (1-β) stays with learned
            mlScore = ((1.0 - beta) * learnedScore) + (beta * neuralScore);
        }
        else
        {
            mlScore = learnedScore;
        }

        var blendedScore = (alpha * mlScore) + ((1.0 - alpha) * heuristicScore);
        var penalty = ComputeSoftGenrePenalty(features.GenreSimilarity, _genrePenaltyFloor);
        return blendedScore * penalty;
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        // Snapshot blending factors atomically — sub-strategies handle their own thread safety.
        double alpha;
        double beta;
        lock (_syncRoot)
        {
            alpha = _alpha;
            beta = _neuralBeta;
        }

        // Score calls are outside the lock to allow parallel scoring across threads.
        var learnedExplanation = _learned.ScoreWithExplanation(features);
        var heuristicExplanation = _heuristic.ScoreWithExplanation(features);

        // When neural is active and beta > 0, blend learned + neural into an ML explanation first,
        // then blend the ML explanation with heuristic. This matches the Score() method's logic:
        // mlScore = (1-β) × learned + β × neural; final = α × ml + (1-α) × heuristic
        ScoreExplanation mlExplanation;
        if (_neural is not null && beta > 0)
        {
            var neuralExplanation = _neural.ScoreWithExplanation(features);
            // Blend learned + neural: result = (1-β) × learned + β × neural
            mlExplanation = learnedExplanation.Blend(neuralExplanation, beta);
        }
        else
        {
            mlExplanation = learnedExplanation;
        }

        // Blend heuristic + ML: result = (1-α) × heuristic + α × ML
        var blended = heuristicExplanation.Blend(mlExplanation, alpha);
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
            result.InteractionContribution,
            result.PeopleContribution,
            result.StudioContribution);

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

        // Also train neural strategy if available (independent of learned success)
        var neuralTrained = _neural is not null && _neural.Train(examples);

        if (result)
        {
            // Check validation loss quality gate
            var validationLoss = _learned.LastValidationLoss;
            var qualityGatePassed = !double.IsNaN(validationLoss) && validationLoss <= ValidationLossThreshold;

            lock (_syncRoot)
            {
                // Always track cumulative examples
                _trainingExampleCount += examples.Count;

                // Compute the target alpha from the sigmoid curve
                var sigmoidAlpha = ComputeSigmoidAlpha(_trainingExampleCount, _alphaMin, _alphaMax);

                if (qualityGatePassed)
                {
                    // Good generalization — let alpha progress at full sigmoid rate
                    _alpha = sigmoidAlpha;
                    _qualityGateFrozen = false;
                }
                else
                {
                    // Soft damping: alpha still advances but is proportionally dampened
                    // based on how far the validation loss exceeds the threshold.
                    // qualityFactor = 1.0 at threshold, 0.0 at 2× threshold (ceiling).
                    var qualityFactor = double.IsNaN(validationLoss)
                        ? 0.5 // NaN (no validation split) → use half progression
                        : Math.Clamp(
                            1.0 - ((validationLoss - ValidationLossThreshold)
                                   / (ValidationLossCeiling - ValidationLossThreshold)),
                            0.0,
                            1.0);

                    _alpha = _alphaMin + ((sigmoidAlpha - _alphaMin) * qualityFactor);
                    _qualityGateFrozen = qualityFactor < 0.01;
                }

                // Update neural beta: blend neural in after NeuralActivationThreshold
                // using a sigmoid ramp from 0 to NeuralMaxBetaFraction.
                // Only activate if the neural strategy was successfully trained.
                if (_neural is not null && neuralTrained && _trainingExampleCount >= NeuralActivationThreshold)
                {
                    var neuralValidationLoss = _neural.LastValidationLoss;
                    var neuralQualityOk = !double.IsNaN(neuralValidationLoss)
                        && neuralValidationLoss <= ValidationLossThreshold;

                    if (neuralQualityOk)
                    {
                        // Linear ramp from 0 to NeuralMaxBetaFraction over 75..175 examples
                        var progress = Math.Clamp(
                            (_trainingExampleCount - NeuralActivationThreshold) / 100.0,
                            0.0,
                            1.0);
                        _neuralBeta = NeuralMaxBetaFraction * progress;
                    }
                    else
                    {
                        // Neural not generalizing well — reduce its influence.
                        // Apply floor to avoid infinitesimal ghost values.
                        _neuralBeta *= 0.5;
                        if (_neuralBeta < NeuralBetaMinFloor)
                        {
                            _neuralBeta = 0.0;
                        }
                    }
                }
                else if (_neural is not null && !neuralTrained && _neuralBeta > 0)
                {
                    // Neural strategy failed to train this round while learned succeeded —
                    // decay β to avoid stale influence, analogous to the learned-failure branch.
                    _neuralBeta *= 0.5;
                    if (_neuralBeta < NeuralBetaMinFloor)
                    {
                        _neuralBeta = 0.0;
                    }
                }
            }

            // Record metrics snapshot and analyze trend BEFORE saving state,
            // so trend-driven alpha/beta adjustments are persisted in the same write.
            MetricsTrend trend;
            lock (_syncRoot)
            {
                _metricsHistory.Add(new MetricsSnapshot
                {
                    Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ValidationLoss = validationLoss,
                    PrecisionAtK = _learned.LastPrecisionAtK,
                    RecallAtK = _learned.LastRecallAtK,
                    NdcgAtK = _learned.LastNdcgAtK,
                    ExampleCount = examples.Count
                });
                const int maxHistory = 10;
                if (_metricsHistory.Count > maxHistory)
                {
                    _metricsHistory.RemoveRange(0, _metricsHistory.Count - maxHistory);
                }

                // Analyze trend from the updated history
                trend = AnalyzeTrend(_metricsHistory);

                // Apply trend-driven alpha/beta adjustments
                if (trend == MetricsTrend.Degrading)
                {
                    // Roll alpha back toward heuristic
                    _alpha = _alphaMin + ((_alpha - _alphaMin) * TrendDegradationDamping);

                    // Also reduce neural influence when trend is degrading
                    if (_neuralBeta > 0)
                    {
                        _neuralBeta *= TrendDegradationDamping;
                        if (_neuralBeta < NeuralBetaMinFloor)
                        {
                            _neuralBeta = 0.0;
                        }
                    }
                }
                else if (trend == MetricsTrend.Improving)
                {
                    // Allow faster alpha progression toward sigmoid target
                    var sigmoidTarget = ComputeSigmoidAlpha(_trainingExampleCount, _alphaMin, _alphaMax);
                    _alpha = Math.Min(sigmoidTarget, _alpha + ((_alphaMax - _alpha) * (1.0 - TrendDegradationDamping)));
                }
            }

            // Log training quality metrics including trend
            _logger?.LogInformation(
                "Training complete: examples={ExampleCount}, valLoss={ValidationLoss:F6}, P@{K}={PrecisionAtK:F3}, R@{K2}={RecallAtK:F3}, NDCG@{K3}={NdcgAtK:F3}, qualityGate={QualityGate}, alpha={Alpha:F4}, neuralBeta={NeuralBeta:F4}, trend={Trend}",
                examples.Count,
                validationLoss,
                RankingMetrics.DefaultK,
                _learned.LastPrecisionAtK,
                RankingMetrics.DefaultK,
                _learned.LastRecallAtK,
                RankingMetrics.DefaultK,
                _learned.LastNdcgAtK,
                qualityGatePassed ? "passed" : "dampened",
                _alpha,
                _neuralBeta,
                trend);

            TrySaveState();
        }
        else
        {
            // Learned training failed (insufficient data). Decay neuralBeta to prevent
            // a stale high value from persisting when the neural strategy may have
            // outdated weights. This ensures cold-start scenarios don't over-weight
            // a potentially unreliable neural model.
            var stateChanged = false;
            lock (_syncRoot)
            {
                if (_neuralBeta > 0)
                {
                    _neuralBeta *= 0.5;
                    if (_neuralBeta < NeuralBetaMinFloor)
                    {
                        _neuralBeta = 0.0;
                    }

                    stateChanged = true;
                }
            }

            if (stateChanged)
            {
                TrySaveState();
            }
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
                    _alpha = (data.Alpha >= _alphaMin && data.Alpha <= _alphaMax)
                        ? data.Alpha
                        : ComputeSigmoidAlpha(_trainingExampleCount, _alphaMin, _alphaMax);

                    // Restore neural beta so it survives server restarts
                    if (data.NeuralBeta >= 0 && data.NeuralBeta <= NeuralMaxBetaFraction)
                    {
                        _neuralBeta = data.NeuralBeta;
                    }

                    if (data.MetricsHistory is { Count: > 0 })
                    {
                        _metricsHistory = new List<MetricsSnapshot>(data.MetricsHistory);
                    }
                }
            }
        }
        catch (IOException ex)
        {
            // Graceful fallback to defaults on I/O error — log for diagnostics
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to load state");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Graceful fallback to defaults on access denied — log for diagnostics
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to load state (access denied)");
        }
        catch (JsonException ex)
        {
            // Graceful fallback to defaults on parse error — log for diagnostics
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to parse state");
        }
    }

    /// <summary>
    ///     Tries to persist current ensemble state to disk.
    ///     Snapshot and serialization are performed under lock to ensure consistency
    ///     with concurrent <see cref="Train"/> calls (analogous to
    ///     <see cref="LearnedScoringStrategy.TrySaveWeights"/>).
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

            // Snapshot and serialize under lock to ensure consistency with concurrent Train() calls
            string json;
            lock (_syncRoot)
            {
                var data = new EnsembleStateData
                {
                    TrainingExampleCount = _trainingExampleCount,
                    Alpha = _alpha,
                    NeuralBeta = _neuralBeta,
                    QualityGateFrozen = _qualityGateFrozen,
                    MetricsHistory = new List<MetricsSnapshot>(_metricsHistory),
                    UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };

                json = JsonSerializer.Serialize(data, SerializerOptions);
            }

            var tempPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _statePath, overwrite: true);
        }
        catch (IOException ex)
        {
            // Non-critical — log for diagnostics but don't fail
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to save state");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Non-critical — log for diagnostics but don't fail
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to save state (access denied)");
        }
        catch (JsonException ex)
        {
            // Non-critical — log for diagnostics but don't fail
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to serialize state");
        }
    }

    /// <summary>
    ///     Analyzes the rolling metrics history to detect training quality trends.
    ///     Uses linear slope over the last <see cref="TrendMinSnapshots"/> snapshots.
    ///     Validation loss slope &gt; 0 indicates degradation (loss is rising);
    ///     NDCG slope &lt; 0 also indicates degradation (ranking quality is falling).
    /// </summary>
    /// <param name="history">The metrics history snapshots (most recent last).</param>
    /// <returns>The detected trend direction.</returns>
    internal static MetricsTrend AnalyzeTrend(IReadOnlyList<MetricsSnapshot> history)
    {
        if (history.Count < TrendMinSnapshots)
        {
            return MetricsTrend.InsufficientData;
        }

        var startIdx = history.Count - TrendMinSnapshots;
        var n = TrendMinSnapshots;
        var meanI = (n - 1) / 2.0;

        double sumLoss = 0, sumNdcg = 0;
        for (var i = 0; i < n; i++)
        {
            sumLoss += history[startIdx + i].ValidationLoss;
            sumNdcg += history[startIdx + i].NdcgAtK;
        }

        var meanLoss = sumLoss / n;
        var meanNdcg = sumNdcg / n;

        double numLoss = 0, numNdcg = 0, denominator = 0;
        for (var i = 0; i < n; i++)
        {
            var di = i - meanI;
            numLoss += di * (history[startIdx + i].ValidationLoss - meanLoss);
            numNdcg += di * (history[startIdx + i].NdcgAtK - meanNdcg);
            denominator += di * di;
        }

        if (denominator < 1e-12)
        {
            return MetricsTrend.Stable;
        }

        var slopeLoss = numLoss / denominator;
        var slopeNdcg = numNdcg / denominator;

        const double slopeThreshold = 0.005;

        var lossDegrading = slopeLoss > slopeThreshold;
        var ndcgDegrading = slopeNdcg < -slopeThreshold;
        var lossImproving = slopeLoss < -slopeThreshold;
        var ndcgImproving = slopeNdcg > slopeThreshold;

        if ((lossDegrading && !ndcgImproving) || (ndcgDegrading && !lossImproving))
        {
            return MetricsTrend.Degrading;
        }

        if ((lossImproving && !ndcgDegrading) || (ndcgImproving && !lossDegrading))
        {
            return MetricsTrend.Improving;
        }

        return MetricsTrend.Stable;
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

        /// <summary>Gets or sets the current neural blending factor beta.</summary>
        public double NeuralBeta { get; set; }

        /// <summary>Gets or sets a value indicating whether the quality gate has frozen alpha progression.</summary>
        public bool QualityGateFrozen { get; set; }

        /// <summary>Gets or sets the ISO 8601 timestamp of the last update.</summary>
        public string UpdatedAt { get; set; } = string.Empty;

        /// <summary>Gets or sets the rolling history of training metrics (last 10 runs).</summary>
        public List<MetricsSnapshot> MetricsHistory { get; set; } = [];
    }

    /// <summary>
    ///     A single point-in-time snapshot of training quality metrics.
    /// </summary>
    internal sealed class MetricsSnapshot
    {
        /// <summary>Gets or sets the ISO 8601 timestamp.</summary>
        public string Timestamp { get; set; } = string.Empty;

        /// <summary>Gets or sets the validation loss (MSE).</summary>
        public double ValidationLoss { get; set; }

        /// <summary>Gets or sets the Precision at K.</summary>
        public double PrecisionAtK { get; set; }

        /// <summary>Gets or sets the Recall at K.</summary>
        public double RecallAtK { get; set; }

        /// <summary>Gets or sets the NDCG at K.</summary>
        public double NdcgAtK { get; set; }

        /// <summary>Gets or sets the number of training examples used.</summary>
        public int ExampleCount { get; set; }
    }
}
