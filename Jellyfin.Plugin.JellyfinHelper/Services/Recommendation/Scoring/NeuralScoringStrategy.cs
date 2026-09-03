using System;
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
///     Neural scoring strategy: a four-hidden-layer MLP learning non-linear feature interactions from watch history via backpropagation.
/// </summary>
/// <remarks>
///     Training uses Adam with L2 regularization, Z-score standardization, He/Xavier init, temporal sample weighting, dropout, and early stopping.
/// </remarks>
public sealed class NeuralScoringStrategy : IScoringStrategy, ITrainableStrategy, IDisposable
{
    /// <summary>
    ///     Number of neurons in the first hidden layer.
    ///     ~2× InputSize (38->76) - best-practice expansion factor for tabular MLPs.
    /// </summary>
    internal const int Hidden1Size = 76;

    /// <summary>
    ///     Neurons in the second hidden layer. 96, deliberately WIDER than Hidden1 so the model can compose high-order feature interactions (genre×critic, people×genre) instead of being forced through an early bottleneck.
    /// </summary>
    internal const int Hidden2Size = 96;

    /// <summary>
    ///     Number of neurons in the third hidden layer.
    ///     48 - half of Hidden2, provides the compression stage.
    /// </summary>
    internal const int Hidden3Size = 48;

    /// <summary>
    ///     Number of neurons in the fourth (final) hidden layer. 24 - enough capacity to encode the final feature combinations feeding into the single sigmoid output neuron.
    /// </summary>
    internal const int Hidden4Size = 24;

    /// <summary>Default learning rate for Adam optimizer.</summary>
    internal const double DefaultLearningRate = 0.005;

    /// <summary>L2 regularization. Higher value counters larger capacity.</summary>
    internal const double L2Lambda = 0.004;

    /// <summary>Adam β1 (first moment exponential decay rate).</summary>
    internal const double AdamBeta1 = 0.9;

    /// <summary>Adam β2 (second moment exponential decay rate).</summary>
    internal const double AdamBeta2 = 0.999;

    /// <summary>Adam ε for numerical stability.</summary>
    internal const double AdamEpsilon = 1e-8;

    /// <summary>Maximum training epochs per <see cref="Train(IReadOnlyList{TrainingExample})"/> call.</summary>
    internal const int MaxTrainingEpochs = 50;

    /// <summary>Minimum examples to train. Higher capacity needs more data.</summary>
    internal const int MinTrainingExamples = 30;

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

    /// <summary>
    ///     Bernoulli dropout keep-probability for hidden activations during training. 0.8 (20% drop) is a mid-range choice for small tabular MLPs; small nets prefer light regularization to preserve capacity.
    /// </summary>
    internal const double DropoutKeepProbability = 0.8;

    /// <summary>Minimum examples before dropout. Protects tiny datasets.</summary>
    internal const int MinExamplesForDropout = 50;

    /// <summary>
    ///     Schema version for persisted weights. A set whose array lengths no longer match the current layer sizes (feature-count or hidden-size change) is discarded on load: the load path warns and resets to defaults so the next training run rebuilds from scratch.
    /// </summary>
    // The recommendation-review feature-value changes do not alter feature count or layer
    // sizes, so array-length validation alone would accept pre-change weights. They ride on the v3 bump
    internal const int CurrentWeightsVersion = 3;

    /// <summary>
    ///     JSON options for weight persistence. Compact (non-indented) output cuts the file to ~a third with no information loss; weights are machine-read only.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ILogger? _logger;
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
    private readonly Lock _syncRoot = new();
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
    [ThreadStatic]
    private static double[]? _tlsH4Pre;
    [ThreadStatic]
    private static double[]? _tlsH4Act;

    /// <summary>
    ///     Thread-local scratch buffer for the input feature vector on the Score() path. Avoids a heap allocation per scored candidate; safe because Score() fully overwrites the buffer via WriteToVector before reading it.
    /// </summary>
    [ThreadStatic]
    private static double[]? _tlsInput;

    private int _adamTimestep;
    private double[] _biasH1;
    private double[] _biasH2;
    private double[] _biasH3;
    private double[] _biasH4;
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
    private double[]? _mBH4;
    private double _mBO;
    private double[]? _mWH1H2;
    private double[]? _mWH2H3;
    private double[]? _mWH3H4;
    private double[]? _mWH4O;
    private double[]? _mWIH;
    private int _trainingGeneration;
    private double[]? _vBH1;
    private double[]? _vBH2;
    private double[]? _vBH3;
    private double[]? _vBH4;
    private double _vBO;
    private double[]? _vWH1H2;
    private double[]? _vWH2H3;
    private double[]? _vWH3H4;
    private double[]? _vWH4O;
    private double[]? _vWIH;
    private double[] _weightsH1H2;
    private double[] _weightsH2H3;
    private double[] _weightsH3H4;
    private double[] _weightsH4O;
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
        _weightsH3H4 = new double[Hidden4Size * Hidden3Size];
        _biasH4 = new double[Hidden4Size];
        _weightsH4O = new double[Hidden4Size];
        _biasOutput = 0.0;

        InitializeWeights(inputSize);
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

    /// <summary>Gets a copy of the input->hidden1 layer weights (for testing).</summary>
    /// <returns>A defensive copy of the input->hidden1 layer weights.</returns>
    internal double[] GetCurrentWeightsHidden()
    {
        try
        {
            _rwLock.EnterReadLock();
            return (double[])_weightsIH.Clone();
        }
        finally
        {
            if (_rwLock.IsReadLockHeld)
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>Gets a copy of the hidden4->output layer weights (for testing).</summary>
    /// <returns>A defensive copy of the hidden4->output layer weights.</returns>
    internal double[] GetCurrentWeightsOutput()
    {
        try
        {
            _rwLock.EnterReadLock();
            return (double[])_weightsH4O.Clone();
        }
        finally
        {
            if (_rwLock.IsReadLockHeld)
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>Gets a copy of the hidden1->hidden2 layer weights (for testing).</summary>
    /// <returns>A defensive copy of the hidden1->hidden2 layer weights.</returns>
    internal double[] GetCurrentWeightsH1H2()
    {
        try
        {
            _rwLock.EnterReadLock();
            return (double[])_weightsH1H2.Clone();
        }
        finally
        {
            if (_rwLock.IsReadLockHeld)
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>Gets a copy of the hidden2->hidden3 layer weights (for testing).</summary>
    /// <returns>A defensive copy of the hidden2->hidden3 layer weights.</returns>
    internal double[] GetCurrentWeightsH2H3()
    {
        try
        {
            _rwLock.EnterReadLock();
            return (double[])_weightsH2H3.Clone();
        }
        finally
        {
            if (_rwLock.IsReadLockHeld)
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>Gets a copy of the hidden3->hidden4 layer weights (for testing).</summary>
    /// <returns>A defensive copy of the hidden3->hidden4 layer weights.</returns>
    internal double[] GetCurrentWeightsH3H4()
    {
        try
        {
            _rwLock.EnterReadLock();
            return (double[])_weightsH3H4.Clone();
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
    public double Score(CandidateFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        if (_disposed)
        {
            return 0.5;
        }

        // Reuse a thread-local input buffer to avoid a heap allocation per candidate. WriteToVector
        // fully overwrites every element, so no stale data leaks between calls.
        _tlsInput ??= new double[CandidateFeatures.FeatureCount];
        var vector = _tlsInput;
        features.WriteToVector(vector);

        _tlsH1Pre ??= new double[Hidden1Size];
        _tlsH1Act ??= new double[Hidden1Size];
        _tlsH2Pre ??= new double[Hidden2Size];
        _tlsH2Act ??= new double[Hidden2Size];
        _tlsH3Pre ??= new double[Hidden3Size];
        _tlsH3Act ??= new double[Hidden3Size];
        _tlsH4Pre ??= new double[Hidden4Size];
        _tlsH4Act ??= new double[Hidden4Size];

        // Clear scratch buffers so stale data from a previous invocation on this thread cannot bleed
        // into the current forward pass.
        Array.Clear(_tlsH1Pre, 0, _tlsH1Pre.Length);
        Array.Clear(_tlsH1Act, 0, _tlsH1Act.Length);
        Array.Clear(_tlsH2Pre, 0, _tlsH2Pre.Length);
        Array.Clear(_tlsH2Act, 0, _tlsH2Act.Length);
        Array.Clear(_tlsH3Pre, 0, _tlsH3Pre.Length);
        Array.Clear(_tlsH3Act, 0, _tlsH3Act.Length);
        Array.Clear(_tlsH4Pre, 0, _tlsH4Pre.Length);
        Array.Clear(_tlsH4Act, 0, _tlsH4Act.Length);

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
                _weightsH3H4,
                _biasH4,
                _weightsH4O,
                _biasOutput,
                _tlsH1Pre,
                _tlsH1Act,
                _tlsH2Pre,
                _tlsH2Act,
                _tlsH3Pre,
                _tlsH3Act,
                _tlsH4Pre,
                _tlsH4Act);

            return ScoringHelper.GuardScore(result);
        }
        finally
        {
            ReleaseReadLockSafely();
        }
    }

    /// <summary>
    ///     Scores a raw feature vector directly without CandidateFeatures allocation. Used by NeuralFeatureImportance for permutation importance where features are raw arrays.
    /// </summary>
    /// <param name="vector">
    ///     A pre-computed feature vector of length <see cref="CandidateFeatures.FeatureCount"/>.
    ///     WARNING: mutated in-place when standardization is active; pass a copy to preserve the original.
    /// </param>
    /// <returns>A score between 0.0 and 1.0.</returns>
    internal double ScoreVector(double[] vector)
    {
        if (_disposed)
        {
            return 0.5;
        }

        _tlsH1Pre ??= new double[Hidden1Size];
        _tlsH1Act ??= new double[Hidden1Size];
        _tlsH2Pre ??= new double[Hidden2Size];
        _tlsH2Act ??= new double[Hidden2Size];
        _tlsH3Pre ??= new double[Hidden3Size];
        _tlsH3Act ??= new double[Hidden3Size];
        _tlsH4Pre ??= new double[Hidden4Size];
        _tlsH4Act ??= new double[Hidden4Size];

        // Clear scratch buffers so stale data from a previous invocation on this thread cannot bleed
        // into the current forward pass.
        Array.Clear(_tlsH1Pre, 0, _tlsH1Pre.Length);
        Array.Clear(_tlsH1Act, 0, _tlsH1Act.Length);
        Array.Clear(_tlsH2Pre, 0, _tlsH2Pre.Length);
        Array.Clear(_tlsH2Act, 0, _tlsH2Act.Length);
        Array.Clear(_tlsH3Pre, 0, _tlsH3Pre.Length);
        Array.Clear(_tlsH3Act, 0, _tlsH3Act.Length);
        Array.Clear(_tlsH4Pre, 0, _tlsH4Pre.Length);
        Array.Clear(_tlsH4Act, 0, _tlsH4Act.Length);

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
                _weightsH3H4,
                _biasH4,
                _weightsH4O,
                _biasOutput,
                _tlsH1Pre,
                _tlsH1Act,
                _tlsH2Pre,
                _tlsH2Act,
                _tlsH3Pre,
                _tlsH3Act,
                _tlsH4Pre,
                _tlsH4Act);

            return ScoringHelper.GuardScore(result);
        }
        finally
        {
            ReleaseReadLockSafely();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Full input-gradient attribution through all four hidden layers, O(H4·H3·H2·H1·InputSize).
    /// </remarks>
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

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
            var h4Pre = new double[Hidden4Size];
            var h4Act = new double[Hidden4Size];
            var rawScore = ForwardPass(
                vector,
                _weightsIH,
                _biasH1,
                _weightsH1H2,
                _biasH2,
                _weightsH2H3,
                _biasH3,
                _weightsH3H4,
                _biasH4,
                _weightsH4O,
                _biasOutput,
                h1Pre,
                h1Act,
                h2Pre,
                h2Act,
                h3Pre,
                h3Act,
                h4Pre,
                h4Act);

            var score = ScoringHelper.GuardScore(rawScore);

            // Input-gradient attribution through all four hidden layers
            var inputSize = CandidateFeatures.FeatureCount;
            var attr = ComputeInputAttribution(inputSize, h1Pre, h2Pre, h3Pre, h4Pre);

            var interactionContrib = ComputeInteractionContribution(attr);

            return BuildScoreExplanation(score, attr, interactionContrib);
        }
        finally
        {
            ReleaseReadLockSafely();
        }
    }

    /// <summary>
    ///     Computes the input-gradient attribution vector through all four hidden layers. Extracted verbatim from ScoreWithExplanation; the ReLU gating, weight indexing, and accumulation order are unchanged.
    /// </summary>
    /// <param name="inputSize">Number of input features (row stride for the input weights).</param>
    /// <param name="h1Pre">Hidden1 pre-activation values.</param>
    /// <param name="h2Pre">Hidden2 pre-activation values.</param>
    /// <param name="h3Pre">Hidden3 pre-activation values.</param>
    /// <param name="h4Pre">Hidden4 pre-activation values.</param>
    /// <returns>Per-input attribution vector [inputSize].</returns>
    private double[] ComputeInputAttribution(
        int inputSize,
        double[] h1Pre,
        double[] h2Pre,
        double[] h3Pre,
        double[] h4Pre)
    {
        var attr = new double[inputSize];

        for (var m = 0; m < Hidden4Size; m++)
        {
            if (h4Pre[m] <= 0)
            {
                continue;
            }

            var outW = _weightsH4O[m];
            for (var l = 0; l < Hidden3Size; l++)
            {
                if (h3Pre[l] <= 0)
                {
                    continue;
                }

                var h3h4W = _weightsH3H4[(m * Hidden3Size) + l];
                var combinedH4H3 = outW * h3h4W;
                AccumulateHidden2Attribution(attr, inputSize, combinedH4H3, l, h1Pre, h2Pre);
            }
        }

        return attr;
    }

    /// <summary>
    ///     Accumulates the hidden2-level attribution for one active (ReLU-open) hidden3 neuron.
    /// </summary>
    /// <param name="attr">Per-input attribution accumulator [inputSize].</param>
    /// <param name="inputSize">Number of input features (row stride for the input weights).</param>
    /// <param name="combinedH4H3">Pre-multiplied output×h3h4 weight product for the active path.</param>
    /// <param name="l">Active hidden3 neuron index.</param>
    /// <param name="h1Pre">Hidden1 pre-activation values.</param>
    /// <param name="h2Pre">Hidden2 pre-activation values.</param>
    private void AccumulateHidden2Attribution(
        double[] attr,
        int inputSize,
        double combinedH4H3,
        int l,
        double[] h1Pre,
        double[] h2Pre)
    {
        for (var k = 0; k < Hidden2Size; k++)
        {
            if (h2Pre[k] <= 0)
            {
                continue;
            }

            var h2h3W = _weightsH2H3[(l * Hidden2Size) + k];
            var combinedOuter = combinedH4H3 * h2h3W;
            AccumulateHidden1Attribution(attr, inputSize, combinedOuter, k, h1Pre);
        }
    }

    /// <summary>
    ///     Accumulates the hidden1-level attribution for one active (ReLU-open) hidden2 neuron.
    /// </summary>
    /// <param name="attr">Per-input attribution accumulator [inputSize].</param>
    /// <param name="inputSize">Number of input features (row stride for the input weights).</param>
    /// <param name="combinedOuter">Pre-multiplied product for the active path down to hidden2.</param>
    /// <param name="k">Active hidden2 neuron index.</param>
    /// <param name="h1Pre">Hidden1 pre-activation values.</param>
    private void AccumulateHidden1Attribution(
        double[] attr,
        int inputSize,
        double combinedOuter,
        int k,
        double[] h1Pre)
    {
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
                attr[i] += combined * _weightsIH[baseIdx + i];
            }
        }
    }

    /// <summary>
    ///     Sums the interaction-feature attribution contributions. Extracted verbatim from ScoreWithExplanation; the summed terms and order are unchanged.
    /// </summary>
    /// <param name="attr">Per-input attribution vector.</param>
    /// <returns>The combined interaction contribution.</returns>
    private static double ComputeInteractionContribution(double[] attr) =>
        attr[(int)FeatureIndex.GenreCountNormalized] +
        attr[(int)FeatureIndex.IsSeries] +
        attr[(int)FeatureIndex.GenreCriticInteraction] +
        attr[(int)FeatureIndex.GenreCollabInteraction] +
        attr[(int)FeatureIndex.CompletionRatio] +
        attr[(int)FeatureIndex.IsAbandoned] +
        attr[(int)FeatureIndex.HasInteraction] +
        attr[(int)FeatureIndex.SeriesProgressionBoost] +
        attr[(int)FeatureIndex.PopularityScore] +
        attr[(int)FeatureIndex.DayOfWeekAffinity] +
        attr[(int)FeatureIndex.HourOfDayAffinity] +
        attr[(int)FeatureIndex.IsWeekend] +
        attr[(int)FeatureIndex.TagSimilarity] +
        attr[(int)FeatureIndex.PeopleGenreInteraction] +
        attr[(int)FeatureIndex.RecencyCriticInteraction] +
        attr[(int)FeatureIndex.GenreUnderexposure] +
        attr[(int)FeatureIndex.GenreDominanceRatio] +
        attr[(int)FeatureIndex.GenreAffinityGap] +
        attr[(int)FeatureIndex.LibraryAddedRecency] +
        attr[(int)FeatureIndex.ContentNearestNeighborScore] +
        attr[(int)FeatureIndex.LanguageAffinity] +
        attr[(int)FeatureIndex.CollectionProgressionBoost] +
        attr[(int)FeatureIndex.SubtitleLanguageAffinity] +
        attr[(int)FeatureIndex.FranchiseAffinity] +
        attr[(int)FeatureIndex.ProductionLocationAffinity] +
        attr[(int)FeatureIndex.InheritedTagSimilarity] +
        attr[(int)FeatureIndex.SeriesCompletability] +
        attr[(int)FeatureIndex.WriterAffinity] +
        attr[(int)FeatureIndex.BillingWeightedPeople] +
        attr[(int)FeatureIndex.GenreStudioIdfPrior];

    /// <summary>
    ///     Builds the ScoreExplanation from the computed score and attribution. Extracted verbatim from ScoreWithExplanation; the assigned contributions are unchanged.
    /// </summary>
    /// <param name="score">The guarded final score.</param>
    /// <param name="attr">Per-input attribution vector.</param>
    /// <param name="interactionContrib">The combined interaction contribution.</param>
    /// <returns>The populated score explanation.</returns>
    private ScoreExplanation BuildScoreExplanation(double score, double[] attr, double interactionContrib) =>
        new()
        {
            FinalScore = score,
            GenreContribution = attr[(int)FeatureIndex.GenreSimilarity],
            CollaborativeContribution = attr[(int)FeatureIndex.CollaborativeScore],
            RatingContribution = attr[(int)FeatureIndex.CombinedCriticScore],
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
                attr[(int)FeatureIndex.CombinedCriticScore],
                attr[(int)FeatureIndex.UserRatingScore],
                attr[(int)FeatureIndex.RecencyScore],
                attr[(int)FeatureIndex.YearProximityScore],
                interactionContrib,
                attr[(int)FeatureIndex.PeopleSimilarity],
                attr[(int)FeatureIndex.StudioMatch]),
            StrategyName = Name
        };

    /// <summary>
    ///     Trains the MLP via backpropagation with Adam optimizer.
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

        var referenceTime = DateTime.UtcNow;
        var inputSize = CandidateFeatures.FeatureCount;

        // Pre-compute all feature vectors as RAW (unstandardized) source of truth. Standardization is
        // deferred until after the train/val split so stats come from the training split only (no leakage).
        var rawVectors = new double[examples.Count][];
        var weights = new double[examples.Count];

        for (var i = 0; i < examples.Count; i++)
        {
            rawVectors[i] = examples[i].Features.ToVector();
            weights[i] = examples[i].ComputeEffectiveWeight(referenceTime);
        }

        var useStandardization = examples.Count >= MinExamplesForStandardization;

        // Hoisted so values are readable after the write-lock finally block when publishing
        // _lastValidationLoss outside the lock (avoids nested _syncRoot-inside-write-lock).
        var capturedUseEarlyStopping = false;
        var capturedBestLoss = double.MaxValue;

        try
        {
            _rwLock.EnterWriteLock();

            _adamTimestep = 0;
            EnsureAdamState(inputSize);

            var gen = _trainingGeneration;
            _trainingGeneration++;
            var rng = new Random(42 + gen);

            var (trainIdx, valIdx, useEarlyStopping) = BuildTrainValSplit(examples.Count, rng);

            // Clone raw vectors into working copies so standardization doesn't mutate originals.
            // Compute stats from TRAINING split only to prevent validation data leakage.
            var vectors = LearnedScoringStrategy.CloneVectors(rawVectors);
            double[]? featureMeans = null;
            double[]? featureStdDevs = null;

            if (useStandardization)
            {
                var trainOnly = new double[trainIdx.Length][];
                for (var j = 0; j < trainIdx.Length; j++)
                {
                    trainOnly[j] = vectors[trainIdx[j]];
                }

                (featureMeans, featureStdDevs) = LearnedScoringStrategy.ComputeFeatureStatistics(trainOnly);
                LearnedScoringStrategy.StandardizeVectors(vectors, featureMeans, featureStdDevs);
            }

            var bestWIH = (double[])_weightsIH.Clone();
            var bestBH1 = (double[])_biasH1.Clone();
            var bestWH1H2 = (double[])_weightsH1H2.Clone();
            var bestBH2 = (double[])_biasH2.Clone();
            var bestWH2H3 = (double[])_weightsH2H3.Clone();
            var bestBH3 = (double[])_biasH3.Clone();
            var bestWH3H4 = (double[])_weightsH3H4.Clone();
            var bestBH4 = (double[])_biasH4.Clone();
            var bestWH4O = (double[])_weightsH4O.Clone();
            var bestBO = _biasOutput;

            var h1Pre = new double[Hidden1Size];
            var h1Act = new double[Hidden1Size];
            var h2Pre = new double[Hidden2Size];
            var h2Act = new double[Hidden2Size];
            var h3Pre = new double[Hidden3Size];
            var h3Act = new double[Hidden3Size];
            var h4Pre = new double[Hidden4Size];
            var h4Act = new double[Hidden4Size];
            var h1Err = new double[Hidden1Size];
            var h2Err = new double[Hidden2Size];
            var h3Err = new double[Hidden3Size];
            var h4Err = new double[Hidden4Size];

            // Bernoulli dropout masks (1 = keep, 0 = drop), kept as double so survivors rescale by 1/keep in-place (inverted dropout: train-time activations match inference magnitude).
            var h1Mask = new double[Hidden1Size];
            var h2Mask = new double[Hidden2Size];
            var h3Mask = new double[Hidden3Size];
            var h4Mask = new double[Hidden4Size];

            // Group the pre-allocated per-layer scratch buffers and the best-so-far snapshot buffers into parameter objects so the backprop/update helpers keep a small parameter list without any extra allocation (the objects wrap the same arrays already allocated above).
            var buffers = new TrainingBuffers(
                h1Pre,
                h1Act,
                h2Pre,
                h2Act,
                h3Pre,
                h3Act,
                h4Pre,
                h4Act,
                h1Err,
                h2Err,
                h3Err,
                h4Err,
                h1Mask,
                h2Mask,
                h3Mask,
                h4Mask);
            var bestWeights = new WeightSnapshot(
                bestWIH, bestBH1, bestWH1H2, bestBH2, bestWH2H3, bestBH3, bestWH3H4, bestBH4, bestWH4O);
            // Gate on the training-split size, not examples.Count: the held-out validation slice gets
            // no gradient updates, so counting it would activate dropout below the starvation threshold.
            var dropoutActive = trainIdx.Length >= MinExamplesForDropout;
            // Dedicated RNG for the dropout draw, seeded off the same generation counter as the
            // shuffle-RNG so a run stays deterministic given the persisted _trainingGeneration.
            var dropoutRng = new Random(1337 + gen);
            var dropoutInvKeep = dropoutActive ? 1.0 / DropoutKeepProbability : 1.0;

            var maxEpochs = useEarlyStopping
                ? MaxTrainingEpochs
                : Math.Min(MaxTrainingEpochs, MaxEpochsWithoutEarlyStopping);

            var epochConfig = new EpochLoopConfig(
                maxEpochs,
                inputSize,
                useEarlyStopping,
                dropoutActive ? DropoutKeepProbability : 1.0,
                dropoutInvKeep);

            var bestLoss = RunTrainingEpochs(
                epochConfig,
                trainIdx,
                valIdx,
                examples,
                vectors,
                weights,
                buffers,
                bestWeights,
                rng,
                dropoutRng,
                ref bestBO);

            // Restore the best weights whenever early stopping observed an improvement, otherwise the reported _lastValidationLoss won't match the persisted model.
            if (useEarlyStopping && bestLoss < double.MaxValue)
            {
                RestoreBestWeights(bestWeights);
                _biasOutput = bestBO;
            }

            // Report the early-stopping validation loss (a genuine held-out out-of-sample number) as the generalization estimate for the ensemble quality gate.
            _featureMeans = featureMeans;
            _featureStdDevs = featureStdDevs;

            capturedUseEarlyStopping = useEarlyStopping;
            capturedBestLoss = bestLoss;
        }
        finally
        {
            if (_rwLock.IsWriteLockHeld)
            {
                _rwLock.ExitWriteLock();
            }
        }

        // Persist weights and log feature importance outside the write lock so concurrent Score() callers (read lock) are not blocked by disk I/O or logging.
        TrySaveWeights();
        LogFeatureImportance(inputSize);

        // Compute ranking metrics outside the write lock (Score() needs read lock). Prefer the caller's held-out slice so P@K/R@K/NDCG are genuine out-of-sample numbers; fall back to the training set only when no held-out slice was passed or it is too small.
        var metricsSource = heldOutForMetrics is { Count: >= 2 } ? heldOutForMetrics : examples;
        var (pAtK, rAtK, nAtK) = RankingMetrics.ComputeAll(metricsSource, this);
        PublishTrainingMetrics(capturedUseEarlyStopping, capturedBestLoss, pAtK, rAtK, nAtK);

        // Permutation importance (debug-only, expensive: O(features x sampleSize) forward passes)
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            var importance = NeuralFeatureImportance.ComputePermutationImportance(this, examples);
            var sorted = importance.OrderByDescending(kv => Math.Abs(kv.Value));
            _logger.LogDebug(
                "NeuralScoringStrategy permutation importance: {Importance}",
                string.Join(", ", sorted.Select(kv => $"{kv.Key}={kv.Value:F4}")));
        }

        return true;
    }

    /// <summary>
    ///     Publishes the validation loss and ranking metrics under _syncRoot. Extracted verbatim from the metrics tail of Train(IReadOnlyList{TrainingExample},IReadOnlyList{TrainingExample}?); the three-case validation-loss mapping is unchanged.
    /// </summary>
    /// <param name="capturedUseEarlyStopping">Whether early stopping was active for the run.</param>
    /// <param name="capturedBestLoss">The best validation loss observed.</param>
    /// <param name="pAtK">Precision@K.</param>
    /// <param name="rAtK">Recall@K.</param>
    /// <param name="nAtK">NDCG@K.</param>
    private void PublishTrainingMetrics(
        bool capturedUseEarlyStopping,
        double capturedBestLoss,
        double pAtK,
        double rAtK,
        double nAtK)
    {
        lock (_syncRoot)
        {
            if (!capturedUseEarlyStopping)
            {
                _lastValidationLoss = double.NaN;
            }
            else if (capturedBestLoss < double.MaxValue)
            {
                _lastValidationLoss = capturedBestLoss;
            }
            else
            {
                _lastValidationLoss = EnsembleScoringStrategy.ValidationLossCeiling;
            }

            _lastPrecisionAtK = pAtK;
            _lastRecallAtK = rAtK;
            _lastNdcgAtK = nAtK;
        }
    }

    /// <summary>
    ///     Builds the shuffled train/validation index split for one training run. Extracted verbatim from Train(IReadOnlyList{TrainingExample},IReadOnlyList{TrainingExample}?); the validation-count formula, Fisher-Yates shuffle, and early-stopping gate are unchanged.
    /// </summary>
    /// <param name="exampleCount">The total number of training examples.</param>
    /// <param name="rng">The shuffle RNG (advanced in place).</param>
    /// <returns>The training indices, validation indices, and whether early stopping is enabled.</returns>
    private static (int[] TrainIdx, int[] ValIdx, bool UseEarlyStopping) BuildTrainValSplit(
        int exampleCount,
        Random rng)
    {
        var valCount = Math.Max(MinValidationExamples, (int)(exampleCount * ValidationSplitRatio));
        valCount = Math.Min(valCount, exampleCount - MinTrainingExamples);
        var useEarlyStopping = valCount >= MinValidationExamples
                               && exampleCount - valCount >= MinTrainingExamples;

        var indices = new int[exampleCount];
        for (var j = 0; j < indices.Length; j++)
        {
            indices[j] = j;
        }

        for (var j = indices.Length - 1; j > 0; j--)
        {
            var k = rng.Next(j + 1);
            (indices[j], indices[k]) = (indices[k], indices[j]);
        }

        if (useEarlyStopping)
        {
            return (indices[..^valCount], indices[^valCount..], true);
        }

        return (indices, [], false);
    }

    /// <summary>
    ///     Runs the full training epoch loop (per-epoch shuffle, per-example forward/backward/Adam step, and early-stopping bookkeeping).
    /// </summary>
    /// <param name="config">Scalar epoch-loop configuration.</param>
    /// <param name="trainIdx">Training-split indices (shuffled in place each epoch).</param>
    /// <param name="valIdx">Validation-split indices (empty when early stopping is off).</param>
    /// <param name="examples">The training examples.</param>
    /// <param name="vectors">The standardized feature vectors aligned to <paramref name="examples"/>.</param>
    /// <param name="weights">The per-example effective weights.</param>
    /// <param name="buffers">Pre-allocated per-layer scratch buffers.</param>
    /// <param name="bestWeights">Best-so-far weight snapshot buffers.</param>
    /// <param name="rng">The shuffle RNG.</param>
    /// <param name="dropoutRng">The dropout draw RNG.</param>
    /// <param name="bestBO">Best-so-far output bias (updated in place).</param>
    /// <returns>The best validation loss observed (<see cref="double.MaxValue"/> if none improved).</returns>
    private double RunTrainingEpochs(
        EpochLoopConfig config,
        int[] trainIdx,
        int[] valIdx,
        IReadOnlyList<TrainingExample> examples,
        double[][] vectors,
        double[] weights,
        TrainingBuffers buffers,
        WeightSnapshot bestWeights,
        Random rng,
        Random dropoutRng,
        ref double bestBO)
    {
        var bestLoss = double.MaxValue;
        var patience = 0;

        for (var epoch = 0; epoch < config.MaxEpochs; epoch++)
        {
            for (var j = trainIdx.Length - 1; j > 0; j--)
            {
                var k = rng.Next(j + 1);
                (trainIdx[j], trainIdx[k]) = (trainIdx[k], trainIdx[j]);
            }

            foreach (var idx in trainIdx)
            {
                TrainOnExample(idx, config, examples, vectors, weights, buffers, dropoutRng);
            }

            if (config.UseEarlyStopping && valIdx.Length > 0
                && UpdateEarlyStopping(examples, vectors, weights, valIdx, bestWeights, ref bestLoss, ref patience, ref bestBO))
            {
                break;
            }
        }

        return bestLoss;
    }

    /// <summary>
    ///     Runs the forward (dropout) pass, error backprop, and Adam weight update for a single training example.
    /// </summary>
    /// <param name="idx">The example index into <paramref name="examples"/>/<paramref name="vectors"/>.</param>
    /// <param name="config">Scalar epoch-loop configuration.</param>
    /// <param name="examples">The training examples.</param>
    /// <param name="vectors">The standardized feature vectors.</param>
    /// <param name="weights">The per-example effective weights.</param>
    /// <param name="buffers">Pre-allocated per-layer scratch buffers.</param>
    /// <param name="dropoutRng">The dropout draw RNG.</param>
    private void TrainOnExample(
        int idx,
        EpochLoopConfig config,
        IReadOnlyList<TrainingExample> examples,
        double[][] vectors,
        double[] weights,
        TrainingBuffers buffers,
        Random dropoutRng)
    {
        var sw = weights[idx];
        if (sw < MinSampleWeight)
        {
            return;
        }

        var vec = vectors[idx];

        // Dropout is applied by RE-RUNNING each hidden layer's activation through a Bernoulli mask, WITHOUT going through the (deterministic, dropout-free) ForwardPass.
        var pred = ForwardPassTraining(
            vec,
            _weightsIH,
            _biasH1,
            _weightsH1H2,
            _biasH2,
            _weightsH2H3,
            _biasH3,
            _weightsH3H4,
            _biasH4,
            _weightsH4O,
            _biasOutput,
            buffers.H1Pre,
            buffers.H1Act,
            buffers.H2Pre,
            buffers.H2Act,
            buffers.H3Pre,
            buffers.H3Act,
            buffers.H4Pre,
            buffers.H4Act,
            buffers.H1Mask,
            buffers.H2Mask,
            buffers.H3Mask,
            buffers.H4Mask,
            dropoutRng,
            config.KeepProbability,
            config.DropoutInvKeep);

        var outErr = (pred - examples[idx].Label) * pred * (1.0 - pred) * sw;

        _adamTimestep++;
        var bc1 = 1.0 - Math.Pow(AdamBeta1, _adamTimestep);
        var bc2 = 1.0 - Math.Pow(AdamBeta2, _adamTimestep);

        // Correct backprop uses the forward-pass weights for error computation; updating
        // weights first would skew gradients.
        ComputeErrorSignals(outErr, config.DropoutInvKeep, buffers);

        ApplyAdamUpdates(outErr, bc1, bc2, config.InputSize, vec, buffers);
    }

    /// <summary>
    ///     Evaluates the validation loss for one epoch and updates early-stopping bookkeeping (best loss, patience, best-weight snapshot).
    /// </summary>
    /// <param name="examples">The training examples.</param>
    /// <param name="vectors">The standardized feature vectors.</param>
    /// <param name="weights">The per-example effective weights.</param>
    /// <param name="valIdx">The validation-split indices.</param>
    /// <param name="bestWeights">Best-so-far weight snapshot buffers.</param>
    /// <param name="bestLoss">Best validation loss so far (updated in place).</param>
    /// <param name="patience">Epochs without improvement (updated in place).</param>
    /// <param name="bestBO">Best-so-far output bias (updated in place).</param>
    /// <returns><c>true</c> when patience is exhausted and training should stop.</returns>
    private bool UpdateEarlyStopping(
        IReadOnlyList<TrainingExample> examples,
        double[][] vectors,
        double[] weights,
        int[] valIdx,
        WeightSnapshot bestWeights,
        ref double bestLoss,
        ref int patience,
        ref double bestBO)
    {
        var valLoss = ComputeMseLoss(examples, vectors, weights, valIdx);
        if (valLoss < bestLoss - EarlyStoppingMinDelta)
        {
            bestLoss = valLoss;
            patience = 0;
            SnapshotBestWeights(bestWeights);
            bestBO = _biasOutput;
            return false;
        }

        patience++;
        if (patience >= EarlyStoppingPatience)
        {
            RestoreBestWeights(bestWeights);
            _biasOutput = bestBO;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Copies the current live weights/biases into the supplied best-so-far buffers.
    /// </summary>
    /// <param name="best">The best-so-far weight/bias snapshot buffers to copy into.</param>
    private void SnapshotBestWeights(WeightSnapshot best)
    {
        Array.Copy(_weightsIH, best.BestWIH, _weightsIH.Length);
        Array.Copy(_biasH1, best.BestBH1, _biasH1.Length);
        Array.Copy(_weightsH1H2, best.BestWH1H2, _weightsH1H2.Length);
        Array.Copy(_biasH2, best.BestBH2, _biasH2.Length);
        Array.Copy(_weightsH2H3, best.BestWH2H3, _weightsH2H3.Length);
        Array.Copy(_biasH3, best.BestBH3, _biasH3.Length);
        Array.Copy(_weightsH3H4, best.BestWH3H4, _weightsH3H4.Length);
        Array.Copy(_biasH4, best.BestBH4, _biasH4.Length);
        Array.Copy(_weightsH4O, best.BestWH4O, _weightsH4O.Length);
    }

    /// <summary>
    ///     Restores the best-so-far weights/biases back into the live weight arrays.
    /// </summary>
    /// <param name="best">The best-so-far weight/bias snapshot buffers to restore from.</param>
    private void RestoreBestWeights(WeightSnapshot best)
    {
        Array.Copy(best.BestWIH, _weightsIH, _weightsIH.Length);
        Array.Copy(best.BestBH1, _biasH1, _biasH1.Length);
        Array.Copy(best.BestWH1H2, _weightsH1H2, _weightsH1H2.Length);
        Array.Copy(best.BestBH2, _biasH2, _biasH2.Length);
        Array.Copy(best.BestWH2H3, _weightsH2H3, _weightsH2H3.Length);
        Array.Copy(best.BestBH3, _biasH3, _biasH3.Length);
        Array.Copy(best.BestWH3H4, _weightsH3H4, _weightsH3H4.Length);
        Array.Copy(best.BestBH4, _biasH4, _biasH4.Length);
        Array.Copy(best.BestWH4O, _weightsH4O, _weightsH4O.Length);
    }

    /// <summary>
    ///     Backpropagates the output error through all four hidden layers, filling the per-layer pre-activation error buffers (δ_pre).
    /// </summary>
    /// <param name="outErr">The output-layer error signal (δ at the sigmoid output).</param>
    /// <param name="dropoutInvKeep">Inverted-dropout scale (1 / keep, or 1.0 when dropout is inactive).</param>
    /// <param name="buffers">Pre-allocated per-layer scratch buffers (pre-activations, masks, errors).</param>
    private void ComputeErrorSignals(double outErr, double dropoutInvKeep, TrainingBuffers buffers)
    {
        // A2 dropout: a neuron with mask == 0 produced no output, so its error signal is zero (no gradient flow) and the downstream weight-update from it is zero too (activation was zero).

        // Hidden4 error (backprop through ReLU + inverted-dropout scale). With inverted dropout a = mask · relu(pre) · invKeep, so ∂a/∂pre = invKeep for a kept neuron and δ_pre = δ_a · invKeep.
        for (var k = 0; k < Hidden4Size; k++)
        {
            buffers.H4Err[k] = (buffers.H4Pre[k] > 0 && buffers.H4Mask[k] > 0)
                ? outErr * _weightsH4O[k] * dropoutInvKeep
                : 0.0;
        }

        ComputeHidden3Error(dropoutInvKeep, buffers);
        ComputeHidden2Error(dropoutInvKeep, buffers);
        ComputeHidden1Error(dropoutInvKeep, buffers);
    }

    /// <summary>
    ///     Backpropagates the hidden4 error into hidden3's pre-activation error buffer. Extracted verbatim from ComputeErrorSignals; the h3Mask[k] == 0.0 gating, weight indexing, accumulation order, and inverted-dropout scaling are unchanged.
    /// </summary>
    /// <param name="dropoutInvKeep">Inverted-dropout scale (1 / keep, or 1.0 when dropout is inactive).</param>
    /// <param name="buffers">Pre-allocated per-layer scratch buffers.</param>
    private void ComputeHidden3Error(double dropoutInvKeep, TrainingBuffers buffers)
    {
        // Hidden3 error (backprop through ReLU + inverted-dropout scale from hidden4). h4Err holds δ_pre for hidden4, so summing against the weights yields δ_a for hidden3; multiplying by invKeep converts that to δ_pre.
        for (var k = 0; k < Hidden3Size; k++)
        {
            if (buffers.H3Pre[k] <= 0 || buffers.H3Mask[k] == 0.0)
            {
                buffers.H3Err[k] = 0.0;
                continue;
            }

            var sum = 0.0;
            for (var m = 0; m < Hidden4Size; m++)
            {
                sum += buffers.H4Err[m] * _weightsH3H4[(m * Hidden3Size) + k];
            }

            buffers.H3Err[k] = sum * dropoutInvKeep;
        }
    }

    /// <summary>
    ///     Backpropagates the hidden3 error into hidden2's pre-activation error buffer. Extracted verbatim from ComputeErrorSignals; the h2Mask[k] &lt;= 0.0 gating, weight indexing, accumulation order, and inverted-dropout scaling are unchanged.
    /// </summary>
    /// <param name="dropoutInvKeep">Inverted-dropout scale (1 / keep, or 1.0 when dropout is inactive).</param>
    /// <param name="buffers">Pre-allocated per-layer scratch buffers.</param>
    private void ComputeHidden2Error(double dropoutInvKeep, TrainingBuffers buffers)
    {
        // Hidden2 layer error (backprop through ReLU + inverted-dropout scale from hidden3)
        for (var k = 0; k < Hidden2Size; k++)
        {
            if (buffers.H2Pre[k] <= 0 || buffers.H2Mask[k] <= 0.0)
            {
                buffers.H2Err[k] = 0.0;
                continue;
            }

            var sum = 0.0;
            for (var l = 0; l < Hidden3Size; l++)
            {
                sum += buffers.H3Err[l] * _weightsH2H3[(l * Hidden2Size) + k];
            }

            buffers.H2Err[k] = sum * dropoutInvKeep;
        }
    }

    /// <summary>
    ///     Backpropagates the hidden2 error into hidden1's pre-activation error buffer. Extracted verbatim from ComputeErrorSignals; the h1Mask[j] &lt;= 0.0 gating, weight indexing, accumulation order, and inverted-dropout scaling are unchanged.
    /// </summary>
    /// <param name="dropoutInvKeep">Inverted-dropout scale (1 / keep, or 1.0 when dropout is inactive).</param>
    /// <param name="buffers">Pre-allocated per-layer scratch buffers.</param>
    private void ComputeHidden1Error(double dropoutInvKeep, TrainingBuffers buffers)
    {
        // Hidden1 layer error (backprop through ReLU + inverted-dropout scale from hidden2)
        for (var j = 0; j < Hidden1Size; j++)
        {
            if (buffers.H1Pre[j] <= 0 || buffers.H1Mask[j] <= 0.0)
            {
                buffers.H1Err[j] = 0.0;
                continue;
            }

            var sum = 0.0;
            for (var k = 0; k < Hidden2Size; k++)
            {
                sum += buffers.H2Err[k] * _weightsH1H2[(k * Hidden1Size) + j];
            }

            buffers.H1Err[j] = sum * dropoutInvKeep;
        }
    }

    /// <summary>
    ///     Applies the Adam weight/bias updates for all five layers using the pre-computed error signals.
    /// </summary>
    /// <param name="outErr">The output-layer error signal (δ at the sigmoid output).</param>
    /// <param name="bc1">Adam first-moment bias-correction denominator (1 - β1^t).</param>
    /// <param name="bc2">Adam second-moment bias-correction denominator (1 - β2^t).</param>
    /// <param name="inputSize">Number of input features (row stride for the input weights).</param>
    /// <param name="vec">The current training example's (standardized) feature vector.</param>
    /// <param name="buffers">Pre-allocated per-layer scratch buffers (activations and errors).</param>
    private void ApplyAdamUpdates(
        double outErr,
        double bc1,
        double bc2,
        int inputSize,
        double[] vec,
        TrainingBuffers buffers)
    {
        var h1Act = buffers.H1Act;
        var h2Act = buffers.H2Act;
        var h3Act = buffers.H3Act;
        var h4Act = buffers.H4Act;
        var h1Err = buffers.H1Err;
        var h2Err = buffers.H2Err;
        var h3Err = buffers.H3Err;
        var h4Err = buffers.H4Err;

        // Output layer Adam update (hidden4 -> output)
        for (var k = 0; k < Hidden4Size; k++)
        {
            var g = (outErr * h4Act[k]) + (L2Lambda * _weightsH4O[k]);
            _mWH4O![k] = (AdamBeta1 * _mWH4O[k]) + ((1 - AdamBeta1) * g);
            _vWH4O![k] = (AdamBeta2 * _vWH4O[k]) + ((1 - AdamBeta2) * g * g);
            _weightsH4O[k] -= DefaultLearningRate * (_mWH4O[k] / bc1) /
                              (Math.Sqrt(_vWH4O[k] / bc2) + AdamEpsilon);
            _weightsH4O[k] = Math.Clamp(_weightsH4O[k], -WeightClamp, WeightClamp);
        }

        ApplyAdamBiasStep(ref _mBO, ref _vBO, ref _biasOutput, outErr, bc1, bc2);

        // Hidden3->Hidden4 layer Adam update
        for (var k = 0; k < Hidden4Size; k++)
        {
            var bIdx = k * Hidden3Size;
            for (var j = 0; j < Hidden3Size; j++)
            {
                var p = bIdx + j;
                var g = (h4Err[k] * h3Act[j]) + (L2Lambda * _weightsH3H4[p]);
                _mWH3H4![p] = (AdamBeta1 * _mWH3H4[p]) + ((1 - AdamBeta1) * g);
                _vWH3H4![p] = (AdamBeta2 * _vWH3H4[p]) + ((1 - AdamBeta2) * g * g);
                _weightsH3H4[p] -= DefaultLearningRate * (_mWH3H4[p] / bc1) /
                                   (Math.Sqrt(_vWH3H4[p] / bc2) + AdamEpsilon);
                _weightsH3H4[p] = Math.Clamp(_weightsH3H4[p], -WeightClamp, WeightClamp);
            }

            ApplyAdamBiasStep(ref _mBH4![k], ref _vBH4![k], ref _biasH4[k], h4Err[k], bc1, bc2);
        }

        // Hidden2->Hidden3 layer Adam update
        for (var k = 0; k < Hidden3Size; k++)
        {
            var bIdx = k * Hidden2Size;
            for (var j = 0; j < Hidden2Size; j++)
            {
                var p = bIdx + j;
                var g = (h3Err[k] * h2Act[j]) + (L2Lambda * _weightsH2H3[p]);
                _mWH2H3![p] = (AdamBeta1 * _mWH2H3[p]) + ((1 - AdamBeta1) * g);
                _vWH2H3![p] = (AdamBeta2 * _vWH2H3[p]) + ((1 - AdamBeta2) * g * g);
                _weightsH2H3[p] -= DefaultLearningRate * (_mWH2H3[p] / bc1) /
                                   (Math.Sqrt(_vWH2H3[p] / bc2) + AdamEpsilon);
                _weightsH2H3[p] = Math.Clamp(_weightsH2H3[p], -WeightClamp, WeightClamp);
            }

            ApplyAdamBiasStep(ref _mBH3![k], ref _vBH3![k], ref _biasH3[k], h3Err[k], bc1, bc2);
        }

        // Hidden1->Hidden2 layer Adam update
        for (var k = 0; k < Hidden2Size; k++)
        {
            var bIdx = k * Hidden1Size;
            for (var j = 0; j < Hidden1Size; j++)
            {
                var p = bIdx + j;
                var g = (h2Err[k] * h1Act[j]) + (L2Lambda * _weightsH1H2[p]);
                _mWH1H2![p] = (AdamBeta1 * _mWH1H2[p]) + ((1 - AdamBeta1) * g);
                _vWH1H2![p] = (AdamBeta2 * _vWH1H2[p]) + ((1 - AdamBeta2) * g * g);
                _weightsH1H2[p] -= DefaultLearningRate * (_mWH1H2[p] / bc1) /
                                   (Math.Sqrt(_vWH1H2[p] / bc2) + AdamEpsilon);
                _weightsH1H2[p] = Math.Clamp(_weightsH1H2[p], -WeightClamp, WeightClamp);
            }

            ApplyAdamBiasStep(ref _mBH2![k], ref _vBH2![k], ref _biasH2[k], h2Err[k], bc1, bc2);
        }

        // Input->Hidden1 layer Adam update
        for (var j = 0; j < Hidden1Size; j++)
        {
            var bIdx = j * inputSize;
            for (var i = 0; i < inputSize; i++)
            {
                var p = bIdx + i;
                var g = (h1Err[j] * vec[i]) + (L2Lambda * _weightsIH[p]);
                _mWIH![p] = (AdamBeta1 * _mWIH[p]) + ((1 - AdamBeta1) * g);
                _vWIH![p] = (AdamBeta2 * _vWIH[p]) + ((1 - AdamBeta2) * g * g);
                _weightsIH[p] -= DefaultLearningRate * (_mWIH[p] / bc1) /
                                 (Math.Sqrt(_vWIH[p] / bc2) + AdamEpsilon);
                _weightsIH[p] = Math.Clamp(_weightsIH[p], -WeightClamp, WeightClamp);
            }

            ApplyAdamBiasStep(ref _mBH1![j], ref _vBH1![j], ref _biasH1[j], h1Err[j], bc1, bc2);
        }
    }

    /// <summary>
    ///     Applies a single Adam bias update step (moment updates, bias-corrected step, clamp).
    /// </summary>
    /// <param name="m">First-moment accumulator for the bias (updated in place).</param>
    /// <param name="v">Second-moment accumulator for the bias (updated in place).</param>
    /// <param name="bias">The bias value being updated (updated in place).</param>
    /// <param name="grad">The gradient (error signal) for the bias.</param>
    /// <param name="bc1">Adam first-moment bias-correction denominator (1 - β1^t).</param>
    /// <param name="bc2">Adam second-moment bias-correction denominator (1 - β2^t).</param>
    private static void ApplyAdamBiasStep(
        ref double m,
        ref double v,
        ref double bias,
        double grad,
        double bc1,
        double bc2)
    {
        m = (AdamBeta1 * m) + ((1 - AdamBeta1) * grad);
        v = (AdamBeta2 * v) + ((1 - AdamBeta2) * grad * grad);
        bias -= DefaultLearningRate * (m / bc1) / (Math.Sqrt(v / bc2) + AdamEpsilon);
        bias = Math.Clamp(bias, -WeightClamp, WeightClamp);
    }

    /// <summary>
    ///     MLP forward pass: input -> hidden₁ (ReLU) -> hidden₂ (ReLU) -> hidden₃ (ReLU) -> hidden₄ (ReLU) -> output (Sigmoid).
    /// </summary>
    /// <param name="input">Input feature vector [InputSize].</param>
    /// <param name="wIH">Input->Hidden1 weights [Hidden1Size × InputSize] row-major.</param>
    /// <param name="bH1">Hidden1 biases [Hidden1Size].</param>
    /// <param name="wH1H2">Hidden1->Hidden2 weights [Hidden2Size × Hidden1Size] row-major.</param>
    /// <param name="bH2">Hidden2 biases [Hidden2Size].</param>
    /// <param name="wH2H3">Hidden2->Hidden3 weights [Hidden3Size × Hidden2Size] row-major.</param>
    /// <param name="bH3">Hidden3 biases [Hidden3Size].</param>
    /// <param name="wH3H4">Hidden3->Hidden4 weights [Hidden4Size × Hidden3Size] row-major.</param>
    /// <param name="bH4">Hidden4 biases [Hidden4Size].</param>
    /// <param name="wH4O">Hidden4->Output weights [Hidden4Size].</param>
    /// <param name="bO">Output bias scalar.</param>
    /// <param name="h1Pre">Pre-allocated buffer for hidden1 pre-activation values [Hidden1Size].</param>
    /// <param name="h1Act">Pre-allocated buffer for hidden1 post-activation values [Hidden1Size].</param>
    /// <param name="h2Pre">Pre-allocated buffer for hidden2 pre-activation values [Hidden2Size].</param>
    /// <param name="h2Act">Pre-allocated buffer for hidden2 post-activation values [Hidden2Size].</param>
    /// <param name="h3Pre">Pre-allocated buffer for hidden3 pre-activation values [Hidden3Size].</param>
    /// <param name="h3Act">Pre-allocated buffer for hidden3 post-activation values [Hidden3Size].</param>
    /// <param name="h4Pre">Pre-allocated buffer for hidden4 pre-activation values [Hidden4Size].</param>
    /// <param name="h4Act">Pre-allocated buffer for hidden4 post-activation values [Hidden4Size].</param>
    /// <returns>Output score in [0, 1] via sigmoid.</returns>
    internal static double ForwardPass(
        double[] input,
        double[] wIH,
        double[] bH1,
        double[] wH1H2,
        double[] bH2,
        double[] wH2H3,
        double[] bH3,
        double[] wH3H4,
        double[] bH4,
        double[] wH4O,
        double bO,
        double[] h1Pre,
        double[] h1Act,
        double[] h2Pre,
        double[] h2Act,
        double[] h3Pre,
        double[] h3Act,
        double[] h4Pre,
        double[] h4Act)
    {
        var inputSize = input.Length;

        // Hidden layer 1: input -> hidden1 (ReLU)
        ComputeReluLayer(input, wIH, bH1, Hidden1Size, inputSize, h1Pre, h1Act);

        // Hidden layer 2: hidden1 -> hidden2 (ReLU)
        ComputeReluLayer(h1Act, wH1H2, bH2, Hidden2Size, Hidden1Size, h2Pre, h2Act);

        // Hidden layer 3: hidden2 -> hidden3 (ReLU)
        ComputeReluLayer(h2Act, wH2H3, bH3, Hidden3Size, Hidden2Size, h3Pre, h3Act);

        // Hidden layer 4: hidden3 -> hidden4 (ReLU)
        ComputeReluLayer(h3Act, wH3H4, bH4, Hidden4Size, Hidden3Size, h4Pre, h4Act);

        // Output layer: hidden4 -> output (Sigmoid)
        var outputZ = bO;
        for (var m = 0; m < Hidden4Size; m++)
        {
            outputZ += wH4O[m] * h4Act[m];
        }

        return Sigmoid(outputZ);
    }

    /// <summary>
    ///     Computes a single fully-connected ReLU layer: pre = W·prevAct + bias, act = relu(pre).
    /// </summary>
    /// <param name="prevAct">The previous layer's activations (or the input vector for layer 1).</param>
    /// <param name="weights">The layer weights [<paramref name="layerSize"/> × <paramref name="prevSize"/>] row-major.</param>
    /// <param name="bias">The layer biases [<paramref name="layerSize"/>].</param>
    /// <param name="layerSize">The number of neurons in this layer.</param>
    /// <param name="prevSize">The number of neurons in the previous layer (row stride).</param>
    /// <param name="pre">Output buffer for pre-activation values [<paramref name="layerSize"/>].</param>
    /// <param name="act">Output buffer for post-activation (ReLU) values [<paramref name="layerSize"/>].</param>
    private static void ComputeReluLayer(
        double[] prevAct,
        double[] weights,
        double[] bias,
        int layerSize,
        int prevSize,
        double[] pre,
        double[] act)
    {
        for (var j = 0; j < layerSize; j++)
        {
            var sum = bias[j];
            var baseIdx = j * prevSize;
            for (var i = 0; i < prevSize; i++)
            {
                sum += weights[baseIdx + i] * prevAct[i];
            }

            pre[j] = sum;
            act[j] = sum > 0 ? sum : 0.0;
        }
    }

    /// <summary>
    ///     Training-time forward pass that additionally applies inverted Bernoulli dropout to each hidden layer's activations.
    /// </summary>
    /// <param name="input">Input feature vector [InputSize].</param>
    /// <param name="wIH">Input->Hidden1 weights [Hidden1Size × InputSize] row-major.</param>
    /// <param name="bH1">Hidden1 biases [Hidden1Size].</param>
    /// <param name="wH1H2">Hidden1->Hidden2 weights.</param>
    /// <param name="bH2">Hidden2 biases.</param>
    /// <param name="wH2H3">Hidden2->Hidden3 weights.</param>
    /// <param name="bH3">Hidden3 biases.</param>
    /// <param name="wH3H4">Hidden3->Hidden4 weights.</param>
    /// <param name="bH4">Hidden4 biases.</param>
    /// <param name="wH4O">Hidden4->Output weights.</param>
    /// <param name="bO">Output bias scalar.</param>
    /// <param name="h1Pre">Buffer for hidden1 pre-activation values.</param>
    /// <param name="h1Act">Buffer for hidden1 post-activation values (dropout-scaled).</param>
    /// <param name="h2Pre">Buffer for hidden2 pre-activation values.</param>
    /// <param name="h2Act">Buffer for hidden2 post-activation values (dropout-scaled).</param>
    /// <param name="h3Pre">Buffer for hidden3 pre-activation values.</param>
    /// <param name="h3Act">Buffer for hidden3 post-activation values (dropout-scaled).</param>
    /// <param name="h4Pre">Buffer for hidden4 pre-activation values.</param>
    /// <param name="h4Act">Buffer for hidden4 post-activation values (dropout-scaled).</param>
    /// <param name="h1Mask">Output - dropout mask for hidden1 (1.0 = kept, 0.0 = dropped).</param>
    /// <param name="h2Mask">Output - dropout mask for hidden2.</param>
    /// <param name="h3Mask">Output - dropout mask for hidden3.</param>
    /// <param name="h4Mask">Output - dropout mask for hidden4.</param>
    /// <param name="rng">RNG used for the Bernoulli draws.</param>
    /// <param name="keepProbability">Probability of keeping a neuron [0..1]. Values >= 1.0 disable dropout.</param>
    /// <param name="invKeepScale">Precomputed 1 / keepProbability so we skip a division per neuron.</param>
    /// <returns>Output score in [0, 1] via sigmoid.</returns>
    internal static double ForwardPassTraining(
        double[] input,
        double[] wIH,
        double[] bH1,
        double[] wH1H2,
        double[] bH2,
        double[] wH2H3,
        double[] bH3,
        double[] wH3H4,
        double[] bH4,
        double[] wH4O,
        double bO,
        double[] h1Pre,
        double[] h1Act,
        double[] h2Pre,
        double[] h2Act,
        double[] h3Pre,
        double[] h3Act,
        double[] h4Pre,
        double[] h4Act,
        double[] h1Mask,
        double[] h2Mask,
        double[] h3Mask,
        double[] h4Mask,
        Random rng,
        double keepProbability,
        double invKeepScale)
    {
        var inputSize = input.Length;
        var dropoutOff = keepProbability >= 1.0;
        var dropout = new DropoutContext(dropoutOff, rng, keepProbability, invKeepScale);

        // Hidden layer 1: input -> hidden1 (ReLU + optional dropout)
        ForwardPassTrainingLayer(
            new LayerComputeSpec(input, inputSize, wIH, bH1, h1Pre, h1Act, h1Mask, Hidden1Size, inputSize),
            dropout);

        // Hidden layer 2: hidden1 -> hidden2 (ReLU + optional dropout)
        ForwardPassTrainingLayer(
            new LayerComputeSpec(h1Act, Hidden1Size, wH1H2, bH2, h2Pre, h2Act, h2Mask, Hidden2Size, Hidden1Size),
            dropout);

        // Hidden layer 3: hidden2 -> hidden3 (ReLU + optional dropout)
        ForwardPassTrainingLayer(
            new LayerComputeSpec(h2Act, Hidden2Size, wH2H3, bH3, h3Pre, h3Act, h3Mask, Hidden3Size, Hidden2Size),
            dropout);

        // Hidden layer 4: hidden3 -> hidden4 (ReLU + optional dropout)
        ForwardPassTrainingLayer(
            new LayerComputeSpec(h3Act, Hidden3Size, wH3H4, bH4, h4Pre, h4Act, h4Mask, Hidden4Size, Hidden3Size),
            dropout);

        // Output layer: hidden4 -> output (Sigmoid, no dropout on the output neuron)
        var outputZ = bO;
        for (var m = 0; m < Hidden4Size; m++)
        {
            outputZ += wH4O[m] * h4Act[m];
        }

        return Sigmoid(outputZ);
    }

    /// <summary>
    ///     Computes one ReLU + optional-dropout hidden layer for ForwardPassTraining. Extracted verbatim from the per-layer loops; the arithmetic, activation, weight indexing, and dropout branch are unchanged.
    /// </summary>
    /// <param name="spec">The layer's compute inputs (activations, weights, bias, buffers, dimensions).</param>
    /// <param name="dropout">The dropout controls (off-flag, RNG, keep-probability, inverse-keep scale).</param>
    private static void ForwardPassTrainingLayer(LayerComputeSpec spec, DropoutContext dropout)
    {
        for (var j = 0; j < spec.LayerSize; j++)
        {
            var sum = spec.Bias[j];
            var baseIdx = j * spec.StrideSize;
            for (var i = 0; i < spec.PrevSize; i++)
            {
                sum += spec.Weights[baseIdx + i] * spec.PrevAct[i];
            }

            spec.Pre[j] = sum;
            var relu = sum > 0 ? sum : 0.0;

            if (dropout.DropoutOff)
            {
                spec.Mask[j] = 1.0;
                spec.Act[j] = relu;
            }
            else
            {
                var keep = dropout.Rng.NextDouble() < dropout.KeepProbability;
                spec.Mask[j] = keep ? 1.0 : 0.0;
                spec.Act[j] = keep ? relu * dropout.InvKeepScale : 0.0;
            }
        }
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
    ///     Initializes weights using He/Kaiming uniform for hidden layers (ReLU) and Xavier/Glorot uniform for the output layer (Sigmoid).
    /// </summary>
    private void InitializeWeights(int inputSize)
    {
        var rng = new Random(42);

        // Input -> Hidden1
        // He/Kaiming uniform for ReLU hidden layers: limit = sqrt(6 / fan_in)
        var limitIH = Math.Sqrt(6.0 / inputSize);
        for (var i = 0; i < _weightsIH.Length; i++)
        {
            _weightsIH[i] = (rng.NextDouble() * 2.0 * limitIH) - limitIH;
        }

        // Hidden1 -> Hidden2
        var limitH1H2 = Math.Sqrt(6.0 / Hidden1Size);
        for (var i = 0; i < _weightsH1H2.Length; i++)
        {
            _weightsH1H2[i] = (rng.NextDouble() * 2.0 * limitH1H2) - limitH1H2;
        }

        // Hidden2 -> Hidden3
        var limitH2H3 = Math.Sqrt(6.0 / Hidden2Size);
        for (var i = 0; i < _weightsH2H3.Length; i++)
        {
            _weightsH2H3[i] = (rng.NextDouble() * 2.0 * limitH2H3) - limitH2H3;
        }

        // Hidden3 -> Hidden4 (He/Kaiming for ReLU)
        var limitH3H4 = Math.Sqrt(6.0 / Hidden3Size);
        for (var i = 0; i < _weightsH3H4.Length; i++)
        {
            _weightsH3H4[i] = (rng.NextDouble() * 2.0 * limitH3H4) - limitH3H4;
        }

        // Hidden4 -> Output (Xavier/Glorot for Sigmoid)
        var limitH4O = Math.Sqrt(6.0 / (Hidden4Size + 1));
        for (var i = 0; i < _weightsH4O.Length; i++)
        {
            _weightsH4O[i] = (rng.NextDouble() * 2.0 * limitH4O) - limitH4O;
        }

        Array.Clear(_biasH1);
        Array.Clear(_biasH2);
        Array.Clear(_biasH3);
        Array.Clear(_biasH4);
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

        var wh3h4Len = Hidden4Size * Hidden3Size;
        _mWH3H4 = new double[wh3h4Len];
        _vWH3H4 = new double[wh3h4Len];
        _mBH4 = new double[Hidden4Size];
        _vBH4 = new double[Hidden4Size];

        _mWH4O = new double[Hidden4Size];
        _vWH4O = new double[Hidden4Size];
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
        var h4Pre = new double[Hidden4Size];
        var h4Act = new double[Hidden4Size];

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
                _weightsH3H4,
                _biasH4,
                _weightsH4O,
                _biasOutput,
                h1Pre,
                h1Act,
                h2Pre,
                h2Act,
                h3Pre,
                h3Act,
                h4Pre,
                h4Act);
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

        // Guard against corrupted/oversized files before reading into memory. Weights JSON is ~120 KB;
        // a 10 MB ceiling gives ample headroom.
        const long MaxWeightsFileSizeBytes = 10 * 1024 * 1024;
        if (new FileInfo(_weightsPath).Length > MaxWeightsFileSizeBytes)
        {
            _logger?.LogWarning(
                "NeuralScoringStrategy: Weights file exceeds {LimitMB}MB ({Path}). Skipping load.",
                MaxWeightsFileSizeBytes / (1024 * 1024),
                _weightsPath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_weightsPath);
            var data = JsonSerializer.Deserialize<NeuralWeightsData>(json);

            if (data is not null && IsValidWeightsData(data))
            {
                // Reject persisted weights containing NaN/Infinity values that would poison scoring.
                if (HasNonFiniteWeights(data))
                {
                    _logger?.LogWarning(
                        "NeuralScoringStrategy: Discarding persisted weights containing NaN/Infinity values");
                }
                else
                {
                    ApplyLoadedWeights(data);
                }
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
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to load weights (access denied)");
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to parse weights");
        }
    }

    /// <summary>
    ///     Validates a deserialized weights payload: the version matches, every weight/bias array has the expected length, and the standardization arrays are either both absent or both FeatureCount long (stale mismatched lengths would crash StandardizeSingleVector at.
    /// </summary>
    /// <param name="data">The deserialized weights payload.</param>
    /// <returns><c>true</c> when the payload is structurally valid and safe to apply.</returns>
    private static bool IsValidWeightsData(NeuralWeightsData data)
    {
        // Validate standardization arrays: both null, or both FeatureCount length.
        var hasValidStandardization = (data.FeatureMeans is null && data.FeatureStdDevs is null)
                                      || data is
                                      {
                                          FeatureMeans: { Length: CandidateFeatures.FeatureCount },
                                          FeatureStdDevs.Length: CandidateFeatures.FeatureCount
                                      };

        return hasValidStandardization
            && data is
            {
                Version: CurrentWeightsVersion, WeightsIH.Length: Hidden1Size * CandidateFeatures.FeatureCount,
                BiasH1.Length: Hidden1Size, WeightsH1H2.Length: Hidden2Size * Hidden1Size,
                BiasH2.Length: Hidden2Size, WeightsH2H3.Length: Hidden3Size * Hidden2Size,
                BiasH3.Length: Hidden3Size, WeightsH3H4.Length: Hidden4Size * Hidden3Size,
                BiasH4.Length: Hidden4Size, WeightsH4O.Length: Hidden4Size
            };
    }

    /// <summary>
    ///     Returns whether any weight/bias/standardization value in the payload is NaN or Infinity.
    /// </summary>
    /// <param name="data">The (already structurally validated) weights payload.</param>
    /// <returns><c>true</c> when at least one value is non-finite and the payload must be discarded.</returns>
    private static bool HasNonFiniteWeights(NeuralWeightsData data)
    {
        return !AllFinite(data.WeightsIH) || !AllFinite(data.BiasH1)
            || !AllFinite(data.WeightsH1H2) || !AllFinite(data.BiasH2)
            || !AllFinite(data.WeightsH2H3) || !AllFinite(data.BiasH3)
            || !AllFinite(data.WeightsH3H4) || !AllFinite(data.BiasH4)
            || !AllFinite(data.WeightsH4O) || !double.IsFinite(data.BiasOutput)
            || (data.FeatureMeans is not null && !AllFinite(data.FeatureMeans))
            || (data.FeatureStdDevs is not null && !AllFinite(data.FeatureStdDevs));
    }

    /// <summary>
    ///     Copies a validated, finite weights payload into the live weight/bias fields and resets the Adam timestep.
    /// </summary>
    /// <param name="data">The validated weights payload to apply.</param>
    private void ApplyLoadedWeights(NeuralWeightsData data)
    {
        _weightsIH = data.WeightsIH;
        _biasH1 = data.BiasH1;
        _weightsH1H2 = data.WeightsH1H2;
        _biasH2 = data.BiasH2;
        _weightsH2H3 = data.WeightsH2H3;
        _biasH3 = data.BiasH3;
        _weightsH3H4 = data.WeightsH3H4;
        _biasH4 = data.BiasH4;
        _weightsH4O = data.WeightsH4O;
        _biasOutput = data.BiasOutput;
        _featureMeans = data.FeatureMeans;
        _featureStdDevs = data.FeatureStdDevs;
        _trainingGeneration = data.TrainingGeneration;
        _adamTimestep = 0;
    }

    /// <summary>
    ///     Persists current weights to disk atomically.
    /// </summary>
    private void TrySaveWeights()
    {
        if (string.IsNullOrEmpty(_weightsPath))
        {
            return;
        }

        // Fast-path exit if already disposed: this can run from a Train() tail racing plugin shutdown,
        // where _rwLock is disposed and EnterReadLock() would throw ObjectDisposedException.
        if (_disposed)
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

            NeuralWeightsData data;
            try
            {
                _rwLock.EnterReadLock();
            }
            catch (ObjectDisposedException)
            {
                // Lock disposed between the _disposed check above and lock acquisition
                // (plugin unload racing with a late Train() tail). Nothing to persist.
                return;
            }

            try
            {
                data = new NeuralWeightsData
                {
                    WeightsIH = (double[])_weightsIH.Clone(),
                    BiasH1 = (double[])_biasH1.Clone(),
                    WeightsH1H2 = (double[])_weightsH1H2.Clone(),
                    BiasH2 = (double[])_biasH2.Clone(),
                    WeightsH2H3 = (double[])_weightsH2H3.Clone(),
                    BiasH3 = (double[])_biasH3.Clone(),
                    WeightsH3H4 = (double[])_weightsH3H4.Clone(),
                    BiasH4 = (double[])_biasH4.Clone(),
                    WeightsH4O = (double[])_weightsH4O.Clone(),
                    BiasOutput = _biasOutput,
                    FeatureMeans = _featureMeans is not null ? (double[])_featureMeans.Clone() : null,
                    FeatureStdDevs = _featureStdDevs is not null ? (double[])_featureStdDevs.Clone() : null,
                    TrainingGeneration = _trainingGeneration,
                    UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Version = CurrentWeightsVersion
                };
            }
            finally
            {
                ReleaseReadLockSafely();
            }

            var json = JsonSerializer.Serialize(data, SerializerOptions);

            // Use AtomicFile so a transient Windows AV/indexer sharing violation on the final File.Move
            // gets a bounded retry instead of silently dropping the save (it also cleans up temp files).
            AtomicFile.WriteAllText(_weightsPath, json);
        }
        catch (ObjectDisposedException ex)
        {
            // Rare tail race: Dispose() fired between the early-exit check and a later lock/IO op.
            // Non-critical: save is best-effort, and a lost save on shutdown is acceptable.
            _logger?.LogDebug(ex, "NeuralScoringStrategy: Save skipped, strategy disposed mid-flight");
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to save weights");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to save weights (access denied)");
        }
        catch (System.Security.SecurityException ex)
        {
            // Non-critical - platform security policy denied write; nothing we can do here.
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to save weights (security policy)");
        }
        catch (NotSupportedException ex)
        {
            // Non-critical - path/filesystem does not support the operation (e.g. reserved names).
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to save weights (unsupported path)");
        }
        catch (ArgumentException ex)
        {
            // Non-critical - malformed path characters surfaced by the OS layer. Weight path is
            // plugin-configured; this indicates a config error, not a runtime failure to recover from.
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to save weights (invalid path)");
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "NeuralScoringStrategy: Failed to serialize weights");
        }
    }

    /// <summary>
    ///     Logs per-feature importance from input->hidden1 weight L2 norms: Importance[f] = sqrt(Σ_j weightsIH[j, f]²), measuring how strongly each input drives hidden activations.
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

        _logger.LogDebug(
            "NeuralScoringStrategy feature importance (L2 norm): {FeatureImportance}",
            string.Join(", ", parts));
    }

    /// <summary>Returns true if all elements in the array are finite (not NaN or Infinity).</summary>
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

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _rwLock.Dispose();
    }

    /// <summary>
    ///     Releases the read lock, tolerating disposal mid-scoring.
    /// </summary>
#pragma warning disable MT1013 // Releasing lock should always be wrapped in finally block
    private void ReleaseReadLockSafely()
    {
        if (!_rwLock.IsReadLockHeld)
        {
            return;
        }

        try
        {
            _rwLock.ExitReadLock();
        }
        catch (ObjectDisposedException)
        {
            // Lock was disposed while we still held the read handle; nothing left to release.
        }
    }
#pragma warning restore MT1013

    /// <summary>
    ///     Groups the pre-allocated per-layer scratch buffers threaded through the training backprop and Adam-update helpers.
    /// </summary>
    /// <param name="H1Pre">Hidden1 pre-activation buffer.</param>
    /// <param name="H1Act">Hidden1 post-activation (dropout-scaled) buffer.</param>
    /// <param name="H2Pre">Hidden2 pre-activation buffer.</param>
    /// <param name="H2Act">Hidden2 post-activation (dropout-scaled) buffer.</param>
    /// <param name="H3Pre">Hidden3 pre-activation buffer.</param>
    /// <param name="H3Act">Hidden3 post-activation (dropout-scaled) buffer.</param>
    /// <param name="H4Pre">Hidden4 pre-activation buffer.</param>
    /// <param name="H4Act">Hidden4 post-activation (dropout-scaled) buffer.</param>
    /// <param name="H1Err">Hidden1 pre-activation error (δ_pre) buffer.</param>
    /// <param name="H2Err">Hidden2 pre-activation error (δ_pre) buffer.</param>
    /// <param name="H3Err">Hidden3 pre-activation error (δ_pre) buffer.</param>
    /// <param name="H4Err">Hidden4 pre-activation error (δ_pre) buffer.</param>
    /// <param name="H1Mask">Hidden1 dropout mask (1.0 = kept, 0.0 = dropped).</param>
    /// <param name="H2Mask">Hidden2 dropout mask.</param>
    /// <param name="H3Mask">Hidden3 dropout mask.</param>
    /// <param name="H4Mask">Hidden4 dropout mask.</param>
    private readonly record struct TrainingBuffers(
        double[] H1Pre,
        double[] H1Act,
        double[] H2Pre,
        double[] H2Act,
        double[] H3Pre,
        double[] H3Act,
        double[] H4Pre,
        double[] H4Act,
        double[] H1Err,
        double[] H2Err,
        double[] H3Err,
        double[] H4Err,
        double[] H1Mask,
        double[] H2Mask,
        double[] H3Mask,
        double[] H4Mask);

    /// <summary>
    ///     Groups the best-so-far weight/bias snapshot buffers used by early stopping. Wraps the arrays cloned once per Train(IReadOnlyList{TrainingExample},IReadOnlyList{TrainingExample}?) call (the output bias scalar is tracked separately by the caller).
    /// </summary>
    /// <param name="BestWIH">Best-so-far input->hidden1 weights.</param>
    /// <param name="BestBH1">Best-so-far hidden1 biases.</param>
    /// <param name="BestWH1H2">Best-so-far hidden1->hidden2 weights.</param>
    /// <param name="BestBH2">Best-so-far hidden2 biases.</param>
    /// <param name="BestWH2H3">Best-so-far hidden2->hidden3 weights.</param>
    /// <param name="BestBH3">Best-so-far hidden3 biases.</param>
    /// <param name="BestWH3H4">Best-so-far hidden3->hidden4 weights.</param>
    /// <param name="BestBH4">Best-so-far hidden4 biases.</param>
    /// <param name="BestWH4O">Best-so-far hidden4->output weights.</param>
    private readonly record struct WeightSnapshot(
        double[] BestWIH,
        double[] BestBH1,
        double[] BestWH1H2,
        double[] BestBH2,
        double[] BestWH2H3,
        double[] BestBH3,
        double[] BestWH3H4,
        double[] BestBH4,
        double[] BestWH4O);

    /// <summary>
    ///     Scalar configuration for the training epoch loop, grouped so
    ///     <see cref="RunTrainingEpochs"/> keeps a small parameter list.
    /// </summary>
    /// <param name="MaxEpochs">Maximum number of epochs to run.</param>
    /// <param name="InputSize">Number of input features (row stride for the input weights).</param>
    /// <param name="UseEarlyStopping">Whether early stopping is active for this run.</param>
    /// <param name="KeepProbability">Bernoulli keep probability (1.0 when dropout is inactive).</param>
    /// <param name="DropoutInvKeep">Inverted-dropout scale (1 / keep, or 1.0 when dropout is inactive).</param>
    private readonly record struct EpochLoopConfig(
        int MaxEpochs,
        int InputSize,
        bool UseEarlyStopping,
        double KeepProbability,
        double DropoutInvKeep);

    /// <summary>
    ///     Groups the compute inputs for a single training forward-pass hidden layer: the source activations, weight matrix, bias, output buffers, and dimensions.
    /// </summary>
    /// <param name="PrevAct">Previous layer's activations (input source).</param>
    /// <param name="PrevSize">Number of neurons in the previous layer.</param>
    /// <param name="Weights">Weight matrix [layerSize × prevSize] row-major.</param>
    /// <param name="Bias">Bias vector for this layer.</param>
    /// <param name="Pre">Buffer for this layer's pre-activation values.</param>
    /// <param name="Act">Buffer for this layer's post-activation (dropout-scaled) values.</param>
    /// <param name="Mask">Buffer for this layer's dropout mask.</param>
    /// <param name="LayerSize">Number of neurons in this layer.</param>
    /// <param name="StrideSize">Row stride for the weight matrix (equals prevSize).</param>
    private readonly record struct LayerComputeSpec(
        double[] PrevAct,
        int PrevSize,
        double[] Weights,
        double[] Bias,
        double[] Pre,
        double[] Act,
        double[] Mask,
        int LayerSize,
        int StrideSize);

    /// <summary>
    ///     Groups the dropout controls threaded through the per-layer training forward pass.
    /// </summary>
    /// <param name="DropoutOff">Whether dropout is disabled for this pass.</param>
    /// <param name="Rng">RNG used for the Bernoulli draws.</param>
    /// <param name="KeepProbability">Probability of keeping a neuron [0..1]. Values >= 1.0 disable dropout.</param>
    /// <param name="InvKeepScale">Precomputed 1 / keepProbability so we skip a division per neuron.</param>
    private readonly record struct DropoutContext(
        bool DropoutOff,
        Random Rng,
        double KeepProbability,
        double InvKeepScale);

    /// <summary>Serializable container for persisted neural network weights.</summary>
    internal sealed class NeuralWeightsData
    {
        /// <summary>Gets or sets the input->hidden1 weights [Hidden1Size × InputSize].</summary>
        public double[] WeightsIH { get; set; } = [];

        /// <summary>Gets or sets the hidden1 biases [Hidden1Size].</summary>
        public double[] BiasH1 { get; set; } = [];

        /// <summary>Gets or sets the hidden1->hidden2 weights [Hidden2Size × Hidden1Size].</summary>
        public double[] WeightsH1H2 { get; set; } = [];

        /// <summary>Gets or sets the hidden2 biases [Hidden2Size].</summary>
        public double[] BiasH2 { get; set; } = [];

        /// <summary>Gets or sets the hidden2->hidden3 weights [Hidden3Size × Hidden2Size].</summary>
        public double[] WeightsH2H3 { get; set; } = [];

        /// <summary>Gets or sets the hidden3 biases [Hidden3Size].</summary>
        public double[] BiasH3 { get; set; } = [];

        /// <summary>Gets or sets the hidden3->hidden4 weights [Hidden4Size × Hidden3Size].</summary>
        public double[] WeightsH3H4 { get; set; } = [];

        /// <summary>Gets or sets the hidden4 biases [Hidden4Size].</summary>
        public double[] BiasH4 { get; set; } = [];

        /// <summary>Gets or sets the hidden4->output weights [Hidden4Size].</summary>
        public double[] WeightsH4O { get; set; } = [];

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