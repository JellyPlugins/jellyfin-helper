using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Neural network scoring strategy using a three-hidden-layer MLP (Multi-Layer Perceptron).
///     Learns non-linear feature interactions from user watch history via backpropagation.
///     Architecture: 23 inputs → 32 hidden₁ (ReLU) → 16 hidden₂ (ReLU) → 8 hidden₃ (ReLU) → 1 output (Sigmoid) = 1,441 parameters.
///     Optimized for NAS/Docker with limited hardware: zero-allocation scoring path,
///     pre-allocated training buffers, ~1,450 FP multiplications per score.
///     No external ML dependencies — pure C# implementation.
/// </summary>
/// <remarks>
///     Training uses Adam optimizer with L2 regularization, Z-score feature standardization,
///     Xavier weight initialization, temporal sample weighting, and early stopping.
///     Genre-mismatch penalties are NOT applied here — handled centrally by the ensemble layer.
///     Weights are persisted to disk so they survive server restarts.
/// </remarks>
public sealed class NeuralScoringStrategy : IScoringStrategy, ITrainableStrategy, IDisposable
{
    /// <summary>Number of neurons in the first hidden layer.</summary>
    internal const int Hidden1Size = 32;

    /// <summary>Number of neurons in the second hidden layer.</summary>
    internal const int Hidden2Size = 16;

    /// <summary>Number of neurons in the third hidden layer.</summary>
    internal const int Hidden3Size = 8;

    /// <summary>Default learning rate for Adam optimizer.</summary>
    internal const double DefaultLearningRate = 0.005;

    /// <summary>L2 regularization strength (weight decay). Slightly higher to counteract increased capacity.</summary>
    internal const double L2Lambda = 0.002;

    /// <summary>Adam β1 (first moment exponential decay rate).</summary>
    internal const double AdamBeta1 = 0.9;

    /// <summary>Adam β2 (second moment exponential decay rate).</summary>
    internal const double AdamBeta2 = 0.999;

    /// <summary>Adam ε for numerical stability.</summary>
    internal const double AdamEpsilon = 1e-8;

    /// <summary>Maximum training epochs per <see cref="Train"/> call.</summary>
    internal const int MaxTrainingEpochs = 50;

    /// <summary>Minimum training examples required before training runs. Higher due to increased model capacity.</summary>
    internal const int MinTrainingExamples = 12;

    /// <summary>Consecutive epochs without improvement before early stopping triggers.</summary>
    internal const int EarlyStoppingPatience = 6;

    /// <summary>Fraction of examples used for validation.</summary>
    internal const double ValidationSplitRatio = 0.2;

    /// <summary>Minimum validation examples required for early stopping.</summary>
    internal const int MinValidationExamples = 2;

    /// <summary>Minimum examples before Z-score standardization is applied.</summary>
    internal const int MinExamplesForStandardization = 10;

    /// <summary>Weight clamp magnitude to prevent gradient explosion.</summary>
    internal const double WeightClamp = 3.0;

    /// <summary>Minimum sample weight below which a training example is skipped (temporal decay floor).</summary>
    internal const double MinSampleWeight = 0.01;

    /// <summary>Early stopping improvement threshold (avoids triggering on noise).</summary>
    internal const double EarlyStoppingMinDelta = 1e-6;

    /// <summary>Maximum epochs when early stopping is disabled (fewer epochs to avoid overfitting).</summary>
    internal const int MaxEpochsWithoutEarlyStopping = 20;

    /// <summary>Schema version for persisted weights. Increment on architecture changes.</summary>
    internal const int CurrentWeightsVersion = 4;

    /// <summary>Legacy constant kept for backward compatibility with tests. Maps to <see cref="Hidden3Size"/>.</summary>
    internal const int HiddenSize = Hidden3Size;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger? _logger;
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly object _syncRoot = new();
    private readonly string? _weightsPath;

    /// <summary>Thread-local scratch buffers to avoid contention on the hot Score() path.</summary>
    [ThreadStatic]
    private static double[]? _tlsH1Pre;
    [ThreadStatic]
    private static double[]? _tlsH1Act;
    [ThreadStatic]
    private static double[]? _tlsH2Pre;
    [ThreadStatic]
    private static double[]? _tlsH2Act;
    [ThreadStatic]
    private static double[]? _tlsH3Pre;
    [ThreadStatic]
    private static double[]? _tlsH3Act;

    private int _adamTimestep;
    private double[] _biasH1;
    private double[] _biasH2;
    private double[] _biasH3;
    private double _biasOutput;
    private volatile bool _disposed;
    private double[]? _featureMeans;
    private double[]? _featureStdDevs;
    private double _lastValidationLoss = double.NaN;
    private double _lastPrecisionAtK = double.NaN;
    private double _lastRecallAtK = double.NaN;
    private double _lastNdcgAtK = double.NaN;
    private double[]? _mBH1;
    private double[]? _mBH2;
    private double[]? _mBH3;
    private double _mBO;
    private double[]? _mWH1H2;
    private double[]? _mWH2H3;
    private double[]? _mWH3O;
    private double[]? _mWIH;
    private int _trainingGeneration;
    private double[]? _vBH1;
    private double[]? _vBH2;
    private double[]? _vBH3;
    private double _vBO;
    private double[]? _vWH1H2;
    private double[]? _vWH2H3;
    private double[]? _vWH3O;
    private double[]? _vWIH;
    private double[] _weightsH1H2;
    private double[] _weightsH2H3;
    private double[] _weightsH3O;
    private double[] _weightsIH;

    /// <summary>
    ///     Initializes a new instance of the <see cref="NeuralScoringStrategy"/> class
    ///     with Xavier-initialized weights for stable gradient flow.
    /// </summary>
    /// <param name="weightsPath">Optional file path for persisting learned weights.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public NeuralScoringStrategy(string? weightsPath = null, ILogger? logger = null)
    {
        _weightsPath = weightsPath;
        _logger = logger;

        var inputSize = CandidateFeatures.FeatureCount;
        _weightsIH = new double[Hidden1Size * inputSize];
        _biasH1 = new double[Hidden1Size];
        _weightsH1H2 = new double[Hidden2Size * Hidden1Size];
        _biasH2 = new double[Hidden2Size];
        _weightsH2H3 = new double[Hidden3Size * Hidden2Size];
        _biasH3 = new double[Hidden3Size];
        _weightsH3O = new double[Hidden3Size];
        _biasOutput = 0.0;

        InitializeXavier(inputSize);
        TryLoadWeights();
    }

    /// <inheritdoc />
    public string Name => "Neural (Adaptive MLP)";

    /// <inheritdoc />
    public string NameKey => "strategyNeural";

    /// <summary>
    ///     Gets the validation loss from the last training run.
    ///     Used by <see cref="EnsembleScoringStrategy"/> to compare against the linear model.
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

    /// <summary>Gets the Precision@K from the last training run.</summary>
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

    /// <summary>Gets the Recall@K from the last training run.</summary>
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

    /// <summary>Gets the NDCG@K from the last training run.</summary>
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

    /// <summary>Gets a copy of the input→hidden1 layer weights (for testing).</summary>
    internal double[] CurrentWeightsHidden
    {
        get
        {
            lock (_syncRoot)
            {
                return (double[])_weightsIH.Clone();
            }
        }
    }

    /// <summary>Gets a copy of the hidden3→output layer weights (for testing).</summary>
    internal double[] CurrentWeightsOutput
    {
        get
        {
            lock (_syncRoot)
            {
                return (double[])_weightsH3O.Clone();
            }
        }
    }

    /// <summary>Gets a copy of the hidden1→hidden2 layer weights (for testing).</summary>
    internal double[] CurrentWeightsH1H2
    {
        get
        {
            lock (_syncRoot)
            {
                return (double[])_weightsH1H2.Clone();
            }
        }
    }

    /// <summary>Gets a copy of the hidden2→hidden3 layer weights (for testing).</summary>
    internal double[] CurrentWeightsH2H3
    {
        get
        {
            lock (_syncRoot)
            {
                return (double[])_weightsH2H3.Clone();
            }
        }
    }

    /// <summary>Gets the current training generation (for testing).</summary>
    internal int TrainingGeneration
    {
        get
        {
            lock (_syncRoot)
            {
                return _trainingGeneration;
            }
        }
    }

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        if (_disposed)
        {
            return 0.5;
        }

        var vector = new double[CandidateFeatures.FeatureCount];
        features.WriteToVector(vector);

        _tlsH1Pre ??= new double[Hidden1Size];
        _tlsH1Act ??= new double[Hidden1Size];
        _tlsH2Pre ??= new double[Hidden2Size];
        _tlsH2Act ??= new double[Hidden2Size];
        _tlsH3Pre ??= new double[Hidden3Size];
        _tlsH3Act ??= new double[Hidden3Size];

        try
        {
            try
            {
                _rwLock.EnterReadLock();
            }
            catch (ObjectDisposedException)
            {
                return 0.5;
            }

            if (_featureMeans is not null && _featureStdDevs is not null)
            {
                LearnedScoringStrategy.StandardizeSingleVector(vector, _featureMeans, _featureStdDevs);
            }

            var result = ForwardPass(
                vector,
                _weightsIH,
                _biasH1,
                _weightsH1H2,
                _biasH2,
                _weightsH2H3,
                _biasH3,
                _weightsH3O,
                _biasOutput,
                _tlsH1Pre,
                _tlsH1Act,
                _tlsH2Pre,
                _tlsH2Act,
                _tlsH3Pre,
                _tlsH3Act);

            return ScoringHelper.GuardScore(result);
        }
        finally
        {
            if (_rwLock.IsReadLockHeld)
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        if (_disposed)
        {
            return new ScoreExplanation { FinalScore = 0.5, StrategyName = Name };
        }

        var vector = new double[CandidateFeatures.FeatureCount];
        features.WriteToVector(vector);

        try
        {
            try
            {
                _rwLock.EnterReadLock();
            }
            catch (ObjectDisposedException)
            {
                return new ScoreExplanation { FinalScore = 0.5, StrategyName = Name };
            }

            if (_featureMeans is not null && _featureStdDevs is not null)
            {
                LearnedScoringStrategy.StandardizeSingleVector(vector, _featureMeans, _featureStdDevs);
            }

            var h1Pre = new double[Hidden1Size];
            var h1Act = new double[Hidden1Size];
            var h2Pre = new double[Hidden2Size];
            var h2Act = new double[Hidden2Size];
            var h3Pre = new double[Hidden3Size];
            var h3Act = new double[Hidden3Size];
            var rawScore = ForwardPass(
                vector,
                _weightsIH,
                _biasH1,
                _weightsH1H2,
                _biasH2,
                _weightsH2H3,
                _biasH3,
                _weightsH3O,
                _biasOutput,
                h1Pre,
                h1Act,
                h2Pre,
                h2Act,
                h3Pre,
                h3Act);

            var score = ScoringHelper.GuardScore(rawScore);

            // Input-gradient attribution through all three hidden layers
            var inputSize = CandidateFeatures.FeatureCount;
            var attr = new double[inputSize];

            for (var l = 0; l < Hidden3Size; l++)
            {
                if (h3Pre[l] <= 0)
                {
                    continue;
                }

                var outW = _weightsH3O[l];
                for (var k = 0; k < Hidden2Size; k++)
                {
                    if (h2Pre[k] <= 0)
                    {
                        continue;
                    }

                    var h2h3W = _weightsH2H3[(l * Hidden2Size) + k];
                    var combinedOuter = outW * h2h3W;
                    for (var j = 0; j < Hidden1Size; j++)
                    {
                        if (h1Pre[j] <= 0)
                        {
                            continue;
                        }

                        var h1h2W = _weightsH1H2[(k * Hidden1Size) + j];
                        var combined = combinedOuter * h1h2W;
                        var baseIdx = j * inputSize;
                        for (var i = 0; i < inputSize; i++)
                        {
                            attr[i] += combined * _weightsIH[baseIdx + i] * vector[i];
                        }
                    }
                }
            }

            var interactionContrib =
                attr[(int)FeatureIndex.GenreCountNormalized] +
                attr[(int)FeatureIndex.IsSeries] +
                attr[(int)FeatureIndex.GenreRatingInteraction] +
                attr[(int)FeatureIndex.GenreCollabInteraction] +
                attr[(int)FeatureIndex.CompletionRatio] +
                attr[(int)FeatureIndex.IsAbandoned] +
                attr[(int)FeatureIndex.HasInteraction] +
                attr[(int)FeatureIndex.SeriesProgressionBoost] +
                attr[(int)FeatureIndex.PopularityScore] +
                attr[(int)FeatureIndex.DayOfWeekAffinity];

            return new ScoreExplanation
            {
                FinalScore = score,
                GenreContribution = attr[(int)FeatureIndex.GenreSimilarity],
                CollaborativeContribution = attr[(int)FeatureIndex.CollaborativeScore],
                RatingContribution = attr[(int)FeatureIndex.RatingScore],
                RecencyContribution = attr[(int)FeatureIndex.RecencyScore],
                YearProximityContribution = attr[(int)FeatureIndex.YearProximityScore],
                UserRatingContribution = attr[(int)FeatureIndex.UserRatingScore],
                PeopleContribution = attr[(int)FeatureIndex.PeopleSimilarity],
                StudioContribution = attr[(int)FeatureIndex.StudioMatch],
                InteractionContribution = interactionContrib,
                GenrePenaltyMultiplier = 1.0,
                DominantSignal = ScoreExplanation.DetermineDominantSignal(
                    attr[(int)FeatureIndex.GenreSimilarity],
                    attr[(int)FeatureIndex.CollaborativeScore],
                    attr[(int)FeatureIndex.RatingScore],
                    attr[(int)FeatureIndex.UserRatingScore],
                    attr[(int)FeatureIndex.RecencyScore],
                    attr[(int)FeatureIndex.YearProximityScore],
                    interactionContrib,
                    attr[(int)FeatureIndex.PeopleSimilarity],
                    attr[(int)FeatureIndex.StudioMatch]),
                StrategyName = Name
            };
        }
        finally
        {
            if (_rwLock.IsReadLockHeld)
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    ///     Trains the MLP via backpropagation with Adam optimizer.
    /// </summary>
    /// <param name="examples">Training examples with features and labels.</param>
    /// <returns>True if training was performed, false if insufficient data.</returns>
    public bool Train(IReadOnlyList<TrainingExample> examples)
    {
        if (examples.Count < MinTrainingExamples)
        {
            return false;
        }

        var referenceTime = DateTime.UtcNow;
        var inputSize = CandidateFeatures.FeatureCount;

        var vectors = new double[examples.Count][];
        var weights = new double[examples.Count];

        for (var i = 0; i < examples.Count; i++)
        {
            vectors[i] = examples[i].Features.ToVector();
            weights[i] = examples[i].ComputeEffectiveWeight(referenceTime);
        }

        double[]? featureMeans = null;
        double[]? featureStdDevs = null;

        if (examples.Count >= MinExamplesForStandardization)
        {
            (featureMeans, featureStdDevs) = LearnedScoringStrategy.ComputeFeatureStatistics(vectors);
            LearnedScoringStrategy.StandardizeVectors(vectors, featureMeans, featureStdDevs);
        }

        try
        {
            _rwLock.EnterWriteLock();

            EnsureAdamState(inputSize);

            var valCount = Math.Max(MinValidationExamples, (int)(examples.Count * ValidationSplitRatio));
            valCount = Math.Min(valCount, examples.Count - MinTrainingExamples);
            var useEarlyStopping = valCount >= MinValidationExamples
                && examples.Count - valCount >= MinTrainingExamples;

            var rng = new Random(42 + _trainingGeneration);
            _trainingGeneration++;

            var indices = new int[examples.Count];
            for (var j = 0; j < indices.Length; j++)
            {
                indices[j] = j;
            }

            for (var j = indices.Length - 1; j > 0; j--)
            {
                var k = rng.Next(j + 1);
                (indices[j], indices[k]) = (indices[k], indices[j]);
            }

            int[] trainIdx;
            int[] valIdx;
            if (useEarlyStopping)
            {
                trainIdx = indices[..^valCount];
                valIdx = indices[^valCount..];
            }
            else
            {
                trainIdx = indices;
                valIdx = [];
            }

            var bestLoss = double.MaxValue;
            var patience = 0;

            var bestWIH = (double[])_weightsIH.Clone();
            var bestBH1 = (double[])_biasH1.Clone();
            var bestWH1H2 = (double[])_weightsH1H2.Clone();
            var bestBH2 = (double[])_biasH2.Clone();
            var bestWH2H3 = (double[])_weightsH2H3.Clone();
            var bestBH3 = (double[])_biasH3.Clone();
            var bestWH3O = (double[])_weightsH3O.Clone();
            var bestBO = _biasOutput;

            var h1Pre = new double[Hidden1Size];
            var h1Act = new double[Hidden1Size];
            var h2Pre = new double[Hidden2Size];
            var h2Act = new double[Hidden2Size];
            var h3Pre = new double[Hidden3Size];
            var h3Act = new double[Hidden3Size];
            var h1Err = new double[Hidden1Size];
            var h2Err = new double[Hidden2Size];
            var h3Err = new double[Hidden3Size];

            var maxEpochs = useEarlyStopping ? MaxTrainingEpochs : Math.Min(MaxTrainingEpochs, MaxEpochsWithoutEarlyStopping);

            for (var epoch = 0; epoch < maxEpochs; epoch++)
            {
                for (var j = trainIdx.Length - 1; j > 0; j--)
                {
                    var k = rng.Next(j + 1);
                    (trainIdx[j], trainIdx[k]) = (trainIdx[k], trainIdx[j]);
                }

                foreach (var idx in trainIdx)
                {
                    var sw = weights[idx];
                    if (sw < MinSampleWeight)
                    {
                        continue;
                    }

                    var vec = vectors[idx];

                    var pred = ForwardPass(
                        vec,
                        _weightsIH,
                        _biasH1,
                        _weightsH1H2,
                        _biasH2,
                        _weightsH2H3,
                        _biasH3,
                        _weightsH3O,
                        _biasOutput,
                        h1Pre,
                        h1Act,
                        h2Pre,
                        h2Act,
                        h3Pre,
                        h3Act);

                    var outErr = (pred - examples[idx].Label) * pred * (1.0 - pred) * sw;

                    _adamTimestep++;
                    var bc1 = 1.0 - Math.Pow(AdamBeta1, _adamTimestep);
                    var bc2 = 1.0 - Math.Pow(AdamBeta2, _adamTimestep);

                    // === Output layer Adam update (hidden3 → output) ===
                    for (var k = 0; k < Hidden3Size; k++)
                    {
                        var g = (outErr * h3Act[k]) + (L2Lambda * _weightsH3O[k]);
                        _mWH3O![k] = (AdamBeta1 * _mWH3O[k]) + ((1 - AdamBeta1) * g);
                        _vWH3O![k] = (AdamBeta2 * _vWH3O[k]) + ((1 - AdamBeta2) * g * g);
                        _weightsH3O[k] -= DefaultLearningRate * (_mWH3O[k] / bc1) / (Math.Sqrt(_vWH3O[k] / bc2) + AdamEpsilon);
                        _weightsH3O[k] = Math.Clamp(_weightsH3O[k], -WeightClamp, WeightClamp);
                    }

                    {
                        var g = outErr;
                        _mBO = (AdamBeta1 * _mBO) + ((1 - AdamBeta1) * g);
                        _vBO = (AdamBeta2 * _vBO) + ((1 - AdamBeta2) * g * g);
                        _biasOutput -= DefaultLearningRate * (_mBO / bc1) / (Math.Sqrt(_vBO / bc2) + AdamEpsilon);
                        _biasOutput = Math.Clamp(_biasOutput, -WeightClamp, WeightClamp);
                    }

                    // === Hidden3 layer error (backprop through ReLU) ===
                    for (var k = 0; k < Hidden3Size; k++)
                    {
                        h3Err[k] = h3Pre[k] > 0 ? outErr * _weightsH3O[k] : 0.0;
                    }

                    // === Hidden2→Hidden3 layer Adam update ===
                    for (var k = 0; k < Hidden3Size; k++)
                    {
                        var bIdx = k * Hidden2Size;
                        for (var j = 0; j < Hidden2Size; j++)
                        {
                            var p = bIdx + j;
                            var g = (h3Err[k] * h2Act[j]) + (L2Lambda * _weightsH2H3[p]);
                            _mWH2H3![p] = (AdamBeta1 * _mWH2H3[p]) + ((1 - AdamBeta1) * g);
                            _vWH2H3![p] = (AdamBeta2 * _vWH2H3[p]) + ((1 - AdamBeta2) * g * g);
                            _weightsH2H3[p] -= DefaultLearningRate * (_mWH2H3[p] / bc1) / (Math.Sqrt(_vWH2H3[p] / bc2) + AdamEpsilon);
                            _weightsH2H3[p] = Math.Clamp(_weightsH2H3[p], -WeightClamp, WeightClamp);
                        }

                        {
                            var g = h3Err[k];
                            _mBH3![k] = (AdamBeta1 * _mBH3[k]) + ((1 - AdamBeta1) * g);
                            _vBH3![k] = (AdamBeta2 * _vBH3[k]) + ((1 - AdamBeta2) * g * g);
                            _biasH3[k] -= DefaultLearningRate * (_mBH3[k] / bc1) / (Math.Sqrt(_vBH3[k] / bc2) + AdamEpsilon);
                            _biasH3[k] = Math.Clamp(_biasH3[k], -WeightClamp, WeightClamp);
                        }
                    }

                    // === Hidden2 layer error (backprop through ReLU from hidden3) ===
                    for (var k = 0; k < Hidden2Size; k++)
                    {
                        if (h2Pre[k] <= 0)
                        {
                            h2Err[k] = 0.0;
                            continue;
                        }

                        var sum = 0.0;
                        for (var l = 0; l < Hidden3Size; l++)
                        {
                            sum += h3Err[l] * _weightsH2H3[(l * Hidden2Size) + k];
                        }

                        h2Err[k] = sum;
                    }

                    // === Hidden1→Hidden2 layer Adam update ===
                    for (var k = 0; k < Hidden2Size; k++)
                    {
                        var bIdx = k * Hidden1Size;
                        for (var j = 0; j < Hidden1Size; j++)
                        {
                            var p = bIdx + j;
                            var g = (h2Err[k] * h1Act[j]) + (L2Lambda * _weightsH1H2[p]);
                            _mWH1H2![p] = (AdamBeta1 * _mWH1H2[p]) + ((1 - AdamBeta1) * g);
                            _vWH1H2![p] = (AdamBeta2 * _vWH1H2[p]) + ((1 - AdamBeta2) * g * g);
                            _weightsH1H2[p] -= DefaultLearningRate * (_mWH1H2[p] / bc1) / (Math.Sqrt(_vWH1H2[p] / bc2) + AdamEpsilon);
                            _weightsH1H2[p] = Math.Clamp(_weightsH1H2[p], -WeightClamp, WeightClamp);
                        }

                        {
                            var g = h2Err[k];
                            _mBH2![k] = (AdamBeta1 * _mBH2[k]) + ((1 - AdamBeta1) * g);
                            _vBH2![k] = (AdamBeta2 * _vBH2[k]) + ((1 - AdamBeta2) * g * g);
                            _biasH2[k] -= DefaultLearningRate * (_mBH2[k] / bc1) / (Math.Sqrt(_vBH2[k] / bc2) + AdamEpsilon);
                            _biasH2[k] = Math.Clamp(_biasH2[k], -WeightClamp, WeightClamp);
                        }
                    }

                    // === Hidden1 layer error (backprop through ReLU from hidden2) ===
                    for (var j = 0; j < Hidden1Size; j++)
                    {
                        if (h1Pre[j] <= 0)
                        {
                            h1Err[j] = 0.0;
                            continue;
                        }

                        var sum = 0.0;
                        for (var k = 0; k < Hidden2Size; k++)
                        {
                            sum += h2Err[k] * _weightsH1H2[(k * Hidden1Size) + j];
                        }

                        h1Err[j] = sum;
                    }

                    // === Input→Hidden1 layer Adam update ===
                    for (var j = 0; j < Hidden1Size; j++)
                    {
                        var bIdx = j * inputSize;
                        for (var i = 0; i < inputSize; i++)
                        {
                            var p = bIdx + i;
                            var g = (h1Err[j] * vec[i]) + (L2Lambda * _weightsIH[p]);
                            _mWIH![p] = (AdamBeta1 * _mWIH[p]) + ((1 - AdamBeta1) * g);
                            _vWIH![p] = (AdamBeta2 * _vWIH[p]) + ((1 - AdamBeta2) * g * g);
                            _weightsIH[p] -= DefaultLearningRate * (_mWIH[p] / bc1) / (Math.Sqrt(_vWIH[p] / bc2) + AdamEpsilon);
                            _weightsIH[p] = Math.Clamp(_weightsIH[p], -WeightClamp, WeightClamp);
                        }

                        {
                            var g = h1Err[j];
                            _mBH1![j] = (AdamBeta1 * _mBH1[j]) + ((1 - AdamBeta1) * g);
                            _vBH1![j] = (AdamBeta2 * _vBH1[j]) + ((1 - AdamBeta2) * g * g);
                            _biasH1[j] -= DefaultLearningRate * (_mBH1[j] / bc1) / (Math.Sqrt(_vBH1[j] / bc2) + AdamEpsilon);
                            _biasH1[j] = Math.Clamp(_biasH1[j], -WeightClamp, WeightClamp);
                        }
                    }
                }

                if (useEarlyStopping && valIdx.Length > 0)
                {
                    var valLoss = ComputeMseLoss(examples, vectors, weights, valIdx);
                    if (valLoss < bestLoss - EarlyStoppingMinDelta)
                    {
                        bestLoss = valLoss;
                        patience = 0;
                        Array.Copy(_weightsIH, bestWIH, _weightsIH.Length);
                        Array.Copy(_biasH1, bestBH1, _biasH1.Length);
                        Array.Copy(_weightsH1H2, bestWH1H2, _weightsH1H2.Length);
                        Array.Copy(_biasH2, bestBH2, _biasH2.Length);
                        Array.Copy(_weightsH2H3, bestWH2H3, _weightsH2H3.Length);
                        Array.Copy(_biasH3, bestBH3, _biasH3.Length);
                        Array.Copy(_weightsH3O, bestWH3O, _weightsH3O.Length);
                        bestBO = _biasOutput;
                    }
                    else
                    {
                        patience++;
                        if (patience >= EarlyStoppingPatience)
                        {
                            Array.Copy(bestWIH, _weightsIH, _weightsIH.Length);
                            Array.Copy(bestBH1, _biasH1, _biasH1.Length);
                            Array.Copy(bestWH1H2, _weightsH1H2, _weightsH1H2.Length);
                            Array.Copy(bestBH2, _biasH2, _biasH2.Length);
                            Array.Copy(bestWH2H3, _weightsH2H3, _weightsH2H3.Length);
                            Array.Copy(bestBH3, _biasH3, _biasH3.Length);
                            Array.Copy(bestWH3O, _weightsH3O, _weightsH3O.Length);
                            _biasOutput = bestBO;
                            break;
                        }
                    }
                }
            }

            _lastValidationLoss = bestLoss < double.MaxValue
                ? bestLoss
                : ComputeMseLoss(examples, vectors, weights, trainIdx);

            _featureMeans = featureMeans;
            _featureStdDevs = featureStdDevs;

            TrySaveWeights();
            LogFeatureImportance(inputSize);
        }
        finally
        {
            if (_rwLock.IsWriteLockHeld)
            {
                _rwLock.ExitWriteLock();
            }
        }

        // Compute ranking metrics outside the write lock (Score() needs read lock).
        var (pAtK, rAtK, nAtK) = RankingMetrics.ComputeAll(examples, this, RankingMetrics.DefaultK);
        lock (_syncRoot)
        {
            _lastPrecisionAtK = pAtK;
            _lastRecallAtK = rAtK;
            _lastNdcgAtK = nAtK;
        }

        return true;
    }

    /// <summary>
    ///     MLP forward pass: input → hidden₁ (ReLU) → hidden₂ (ReLU) → hidden₃ (ReLU) → output (Sigmoid).
    ///     Uses pre-allocated buffers for hidden activations to avoid allocation.
    /// </summary>
    /// <param name="input">Input feature vector [InputSize].</param>
    /// <param name="wIH">Input→Hidden1 weights [Hidden1Size × InputSize] row-major.</param>
    /// <param name="bH1">Hidden1 biases [Hidden1Size].</param>
    /// <param name="wH1H2">Hidden1→Hidden2 weights [Hidden2Size × Hidden1Size] row-major.</param>
    /// <param name="bH2">Hidden2 biases [Hidden2Size].</param>
    /// <param name="wH2H3">Hidden2→Hidden3 weights [Hidden3Size × Hidden2Size] row-major.</param>
    /// <param name="bH3">Hidden3 biases [Hidden3Size].</param>
    /// <param name="wH3O">Hidden3→Output weights [Hidden3Size].</param>
    /// <param name="bO">Output bias scalar.</param>
    /// <param name="h1Pre">Pre-allocated buffer for hidden1 pre-activation values [Hidden1Size].</param>
    /// <param name="h1Act">Pre-allocated buffer for hidden1 post-activation values [Hidden1Size].</param>
    /// <param name="h2Pre">Pre-allocated buffer for hidden2 pre-activation values [Hidden2Size].</param>
    /// <param name="h2Act">Pre-allocated buffer for hidden2 post-activation values [Hidden2Size].</param>
    /// <param name="h3Pre">Pre-allocated buffer for hidden3 pre-activation values [Hidden3Size].</param>
    /// <param name="h3Act">Pre-allocated buffer for hidden3 post-activation values [Hidden3Size].</param>
    /// <returns>Output score in [0, 1] via sigmoid.</returns>
    internal static double ForwardPass(
        double[] input,
        double[] wIH,
        double[] bH1,
        double[] wH1H2,
        double[] bH2,
        double[] wH2H3,
        double[] bH3,
        double[] wH3O,
        double bO,
        double[] h1Pre,
        double[] h1Act,
        double[] h2Pre,
        double[] h2Act,
        double[] h3Pre,
        double[] h3Act)
    {
        var inputSize = input.Length;

        // Hidden layer 1: input → hidden1 (ReLU)
        for (var j = 0; j < Hidden1Size; j++)
        {
            var sum = bH1[j];
            var baseIdx = j * inputSize;
            for (var i = 0; i < inputSize; i++)
            {
                sum += wIH[baseIdx + i] * input[i];
            }

            h1Pre[j] = sum;
            h1Act[j] = sum > 0 ? sum : 0.0;
        }

        // Hidden layer 2: hidden1 → hidden2 (ReLU)
        for (var k = 0; k < Hidden2Size; k++)
        {
            var sum = bH2[k];
            var baseIdx = k * Hidden1Size;
            for (var j = 0; j < Hidden1Size; j++)
            {
                sum += wH1H2[baseIdx + j] * h1Act[j];
            }

            h2Pre[k] = sum;
            h2Act[k] = sum > 0 ? sum : 0.0;
        }

        // Hidden layer 3: hidden2 → hidden3 (ReLU)
        for (var l = 0; l < Hidden3Size; l++)
        {
            var sum = bH3[l];
            var baseIdx = l * Hidden2Size;
            for (var k = 0; k < Hidden2Size; k++)
            {
                sum += wH2H3[baseIdx + k] * h2Act[k];
            }

            h3Pre[l] = sum;
            h3Act[l] = sum > 0 ? sum : 0.0;
        }

        // Output layer: hidden3 → output (Sigmoid)
        var outputZ = bO;
        for (var l = 0; l < Hidden3Size; l++)
        {
            outputZ += wH3O[l] * h3Act[l];
        }

        return Sigmoid(outputZ);
    }

    /// <summary>
    ///     Numerically stable sigmoid: 1 / (1 + exp(-x)).
    ///     Guards against overflow for large |x|.
    /// </summary>
    /// <param name="x">The input value.</param>
    /// <returns>The sigmoid output in (0, 1).</returns>
    internal static double Sigmoid(double x)
    {
        if (x >= 0)
        {
            var ez = Math.Exp(-x);
            return 1.0 / (1.0 + ez);
        }
        else
        {
            var ez = Math.Exp(x);
            return ez / (1.0 + ez);
        }
    }

    /// <summary>
    ///     Xavier/Glorot uniform initialization for stable gradient flow.
    ///     Each layer's weights ~ U(-limit, limit) where limit = sqrt(6 / (fan_in + fan_out)).
    /// </summary>
    private void InitializeXavier(int inputSize)
    {
        var rng = new Random(42);

        // Input → Hidden1
        var limitIH = Math.Sqrt(6.0 / (inputSize + Hidden1Size));
        for (var i = 0; i < _weightsIH.Length; i++)
        {
            _weightsIH[i] = (rng.NextDouble() * 2.0 * limitIH) - limitIH;
        }

        // Hidden1 → Hidden2
        var limitH1H2 = Math.Sqrt(6.0 / (Hidden1Size + Hidden2Size));
        for (var i = 0; i < _weightsH1H2.Length; i++)
        {
            _weightsH1H2[i] = (rng.NextDouble() * 2.0 * limitH1H2) - limitH1H2;
        }

        // Hidden2 → Hidden3
        var limitH2H3 = Math.Sqrt(6.0 / (Hidden2Size + Hidden3Size));
        for (var i = 0; i < _weightsH2H3.Length; i++)
        {
            _weightsH2H3[i] = (rng.NextDouble() * 2.0 * limitH2H3) - limitH2H3;
        }

        // Hidden3 → Output
        var limitH3O = Math.Sqrt(6.0 / (Hidden3Size + 1));
        for (var i = 0; i < _weightsH3O.Length; i++)
        {
            _weightsH3O[i] = (rng.NextDouble() * 2.0 * limitH3O) - limitH3O;
        }

        Array.Clear(_biasH1);
        Array.Clear(_biasH2);
        Array.Clear(_biasH3);
    }

    /// <summary>
    ///     Ensures Adam optimizer moment arrays are allocated.
    /// </summary>
    private void EnsureAdamState(int inputSize)
    {
        var wihLen = Hidden1Size * inputSize;
        if (_mWIH is not null && _mWIH.Length == wihLen)
        {
            return;
        }

        _mWIH = new double[wihLen];
        _vWIH = new double[wihLen];
        _mBH1 = new double[Hidden1Size];
        _vBH1 = new double[Hidden1Size];

        var wh1h2Len = Hidden2Size * Hidden1Size;
        _mWH1H2 = new double[wh1h2Len];
        _vWH1H2 = new double[wh1h2Len];
        _mBH2 = new double[Hidden2Size];
        _vBH2 = new double[Hidden2Size];

        var wh2h3Len = Hidden3Size * Hidden2Size;
        _mWH2H3 = new double[wh2h3Len];
        _vWH2H3 = new double[wh2h3Len];
        _mBH3 = new double[Hidden3Size];
        _vBH3 = new double[Hidden3Size];

        _mWH3O = new double[Hidden3Size];
        _vWH3O = new double[Hidden3Size];
        _mBO = 0;
        _vBO = 0;

        _adamTimestep = 0;
    }

    /// <summary>
    ///     Computes weighted MSE loss on a subset of examples.
    /// </summary>
    private double ComputeMseLoss(
        IReadOnlyList<TrainingExample> examples,
        double[][] vectors,
        double[] effectiveWeights,
        int[] indices)
    {
        var totalLoss = 0.0;
        var totalWeight = 0.0;
        var h1Pre = new double[Hidden1Size];
        var h1Act = new double[Hidden1Size];
        var h2Pre = new double[Hidden2Size];
        var h2Act = new double[Hidden2Size];
        var h3Pre = new double[Hidden3Size];
        var h3Act = new double[Hidden3Size];

        foreach (var idx in indices)
        {
            var pred = ForwardPass(
                vectors[idx],
                _weightsIH,
                _biasH1,
                _weightsH1H2,
                _biasH2,
                _weightsH2H3,
                _biasH3,
                _weightsH3O,
                _biasOutput,
                h1Pre,
                h1Act,
                h2Pre,
                h2Act,
                h3Pre,
                h3Act);
            var error = pred - examples[idx].Label;
            var w = effectiveWeights[idx];
            totalLoss += w * error * error;
            totalWeight += w;
        }

        return totalWeight > 0 ? totalLoss / totalWeight : 0.0;
    }

    /// <summary>Tries to load persisted weights from disk.</summary>
    private void TryLoadWeights()
    {
        if (string.IsNullOrEmpty(_weightsPath) || !File.Exists(_weightsPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_weightsPath);
            var data = JsonSerializer.Deserialize<NeuralWeightsData>(json);
            // Validate standardization arrays: both must be null or both must have FeatureCount length.
            // Stale files with mismatched lengths would crash StandardizeSingleVector at scoring time.
            var hasValidStandardization = data is null
                || (data.FeatureMeans is null && data.FeatureStdDevs is null)
                || (data.FeatureMeans is { Length: CandidateFeatures.FeatureCount }
                    && data.FeatureStdDevs is { Length: CandidateFeatures.FeatureCount });

            if (data is not null
                && hasValidStandardization
                && data.Version == CurrentWeightsVersion
                && data.WeightsIH?.Length == Hidden1Size * CandidateFeatures.FeatureCount
                && data.BiasH1 is { Length: Hidden1Size }
                && data.WeightsH1H2?.Length == Hidden2Size * Hidden1Size
                && data.BiasH2 is { Length: Hidden2Size }
                && data.WeightsH2H3?.Length == Hidden3Size * Hidden2Size
                && data.BiasH3 is { Length: Hidden3Size }
                && data.WeightsH3O is { Length: Hidden3Size })
            {
                _weightsIH = data.WeightsIH;
                _biasH1 = data.BiasH1;
                _weightsH1H2 = data.WeightsH1H2;
                _biasH2 = data.BiasH2;
                _weightsH2H3 = data.WeightsH2H3;
                _biasH3 = data.BiasH3;
                _weightsH3O = data.WeightsH3O;
                _biasOutput = data.BiasOutput;
                _featureMeans = data.FeatureMeans;
                _featureStdDevs = data.FeatureStdDevs;
                _trainingGeneration = data.TrainingGeneration;
                _adamTimestep = 0;
            }
            else if (data is not null)
            {
                _logger?.LogWarning(
                    "NeuralScoringStrategy: Discarding persisted weights (version={FileVersion}, expected={ExpectedVersion}). Resetting to defaults",
                    data.Version,
                    CurrentWeightsVersion);
            }
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to load weights");
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to parse weights");
        }
    }

    /// <summary>Persists current weights to disk atomically. Must be called under write lock or during init.</summary>
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

            var data = new NeuralWeightsData
            {
                WeightsIH = (double[])_weightsIH.Clone(),
                BiasH1 = (double[])_biasH1.Clone(),
                WeightsH1H2 = (double[])_weightsH1H2.Clone(),
                BiasH2 = (double[])_biasH2.Clone(),
                WeightsH2H3 = (double[])_weightsH2H3.Clone(),
                BiasH3 = (double[])_biasH3.Clone(),
                WeightsH3O = (double[])_weightsH3O.Clone(),
                BiasOutput = _biasOutput,
                FeatureMeans = _featureMeans is not null ? (double[])_featureMeans.Clone() : null,
                FeatureStdDevs = _featureStdDevs is not null ? (double[])_featureStdDevs.Clone() : null,
                TrainingGeneration = _trainingGeneration,
                UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Version = CurrentWeightsVersion
            };
            var json = JsonSerializer.Serialize(data, SerializerOptions);

            var tempPath = _weightsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _weightsPath, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to save weights");
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to serialize weights");
        }
    }

    /// <summary>
    ///     Logs per-feature importance based on input→hidden1 weight L2 norms.
    ///     Importance[f] = sqrt(Σ_j weightsIH[j, f]²) — measures how strongly
    ///     each input feature drives hidden layer activations.
    ///     Must be called under write lock.
    /// </summary>
    private void LogFeatureImportance(int inputSize)
    {
        if (_logger is null || !_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var featureNames = Enum.GetNames<FeatureIndex>();
        var importances = new double[inputSize];

        for (var f = 0; f < inputSize; f++)
        {
            var sumSq = 0.0;
            for (var j = 0; j < Hidden1Size; j++)
            {
                var w = _weightsIH[(j * inputSize) + f];
                sumSq += w * w;
            }

            importances[f] = Math.Sqrt(sumSq);
        }

        var ranked = new (string Name, double Importance)[inputSize];
        for (var i = 0; i < inputSize; i++)
        {
            ranked[i] = (i < featureNames.Length ? featureNames[i] : $"Feature{i}", importances[i]);
        }

        Array.Sort(ranked, (a, b) => b.Importance.CompareTo(a.Importance));

        var parts = new string[ranked.Length];
        for (var i = 0; i < ranked.Length; i++)
        {
            parts[i] = string.Format(CultureInfo.InvariantCulture, "{0}={1:F4}", ranked[i].Name, ranked[i].Importance);
        }

        _logger.LogDebug("NeuralScoringStrategy feature importance (L2 norm): {FeatureImportance}", string.Join(", ", parts));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _rwLock.Dispose();
    }

    /// <summary>Serializable container for persisted neural network weights.</summary>
    internal sealed class NeuralWeightsData
    {
        /// <summary>Gets or sets the input→hidden1 weights [Hidden1Size × InputSize].</summary>
        public double[] WeightsIH { get; set; } = [];

        /// <summary>Gets or sets the hidden1 biases [Hidden1Size].</summary>
        public double[] BiasH1 { get; set; } = [];

        /// <summary>Gets or sets the hidden1→hidden2 weights [Hidden2Size × Hidden1Size].</summary>
        public double[] WeightsH1H2 { get; set; } = [];

        /// <summary>Gets or sets the hidden2 biases [Hidden2Size].</summary>
        public double[] BiasH2 { get; set; } = [];

        /// <summary>Gets or sets the hidden2→hidden3 weights [Hidden3Size × Hidden2Size].</summary>
        public double[] WeightsH2H3 { get; set; } = [];

        /// <summary>Gets or sets the hidden3 biases [Hidden3Size].</summary>
        public double[] BiasH3 { get; set; } = [];

        /// <summary>Gets or sets the hidden3→output weights [Hidden3Size].</summary>
        public double[] WeightsH3O { get; set; } = [];

        /// <summary>Gets or sets the output bias.</summary>
        public double BiasOutput { get; set; }

        /// <summary>Gets or sets the per-feature means for Z-score standardization.</summary>
        public double[]? FeatureMeans { get; set; }

        /// <summary>Gets or sets the per-feature standard deviations for Z-score standardization.</summary>
        public double[]? FeatureStdDevs { get; set; }

        /// <summary>Gets or sets the training generation counter.</summary>
        public int TrainingGeneration { get; set; }

        /// <summary>Gets or sets the ISO 8601 timestamp of the last update.</summary>
        public string UpdatedAt { get; set; } = string.Empty;

        /// <summary>Gets or sets the schema version.</summary>
        public int Version { get; set; }
    }
}
