using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Neural network scoring strategy using a single-hidden-layer MLP (Multi-Layer Perceptron).
///     Learns non-linear feature interactions from user watch history via backpropagation.
///     Architecture: 18 inputs → 8 hidden (ReLU) → 1 output (Sigmoid) = 161 parameters.
///     Optimized for NAS/Docker with limited hardware: zero-allocation scoring path,
///     pre-allocated training buffers, ~130 FP multiplications per score.
///     No external ML dependencies — pure C# implementation.
/// </summary>
/// <remarks>
///     Training uses Adam optimizer with L2 regularization, Z-score feature standardization,
///     Xavier weight initialization, temporal sample weighting, and early stopping.
///     Genre-mismatch penalties are NOT applied here — handled centrally by the ensemble layer.
///     Weights are persisted to disk so they survive server restarts.
/// </remarks>
public sealed class NeuralScoringStrategy : IScoringStrategy, ITrainableStrategy
{
    /// <summary>Number of neurons in the hidden layer.</summary>
    internal const int HiddenSize = 8;

    /// <summary>Default learning rate for Adam optimizer.</summary>
    internal const double DefaultLearningRate = 0.005;

    /// <summary>L2 regularization strength (weight decay).</summary>
    internal const double L2Lambda = 0.001;

    /// <summary>Adam β1 (first moment exponential decay rate).</summary>
    internal const double AdamBeta1 = 0.9;

    /// <summary>Adam β2 (second moment exponential decay rate).</summary>
    internal const double AdamBeta2 = 0.999;

    /// <summary>Adam ε for numerical stability.</summary>
    internal const double AdamEpsilon = 1e-8;

    /// <summary>Maximum training epochs per <see cref="Train"/> call.</summary>
    internal const int MaxTrainingEpochs = 50;

    /// <summary>Minimum training examples required before training runs.</summary>
    internal const int MinTrainingExamples = 8;

    /// <summary>Consecutive epochs without improvement before early stopping triggers.</summary>
    internal const int EarlyStoppingPatience = 5;

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
    internal const int CurrentWeightsVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    // Readonly fields first (SA1214)
    private readonly ILogger? _logger;
    private readonly double[] _scratchHiddenAct = new double[HiddenSize];
    private readonly double[] _scratchHiddenPre = new double[HiddenSize];
    private readonly object _syncRoot = new();
    private readonly string? _weightsPath;

    // Non-readonly fields
    private int _adamTimestep;
    private double[] _biasHidden;
    private double _biasOutput;
    private double[]? _featureMeans;
    private double[]? _featureStdDevs;
    private double _lastValidationLoss = double.NaN;
    private double[]? _mBH;
    private double _mBO;
    private double[]? _mWH;
    private double[]? _mWO;
    private int _trainingGeneration;
    private double[]? _vBH;
    private double _vBO;
    private double[]? _vWH;
    private double[]? _vWO;
    private double[] _weightsHidden;
    private double[] _weightsOutput;

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
        _weightsHidden = new double[HiddenSize * inputSize];
        _biasHidden = new double[HiddenSize];
        _weightsOutput = new double[HiddenSize];
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

    /// <summary>Gets a copy of the hidden layer weights (for testing).</summary>
    internal double[] CurrentWeightsHidden
    {
        get
        {
            lock (_syncRoot)
            {
                return (double[])_weightsHidden.Clone();
            }
        }
    }

    /// <summary>Gets a copy of the output layer weights (for testing).</summary>
    internal double[] CurrentWeightsOutput
    {
        get
        {
            lock (_syncRoot)
            {
                return (double[])_weightsOutput.Clone();
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
        var vector = new double[CandidateFeatures.FeatureCount];
        features.WriteToVector(vector);

        lock (_syncRoot)
        {
            if (_featureMeans is not null && _featureStdDevs is not null)
            {
                LearnedScoringStrategy.StandardizeSingleVector(vector, _featureMeans, _featureStdDevs);
            }

            // Uses pre-allocated scratch buffers for zero-allocation scoring.
            // Safe because we're under _syncRoot — no concurrent access.
            return ForwardPass(
                vector,
                _weightsHidden,
                _biasHidden,
                _weightsOutput,
                _biasOutput,
                _scratchHiddenPre,
                _scratchHiddenAct);
        }
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        var vector = new double[CandidateFeatures.FeatureCount];
        features.WriteToVector(vector);

        lock (_syncRoot)
        {
            if (_featureMeans is not null && _featureStdDevs is not null)
            {
                LearnedScoringStrategy.StandardizeSingleVector(vector, _featureMeans, _featureStdDevs);
            }

            // Must allocate fresh buffers here (not shared scratch) because the
            // hiddenPre values are needed after ForwardPass for gradient attribution.
            var hiddenPre = new double[HiddenSize];
            var hiddenAct = new double[HiddenSize];
            var score = ForwardPass(
                vector,
                _weightsHidden,
                _biasHidden,
                _weightsOutput,
                _biasOutput,
                hiddenPre,
                hiddenAct);

            // Input-gradient attribution: contribution[i] = Σ_h(outputW[h] · reluGrad[h] · hiddenW[h,i]) · input[i]
            var inputSize = CandidateFeatures.FeatureCount;
            var attr = new double[inputSize];
            for (var h = 0; h < HiddenSize; h++)
            {
                if (hiddenPre[h] <= 0)
                {
                    continue;
                }

                var outW = _weightsOutput[h];
                var baseIdx = h * inputSize;
                for (var i = 0; i < inputSize; i++)
                {
                    attr[i] += outW * _weightsHidden[baseIdx + i] * vector[i];
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

        lock (_syncRoot)
        {
            EnsureAdamState(inputSize);

            var valCount = Math.Max(MinValidationExamples, (int)(examples.Count * ValidationSplitRatio));
            valCount = Math.Min(valCount, examples.Count - MinTrainingExamples);
            var useEarlyStopping = valCount >= MinValidationExamples
                && examples.Count - valCount >= MinTrainingExamples;

            // Deterministic seed varies by generation to prevent identical train/val splits
            // across successive Train() calls while keeping results reproducible per generation.
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

            var bestWH = (double[])_weightsHidden.Clone();
            var bestBH = (double[])_biasHidden.Clone();
            var bestWO = (double[])_weightsOutput.Clone();
            var bestBO = _biasOutput;

            var hPre = new double[HiddenSize];
            var hAct = new double[HiddenSize];
            var hErr = new double[HiddenSize];

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
                        _weightsHidden,
                        _biasHidden,
                        _weightsOutput,
                        _biasOutput,
                        hPre,
                        hAct);

                    // Apply sigmoid derivative for correct backpropagation gradient:
                    // dL/dz = (pred - label) × sigmoid'(z) × sampleWeight
                    // where sigmoid'(z) = pred × (1 - pred)
                    var outErr = (pred - examples[idx].Label) * pred * (1.0 - pred) * sw;

                    _adamTimestep++;
                    var bc1 = 1.0 - Math.Pow(AdamBeta1, _adamTimestep);
                    var bc2 = 1.0 - Math.Pow(AdamBeta2, _adamTimestep);

                    // Output layer Adam update
                    for (var h = 0; h < HiddenSize; h++)
                    {
                        var g = (outErr * hAct[h]) + (L2Lambda * _weightsOutput[h]);
                        _mWO![h] = (AdamBeta1 * _mWO[h]) + ((1 - AdamBeta1) * g);
                        _vWO![h] = (AdamBeta2 * _vWO[h]) + ((1 - AdamBeta2) * g * g);
                        _weightsOutput[h] -= DefaultLearningRate * (_mWO[h] / bc1) / (Math.Sqrt(_vWO[h] / bc2) + AdamEpsilon);
                        _weightsOutput[h] = Math.Clamp(_weightsOutput[h], -WeightClamp, WeightClamp);
                    }

                    {
                        var g = outErr;
                        _mBO = (AdamBeta1 * _mBO) + ((1 - AdamBeta1) * g);
                        _vBO = (AdamBeta2 * _vBO) + ((1 - AdamBeta2) * g * g);
                        _biasOutput -= DefaultLearningRate * (_mBO / bc1) / (Math.Sqrt(_vBO / bc2) + AdamEpsilon);
                        _biasOutput = Math.Clamp(_biasOutput, -WeightClamp, WeightClamp);
                    }

                    // Hidden layer error (backprop through ReLU)
                    for (var h = 0; h < HiddenSize; h++)
                    {
                        hErr[h] = hPre[h] > 0 ? outErr * _weightsOutput[h] : 0.0;
                    }

                    // Hidden layer Adam update
                    for (var h = 0; h < HiddenSize; h++)
                    {
                        var bIdx = h * inputSize;
                        for (var i = 0; i < inputSize; i++)
                        {
                            var p = bIdx + i;
                            var g = (hErr[h] * vec[i]) + (L2Lambda * _weightsHidden[p]);
                            _mWH![p] = (AdamBeta1 * _mWH[p]) + ((1 - AdamBeta1) * g);
                            _vWH![p] = (AdamBeta2 * _vWH[p]) + ((1 - AdamBeta2) * g * g);
                            _weightsHidden[p] -= DefaultLearningRate * (_mWH[p] / bc1) / (Math.Sqrt(_vWH[p] / bc2) + AdamEpsilon);
                            _weightsHidden[p] = Math.Clamp(_weightsHidden[p], -WeightClamp, WeightClamp);
                        }

                        {
                            var g = hErr[h] + (L2Lambda * _biasHidden[h]);
                            _mBH![h] = (AdamBeta1 * _mBH[h]) + ((1 - AdamBeta1) * g);
                            _vBH![h] = (AdamBeta2 * _vBH[h]) + ((1 - AdamBeta2) * g * g);
                            _biasHidden[h] -= DefaultLearningRate * (_mBH[h] / bc1) / (Math.Sqrt(_vBH[h] / bc2) + AdamEpsilon);
                            _biasHidden[h] = Math.Clamp(_biasHidden[h], -WeightClamp, WeightClamp);
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
                        Array.Copy(_weightsHidden, bestWH, _weightsHidden.Length);
                        Array.Copy(_biasHidden, bestBH, _biasHidden.Length);
                        Array.Copy(_weightsOutput, bestWO, _weightsOutput.Length);
                        bestBO = _biasOutput;
                    }
                    else
                    {
                        patience++;
                        if (patience >= EarlyStoppingPatience)
                        {
                            Array.Copy(bestWH, _weightsHidden, _weightsHidden.Length);
                            Array.Copy(bestBH, _biasHidden, _biasHidden.Length);
                            Array.Copy(bestWO, _weightsOutput, _weightsOutput.Length);
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
        }

        TrySaveWeights();
        return true;
    }

    /// <summary>
    ///     MLP forward pass: input → hidden (ReLU) → output (Sigmoid).
    ///     Uses pre-allocated buffers for hidden activations to avoid allocation.
    /// </summary>
    /// <param name="input">Input feature vector [InputSize].</param>
    /// <param name="wH">Hidden weights [HiddenSize × InputSize] row-major.</param>
    /// <param name="bH">Hidden biases [HiddenSize].</param>
    /// <param name="wO">Output weights [HiddenSize].</param>
    /// <param name="bO">Output bias scalar.</param>
    /// <param name="hiddenPre">Pre-allocated buffer for pre-activation values [HiddenSize].</param>
    /// <param name="hiddenAct">Pre-allocated buffer for post-activation values [HiddenSize].</param>
    /// <returns>Output score in [0, 1] via sigmoid.</returns>
    internal static double ForwardPass(
        double[] input,
        double[] wH,
        double[] bH,
        double[] wO,
        double bO,
        double[] hiddenPre,
        double[] hiddenAct)
    {
        var inputSize = input.Length;

        for (var h = 0; h < HiddenSize; h++)
        {
            var sum = bH[h];
            var baseIdx = h * inputSize;
            for (var i = 0; i < inputSize; i++)
            {
                sum += wH[baseIdx + i] * input[i];
            }

            hiddenPre[h] = sum;
            hiddenAct[h] = sum > 0 ? sum : 0.0;
        }

        var outputZ = bO;
        for (var h = 0; h < HiddenSize; h++)
        {
            outputZ += wO[h] * hiddenAct[h];
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
    ///     Hidden weights ~ U(-limit, limit) where limit = sqrt(6 / (fan_in + fan_out)).
    /// </summary>
    private void InitializeXavier(int inputSize)
    {
        var rng = new Random(42);

        var limitH = Math.Sqrt(6.0 / (inputSize + HiddenSize));
        for (var i = 0; i < _weightsHidden.Length; i++)
        {
            _weightsHidden[i] = (rng.NextDouble() * 2.0 * limitH) - limitH;
        }

        var limitO = Math.Sqrt(6.0 / (HiddenSize + 1));
        for (var i = 0; i < _weightsOutput.Length; i++)
        {
            _weightsOutput[i] = (rng.NextDouble() * 2.0 * limitO) - limitO;
        }

        Array.Clear(_biasHidden);
    }

    /// <summary>
    ///     Ensures Adam optimizer moment arrays are allocated.
    ///     Only allocates once; subsequent calls are no-ops.
    /// </summary>
    private void EnsureAdamState(int inputSize)
    {
        var whLen = HiddenSize * inputSize;
        if (_mWH is not null && _mWH.Length == whLen)
        {
            return;
        }

        _mWH = new double[whLen];
        _vWH = new double[whLen];
        _mBH = new double[HiddenSize];
        _vBH = new double[HiddenSize];
        _mWO = new double[HiddenSize];
        _vWO = new double[HiddenSize];
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
        var hPre = new double[HiddenSize];
        var hAct = new double[HiddenSize];

        foreach (var idx in indices)
        {
            var pred = ForwardPass(
                vectors[idx],
                _weightsHidden,
                _biasHidden,
                _weightsOutput,
                _biasOutput,
                hPre,
                hAct);
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
            if (data is not null
                && data.Version == CurrentWeightsVersion
                && data.WeightsHidden?.Length == HiddenSize * CandidateFeatures.FeatureCount
                && data.BiasHidden is { Length: HiddenSize }
                && data.WeightsOutput is { Length: HiddenSize })
            {
                _weightsHidden = data.WeightsHidden;
                _biasHidden = data.BiasHidden;
                _weightsOutput = data.WeightsOutput;
                _biasOutput = data.BiasOutput;
                _featureMeans = data.FeatureMeans;
                _featureStdDevs = data.FeatureStdDevs;
                _trainingGeneration = data.TrainingGeneration;

                // Reset Adam timestep: the moment arrays (m/v) are NOT persisted,
                // so restoring a high timestep with zero moments would cause incorrect
                // bias correction factors (bc1/bc2 ≈ 1.0 with zero numerators).
                // Starting fresh ensures Adam's adaptive learning rate works correctly.
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

    /// <summary>Persists current weights to disk atomically.</summary>
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

            string json;
            lock (_syncRoot)
            {
                var data = new NeuralWeightsData
                {
                    WeightsHidden = (double[])_weightsHidden.Clone(),
                    BiasHidden = (double[])_biasHidden.Clone(),
                    WeightsOutput = (double[])_weightsOutput.Clone(),
                    BiasOutput = _biasOutput,
                    FeatureMeans = _featureMeans is not null ? (double[])_featureMeans.Clone() : null,
                    FeatureStdDevs = _featureStdDevs is not null ? (double[])_featureStdDevs.Clone() : null,
                    TrainingGeneration = _trainingGeneration,
                    AdamTimestep = _adamTimestep,
                    UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Version = CurrentWeightsVersion
                };
                json = JsonSerializer.Serialize(data, SerializerOptions);
            }

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

    /// <summary>Serializable container for persisted neural network weights.</summary>
    internal sealed class NeuralWeightsData
    {
        /// <summary>Gets or sets the hidden layer weights [HiddenSize × InputSize].</summary>
        public double[] WeightsHidden { get; set; } = [];

        /// <summary>Gets or sets the hidden layer biases [HiddenSize].</summary>
        public double[] BiasHidden { get; set; } = [];

        /// <summary>Gets or sets the output layer weights [HiddenSize].</summary>
        public double[] WeightsOutput { get; set; } = [];

        /// <summary>Gets or sets the output bias.</summary>
        public double BiasOutput { get; set; }

        /// <summary>Gets or sets the per-feature means for Z-score standardization.</summary>
        public double[]? FeatureMeans { get; set; }

        /// <summary>Gets or sets the per-feature standard deviations for Z-score standardization.</summary>
        public double[]? FeatureStdDevs { get; set; }

        /// <summary>Gets or sets the training generation counter.</summary>
        public int TrainingGeneration { get; set; }

        /// <summary>Gets or sets the Adam optimizer timestep counter.</summary>
        public int AdamTimestep { get; set; }

        /// <summary>Gets or sets the ISO 8601 timestamp of the last update.</summary>
        public string UpdatedAt { get; set; } = string.Empty;

        /// <summary>Gets or sets the schema version.</summary>
        public int Version { get; set; }
    }
}