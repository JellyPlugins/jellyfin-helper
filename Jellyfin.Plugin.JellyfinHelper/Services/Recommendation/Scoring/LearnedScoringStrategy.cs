using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Adaptive ML scoring strategy using a linear model with learned weights.
///     Learns personalized feature weights from user watch history via stochastic gradient descent (SGD).
///     Genre-mismatch penalties are NOT applied here — they are handled centrally by the
///     ensemble layer to avoid double-penalization.
///     No external ML dependencies required — pure C# implementation.
/// </summary>
/// <remarks>
///     Architecture: 26 input features → 26 weights + 1 bias → clamp(0,1) → score (0–1).
///     Features include 2 interaction terms (genre×rating, genre×collab), people similarity,
///     studio match, completion ratio, abandoned flag, has-interaction flag, temporal features,
///     tag similarity, 2 cross-feature interaction terms (people×genre, recency×rating),
///     and 3 genre exposure features (underexposure, dominance ratio, affinity gap).
///     Training uses mean squared error (MSE) loss with L2 regularization, sample weighting
///     (temporal decay), Z-score feature standardization (applied both at training and scoring time),
///     and early stopping.
///     K-fold cross-validation computes standardization statistics per-fold from the training
///     fold only, avoiding data leakage from validation data into the feature normalization.
///     Weights are persisted to disk so they survive server restarts.
/// </remarks>
public sealed class LearnedScoringStrategy : IScoringStrategy, ITrainableStrategy
{
    /// <summary>Default learning rate for gradient descent.</summary>
    internal const double DefaultLearningRate = 0.02;

    /// <summary>L2 regularization strength (weight decay).</summary>
    internal const double L2Lambda = 0.001;

    /// <summary>Maximum number of training epochs per <see cref="Train"/> call.</summary>
    internal const int MaxTrainingEpochs = 30;

    /// <summary>Minimum number of training examples required before training runs.</summary>
    internal const int MinTrainingExamples = 5;

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

    /// <summary>
    ///     Minimum number of examples before Z-score standardization is applied.
    ///     Below this threshold, raw features are used to avoid unstable statistics.
    /// </summary>
    internal const int MinExamplesForStandardization = 10;

    /// <summary>
    ///     Current schema version for persisted weights. Increment when the feature set or
    ///     weight semantics change so that stale weights are discarded on load.
    /// </summary>
    internal const int CurrentWeightsVersion = 11;

    /// <summary>Cached JSON serializer options for weight persistence.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger? _logger;
    private readonly object _syncRoot = new();
    private readonly string? _weightsPath;
    private double _bias;
    private double _lastValidationLoss = double.NaN;
    private double _lastPrecisionAtK = double.NaN;
    private double _lastRecallAtK = double.NaN;
    private double _lastNdcgAtK = double.NaN;
    private int _trainingGeneration;
    private double[] _weights;

    /// <summary>
    ///     Persisted Z-score standardization statistics. When non-null, scoring applies
    ///     the same standardization that was used during training to ensure consistency.
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

        // Initialize with genre-dominant weights — genre match is the strongest signal
        _weights = DefaultWeights.CreateWeightArray();
        _bias = DefaultWeights.Bias; // positive bias; note raw score may exceed 1.0 with all features at max and is clamped

        // Try to load persisted weights
        TryLoadWeights();
    }

    /// <inheritdoc />
    public string Name => "Learned (Adaptive ML)";

    /// <inheritdoc />
    public string NameKey => "strategyLearned";

    /// <summary>
    ///     Gets the validation loss from the last training run.
    ///     Used by <see cref="EnsembleScoringStrategy"/> to gate alpha progression.
    ///     Returns <see cref="double.NaN"/> if no training has been performed.
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
    ///     Gets the Precision@K from the last training run.
    ///     Measures what fraction of top-K predicted items are actually relevant.
    ///     Returns <see cref="double.NaN"/> if no training has been performed.
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
    ///     Gets the Recall@K from the last training run.
    ///     Measures what fraction of all relevant items appear in the top-K predictions.
    ///     Returns <see cref="double.NaN"/> if no training has been performed.
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
    ///     Gets the NDCG@K from the last training run.
    ///     Measures ranking quality by rewarding relevant items at higher positions.
    ///     Returns <see cref="double.NaN"/> if no training has been performed.
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
    ///     Gets a copy of the current weights (for testing/debugging).
    /// </summary>
    internal double[] CurrentWeights
    {
        get
        {
            lock (_syncRoot)
            {
                return (double[])_weights.Clone();
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

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
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
    public bool Train(IReadOnlyList<TrainingExample> examples)
    {
        if (examples.Count < MinTrainingExamples)
        {
            return false;
        }

        // Capture a consistent reference time for temporal decay within this batch
        var referenceTime = DateTime.UtcNow;

        // Pre-compute all feature vectors ONCE before training.
        // These are the RAW (unstandardized) vectors used as the source of truth.
        // Standardization is applied per-fold (K-fold) and on all data (final pass)
        // by cloning into working copies, keeping rawVectors pristine.
        var rawVectors = new double[examples.Count][];
        var effectiveWeights = new double[examples.Count];

        for (var i = 0; i < examples.Count; i++)
        {
            rawVectors[i] = examples[i].Features.ToVector();
            effectiveWeights[i] = examples[i].ComputeEffectiveWeight(referenceTime);
        }

        // Determine whether standardization should be applied at all.
        // Thread-safety note: featureMeans/featureStdDevs are computed from local
        // data (no shared state), then assigned to instance fields INSIDE the lock below.
        // Score()/ScoreWithExplanation() read these fields under the same lock.
        var useStandardization = examples.Count >= MinExamplesForStandardization;

        lock (_syncRoot)
        {
            // Handle standardization transition: if we're applying standardization for the
            // first time but weights were previously trained on raw (unstandardized) features,
            // reset to defaults. Without this, the old weights would produce wildly wrong
            // predictions on the now-standardized inputs until gradient descent corrects them.
            var standardizationModeChanged = useStandardization != (_featureMeans is not null);
            if (standardizationModeChanged)
            {
                _weights = DefaultWeights.CreateWeightArray();
                _bias = DefaultWeights.Bias;
                _logger?.LogInformation(
                    "LearnedScoringStrategy: Reset weights to defaults after standardization mode change (generation {Gen})",
                    _trainingGeneration);
            }

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
                // === K-fold cross-validation for reliable loss estimation ===
                // Each fold computes standardization statistics from its TRAINING fold only,
                // preventing validation data from leaking into the feature normalization.
                // rawVectors is never mutated — each fold clones into working copies.
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

                    // Per-fold standardization: compute statistics from TRAINING fold only,
                    // then apply to BOTH train and validation vectors using the same stats.
                    // This prevents validation data from influencing the normalization.
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

            // === Final training on ALL data (no validation holdout) ===
            // Clone raw vectors for the final pass so standardization doesn't
            // mutate the originals (rawVectors stays pristine for ranking metrics).
            var finalVectors = CloneVectors(rawVectors);
            double[]? featureMeans = null;
            double[]? featureStdDevs = null;

            if (useStandardization)
            {
                (featureMeans, featureStdDevs) = ComputeFeatureStatistics(finalVectors);
                StandardizeVectors(finalVectors, featureMeans, featureStdDevs);
            }

            // Reset weights to defaults for a clean final training pass
            _weights = DefaultWeights.CreateWeightArray();
            _bias = DefaultWeights.Bias;

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

            LogFeatureImportance();
        } // release _syncRoot before disk I/O and ranking metrics to avoid blocking concurrent Score() calls

        // Compute ranking metrics OUTSIDE the lock — ComputeAll() calls Score() internally,
        // which acquires _syncRoot. While Monitor is reentrant (no deadlock), holding the lock
        // during the entire scoring loop unnecessarily blocks all concurrent Score() callers.
        // This mirrors the pattern used by NeuralScoringStrategy.Train().
        var (pAtK, rAtK, nAtK) = RankingMetrics.ComputeAll(examples, this, RankingMetrics.DefaultK);
        lock (_syncRoot)
        {
            _lastPrecisionAtK = pAtK;
            _lastRecallAtK = rAtK;
            _lastNdcgAtK = nAtK;
        }

        // Persist outside the lock — TrySaveWeights() takes its own lock for
        // a brief snapshot, then performs serialization and file I/O without
        // holding the scoring lock. This prevents slow disk writes from blocking
        // all concurrent Score()/ScoreWithExplanation() callers.
        TrySaveWeights();

        return true;
    }

    /// <summary>
    ///     Computes Z-score statistics (mean, stddev) for each feature across all training vectors.
    ///     Uses Bessel's correction (n-1 denominator) for unbiased sample standard deviation.
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
    ///     Standardizes feature vectors in-place using Z-score normalization.
    ///     Features with zero or near-zero standard deviation are left unchanged.
    /// </summary>
    /// <param name="vectors">The feature vectors to standardize (modified in-place).</param>
    /// <param name="means">The per-feature means.</param>
    /// <param name="stdDevs">The per-feature standard deviations.</param>
    internal static void StandardizeVectors(double[][] vectors, double[] means, double[] stdDevs)
    {
        for (var i = 0; i < vectors.Length; i++)
        {
            StandardizeSingleVector(vectors[i], means, stdDevs);
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
    ///     Creates a deep clone of a jagged vector array.
    ///     Used to create per-fold/per-split working copies so that in-place standardization
    ///     does not mutate the raw (unstandardized) source vectors.
    ///     Shared by both <see cref="LearnedScoringStrategy"/> and <see cref="NeuralScoringStrategy"/>.
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
    ///     Returns the best validation loss (or training loss if no validation set).
    ///     Modifies _weights and _bias in-place. Must be called under lock.
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
                var vector = precomputedVectors[idx];
                var sampleWeight = effectiveWeights[idx];

                if (sampleWeight < MinSampleWeight)
                {
                    continue;
                }

                var z = ScoringHelper.ComputeRawScore(vector, _weights, _bias);
                var predicted = Math.Clamp(z, 0.0, 1.0);
                var error = (predicted - examples[idx].Label) * sampleWeight;

                if ((z <= 0 && error < 0) || (z >= 1 && error > 0))
                {
                    continue;
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

            if (useEarlyStopping && valIndices.Length > 0)
            {
                var valLoss = ComputeMseLoss(examples, precomputedVectors, effectiveWeights, valIndices, _weights, _bias);

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
                        break;
                    }
                }
            }
        }

        return bestLoss < double.MaxValue
            ? bestLoss
            : ComputeTrainingLoss(examples, precomputedVectors, effectiveWeights, _weights, _bias);
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
        var allIndices = new int[examples.Count];
        for (var i = 0; i < allIndices.Length; i++)
        {
            allIndices[i] = i;
        }

        return ComputeMseLoss(examples, precomputedVectors, effectiveWeights, allIndices, weights, bias);
    }

    /// <summary>
    ///     Logs per-feature importance based on absolute weight magnitudes.
    ///     For a linear model, |weight[f]| directly indicates feature f's influence on the score.
    ///     Must be called under lock.
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

        _logger.LogDebug("LearnedScoringStrategy feature weights (sorted by |w|): {FeatureWeights}", string.Join(", ", parts));
    }

    /// <summary>
    ///     Checks whether all elements in the array are finite (not NaN or Infinity).
    /// </summary>
    private static bool AllFinite(double[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
            {
                return false;
            }
        }

        return true;
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

        try
        {
            var json = File.ReadAllText(_weightsPath);
            var data = JsonSerializer.Deserialize<WeightsData>(json);
            if (data?.Weights is { Length: CandidateFeatures.FeatureCount }
                && data.Version == CurrentWeightsVersion)
            {
                // Validate standardization stats: both must be null together or both exactly FeatureCount long.
                var meansValid = data.FeatureMeans is null || data.FeatureMeans.Length == CandidateFeatures.FeatureCount;
                var stdDevsValid = data.FeatureStdDevs is null || data.FeatureStdDevs.Length == CandidateFeatures.FeatureCount;
                var bothNullOrBothPresent = (data.FeatureMeans is null) == (data.FeatureStdDevs is null);

                // Validate all loaded values are finite (not NaN/Infinity).
                // A corrupt-but-parseable JSON could contain NaN values that would
                // silently produce wrong scores without causing obvious failures.
                if (!AllFinite(data.Weights) || !double.IsFinite(data.Bias))
                {
                    _logger?.LogWarning(
                        "LearnedScoringStrategy: Discarding persisted weights containing NaN/Infinity values. Resetting to defaults");
                    return;
                }

                // Lock field assignments for consistency with Score()/Train() which
                // read these fields under the same lock. While TryLoadWeights() is
                // currently only called from the constructor (before the object is shared),
                // the lock ensures correctness if the call site ever changes.
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
                        // Mismatched stats — can't safely apply loaded weights either, because
                        // they may have been trained in standardized space. Reset everything
                        // to defaults and let the next Train() call re-fit from scratch.
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
                // Version mismatch or incompatible weights — reset to defaults.
                // This is expected after feature vector changes (version bump).
                _logger?.LogWarning(
                    "LearnedScoringStrategy: Discarding persisted weights (file version={FileVersion}, "
                    + "expected={ExpectedVersion}, featureCount={FeatureCount}). Resetting to defaults",
                    data.Version,
                    CurrentWeightsVersion,
                    data.Weights?.Length ?? 0);
            }
        }
        catch (IOException ex)
        {
            // Graceful fallback to default weights on I/O error — log for diagnostics
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to load weights");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Graceful fallback to default weights on access denied — log for diagnostics
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to load weights (access denied)");
        }
        catch (JsonException ex)
        {
            // Graceful fallback to default weights on parse error — log for diagnostics
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to parse weights");
        }
    }

    /// <summary>
    ///     Persists current weights to disk synchronously.
    /// </summary>
    private void TrySaveWeights()
    {
        if (string.IsNullOrEmpty(_weightsPath))
        {
            return;
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

            var tempPath = _weightsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _weightsPath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // best effort — temp file cleanup is non-critical
                }

                throw;
            }
        }
        catch (IOException ex)
        {
            // Non-critical — log for diagnostics but don't fail
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to save weights");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Non-critical — log for diagnostics but don't fail
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to save weights (access denied)");
        }
        catch (JsonException ex)
        {
            // Non-critical — log for diagnostics but don't fail
            _logger?.LogWarning(ex, "LearnedScoringStrategy: Failed to serialize weights");
        }
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
