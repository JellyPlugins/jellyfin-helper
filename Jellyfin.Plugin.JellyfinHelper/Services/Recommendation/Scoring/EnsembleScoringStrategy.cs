using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Ensemble scoring strategy that combines the learned (adaptive ML) strategy with the heuristic (rule-based) strategy for steadier recommendations.
/// </summary>
/// <remarks>
///     Architecture: score = (α × Learned.Score + (1 - α) × Heuristic.Score) × softPenalty(genreSimilarity) where α is computed via sigmoid: α = αMin + (αMax - αMin) / (1 + e^(-k × (n - midpoint))) but soft-dampened when the learned model's validation loss exceeds.
/// </remarks>
public sealed class EnsembleScoringStrategy : IScoringStrategy, ITrainableStrategy, IDisposable
{
    /// <summary>
    ///     Default minimum blending factor (heuristic dominates with no training data). Set to 0.3 so that even without ML data, the learned strategy contributes 30% (using its default genre-dominant weights) for a smoother cold-start experience.
    /// </summary>
    internal const double DefaultAlphaMin = 0.3;

    /// <summary>
    ///     Default maximum blending factor (learned dominates with abundant data). Capped at 0.75 instead of 1.0 so that heuristic rules always contribute at least 25% - this guards against overfitting when the ML model has limited diversity.
    /// </summary>
    internal const double DefaultAlphaMax = 0.75;

    /// <summary>
    ///     Sigmoid steepness for alpha transition.
    ///     k=0.05 yields a gentle S-curve that transitions over ~80 examples (from ~10 to ~90).
    /// </summary>
    internal const double AlphaSigmoidK = 0.05;

    /// <summary>
    ///     Default sigmoid midpoint (number of examples where alpha = (αMin + αMax) / 2). 50 examples is a reasonable threshold for a typical user's first few weeks of activity.
    /// </summary>
    internal const double DefaultSigmoidMidpoint = 50.0;

    /// <summary>
    ///     Maximum absolute shift allowed for the adaptive sigmoid midpoint. Prevents runaway adaptation from driving the midpoint to extreme values.
    /// </summary>
    internal const double MaxMidpointShift = 20.0;

    /// <summary>
    ///     Step size for sigmoid midpoint adaptation per training run with cohort signal.
    ///     Small steps ensure gradual convergence without oscillation.
    /// </summary>
    internal const double MidpointAdaptationStep = 3.0;

    /// <summary>
    ///     Multiplicative decay factor applied to the midpoint offset when neither exploration cohort beats control.
    /// </summary>
    internal const double MidpointDecayFactor = 0.98;

    /// <summary>
    ///     Genre similarity threshold below which the soft penalty ramps down. Items above this threshold receive no penalty (multiplier = 1.0).
    /// </summary>
    /// <remarks>
    ///     References the shared constant to stay consistent with <see cref="HeuristicScoringStrategy"/>.
    /// </remarks>
    internal const double GenrePenaltyThreshold = ScoringHelper.DefaultGenrePenaltyThreshold;

    /// <summary>
    ///     Default minimum penalty multiplier for items with zero genre overlap. Items with GenreSimilarity = 0 get score × 0.10 (a 90% penalty).
    /// </summary>
    internal const double DefaultGenrePenaltyFloor = 0.10;

    /// <summary>
    ///     Validation loss (MSE) threshold for full alpha progression. Below this threshold, alpha advances at full sigmoid rate.
    /// </summary>
    internal const double ValidationLossThreshold = 0.30;

    /// <summary>
    ///     Upper bound for soft damping. When validation loss reaches this value (2× threshold), alpha is fully dampened back to DefaultAlphaMin.
    /// </summary>
    internal const double ValidationLossCeiling = ValidationLossThreshold * 2.0;

    /// <summary>Examples needed before neural blending starts. Ensures enough data.</summary>
    internal const int NeuralActivationThreshold = 150;

    /// <summary>
    ///     Maximum fraction of the learned weight (α) that can be re-allocated to the neural strategy.
    /// </summary>
    internal const double NeuralMaxBetaFraction = 0.4;

    /// <summary>
    ///     Minimum neural beta below which the neural strategy is deactivated. Prevents infinitesimal floating-point ghost values from keeping the neural path active with no meaningful contribution.
    /// </summary>
    internal const double NeuralBetaMinFloor = 0.01;

    /// <summary>
    ///     Minimum number of metrics snapshots required before trend analysis activates. Below this count, AnalyzeTrend returns InsufficientData.
    /// </summary>
    internal const int TrendMinSnapshots = 5;

    /// <summary>
    ///     Alpha damping factor applied per training round when a degrading trend is detected.
    /// </summary>
    internal const double TrendDegradationDamping = 0.90;

    /// <summary>
    ///     Alpha boost factor applied when an improving trend is detected. The quality factor is multiplied by this value (capped at 1.0) to allow faster alpha progression when the model is consistently improving.
    /// </summary>
    internal const double TrendImprovementBoost = 1.15;

    /// <summary>
    ///     Cached JSON serializer options for ensemble state persistence. Compact (non-indented) output - the ensemble state file is small (~400 bytes with defaults) and machine-read only, so indentation adds no operational value.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly HeuristicScoringStrategy _heuristic;
    private readonly LearnedScoringStrategy _learned;
    private readonly ILogger? _logger;
    private readonly NeuralScoringStrategy? _neural;
    private readonly string? _statePath;
    private readonly object _syncRoot = new();
    private double _alpha;
    private double _alphaMax;
    private double _alphaMin;
    private double _genrePenaltyFloor;
    private List<MetricsSnapshot> _metricsHistory = [];
    private double _neuralBeta;
    private bool _qualityGateFrozen;
    private double _sigmoidMidpointOffset;
    private int _trainingExampleCount;

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

        // Guard: the heuristic must have its genre penalty disabled (floor = 1.0) because the ensemble applies the penalty centrally via ComputeSoftGenrePenalty after blending.
        if (BitConverter.DoubleToInt64Bits(heuristic.GenrePenaltyFloor) != BitConverter.DoubleToInt64Bits(1.0))
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
    public enum MetricsTrend
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

    /// <summary>
    ///     Gets the current adaptive sigmoid midpoint offset (for testing/debugging). Negative values shift the midpoint earlier (ML trusted sooner), positive values shift it later (more conservative).
    /// </summary>
    internal double SigmoidMidpointOffset
    {
        get
        {
            lock (_syncRoot)
            {
                return _sigmoidMidpointOffset;
            }
        }
    }

    /// <summary>
    ///     Gets the effective sigmoid midpoint (default + adaptive offset) for testing/debugging.
    /// </summary>
    internal double EffectiveSigmoidMidpoint => DefaultSigmoidMidpoint + SigmoidMidpointOffset;

    /// <summary>
    ///     Captures a coherent immutable snapshot of the ensemble's live internal state under a single lock.
    ///     Reading the per-field getters individually would each take <c>_syncRoot</c> separately and risk a torn
    ///     read across fields; this method reads every field in one lock acquisition. Purely diagnostic - it never
    ///     mutates state or triggers persistence.
    /// </summary>
    /// <returns>An <see cref="EnsembleDiagnostics"/> record with the current blending factors, quality-gate state, sigmoid midpoint, trend, and counts.</returns>
    internal EnsembleDiagnostics GetDiagnosticsSnapshot()
    {
        lock (_syncRoot)
        {
            return new EnsembleDiagnostics
            {
                Alpha = _alpha,
                NeuralBeta = _neuralBeta,
                QualityGateFrozen = _qualityGateFrozen,
                SigmoidMidpointOffset = _sigmoidMidpointOffset,
                EffectiveSigmoidMidpoint = DefaultSigmoidMidpoint + _sigmoidMidpointOffset,
                Trend = AnalyzeTrend(_metricsHistory),
                TrainingExampleCount = _trainingExampleCount,
                MetricsHistoryCount = _metricsHistory.Count,
                AlphaMin = _alphaMin,
                AlphaMax = _alphaMax,
                NeuralEnabled = _neural is not null
            };
        }
    }

    /// <summary>
    ///     Updates the alpha bounds and genre-penalty floor from the current plugin configuration without requiring a server restart.
    /// </summary>
    /// <param name="alphaMin">New minimum blending factor.</param>
    /// <param name="alphaMax">New maximum blending factor.</param>
    /// <param name="genrePenaltyFloor">New minimum genre-penalty multiplier.</param>
    public void Reconfigure(double alphaMin, double alphaMax, double genrePenaltyFloor)
    {
        var newMin = Math.Clamp(alphaMin, 0.0, 1.0);
        var newMax = Math.Clamp(alphaMax, newMin, 1.0);
        var newFloor = Math.Clamp(genrePenaltyFloor, 0.0, 1.0);

        lock (_syncRoot)
        {
            _alphaMin = newMin;
            _alphaMax = newMax;
            _genrePenaltyFloor = newFloor;
            _alpha = Math.Clamp(_alpha, newMin, newMax);
        }
    }

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        // Snapshot blending factors atomically - sub-strategies handle their own thread safety.
        double alpha;
        double beta;
        double floor;
        lock (_syncRoot)
        {
            alpha = _alpha;
            beta = _neuralBeta;
            floor = _genrePenaltyFloor;
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
        var penalty = ComputeSoftGenrePenalty(features.GenreSimilarity, floor);
        return blendedScore * penalty;
    }

    /// <summary>
    ///     Scores a candidate with an alpha offset applied for cohort-based exploration. The offset is added to the current alpha and clamped to [αMin, αMax].
    /// </summary>
    /// <param name="features">The pre-computed feature signals for the candidate.</param>
    /// <param name="alphaOffset">The alpha offset from the strategy selector (can be positive or negative).</param>
    /// <returns>A score between 0.0 and 1.0, where higher means more recommended.</returns>
    internal double ScoreWithOffset(CandidateFeatures features, double alphaOffset)
    {
        // Fast path: no offset means standard scoring
        if (Math.Abs(alphaOffset) < 1e-10)
        {
            return Score(features);
        }

        double alpha;
        double beta;
        double floor;
        lock (_syncRoot)
        {
            alpha = Math.Clamp(_alpha + alphaOffset, _alphaMin, _alphaMax);
            beta = _neuralBeta;
            floor = _genrePenaltyFloor;
        }

        var learnedScore = _learned.Score(features);
        var heuristicScore = _heuristic.Score(features);

        double mlScore;
        if (_neural is not null && beta > 0)
        {
            var neuralScore = _neural.Score(features);
            mlScore = ((1.0 - beta) * learnedScore) + (beta * neuralScore);
        }
        else
        {
            mlScore = learnedScore;
        }

        var blendedScore = (alpha * mlScore) + ((1.0 - alpha) * heuristicScore);
        var penalty = ComputeSoftGenrePenalty(features.GenreSimilarity, floor);
        return blendedScore * penalty;
    }

    /// <summary>
    ///     Scores a candidate with explanation and an alpha offset for cohort-based exploration.
    /// </summary>
    /// <param name="features">The pre-computed feature signals for the candidate.</param>
    /// <param name="alphaOffset">The alpha offset from the strategy selector.</param>
    /// <returns>A detailed score explanation including per-feature contributions.</returns>
    internal ScoreExplanation ScoreWithExplanationAndOffset(CandidateFeatures features, double alphaOffset)
    {
        // Fast path: no offset means standard scoring
        if (Math.Abs(alphaOffset) < 1e-10)
        {
            return ScoreWithExplanation(features);
        }

        double alpha;
        double beta;
        double floor;
        lock (_syncRoot)
        {
            alpha = Math.Clamp(_alpha + alphaOffset, _alphaMin, _alphaMax);
            beta = _neuralBeta;
            floor = _genrePenaltyFloor;
        }

        var learnedExplanation = _learned.ScoreWithExplanation(features);
        var heuristicExplanation = _heuristic.ScoreWithExplanation(features);

        ScoreExplanation mlExplanation;
        if (_neural is not null && beta > 0)
        {
            var neuralExplanation = _neural.ScoreWithExplanation(features);
            mlExplanation = learnedExplanation.Blend(neuralExplanation, beta);
        }
        else
        {
            mlExplanation = learnedExplanation;
        }

        var blended = heuristicExplanation.Blend(mlExplanation, alpha);
        var penalty = ComputeSoftGenrePenalty(features.GenreSimilarity, floor);
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

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        // Snapshot blending factors atomically - sub-strategies handle their own thread safety.
        double alpha;
        double beta;
        double floor;
        lock (_syncRoot)
        {
            alpha = _alpha;
            beta = _neuralBeta;
            floor = _genrePenaltyFloor;
        }

        // Score calls are outside the lock to allow parallel scoring across threads.
        var learnedExplanation = _learned.ScoreWithExplanation(features);
        var heuristicExplanation = _heuristic.ScoreWithExplanation(features);

        // When neural is active and beta > 0, blend learned + neural into an ML explanation first, then blend the ML explanation with heuristic.
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
        var penalty = ComputeSoftGenrePenalty(features.GenreSimilarity, floor);
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
    public bool Train(IReadOnlyList<TrainingExample> examples) => Train(examples, heldOutForMetrics: null);

    /// <inheritdoc />
    public bool Train(IReadOnlyList<TrainingExample> examples, IReadOnlyList<TrainingExample>? heldOutForMetrics)
    {
        ArgumentNullException.ThrowIfNull(examples);

        var result = ((ITrainableStrategy)_learned).Train(examples, heldOutForMetrics);

        // Also train neural strategy if available (independent of learned success)
        var neuralTrained = _neural is not null
            && ((ITrainableStrategy)_neural).Train(examples, heldOutForMetrics);

        if (result)
        {
            // Check validation loss quality gate
            var validationLoss = _learned.LastValidationLoss;
            var qualityGatePassed = !double.IsNaN(validationLoss) && validationLoss <= ValidationLossThreshold;
            var trend = MetricsTrend.Stable;

            lock (_syncRoot)
            {
                // Track examples seen across training rounds.
                _trainingExampleCount += examples.Count;

                // Compute the target alpha from the sigmoid curve using the adaptive midpoint.
                // The midpoint offset is adjusted by ApplyCohortFeedback based on cohort watch-rates.
                var effectiveMidpoint = DefaultSigmoidMidpoint + _sigmoidMidpointOffset;
                var sigmoidAlpha = ComputeSigmoidAlpha(_trainingExampleCount, effectiveMidpoint, _alphaMin, _alphaMax);

                UpdateAlphaFromQualityGate(qualityGatePassed, validationLoss, sigmoidAlpha);

                UpdateNeuralBeta(neuralTrained);

                RecordMetricsSnapshot(validationLoss, examples.Count);

                // Analyze trend from the updated history
                trend = AnalyzeTrend(_metricsHistory);

                ApplyTrendAdjustments(trend);
            }

            if (_logger is not null && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
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
            }

            TrySaveState();
        }
        else
        {
            // Learned training failed (insufficient data).
            lock (_syncRoot)
            {
                RecordColdStartPlaceholder(examples.Count);

                DecayNeuralBetaOnFailure();
            }

            // Both operations inside the lock always mutate state, so a save is always required.
            TrySaveState();
        }

        return result;
    }

    /// <summary>
    ///     Updates _alpha and the quality-gate freeze flag from the sigmoid target. Caller must hold _syncRoot.
    /// </summary>
    private void UpdateAlphaFromQualityGate(bool qualityGatePassed, double validationLoss, double sigmoidAlpha)
    {
        if (qualityGatePassed)
        {
            // Good generalization - let alpha progress at full sigmoid rate
            _alpha = sigmoidAlpha;
            _qualityGateFrozen = false;
        }
        else
        {
            // Soft damping: alpha still advances but is proportionally dampened based on how far the validation loss exceeds the threshold.
            var qualityFactor = double.IsNaN(validationLoss)
                ? 0.5 // NaN (no validation split) -> use half progression
                : Math.Clamp(
                    1.0 - ((validationLoss - ValidationLossThreshold)
                           / (ValidationLossCeiling - ValidationLossThreshold)),
                    0.0,
                    1.0);

            _alpha = _alphaMin + ((sigmoidAlpha - _alphaMin) * qualityFactor);
            _qualityGateFrozen = qualityFactor < 0.01;
        }
    }

    /// <summary>
    ///     Updates the neural blending factor β via the activation ramp / decay logic. Caller must hold _syncRoot.
    /// </summary>
    private void UpdateNeuralBeta(bool neuralTrained)
    {
        // Update neural beta: blend neural in after NeuralActivationThreshold using a sigmoid ramp from 0 to NeuralMaxBetaFraction.
        if (_neural is not null && neuralTrained && _trainingExampleCount >= NeuralActivationThreshold)
        {
            var neuralValidationLoss = _neural.LastValidationLoss;
            var neuralQualityOk = !double.IsNaN(neuralValidationLoss)
                                  && neuralValidationLoss <= ValidationLossThreshold;

            if (neuralQualityOk)
            {
                // Linear ramp from activation threshold over next 100 examples
                var progress = Math.Clamp(
                    (_trainingExampleCount - NeuralActivationThreshold) / 100.0,
                    0.0,
                    1.0);
                var rampTarget = NeuralMaxBetaFraction * progress;
                _neuralBeta = Math.Max(_neuralBeta, rampTarget);
            }
            else
            {
                // Neural not generalizing well - reduce its influence.
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
            // Neural strategy failed to train this round while learned succeeded -
            // decay β to avoid stale influence, analogous to the learned-failure branch.
            _neuralBeta *= 0.5;
            if (_neuralBeta < NeuralBetaMinFloor)
            {
                _neuralBeta = 0.0;
            }
        }
    }

    /// <summary>
    ///     Appends a real (successful-training) metrics snapshot and trims history. Caller must hold _syncRoot.
    /// </summary>
    private void RecordMetricsSnapshot(double validationLoss, int exampleCount)
    {
        // Record metrics snapshot and analyze trend BEFORE saving state,
        // so trend-driven alpha/beta adjustments are persisted in the same write.
        _metricsHistory.Add(
            new MetricsSnapshot
            {
                Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ValidationLoss = validationLoss,
                PrecisionAtK = _learned.LastPrecisionAtK,
                RecallAtK = _learned.LastRecallAtK,
                NdcgAtK = _learned.LastNdcgAtK,
                ExampleCount = exampleCount
            });
        const int maxHistory = 10;
        if (_metricsHistory.Count > maxHistory)
        {
            _metricsHistory.RemoveRange(0, _metricsHistory.Count - maxHistory);
        }
    }

    /// <summary>
    ///     Applies trend-driven alpha/beta adjustments after the trend is analyzed. Caller must hold _syncRoot.
    /// </summary>
    private void ApplyTrendAdjustments(MetricsTrend trend)
    {
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
            // Allow faster alpha progression toward sigmoid target (using adaptive midpoint)
            var sigmoidTarget = ComputeSigmoidAlpha(
                _trainingExampleCount,
                DefaultSigmoidMidpoint + _sigmoidMidpointOffset,
                _alphaMin,
                _alphaMax);
            _alpha = Math.Min(sigmoidTarget, _alpha + ((_alphaMax - _alpha) * (1.0 - TrendDegradationDamping)));
        }
    }

    /// <summary>
    ///     Appends a cold-start placeholder metrics snapshot (NaN loss) and trims history. Caller must hold _syncRoot.
    /// </summary>
    private void RecordColdStartPlaceholder(int exampleCount)
    {
        _metricsHistory.Add(
            new MetricsSnapshot
            {
                Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ValidationLoss = double.NaN,
                PrecisionAtK = 0.0,
                RecallAtK = 0.0,
                NdcgAtK = 0.0,
                ExampleCount = exampleCount
            });
        const int maxHistory = 10;
        if (_metricsHistory.Count > maxHistory)
        {
            _metricsHistory.RemoveRange(0, _metricsHistory.Count - maxHistory);
        }
    }

    /// <summary>
    ///     Decays the neural blending factor β when learned training fails this round. Caller must hold _syncRoot.
    /// </summary>
    private void DecayNeuralBetaOnFailure()
    {
        if (_neuralBeta > 0)
        {
            // Decay neuralBeta to prevent a stale high value from persisting when the neural strategy may have outdated weights.
            _neuralBeta *= 0.5;
            if (_neuralBeta < NeuralBetaMinFloor)
            {
                _neuralBeta = 0.0;
            }
        }
    }

    /// <summary>
    ///     Applies cohort-based feedback to adapt the sigmoid midpoint. Compares watch-rates across exploration cohorts: if explore-high users watch more recommended items than control, the midpoint shifts down (ML trusted sooner).
    /// </summary>
    /// <param name="previousResults">The recommendation results from the previous run (with Cohort tags).</param>
    /// <param name="watchedItemLookup">
    ///     Per-user lookup of item IDs that were watched since the recommendations were generated.
    ///     Key = userId, Value = set of watched item IDs.
    /// </param>
    internal void ApplyCohortFeedback(
        IReadOnlyList<RecommendationResult> previousResults,
        Dictionary<Guid, HashSet<Guid>> watchedItemLookup)
    {
        // Minimum recommendations per cohort to consider the signal statistically meaningful
        const int minRecsPerCohort = 5;

        var tallies = TallyCohortOutcomes(previousResults, watchedItemLookup);

        // Need sufficient data in control AND at least one exploration cohort
        if (tallies.ControlTotal < minRecsPerCohort)
        {
            return;
        }

        var controlRate = (double)tallies.ControlWatched / tallies.ControlTotal;

        // Check explore-high: if it outperforms control, shift midpoint down (trust ML sooner)
        if (tallies.HighTotal >= minRecsPerCohort
            && TryAdaptMidpoint(
                tallies.HighWatched,
                tallies.HighTotal,
                controlRate,
                -MidpointAdaptationStep,
                "explore-high",
                minRecsPerCohort))
        {
            return;
        }

        // Check explore-low: if it outperforms control, shift midpoint up (more conservative)
        if (tallies.LowTotal >= minRecsPerCohort
            && TryAdaptMidpoint(
                tallies.LowWatched,
                tallies.LowTotal,
                controlRate,
                MidpointAdaptationStep,
                "explore-low",
                minRecsPerCohort))
        {
            return;
        }

        // Reaching this point means either: (a) at least one exploration cohort had enough samples AND lost to control, OR (b) neither exploration cohort had enough samples to be evaluated at all.
        if (tallies.HighTotal < minRecsPerCohort && tallies.LowTotal < minRecsPerCohort)
        {
            return;
        }

        DecayMidpointOffset(controlRate);
    }

    /// <summary>
    ///     Accumulates per-cohort watched/total counters over the previous recommendation results.
    /// </summary>
    /// <param name="previousResults">The previous-run recommendation results.</param>
    /// <param name="watchedItemLookup">Per-user set of item ids watched since generation.</param>
    /// <returns>The per-cohort tallies.</returns>
    private static CohortTallies TallyCohortOutcomes(
        IReadOnlyList<RecommendationResult> previousResults,
        Dictionary<Guid, HashSet<Guid>> watchedItemLookup)
    {
        var tallies = default(CohortTallies);

        foreach (var result in previousResults)
        {
            if (result.Recommendations.Count == 0)
            {
                continue;
            }

            var cohort = result.Cohort ?? "control";
            watchedItemLookup.TryGetValue(result.UserId, out var userWatched);

            foreach (var rec in result.Recommendations)
            {
                var wasWatched = userWatched is not null && userWatched.Contains(rec.ItemId);
                TallyRecommendation(ref tallies, cohort, wasWatched);
            }
        }

        return tallies;
    }

    /// <summary>
    ///     Applies a single recommendation's outcome to the per-cohort counters. Extracted verbatim from the switch body in TallyCohortOutcomes.
    /// </summary>
    /// <param name="tallies">The running per-cohort tallies, mutated in place.</param>
    /// <param name="cohort">The cohort the recommendation belongs to.</param>
    /// <param name="wasWatched">Whether the recommended item was subsequently watched.</param>
    private static void TallyRecommendation(ref CohortTallies tallies, string cohort, bool wasWatched)
    {
        switch (cohort)
        {
            case "explore-high":
                tallies.HighTotal++;
                if (wasWatched)
                {
                    tallies.HighWatched++;
                }

                break;
            case "explore-low":
                tallies.LowTotal++;
                if (wasWatched)
                {
                    tallies.LowWatched++;
                }

                break;
            default:
                tallies.ControlTotal++;
                if (wasWatched)
                {
                    tallies.ControlWatched++;
                }

                break;
        }
    }

    /// <summary>
    ///     Applies a midpoint offset shift when an exploration cohort outperforms control. Extracted verbatim from the explore-high / explore-low branches of ApplyCohortFeedback.
    /// </summary>
    /// <param name="cohortWatched">Watched count for the exploration cohort.</param>
    /// <param name="cohortTotal">Total recommendations for the exploration cohort.</param>
    /// <param name="controlRate">The control cohort's watch rate.</param>
    /// <param name="step">The signed midpoint adaptation step to apply.</param>
    /// <param name="cohortName">The cohort name (for logging).</param>
    /// <param name="minRecsPerCohort">Minimum samples for a meaningful signal.</param>
    /// <returns><c>true</c> when the cohort won and the midpoint was adjusted.</returns>
    private bool TryAdaptMidpoint(
        int cohortWatched,
        int cohortTotal,
        double controlRate,
        double step,
        string cohortName,
        int minRecsPerCohort)
    {
        if (cohortTotal < minRecsPerCohort)
        {
            return false;
        }

        var cohortRate = (double)cohortWatched / cohortTotal;
        if (cohortRate <= controlRate)
        {
            return false;
        }

        lock (_syncRoot)
        {
            _sigmoidMidpointOffset = Math.Clamp(
                _sigmoidMidpointOffset + step,
                -MaxMidpointShift,
                MaxMidpointShift);
        }

        if (_logger is not null && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Cohort feedback: {Cohort} ({CohortRate:P1}) > control ({ControlRate:P1}) → midpoint offset adjusted to {Offset:F1}",
                cohortName,
                cohortRate,
                controlRate,
                _sigmoidMidpointOffset);
        }

        TrySaveState();
        return true;
    }

    /// <summary>
    ///     Applies the mild anti-drift decay to the sigmoid midpoint offset when control is optimal.
    /// </summary>
    /// <param name="controlRate">The control cohort's watch rate (for logging).</param>
    private void DecayMidpointOffset(double controlRate)
    {
        // Control is optimal - no cohort-driven adaptation, but apply a mild decay so a saturated offset from earlier runs drifts back toward the default midpoint over time.
        var decayed = false;
        double decayedOffset;
        lock (_syncRoot)
        {
            if (Math.Abs(_sigmoidMidpointOffset) > 1e-6)
            {
                _sigmoidMidpointOffset *= MidpointDecayFactor;
                decayed = true;
            }

            decayedOffset = _sigmoidMidpointOffset;
        }

        if (decayed)
        {
            TrySaveState();
        }

        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Cohort feedback: control ({ControlRate:P1}) is optimal, midpoint offset decayed to {Offset:F2}",
                controlRate,
                decayedOffset);
        }
    }

    /// <summary>
    ///     Computes a soft genre-mismatch penalty that ramps linearly from penaltyFloor (at GenreSimilarity = 0) to 1.0 (at GenreSimilarity >= GenrePenaltyThreshold).
    /// </summary>
    /// <param name="genreSimilarity">The candidate's genre similarity score (0-1).</param>
    /// <param name="penaltyFloor">
    ///     Minimum penalty multiplier (default: <see cref="DefaultGenrePenaltyFloor"/>).
    /// </param>
    /// <returns>A penalty multiplier between <paramref name="penaltyFloor"/> and 1.0.</returns>
    internal static double ComputeSoftGenrePenalty(
        double genreSimilarity,
        double penaltyFloor = DefaultGenrePenaltyFloor)
    {
        return ScoringHelper.ComputeSoftGenrePenalty(genreSimilarity, penaltyFloor);
    }

    /// <summary>
    ///     Computes the blending factor α using a sigmoid function for smooth transitions. Formula: α = αMin + (αMax - αMin) / (1 + e^(-k × (n - midpoint))).
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
        return ComputeSigmoidAlpha(trainingExampleCount, DefaultSigmoidMidpoint, alphaMin, alphaMax);
    }

    /// <summary>
    ///     Computes the blending factor α using a sigmoid function with an explicit midpoint.
    ///     Used internally when the adaptive midpoint offset is applied.
    /// </summary>
    /// <param name="trainingExampleCount">The cumulative number of training examples.</param>
    /// <param name="midpoint">The sigmoid midpoint (number of examples for α = midpoint value).</param>
    /// <param name="alphaMin">Minimum alpha value.</param>
    /// <param name="alphaMax">Maximum alpha value.</param>
    /// <returns>A blending factor between <paramref name="alphaMin"/> and <paramref name="alphaMax"/>.</returns>
    internal static double ComputeSigmoidAlpha(
        int trainingExampleCount,
        double midpoint,
        double alphaMin,
        double alphaMax)
    {
        var exponent = -AlphaSigmoidK * (trainingExampleCount - midpoint);
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
    /// </summary>
    private void TryLoadState()
    {
        if (string.IsNullOrEmpty(_statePath) || !File.Exists(_statePath))
        {
            return;
        }

        // Guard against corrupted/replaced oversized files before reading into memory.
        // Ensemble state JSON is small (~50 KB with history); a 10 MB ceiling gives ample headroom.
        const long MaxStateFileSizeBytes = 10 * 1024 * 1024;
        if (new FileInfo(_statePath).Length > MaxStateFileSizeBytes)
        {
            _logger?.LogWarning(
                "EnsembleScoringStrategy: State file exceeds {LimitMB}MB ({Path}). Skipping load.",
                MaxStateFileSizeBytes / (1024 * 1024),
                _statePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            // Use the same SerializerOptions as TrySaveState so a MetricsSnapshot row that was persisted with ValidationLoss = NaN (cold-start placeholder) round-trips cleanly.
            var data = JsonSerializer.Deserialize<EnsembleStateData>(json, SerializerOptions);
            if (data is null)
            {
                return;
            }

            // Schema version guard: if the persisted file was written by an older (or newer) incompatible schema, discard it and start from defaults rather than applying potentially mis-mapped fields that could corrupt runtime state.
            if (data.SchemaVersion != EnsembleStateData.CurrentSchemaVersion)
            {
                _logger?.LogWarning(
                    "EnsembleScoringStrategy: State schema version mismatch (file={FileVersion}, expected={Expected}). Resetting to defaults.",
                    data.SchemaVersion,
                    EnsembleStateData.CurrentSchemaVersion);
                return;
            }

            // Reject only when the state is entirely empty.
            if (data.TrainingExampleCount <= 0 && (data.MetricsHistory is null || data.MetricsHistory.Count == 0))
            {
                return;
            }

            ApplyLoadedState(data);
        }
        catch (IOException ex)
        {
            // Graceful fallback to defaults on I/O error - log for diagnostics
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to load state");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Graceful fallback to defaults on access denied - log for diagnostics
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to load state (access denied)");
        }
        catch (JsonException ex)
        {
            // Graceful fallback to defaults on parse error - log for diagnostics
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to parse state");
        }
    }

    /// <summary>
    ///     Applies validated persisted state to the live fields under lock. Extracted verbatim from TryLoadState; the restore rules are unchanged.
    /// </summary>
    /// <param name="data">The validated persisted ensemble state.</param>
    private void ApplyLoadedState(EnsembleStateData data)
    {
        lock (_syncRoot)
        {
            _trainingExampleCount = data.TrainingExampleCount;
            _qualityGateFrozen = data.QualityGateFrozen;

            // Restore persisted alpha directly instead of recomputing via sigmoid, so that the quality-gate freeze state is preserved across restarts.
            _alpha = (data.Alpha >= _alphaMin && data.Alpha <= _alphaMax)
                ? data.Alpha
                : ComputeSigmoidAlpha(_trainingExampleCount, _alphaMin, _alphaMax);

            // Restore neural beta only when enough training data exists.
            // Cap to the current ramp ceiling so that a state persisted under an older
            // (lower) NeuralActivationThreshold cannot overshoot the beta value the live
            // ramp would produce at the same example count after a threshold increase.
            if (_neural is not null
                && data.TrainingExampleCount >= NeuralActivationThreshold
                && data.NeuralBeta is >= 0 and <= NeuralMaxBetaFraction)
            {
                var rampCeiling = NeuralMaxBetaFraction * Math.Clamp(
                    (data.TrainingExampleCount - NeuralActivationThreshold) / 100.0,
                    0.0,
                    1.0);
                _neuralBeta = Math.Min(data.NeuralBeta, rampCeiling);
            }
            else if (_neural is not null && data.NeuralBeta is >= 0 and <= NeuralMaxBetaFraction)
            {
                _neuralBeta = 0.0;
            }
            else if (_neural is not null && data.NeuralBeta > NeuralMaxBetaFraction &&
                     _logger is not null && _logger.IsEnabled(LogLevel.Information))
            {
                // A persisted NeuralBeta above the current ceiling usually means the ceiling was lowered in an update.
                _logger.LogInformation(
                    "EnsembleScoringStrategy: discarded persisted NeuralBeta={PersistedBeta:F3} (exceeds NeuralMaxBetaFraction={Ceiling:F3}). Ramp will restart from 0.",
                    data.NeuralBeta,
                    NeuralMaxBetaFraction);
            }

            // Restore adaptive sigmoid midpoint offset (clamped to valid range).
            if (Math.Abs(data.SigmoidMidpointOffset) <= MaxMidpointShift)
            {
                _sigmoidMidpointOffset = data.SigmoidMidpointOffset;
            }

            if (data.MetricsHistory is { Count: > 0 })
            {
                _metricsHistory = new List<MetricsSnapshot>(data.MetricsHistory);
            }
        }
    }

    /// <summary>
    ///     Tries to persist current ensemble state to disk. Snapshot and serialization are performed under lock to ensure consistency with concurrent Train(IReadOnlyList{TrainingExample}) calls (analogous to LearnedScoringStrategy.TrySaveWeights).
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
                    SigmoidMidpointOffset = _sigmoidMidpointOffset,
                    MetricsHistory = [.. _metricsHistory],
                    UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };

                json = JsonSerializer.Serialize(data, SerializerOptions);
            }

            // Use AtomicFile so a transient Windows AV/indexer sharing violation on the
            // final File.Move gets a bounded retry instead of silently dropping the save.
            AtomicFile.WriteAllText(_statePath, json);
        }
        catch (IOException ex)
        {
            // Non-critical - log for diagnostics but don't fail
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to save state");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Non-critical - log for diagnostics but don't fail
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to save state (access denied)");
        }
        catch (JsonException ex)
        {
            // Non-critical - log for diagnostics but don't fail
            _logger?.LogWarning(ex, "EnsembleScoringStrategy: Failed to serialize state");
        }
    }

    /// <summary>
    ///     Analyzes the rolling metrics history to detect training quality trends. Uses linear slope over the last TrendMinSnapshots snapshots.
    /// </summary>
    /// <param name="history">The metrics history snapshots (most recent last).</param>
    /// <returns>The detected trend direction.</returns>
    internal static MetricsTrend AnalyzeTrend(IReadOnlyList<MetricsSnapshot> history)
    {
        // Filter out cold-start placeholder rows (ValidationLoss = NaN) BEFORE selecting the trailing window. A mixed history (e.g.
        var realRows = new List<MetricsSnapshot>(history.Count);
        foreach (var snapshot in history)
        {
            if (double.IsFinite(snapshot.ValidationLoss))
            {
                realRows.Add(snapshot);
            }
        }

        if (realRows.Count < TrendMinSnapshots)
        {
            return MetricsTrend.InsufficientData;
        }

        var startIdx = realRows.Count - TrendMinSnapshots;
        var n = TrendMinSnapshots;
        var meanI = (n - 1) / 2.0;

        double sumLoss = 0, sumNdcg = 0;
        for (var i = 0; i < n; i++)
        {
            sumLoss += realRows[startIdx + i].ValidationLoss;
            sumNdcg += realRows[startIdx + i].NdcgAtK;
        }

        var meanLoss = sumLoss / n;
        var meanNdcg = sumNdcg / n;

        double numLoss = 0, numNdcg = 0, denominator = 0;
        for (var i = 0; i < n; i++)
        {
            var di = i - meanI;
            numLoss += di * (realRows[startIdx + i].ValidationLoss - meanLoss);
            numNdcg += di * (realRows[startIdx + i].NdcgAtK - meanNdcg);
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
    ///     Disposes the composed neural strategy, which owns a ReaderWriterLockSlim.
    /// </summary>
    public void Dispose()
    {
        _neural?.Dispose();
    }

    /// <summary>
    ///     Per-cohort watched/total counters used by <see cref="ApplyCohortFeedback"/>.
    /// </summary>
    private struct CohortTallies
    {
        public int ControlWatched;
        public int ControlTotal;
        public int HighWatched;
        public int HighTotal;
        public int LowWatched;
        public int LowTotal;
    }

    /// <summary>
    ///     Serializable container for persisted ensemble state.
    /// </summary>
    internal sealed class EnsembleStateData
    {
        /// <summary>Increment this constant whenever the persisted schema changes.</summary>
        internal const int CurrentSchemaVersion = 1;

        private List<MetricsSnapshot> _metricsHistory = [];

        /// <summary>Gets or sets the schema version written when this state was last saved.</summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Gets or sets the cumulative number of training examples seen.</summary>
        public int TrainingExampleCount { get; set; }

        /// <summary>Gets or sets the current blending factor alpha.</summary>
        public double Alpha { get; set; }

        /// <summary>Gets or sets the current neural blending factor beta.</summary>
        public double NeuralBeta { get; set; }

        /// <summary>Gets or sets a value indicating whether the quality gate has frozen alpha progression.</summary>
        public bool QualityGateFrozen { get; set; }

        /// <summary>Gets or sets the adaptive sigmoid midpoint offset.
        /// Positive = ML trusted sooner, negative = more conservative.</summary>
        public double SigmoidMidpointOffset { get; set; }

        /// <summary>Gets or sets the ISO 8601 timestamp of the last update.</summary>
        public string UpdatedAt { get; set; } = string.Empty;

        /// <summary>Gets or sets the rolling history of training metrics (last 10 runs).
        /// Setter coalesces null to empty to prevent NRE from deserialized state data.</summary>
        public List<MetricsSnapshot> MetricsHistory
        {
            get => _metricsHistory;
            set => _metricsHistory = value ?? [];
        }
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