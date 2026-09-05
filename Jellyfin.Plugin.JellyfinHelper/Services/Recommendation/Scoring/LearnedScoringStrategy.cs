using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Adaptive ML scoring strategy using a linear model with learned weights. Learns personalized feature weights from user watch history via stochastic gradient descent (SGD).
/// </summary>
/// <remarks>
///     Architecture: 38 input features -> 38 weights + 1 bias -> clamp(0,1) -> score.
/// </remarks>
public sealed class LearnedScoringStrategy : IScoringStrategy, ITrainableStrategy
{
    /// <summary>Default learning rate for gradient descent.</summary>
    internal const double DefaultLearningRate = 0.02;

    /// <summary>L2 regularization strength (weight decay).</summary>
    internal const double L2Lambda = 0.001;

    /// <summary>Maximum number of training epochs per <see cref="Train(IReadOnlyList{TrainingExample})"/> call.</summary>
    internal const int MaxTrainingEpochs = 30;

    /// <summary>Minimum examples to train. Prevents overfitting on tiny libraries.</summary>
    internal const int MinTrainingExamples = 12;

    /// <summary>Number of consecutive epochs without improvement before early stopping triggers.</summary>
    internal const int EarlyStoppingPatience = 3;

    /// <summary>Minimum fraction of examples used for validation (rest is training).</summary>
    internal const double ValidationSplitRatio = 0.2;

    /// <summary>Number of folds for k-fold cross-validation. Set to 1 to disable k-fold (simple split).</summary>
    internal const int KFoldCount = 3;

    /// <summary>Minimum number of examples required per fold for k-fold cross-validation.</summary>
    internal const int MinExamplesPerFold = 3;

    /// <summary>Minimum number of validation examples required for early stopping.</summary>
    internal const int MinValidationExamples = 2;

    /// <summary>Minimum sample weight below which a training example is skipped (temporal decay floor).</summary>
    internal const double MinSampleWeight = 0.01;

    /// <summary>Early stopping improvement threshold (avoids triggering on noise).</summary>
    internal const double EarlyStoppingMinDelta = 1e-6;

    /// <summary>Maximum epochs when early stopping is disabled (fewer epochs to avoid overfitting on small datasets).</summary>
    internal const int MaxEpochsWithoutEarlyStopping = 15;

    /// <summary>Minimum examples before standardization. Avoids unstable stats.</summary>
    internal const int MinExamplesForStandardization = 20;

    /// <summary>
    ///     Current schema version for persisted weights. Increment when the feature set or weight semantics change so that stale weights are discarded on load.
    /// </summary>
    // Bumped 2 -> 3: features 9/10/11/12/15 moved to genre-level aggregates. Keeping v2 would silently
    // break scoring due to old weights on new feature semantics.
    // The recommendation-review metadata changes also alter feature VALUES and ride on this
    internal const int CurrentWeightsVersion = 3;

    /// <summary>
    ///     Cached JSON serializer options for weight persistence. Compact (non-indented) output - the file is machine-read only and roughly halves in size (~1.5 KB vs ~3 KB) with no loss of information.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ILogger? _logger;
    private readonly Lock _syncRoot = new();
    private readonly string? _weightsPath;
    private double _bias;
    private double _lastValidationLoss = double.NaN;
    private double _lastPrecisionAtK = double.NaN;
    private double _lastRecallAtK = double.NaN;
    private double _lastNdcgAtK = double.NaN;
    private int _trainingGeneration;
    private double[] _weights;
    private bool _lastWeightsSaveSucceeded;

    /// <summary>
    ///     Persisted Z-score standardization statistics. When non-null, scoring applies the same standardization that was used during training to ensure consistency.
    /// </summary>
    private double[]? _featureMeans;

    private double[]? _featureStdDevs;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LearnedScoringStrategy" /> class
    ///     with default initial weights optimized for genre-driven recommendations.
    /// </summary>
    /// <param name="weightsPath">
    ///     Optional file path for persisting learned weights.
    ///     If null, weights are kept in memory only.
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public LearnedScoringStrategy(string? weightsPath = null, ILogger? logger = null)
    {
        _weightsPath = weightsPath;
        _logger = logger;

        // Initialize with genre-dominant weights - genre match is the strongest signal
        _weights = DefaultWeights.CreateWeightArray();
        _bias = DefaultWeights
            .Bias; // positive bias; note raw score may exceed 1.0 with all features at max and is clamped

        // Try to load persisted weights
        TryLoadWeights();
    }

    /// <inheritdoc />
    public string Name => "Learned (Adaptive ML)";

    /// <inheritdoc />
    public string NameKey => "strategyLearned";

    /// <summary>
    ///     Gets the validation loss from the last training run. Used by EnsembleScoringStrategy to gate alpha progression.
    /// </summary>
    internal double LastValidationLoss
    {
        get
        {
            lock (_syncRoot)
            {
                return _lastValidationLoss;
            }
        }
    }

    /// <summary>
    ///     Gets a value indicating whether the most recent training run durably persisted its weights. False when the weights path
    ///     write failed, so a caller can avoid stamping a fresh last-trained time over weights that were not saved.
    /// </summary>
    internal bool LastWeightsSaveSucceeded
    {
        get
        {
            lock (_syncRoot)
            {
                return _lastWeightsSaveSucceeded;
            }
        }
    }

    /// <summary>
    ///     Gets the Precision@K from the last training run. Measures what fraction of top-K predicted items are actually relevant.
    /// </summary>
    internal double LastPrecisionAtK
    {
        get
        {
            lock (_syncRoot)
            {
                return _lastPrecisionAtK;
            }
        }
    }

    /// <summary>
    ///     Gets the Recall@K from the last training run. Measures what fraction of all relevant items appear in the top-K predictions.
    /// </summary>
    internal double LastRecallAtK
    {
        get
        {
            lock (_syncRoot)
            {
                return _lastRecallAtK;
            }
        }
    }

    /// <summary>
    ///     Gets the NDCG@K from the last training run. Measures ranking quality by rewarding relevant items at higher positions.
    /// </summary>
    internal double LastNdcgAtK
    {
        get
        {
            lock (_syncRoot)
            {
                return _lastNdcgAtK;
            }
        }
    }

    /// <summary>
    ///     Gets the current bias value (for testing/debugging).
    /// </summary>
    internal double CurrentBias
    {
        get
        {
            lock (_syncRoot)
            {
                return _bias;
            }
        }
    }

    /// <summary>
    ///     Returns a snapshot of the persisted per-feature training-set means, or null when the model has not
    ///     yet trained under standardization. Exposed as a method (not a property) because it clones the
    ///     backing array on each call. Discovery uses these to impute the features it cannot compute for
    ///     external (TMDb) candidates to their training-set mean, so a placeholder standardizes to ~0
    ///     ("no information") instead of biasing the score with an arbitrary constant.
    /// </summary>
    /// <returns>A cloned copy of the per-feature means, or null if unavailable.</returns>
    internal IReadOnlyList<double>? GetFeatureMeans()
    {
        lock (_syncRoot)
        {
            return _featureMeans is not null ? (double[])_featureMeans.Clone() : null;
        }
    }

    /// <summary>
    ///     Gets a copy of the current weights (for testing/debugging).
    /// </summary>
    /// <returns>A defensive copy of the current weight vector.</returns>
    internal double[] GetCurrentWeights()
    {
        lock (_syncRoot)
        {
            return (double[])_weights.Clone();
        }
    }

    /// <summary>
    ///     Returns a snapshot of the persisted per-feature standardization std-devs, or null when the model
    ///     has not yet trained under standardization. The companion to <see cref="GetFeatureMeans"/>: a
    ///     warm-start must copy both, because scoring only standardizes when both are non-null and
    ///     <see cref="SeedFrom"/> relies on carrying the full standardization state across the seed.
    /// </summary>
    /// <returns>A cloned copy of the per-feature std-devs, or null if unavailable.</returns>
    internal IReadOnlyList<double>? GetFeatureStdDevs()
    {
        lock (_syncRoot)
        {
            return _featureStdDevs is not null ? (double[])_featureStdDevs.Clone() : null;
        }
    }

    /// <summary>
    ///     Seeds this model's weights, bias, and standardization statistics from another (typically the
    ///     global) model, so a new per-user model starts from the global fit instead of cold defaults.
    /// </summary>
    /// <remarks>
    ///     The standardization stats (<c>_featureMeans</c>/<c>_featureStdDevs</c>) MUST be carried over, not
    ///     just weights and bias. The global model is trained standardized; a per-user model with fewer than
    ///     <see cref="MinExamplesForStandardization"/> examples will train unstandardized. Without the seeded
    ///     stats, <c>WarmStartWeightsForModeChange</c> would see no prior standardization, skip the rescale,
    ///     and apply standardized-space weights to raw features - silently wrong scores. The training
    ///     generation is reset to 0 so this model's SGD RNG seed does not inherit the source's progression.
    ///     Only the learned weights carry over as a prior. The owning ensemble's blend factor (alpha) is
    ///     deliberately not seeded: it must grow from the per-user example count so a data-poor user blends
    ///     conservatively rather than inheriting the global model's confidence and being overrun by it.
    /// </remarks>
    /// <param name="source">The model to copy weights and standardization state from.</param>
    internal void SeedFrom(LearnedScoringStrategy source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Capture weights, bias, and standardization stats under a single source lock. Reading them through
        // the individual accessors would take four separate locks, so a concurrent source.Train could leave
        // the target with weights from one model state and standardization stats from another, and Score would
        // then normalize with mismatched statistics.
        var snapshot = source.CaptureSeedSnapshot();

        lock (_syncRoot)
        {
            _weights = snapshot.Weights;
            _bias = snapshot.Bias;
            _featureMeans = snapshot.FeatureMeans;
            _featureStdDevs = snapshot.FeatureStdDevs;
            _trainingGeneration = 0;
        }
    }

    /// <summary>
    ///     Takes a consistent copy of the weights, bias, and standardization stats under one lock so a warm
    ///     start seeds from a single coherent model state rather than a mix of concurrent training states.
    /// </summary>
    private (double[] Weights, double Bias, double[]? FeatureMeans, double[]? FeatureStdDevs) CaptureSeedSnapshot()
    {
        lock (_syncRoot)
        {
            return (
                (double[])_weights.Clone(),
                _bias,
                _featureMeans is not null ? (double[])_featureMeans.Clone() : null,
                _featureStdDevs is not null ? (double[])_featureStdDevs.Clone() : null);
        }
    }

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        // Rent from ArrayPool to avoid 1000+ allocations per recommendation run.
        // Safe across async continuations because each call gets its own rented buffer.
        var vector = ArrayPool<double>.Shared.Rent(CandidateFeatures.FeatureCount);
        try
        {
            // Clear only the portion we use (Rent may return a larger array)
            Array.Clear(vector, 0, CandidateFeatures.FeatureCount);
            features.WriteToVector(vector);

            lock (_syncRoot)
            {
                // Apply Z-score standardization if statistics are available
                if (_featureMeans is not null && _featureStdDevs is not null)
                {
                    StandardizeSingleVector(vector, _featureMeans, _featureStdDevs);
                }

                return Math.Clamp(ScoringHelper.ComputeRawScore(vector, _weights, _bias), 0.0, 1.0);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(vector);
        }
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        var vector = ArrayPool<double>.Shared.Rent(CandidateFeatures.FeatureCount);
        try
        {
            Array.Clear(vector, 0, CandidateFeatures.FeatureCount);
            features.WriteToVector(vector);

            lock (_syncRoot)
            {
                // Apply Z-score standardization if statistics are available
                if (_featureMeans is not null && _featureStdDevs is not null)
                {
                    StandardizeSingleVector(vector, _featureMeans, _featureStdDevs);
                }

                return ScoringHelper.BuildExplanation(vector, _weights, _bias, Name);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(vector);
        }
    }

    /// <summary>
    ///     Trains the model weights from labelled examples using stochastic gradient descent (SGD).
    /// </summary>
    /// <param name="examples">Training examples with features and labels.</param>
    /// <returns>True if training was performed, false if insufficient data.</returns>
    public bool Train(IReadOnlyList<TrainingExample> examples) => Train(examples, heldOutForMetrics: null);

    /// <inheritdoc />
    public bool Train(IReadOnlyList<TrainingExample> examples, IReadOnlyList<TrainingExample>? heldOutForMetrics)
    {
        ArgumentNullException.ThrowIfNull(examples);

        if (examples.Count < MinTrainingExamples)
        {
            return false;
        }

        // Capture a consistent reference time for temporal decay within this batch
        var referenceTime = DateTime.UtcNow;

        // Pre-compute all feature vectors ONCE before training. These are the RAW (unstandardized) vectors used as the source of truth.
        var rawVectors = new double[examples.Count][];
        var effectiveWeights = new double[examples.Count];

        for (var i = 0; i < examples.Count; i++)
        {
            rawVectors[i] = examples[i].Features.ToVector();
            effectiveWeights[i] = examples[i].ComputeEffectiveWeight(referenceTime);
        }

        // Determine whether standardization should be applied at all. Thread-safety note: featureMeans/featureStdDevs are computed from local data (no shared state), then assigned to instance fields INSIDE the lock below.
        var useStandardization = examples.Count >= MinExamplesForStandardization;

        lock (_syncRoot)
        {
            WarmStartWeightsForModeChange(useStandardization, rawVectors);

            // Use a varying seed based on training generation to avoid always placing
            // the same examples in validation. Still deterministic per generation.
            var rng = new Random(42 + _trainingGeneration);
            _trainingGeneration++;

            // Create shuffled index array
            var allIndices = new int[examples.Count];
            for (var j = 0; j < allIndices.Length; j++)
            {
                allIndices[j] = j;
            }

            // Fisher-Yates shuffle for random split
            for (var j = allIndices.Length - 1; j > 0; j--)
            {
                var k = rng.Next(j + 1);
                (allIndices[j], allIndices[k]) = (allIndices[k], allIndices[j]);
            }

            // Determine whether to use k-fold cross-validation or simple split
            var useKFold = examples.Count >= KFoldCount * MinExamplesPerFold;
            var kFoldLossSum = 0.0;
            var kFoldLossCount = 0;

            if (useKFold)
            {
                RunKFoldCrossValidation(
                    examples,
                    rawVectors,
                    effectiveWeights,
                    allIndices,
                    rng,
                    useStandardization,
                    ref kFoldLossSum,
                    ref kFoldLossCount);
            }

            RunFinalTrainingPass(
                examples,
                rawVectors,
                effectiveWeights,
                allIndices,
                rng,
                useStandardization,
                kFoldLossSum,
                kFoldLossCount);

            LogFeatureImportance();
        } // release _syncRoot before disk I/O and ranking metrics to avoid blocking concurrent Score() calls

        // Compute ranking metrics OUTSIDE the lock - ComputeAll() calls Score() internally, which acquires _syncRoot.
        var metricsSource = heldOutForMetrics is { Count: >= 2 } ? heldOutForMetrics : examples;
        var (pAtK, rAtK, nAtK) = RankingMetrics.ComputeAll(metricsSource, this);
        lock (_syncRoot)
        {
            _lastPrecisionAtK = pAtK;
            _lastRecallAtK = rAtK;
            _lastNdcgAtK = nAtK;
        }

        // Persist outside the lock - TrySaveWeights() takes its own lock for a brief snapshot, then performs serialization and file I/O without holding the scoring lock.
        var saved = TrySaveWeights();
        lock (_syncRoot)
        {
            _lastWeightsSaveSucceeded = saved;
        }

        return true;
    }

    /// <summary>
    ///     Runs k-fold cross-validation for reliable loss estimation.
    /// </summary>
    /// <param name="examples">Training examples with features and labels.</param>
    /// <param name="rawVectors">The pristine (unstandardized) feature vectors, never mutated here.</param>
    /// <param name="effectiveWeights">Per-example temporal-decay sample weights.</param>
    /// <param name="allIndices">The shuffled index array spanning all examples.</param>
    /// <param name="rng">The deterministic RNG used for per-epoch shuffling.</param>
    /// <param name="useStandardization">Whether Z-score standardization should be applied.</param>
    /// <param name="kFoldLossSum">Accumulator for the summed per-fold validation loss.</param>
    /// <param name="kFoldLossCount">Accumulator for the number of folds that contributed a loss.</param>
    private void RunKFoldCrossValidation(
        IReadOnlyList<TrainingExample> examples,
        double[][] rawVectors,
        double[] effectiveWeights,
        int[] allIndices,
        Random rng,
        bool useStandardization,
        ref double kFoldLossSum,
        ref int kFoldLossCount)
    {
        // Each fold computes standardization statistics from its TRAINING fold only, preventing validation data from leaking into the feature normalization.
        var foldSize = examples.Count / KFoldCount;
        var savedWeights = (double[])_weights.Clone();
        var savedBias = _bias;

        for (var fold = 0; fold < KFoldCount; fold++)
        {
            // Determine fold boundaries
            var valStart = fold * foldSize;
            var valEnd = fold == KFoldCount - 1 ? examples.Count : valStart + foldSize;

            // Build train/val index arrays for this fold
            var foldValIndices = allIndices[valStart..valEnd];
            var foldTrainIndices = new int[examples.Count - foldValIndices.Length];
            var ti = 0;
            for (var j = 0; j < allIndices.Length; j++)
            {
                if (j < valStart || j >= valEnd)
                {
                    foldTrainIndices[ti++] = allIndices[j];
                }
            }

            // Clone raw vectors into working copies so per-fold standardization
            // does not mutate the originals (needed for subsequent folds + final pass).
            var foldVectors = CloneVectors(rawVectors);

            // Per-fold standardization: compute statistics from TRAINING fold only, then apply to BOTH train and validation vectors using the same stats.
            if (useStandardization)
            {
                var trainOnly = new double[foldTrainIndices.Length][];
                for (var j = 0; j < foldTrainIndices.Length; j++)
                {
                    trainOnly[j] = foldVectors[foldTrainIndices[j]];
                }

                var (foldMeans, foldStdDevs) = ComputeFeatureStatistics(trainOnly);
                StandardizeVectors(foldVectors, foldMeans, foldStdDevs);
            }

            // Reset weights to defaults for each fold (fresh start)
            _weights = DefaultWeights.CreateWeightArray();
            _bias = DefaultWeights.Bias;

            // Train on this fold's training set with early stopping
            var foldLoss = TrainSingleSplit(
                examples,
                foldVectors,
                effectiveWeights,
                foldTrainIndices,
                foldValIndices,
                rng,
                useEarlyStopping: true);
            kFoldLossSum += foldLoss;
            kFoldLossCount++;
        }

        // Restore weights for final training on all data
        _weights = savedWeights;
        _bias = savedBias;
    }

    /// <summary>
    ///     Runs the final warm-started training pass on ALL data (no validation holdout) and persists the resulting validation loss and standardization statistics.
    /// </summary>
    /// <param name="examples">Training examples with features and labels.</param>
    /// <param name="rawVectors">The pristine (unstandardized) feature vectors, never mutated here.</param>
    /// <param name="effectiveWeights">Per-example temporal-decay sample weights.</param>
    /// <param name="allIndices">The shuffled index array spanning all examples.</param>
    /// <param name="rng">The deterministic RNG used for per-epoch shuffling.</param>
    /// <param name="useStandardization">Whether Z-score standardization should be applied.</param>
    /// <param name="kFoldLossSum">The summed per-fold validation loss from k-fold cross-validation.</param>
    /// <param name="kFoldLossCount">The number of folds that contributed a loss.</param>
    private void RunFinalTrainingPass(
        IReadOnlyList<TrainingExample> examples,
        double[][] rawVectors,
        double[] effectiveWeights,
        int[] allIndices,
        Random rng,
        bool useStandardization,
        double kFoldLossSum,
        int kFoldLossCount)
    {
        // Clone raw vectors for the final pass so standardization doesn't
        // mutate the originals (rawVectors stays pristine for ranking metrics).
        var finalVectors = CloneVectors(rawVectors);
        double[]? featureMeans = null;
        double[]? featureStdDevs = null;

        if (useStandardization)
        {
            // Intentional trade-off: stats here are computed from the FULL dataset, whereas each k-fold fold used its training-fold subset only (to prevent leakage).
            (featureMeans, featureStdDevs) = ComputeFeatureStatistics(finalVectors);
            StandardizeVectors(finalVectors, featureMeans, featureStdDevs);
        }

        // Warm-start final pass: begin from the previously-learned weights (restored above from savedWeights) rather than resetting to defaults.
        var finalLoss = TrainSingleSplit(
            examples,
            finalVectors,
            effectiveWeights,
            allIndices,
            valIndices: [],
            rng,
            useEarlyStopping: false);

        // Store validation loss for ensemble alpha gating
        // K-fold average loss is more reliable; fall back to training loss if k-fold wasn't used
        _lastValidationLoss = kFoldLossCount > 0
            ? kFoldLossSum / kFoldLossCount
            : finalLoss;

        // Persist Z-score statistics from the final (all-data) pass so scoring
        // uses the same standardization that the final weights were trained on.
        _featureMeans = featureMeans;
        _featureStdDevs = featureStdDevs;
    }

    /// <summary>
    ///     Computes Z-score statistics (mean, stddev) for each feature across all training vectors.
    /// </summary>
    /// <param name="vectors">The pre-computed feature vectors.</param>
    /// <returns>A tuple of (means, stdDevs) arrays indexed by feature.</returns>
    internal static (double[] Means, double[] StdDevs) ComputeFeatureStatistics(double[][] vectors)
    {
        var featureCount = CandidateFeatures.FeatureCount;
        var means = new double[featureCount];
        var stdDevs = new double[featureCount];
        var n = vectors.Length;

        if (n == 0)
        {
            return (means, stdDevs);
        }

        // Compute means
        for (var i = 0; i < n; i++)
        {
            if (vectors[i].Length < featureCount)
            {
                throw new ArgumentException($"Vector at index {i} has length {vectors[i].Length}, expected at least {featureCount}.", nameof(vectors));
            }

            for (var f = 0; f < featureCount; f++)
            {
                means[f] += vectors[i][f];
            }
        }

        for (var f = 0; f < featureCount; f++)
        {
            means[f] /= n;
        }

        // Compute standard deviations
        for (var i = 0; i < n; i++)
        {
            for (var f = 0; f < featureCount; f++)
            {
                var diff = vectors[i][f] - means[f];
                stdDevs[f] += diff * diff;
            }
        }

        for (var f = 0; f < featureCount; f++)
        {
            // Use Bessel's correction (n-1) for unbiased sample standard deviation
            stdDevs[f] = n > 1 ? Math.Sqrt(stdDevs[f] / (n - 1)) : 0.0;
        }

        return (means, stdDevs);
    }

    /// <summary>
    ///     Warm-starts the weights across a standardization mode change. Weights learned in raw feature space
    ///     compute a different function when applied to standardized features (and vice-versa), so a naive
    ///     keep would corrupt the model. Instead of discarding the learned weights (the old behaviour, which
    ///     threw away up to MinExamplesForStandardization-1 examples of learning at the 19->21 boundary),
    ///     transform them exactly into the new space so the decision function is preserved, then let the final
    ///     pass fine-tune. No-op when the mode is unchanged. Must be called under <see cref="_syncRoot" />.
    /// </summary>
    /// <param name="useStandardization">Whether the current training pass will standardize features.</param>
    /// <param name="rawVectors">The raw (unstandardized) feature vectors for this pass.</param>
    private void WarmStartWeightsForModeChange(bool useStandardization, double[][] rawVectors)
    {
        var standardizationModeChanged = useStandardization != (_featureMeans is not null);
        if (!standardizationModeChanged)
        {
            return;
        }

        if (useStandardization)
        {
            // raw -> standardized: rescale using the stats the final pass will train under.
            var (means, stdDevs) = ComputeFeatureStatistics(rawVectors);
            RescaleWeightsForStandardizationChange(toStandardized: true, means, stdDevs);
        }
        else if (_featureMeans is not null && _featureStdDevs is not null)
        {
            // standardized -> raw: reverse using the stats the current weights were trained under.
            RescaleWeightsForStandardizationChange(toStandardized: false, _featureMeans, _featureStdDevs);
        }

        if (_logger is not null && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "LearnedScoringStrategy: Rescaled weights into {Space} feature space (warm start) after standardization mode change (generation {Gen})",
                useStandardization ? "standardized" : "raw",
                _trainingGeneration);
        }
    }

    /// <summary>
    ///     Transforms the current weights/bias between raw and standardized feature space so the model's
    ///     decision function is preserved across a standardization mode change, giving the final training
    ///     pass a warm start instead of resetting to defaults.
    /// </summary>
    /// <remarks>
    ///     The raw score is s = Σ wᵢ·xᵢ + b. With standardized features x'ᵢ = (xᵢ − μᵢ)/σᵢ (so
    ///     xᵢ = σᵢ·x'ᵢ + μᵢ), substituting gives s = Σ (wᵢ·σᵢ)·x'ᵢ + (b + Σ wᵢ·μᵢ). Hence raw → standardized
    ///     is w'ᵢ = wᵢ·σᵢ and b' = b + Σ wᵢ·μᵢ; the reverse is wᵢ = w'ᵢ/σᵢ and b = b' − Σ wᵢ·μᵢ. Features whose
    ///     σᵢ ≤ 1e-8 are passed through unchanged by <see cref="StandardizeSingleVector" /> (identity, no
    ///     divide-by-zero), so their weights are left untouched here to match exactly.
    /// </remarks>
    /// <param name="toStandardized">True for raw → standardized; false for the reverse.</param>
    /// <param name="means">Per-feature means defining the standardized space.</param>
    /// <param name="stdDevs">Per-feature standard deviations defining the standardized space.</param>
    private void RescaleWeightsForStandardizationChange(bool toStandardized, double[] means, double[] stdDevs)
    {
        var featureCount = Math.Min(_weights.Length, Math.Min(means.Length, stdDevs.Length));
        var biasDelta = 0.0;

        for (var f = 0; f < featureCount; f++)
        {
            // Mirror StandardizeSingleVector's guard: a near-constant feature is never standardized, so its
            // weight lives in the same (raw) space in both modes and must not be rescaled.
            if (stdDevs[f] <= 1e-8)
            {
                continue;
            }

            if (toStandardized)
            {
                biasDelta += _weights[f] * means[f];
                _weights[f] *= stdDevs[f];
            }
            else
            {
                _weights[f] /= stdDevs[f];
                biasDelta -= _weights[f] * means[f];
            }
        }

        _bias += biasDelta;
    }

    /// <summary>
    ///     Standardizes feature vectors in-place using Z-score normalization.
    ///     Features with zero or near-zero standard deviation are left unchanged.
    /// </summary>
    /// <param name="vectors">The feature vectors to standardize (modified in-place).</param>
    /// <param name="means">The per-feature means.</param>
    /// <param name="stdDevs">The per-feature standard deviations.</param>
    internal static void StandardizeVectors(double[][] vectors, double[] means, double[] stdDevs)
    {
        foreach (var t in vectors)
        {
            StandardizeSingleVector(t, means, stdDevs);
        }
    }

    /// <summary>
    ///     Standardizes a single feature vector in-place using Z-score normalization.
    ///     Features with zero or near-zero standard deviation are left unchanged.
    /// </summary>
    /// <param name="vector">The feature vector to standardize (modified in-place).</param>
    /// <param name="means">The per-feature means.</param>
    /// <param name="stdDevs">The per-feature standard deviations.</param>
    internal static void StandardizeSingleVector(double[] vector, double[] means, double[] stdDevs)
    {
        var featureCount = Math.Min(vector.Length, means.Length);
        for (var f = 0; f < featureCount; f++)
        {
            if (stdDevs[f] > 1e-8)
            {
                vector[f] = (vector[f] - means[f]) / stdDevs[f];
            }
        }
    }

    /// <summary>
    ///     Creates a deep clone of a jagged vector array. Used to create per-fold/per-split working copies so that in-place standardization does not mutate the raw (unstandardized) source vectors.
    /// </summary>
    /// <param name="source">The source vectors to clone.</param>
    /// <returns>A new array with independently cloned inner arrays.</returns>
    internal static double[][] CloneVectors(double[][] source)
    {
        var clone = new double[source.Length][];
        for (var i = 0; i < source.Length; i++)
        {
            clone[i] = (double[])source[i].Clone();
        }

        return clone;
    }

    /// <summary>
    ///     Computes the weighted mean squared error loss on a subset of examples.
    /// </summary>
    private static double ComputeMseLoss(
        IReadOnlyList<TrainingExample> examples,
        double[][] precomputedVectors,
        double[] effectiveWeights,
        int[] indices,
        double[] weights,
        double bias)
    {
        var totalLoss = 0.0;
        var totalWeight = 0.0;

        foreach (var idx in indices)
        {
            var predicted = Math.Clamp(ScoringHelper.ComputeRawScore(precomputedVectors[idx], weights, bias), 0.0, 1.0);
            var error = predicted - examples[idx].Label;
            var w = effectiveWeights[idx];
            totalLoss += w * error * error;
            totalWeight += w;
        }

        return totalWeight > 0 ? totalLoss / totalWeight : 0.0;
    }

    /// <summary>
    ///     Trains a single train/validation split with optional early stopping.
    /// </summary>
    private double TrainSingleSplit(
        IReadOnlyList<TrainingExample> examples,
        double[][] precomputedVectors,
        double[] effectiveWeights,
        int[] trainIndices,
        int[] valIndices,
        Random rng,
        bool useEarlyStopping)
    {
        useEarlyStopping = useEarlyStopping && valIndices.Length >= MinValidationExamples;

        trainIndices = (int[])trainIndices.Clone();

        var bestLoss = double.MaxValue;
        var patienceCounter = 0;
        var bestWeights = (double[])_weights.Clone();
        var bestBias = _bias;

        var maxEpochs = useEarlyStopping ? MaxTrainingEpochs : MaxEpochsWithoutEarlyStopping;

        for (var epoch = 0; epoch < maxEpochs; epoch++)
        {
            // Cosine annealing learning rate decay
            var lr = DefaultLearningRate * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            // Fisher-Yates shuffle training indices each epoch
            for (var j = trainIndices.Length - 1; j > 0; j--)
            {
                var k = rng.Next(j + 1);
                (trainIndices[j], trainIndices[k]) = (trainIndices[k], trainIndices[j]);
            }

            foreach (var idx in trainIndices)
            {
                ApplySgdUpdate(examples, precomputedVectors, effectiveWeights, idx, lr);
            }

            if (useEarlyStopping && valIndices.Length > 0 && CheckEarlyStopping(
                    examples,
                    precomputedVectors,
                    effectiveWeights,
                    valIndices,
                    bestWeights,
                    ref bestLoss,
                    ref bestBias,
                    ref patienceCounter))
            {
                break;
            }
        }

        return bestLoss < double.MaxValue
            ? bestLoss
            : ComputeTrainingLoss(examples, precomputedVectors, effectiveWeights, _weights, _bias);
    }

    /// <summary>
    ///     Applies a single SGD weight/bias update for one training example.
    /// </summary>
    /// <param name="examples">Training examples with features and labels.</param>
    /// <param name="precomputedVectors">The per-example feature vectors (possibly standardized).</param>
    /// <param name="effectiveWeights">Per-example temporal-decay sample weights.</param>
    /// <param name="idx">Index of the example to update on.</param>
    /// <param name="lr">The current (cosine-annealed) learning rate for this epoch.</param>
    private void ApplySgdUpdate(
        IReadOnlyList<TrainingExample> examples,
        double[][] precomputedVectors,
        double[] effectiveWeights,
        int idx,
        double lr)
    {
        var vector = precomputedVectors[idx];
        var sampleWeight = effectiveWeights[idx];

        if (sampleWeight < MinSampleWeight)
        {
            return;
        }

        var z = ScoringHelper.ComputeRawScore(vector, _weights, _bias);
        var predicted = Math.Clamp(z, 0.0, 1.0);
        var error = (predicted - examples[idx].Label) * sampleWeight;

        // Saturation guard: skip gradient update when the raw score is already clamped AND the gradient would push it further into saturation.
        if ((z <= 0 && error > 0) || (z >= 1 && error < 0))
        {
            return;
        }

        var len = Math.Min(vector.Length, _weights.Length);
        for (var i = 0; i < len; i++)
        {
            var gradient = (error * vector[i]) + (L2Lambda * _weights[i]);
            _weights[i] -= lr * gradient;
            _weights[i] = Math.Clamp(_weights[i], -2.0, 2.0);
        }

        _bias -= lr * error;
        _bias = Math.Clamp(_bias, -1.0, 1.0);
    }

    /// <summary>
    ///     Evaluates the early-stopping criterion after an epoch, tracking the best-so-far weights.
    /// </summary>
    /// <param name="examples">Training examples with features and labels.</param>
    /// <param name="precomputedVectors">The per-example feature vectors (possibly standardized).</param>
    /// <param name="effectiveWeights">Per-example temporal-decay sample weights.</param>
    /// <param name="valIndices">Indices of the validation subset.</param>
    /// <param name="bestWeights">The best-so-far weight snapshot buffer (updated in-place).</param>
    /// <param name="bestLoss">The best-so-far validation loss (updated in-place).</param>
    /// <param name="bestBias">The best-so-far bias (updated in-place).</param>
    /// <param name="patienceCounter">The consecutive-no-improvement counter (updated in-place).</param>
    /// <returns>True if early stopping should break the epoch loop; otherwise false.</returns>
    private bool CheckEarlyStopping(
        IReadOnlyList<TrainingExample> examples,
        double[][] precomputedVectors,
        double[] effectiveWeights,
        int[] valIndices,
        double[] bestWeights,
        ref double bestLoss,
        ref double bestBias,
        ref int patienceCounter)
    {
        var valLoss = ComputeMseLoss(
            examples,
            precomputedVectors,
            effectiveWeights,
            valIndices,
            _weights,
            _bias);

        if (valLoss < bestLoss - EarlyStoppingMinDelta)
        {
            bestLoss = valLoss;
            patienceCounter = 0;
            Array.Copy(_weights, bestWeights, _weights.Length);
            bestBias = _bias;
        }
        else
        {
            patienceCounter++;
            if (patienceCounter >= EarlyStoppingPatience)
            {
                Array.Copy(bestWeights, _weights, _weights.Length);
                _bias = bestBias;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Computes the weighted training loss across all examples (used when no validation split).
    /// </summary>
    private static double ComputeTrainingLoss(
        IReadOnlyList<TrainingExample> examples,
        double[][] precomputedVectors,
        double[] effectiveWeights,
        double[] weights,
        double bias)
    {
        var totalLoss = 0.0;
        var totalWeight = 0.0;

        for (var idx = 0; idx < examples.Count; idx++)
        {
            var predicted = Math.Clamp(ScoringHelper.ComputeRawScore(precomputedVectors[idx], weights, bias), 0.0, 1.0);
            var error = predicted - examples[idx].Label;
            var w = effectiveWeights[idx];
            totalLoss += w * error * error;
            totalWeight += w;
        }

        return totalWeight > 0 ? totalLoss / totalWeight : 0.0;
    }

    /// <summary>
    ///     Logs per-feature importance based on absolute weight magnitudes. For a linear model, |weight[f]| directly indicates feature f's influence on the score.
    /// </summary>
    private void LogFeatureImportance()
    {
        if (_logger is null || !_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var featureNames = Enum.GetNames<FeatureIndex>();
        var inputSize = _weights.Length;

        // Sort by absolute weight descending for readability
        var ranked = new (string Name, double Weight)[inputSize];
        for (var i = 0; i < inputSize; i++)
        {
            ranked[i] = (i < featureNames.Length ? featureNames[i] : $"Feature{i}", _weights[i]);
        }

        Array.Sort(ranked, (a, b) => Math.Abs(b.Weight).CompareTo(Math.Abs(a.Weight)));

        var parts = new string[ranked.Length];
        for (var i = 0; i < ranked.Length; i++)
        {
            parts[i] = string.Format(CultureInfo.InvariantCulture, "{0}={1:F4}", ranked[i].Name, ranked[i].Weight);
        }

        _logger.LogDebug(
            "LearnedScoringStrategy feature weights (sorted by |w|): {FeatureWeights}",
            string.Join(", ", parts));
    }

    /// <summary>
    ///     Checks whether all elements in the array are finite (not NaN or Infinity).
    /// </summary>
    private static bool AllFinite(double[] values)
    {
        return values.All(double.IsFinite);
    }

    /// <summary>
    ///     Tries to load persisted weights from disk.
    /// </summary>
    private void TryLoadWeights()
    {
        if (string.IsNullOrEmpty(_weightsPath) || !File.Exists(_weightsPath))
        {
            return;
        }

        if (!IsWeightsFileWithinSizeLimit(_weightsPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_weightsPath);
            var data = JsonSerializer.Deserialize<WeightsData>(json);
            ApplyLoadedWeights(data);
        }
        catch (IOException ex)
        {
            // Graceful fallback to default weights on I/O error - log for diagnostics
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to load weights");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Graceful fallback to default weights on access denied - log for diagnostics
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to load weights (access denied)");
        }
        catch (JsonException ex)
        {
            // Graceful fallback to default weights on parse error - log for diagnostics
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to parse weights");
        }
    }

    /// <summary>
    ///     Guards against corrupted/replaced oversized weights files before reading into memory.
    /// </summary>
    /// <param name="weightsPath">The path to the persisted weights file.</param>
    /// <returns>True if the file is within the size limit and safe to read; otherwise false.</returns>
    private bool IsWeightsFileWithinSizeLimit(string weightsPath)
    {
        // Guard against corrupted/replaced oversized files before reading into memory.
        // Linear weights JSON is tiny (~10 KB); a 5 MB ceiling gives ample headroom.
        const long MaxWeightsFileSizeBytes = 5 * 1024 * 1024;
        if (new FileInfo(weightsPath).Length > MaxWeightsFileSizeBytes)
        {
            _logger?.LogWarning(
                "LearnedScoringStrategy: Weights file exceeds {LimitMB}MB ({Path}). Skipping load.",
                MaxWeightsFileSizeBytes / (1024 * 1024),
                weightsPath);
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Validates and applies deserialized weights to the instance fields, resetting to defaults on version mismatch, non-finite values, or mismatched standardization statistics.
    /// </summary>
    /// <param name="data">The deserialized weights container, or null if deserialization returned null.</param>
    private void ApplyLoadedWeights(WeightsData? data)
    {
        if (data is { Weights: { Length: CandidateFeatures.FeatureCount }, Version: CurrentWeightsVersion })
        {
            // Validate standardization stats: both must be null together or both exactly FeatureCount long.
            var meansValid = data.FeatureMeans is null ||
                             data.FeatureMeans.Length == CandidateFeatures.FeatureCount;
            var stdDevsValid = data.FeatureStdDevs is null ||
                               data.FeatureStdDevs.Length == CandidateFeatures.FeatureCount;
            var bothNullOrBothPresent = (data.FeatureMeans is null) == (data.FeatureStdDevs is null);

            // Validate all loaded values are finite (not NaN/Infinity). A corrupt-but-parseable JSON could contain NaN values that would silently produce wrong scores without causing obvious failures.
            if (!AllFinite(data.Weights) || !double.IsFinite(data.Bias))
            {
                _logger?.LogWarning(
                    "LearnedScoringStrategy: Discarding persisted weights containing NaN/Infinity values. Resetting to defaults");
                return;
            }

            // Lock field assignments for consistency with Score()/Train() which read these fields under the same lock.
            lock (_syncRoot)
            {
                if (meansValid && stdDevsValid && bothNullOrBothPresent
                    && (data.FeatureMeans is null || AllFinite(data.FeatureMeans))
                    && (data.FeatureStdDevs is null || AllFinite(data.FeatureStdDevs)))
                {
                    _weights = data.Weights;
                    _bias = data.Bias;
                    _trainingGeneration = data.TrainingGeneration;
                    _featureMeans = data.FeatureMeans;
                    _featureStdDevs = data.FeatureStdDevs;
                }
                else
                {
                    // Mismatched stats - can't safely apply loaded weights either, because they may have been trained in standardized space.
                    _weights = DefaultWeights.CreateWeightArray();
                    _bias = DefaultWeights.Bias;
                    _trainingGeneration = 0;
                    _featureMeans = null;
                    _featureStdDevs = null;
                    _logger?.LogWarning(
                        "LearnedScoringStrategy: Discarding weights + mismatched standardization stats (means={MeansLen}, stdDevs={StdDevsLen})",
                        data.FeatureMeans?.Length ?? -1,
                        data.FeatureStdDevs?.Length ?? -1);
                }
            }
        }
        else if (data is not null)
        {
            // Version mismatch or incompatible weights - reset to defaults.
            // This is expected after feature vector changes (version bump).
            _logger?.LogWarning(
                "LearnedScoringStrategy: Discarding persisted weights (file version={FileVersion}, "
                + "expected={ExpectedVersion}, featureCount={FeatureCount}). Resetting to defaults",
                data.Version,
                CurrentWeightsVersion,
                data.Weights?.Length ?? -1);
        }
    }

    /// <summary>
    ///     Persists current weights to disk synchronously.
    /// </summary>
    private bool TrySaveWeights()
    {
        if (string.IsNullOrEmpty(_weightsPath))
        {
            // No path configured means in-memory-only operation; there is no persisted file whose freshness
            // could go out of sync, so treat this as a successful no-op.
            return true;
        }

        try
        {
            var dir = Path.GetDirectoryName(_weightsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Snapshot and serialize under lock to ensure consistency with concurrent Train() calls
            string json;
            lock (_syncRoot)
            {
                var data = new WeightsData
                {
                    Weights = (double[])_weights.Clone(),
                    Bias = _bias,
                    FeatureMeans = _featureMeans is not null ? (double[])_featureMeans.Clone() : null,
                    FeatureStdDevs = _featureStdDevs is not null ? (double[])_featureStdDevs.Clone() : null,
                    TrainingGeneration = _trainingGeneration,
                    UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Version = CurrentWeightsVersion
                };
                json = JsonSerializer.Serialize(data, SerializerOptions);
            }

            // Use AtomicFile so a transient Windows AV/indexer sharing violation on the final File.Move gets a bounded retry instead of silently dropping the save (it also cleans up temp files).
            AtomicFile.WriteAllText(_weightsPath, json);
            return true;
        }
        catch (IOException ex)
        {
            // Non-critical - log for diagnostics but don't fail
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to save weights");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Non-critical - log for diagnostics but don't fail
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to save weights (access denied)");
        }
        catch (System.Security.SecurityException ex)
        {
            // Non-critical - platform security policy denied write; nothing we can do here.
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to save weights (security policy)");
        }
        catch (NotSupportedException ex)
        {
            // Non-critical - path/filesystem does not support the operation (e.g. reserved names).
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to save weights (unsupported path)");
        }
        catch (ArgumentException ex)
        {
            // Non-critical - malformed path characters surfaced by the OS layer. Weight path is
            // plugin-configured; this indicates a config error, not a runtime failure to recover from.
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to save weights (invalid path)");
        }
        catch (JsonException ex)
        {
            // Non-critical - log for diagnostics but don't fail
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to serialize weights");
        }

        // Any catch above means the weights are not persisted; report that so callers do not treat the
        // in-memory weights as durably saved.
        return false;
    }

    /// <summary>
    ///     Serializable container for persisted weights.
    /// </summary>
    internal sealed class WeightsData
    {
        /// <summary>Gets or sets the feature weights array.</summary>
        public double[] Weights { get; set; } = [];

        /// <summary>Gets or sets the bias term.</summary>
        public double Bias { get; set; }

        /// <summary>Gets or sets the per-feature means for Z-score standardization.</summary>
        public double[]? FeatureMeans { get; set; }

        /// <summary>Gets or sets the per-feature standard deviations for Z-score standardization.</summary>
        public double[]? FeatureStdDevs { get; set; }

        /// <summary>Gets or sets the training generation counter for seed variation.</summary>
        public int TrainingGeneration { get; set; }

        /// <summary>Gets or sets the ISO 8601 timestamp of the last update.</summary>
        public string UpdatedAt { get; set; } = string.Empty;

        /// <summary>Gets or sets the schema version.</summary>
        public int Version { get; set; }
    }
}