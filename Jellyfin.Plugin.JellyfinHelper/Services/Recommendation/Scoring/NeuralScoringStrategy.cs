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
///     Neural scoring strategy: a four-hidden-layer MLP learning non-linear feature interactions from
///     watch history via backpropagation.
///     <para>
///         Architecture:
///         <c>InputSize -> 76 hidden₁ (ReLU) -> 96 hidden₂ (ReLU) -> 48 hidden₃ (ReLU) ->
///         24 hidden₄ (ReLU) -> 1 output (Sigmoid)</c>.
///     </para>
///     <para>
///         Parameter count with 38 inputs:
///         <c>(38·76 + 76) + (76·96 + 96) + (96·48 + 48) + (48·24 + 24) + (24·1 + 1) = 16 213</c>.
///         Hidden₁ = 76 gives a ~2.0× first-layer expansion (tabular-MLP best practice); the wider
///         hidden₂ = 96 avoids an early bottleneck. Hidden₂/₃/₄ depend on the prior layer, not
///         InputSize, sized to avoid over-parameterising for small watch-history datasets.
///     </para>
///     <para>
///         Bernoulli dropout (keep-p = <see cref="DropoutKeepProbability"/>) applies to hidden
///         activations DURING TRAINING only; inference is deterministic and dropout-free so
///         recommendations are reproducible per weight set. Inverted dropout scales survivors by
///         <c>1 / keep</c> so train-time expected magnitude matches inference, keeping L2, weight
///         clamping and Xavier/He init calibrated.
///     </para>
///     Optimized for NAS/Docker: zero-allocation scoring path, pre-allocated training buffers,
///     ~14 k FP multiplications per score. No external ML dependencies.
/// </summary>
/// <remarks>
///     Training uses Adam with L2 regularization, Z-score standardization, He/Xavier init, temporal
///     sample weighting, dropout (v3 A2), and early stopping. Genre-mismatch penalties are applied
///     centrally by the ensemble, not here. Weights persist to disk; a persisted set whose array
///     lengths do not match the current architecture (38 inputs, Hidden1 = 76) is discarded on load.
/// </remarks>
public sealed class NeuralScoringStrategy : IScoringStrategy, ITrainableStrategy, IDisposable
{
    /// <summary>
    ///     Number of neurons in the first hidden layer.
    ///     ~2× InputSize (38->76) - best-practice expansion factor for tabular MLPs.
    /// </summary>
    internal const int Hidden1Size = 76;

    /// <summary>
    ///     Neurons in the second hidden layer. 96, deliberately WIDER than Hidden1 so the model can
    ///     compose high-order feature interactions (genre×critic, people×genre) instead of being forced
    ///     through an early bottleneck. The 76->96->48->24 trapezoid mirrors classical tabular topologies.
    /// </summary>
    internal const int Hidden2Size = 96;

    /// <summary>
    ///     Number of neurons in the third hidden layer.
    ///     48 - half of Hidden2, provides the compression stage.
    /// </summary>
    internal const int Hidden3Size = 48;

    /// <summary>
    ///     Number of neurons in the fourth (final) hidden layer.
    ///     24 - enough capacity to encode the final feature combinations feeding
    ///     into the single sigmoid output neuron.
    /// </summary>
    internal const int Hidden4Size = 24;

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

    /// <summary>Maximum training epochs per <see cref="Train(IReadOnlyList{TrainingExample})"/> call.</summary>
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

    /// <summary>
    ///     Bernoulli dropout keep-probability for hidden activations during training. 0.8 (20% drop) is
    ///     a mid-range choice for small tabular MLPs; small nets prefer light regularization to preserve
    ///     capacity. Applied ONLY during <see cref="Train(IReadOnlyList{TrainingExample})"/>; inference
    ///     (<see cref="Score"/> / <see cref="ScoreVector"/> / <see cref="ScoreWithExplanation"/>) is
    ///     deterministic and dropout-free. Values >= 1.0 disable dropout (useful for tests).
    /// </summary>
    internal const double DropoutKeepProbability = 0.8;

    /// <summary>
    ///     Minimum training examples below which dropout is disabled. On tiny datasets dropout starves
    ///     per-sample gradients and hurts convergence; L2 + early-stopping already regularize enough there.
    /// </summary>
    internal const int MinExamplesForDropout = 30;

    /// <summary>
    ///     Schema version for persisted weights. A set whose array lengths no longer match the current
    ///     layer sizes (feature-count or hidden-size change) is discarded on load: the load path warns
    ///     and resets to defaults so the next training run rebuilds from scratch.
    /// </summary>
    internal const int CurrentWeightsVersion = 3;

    /// <summary>
    ///     JSON options for weight persistence. Compact (non-indented) output cuts the file to ~a third
    ///     with no information loss; weights are machine-read only.
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

    /// <summary>Thread-local scratch buffer for the input feature vector on the Score() path.
    /// Avoids a heap allocation per scored candidate; safe because Score() fully overwrites
    /// the buffer via WriteToVector before reading it.</summary>
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

    /// <summary>Gets a copy of the input->hidden1 layer weights (for testing).</summary>
    internal double[] CurrentWeightsHidden
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return (double[])_weightsIH.Clone();
            }
            finally
            {
                ReleaseReadLockSafely();
            }
        }
    }

    /// <summary>Gets a copy of the hidden4->output layer weights (for testing).</summary>
    internal double[] CurrentWeightsOutput
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return (double[])_weightsH4O.Clone();
            }
            finally
            {
                ReleaseReadLockSafely();
            }
        }
    }

    /// <summary>Gets a copy of the hidden1->hidden2 layer weights (for testing).</summary>
    internal double[] CurrentWeightsH1H2
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return (double[])_weightsH1H2.Clone();
            }
            finally
            {
                ReleaseReadLockSafely();
            }
        }
    }

    /// <summary>Gets a copy of the hidden2->hidden3 layer weights (for testing).</summary>
    internal double[] CurrentWeightsH2H3
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return (double[])_weightsH2H3.Clone();
            }
            finally
            {
                ReleaseReadLockSafely();
            }
        }
    }

    /// <summary>Gets a copy of the hidden3->hidden4 layer weights (for testing).</summary>
    internal double[] CurrentWeightsH3H4
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return (double[])_weightsH3H4.Clone();
            }
            finally
            {
                ReleaseReadLockSafely();
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
    ///     Scores a raw feature vector directly without CandidateFeatures allocation. Used by
    ///     <see cref="NeuralFeatureImportance"/> for permutation importance where features are raw
    ///     arrays. Applies Z-score standardization and the full forward pass identically to <see cref="Score"/>.
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
    ///     Full input-gradient attribution through all four hidden layers,
    ///     O(H4·H3·H2·H1·InputSize). For single-item explanation only (why a specific recommendation
    ///     was made); do NOT call in batch. Use <see cref="Score"/> for batch scoring.
    /// </remarks>
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
                    for (var k = 0; k < Hidden2Size; k++)
                    {
                        if (h2Pre[k] <= 0)
                        {
                            continue;
                        }

                        var h2h3W = _weightsH2H3[(l * Hidden2Size) + k];
                        var combinedOuter = combinedH4H3 * h2h3W;
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
                }
            }

            var interactionContrib =
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

            return new ScoreExplanation
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
        }
        finally
        {
            ReleaseReadLockSafely();
        }
    }

    /// <summary>
    ///     Trains the MLP via backpropagation with Adam optimizer.
    /// </summary>
    /// <param name="examples">Training examples with features and labels.</param>
    /// <returns>True if training was performed, false if insufficient data.</returns>
    public bool Train(IReadOnlyList<TrainingExample> examples) => Train(examples, heldOutForMetrics: null);

    /// <inheritdoc />
    public bool Train(IReadOnlyList<TrainingExample> examples, IReadOnlyList<TrainingExample>? heldOutForMetrics)
    {
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

            var valCount = Math.Max(MinValidationExamples, (int)(examples.Count * ValidationSplitRatio));
            valCount = Math.Min(valCount, examples.Count - MinTrainingExamples);
            var useEarlyStopping = valCount >= MinValidationExamples
                                   && examples.Count - valCount >= MinTrainingExamples;

            var gen = _trainingGeneration;
            _trainingGeneration++;
            var rng = new Random(42 + gen);

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

            var bestLoss = double.MaxValue;
            var patience = 0;

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

            // Bernoulli dropout masks (1 = keep, 0 = drop), kept as double so survivors rescale by
            // 1/keep in-place (inverted dropout: train-time activations match inference magnitude).
            // Disabled when trainIdx.Length < MinExamplesForDropout (gradients would starve) or
            // DropoutKeepProbability >= 1.0 (test opt-out). Always allocated so backprop reads them
            // without null-guards; when inactive they are filled with 1.0 (identity).
            var h1Mask = new double[Hidden1Size];
            var h2Mask = new double[Hidden2Size];
            var h3Mask = new double[Hidden3Size];
            var h4Mask = new double[Hidden4Size];
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

                    // Dropout is applied by RE-RUNNING each hidden layer's activation through a
                    // Bernoulli mask, WITHOUT going through the (deterministic, dropout-free) ForwardPass.
                    // This keeps ForwardPass the single source of truth for inference and avoids a second
                    // code path that could drift. Masked+rescaled activations feed the next layer as if
                    // the neuron were absent for this step.
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
                        h1Pre,
                        h1Act,
                        h2Pre,
                        h2Act,
                        h3Pre,
                        h3Act,
                        h4Pre,
                        h4Act,
                        h1Mask,
                        h2Mask,
                        h3Mask,
                        h4Mask,
                        dropoutRng,
                        dropoutActive ? DropoutKeepProbability : 1.0,
                        dropoutInvKeep);

                    var outErr = (pred - examples[idx].Label) * pred * (1.0 - pred) * sw;

                    _adamTimestep++;
                    var bc1 = 1.0 - Math.Pow(AdamBeta1, _adamTimestep);
                    var bc2 = 1.0 - Math.Pow(AdamBeta2, _adamTimestep);

                    // === Compute ALL error signals BEFORE updating any weights ===
                    // Correct backprop uses the forward-pass weights for error computation; updating
                    // weights first would skew gradients.
                    //
                    // A2 dropout: a neuron with mask == 0 produced no output, so its error signal is
                    // zero (no gradient flow) and the downstream weight-update from it is zero too
                    // (activation was zero). The mask check duplicates the pre>0 check for clarity.

                    // Hidden4 error (backprop through ReLU + inverted-dropout scale). With inverted
                    // dropout a = mask · relu(pre) · invKeep, so ∂a/∂pre = invKeep for a kept neuron and
                    // δ_pre = δ_a · invKeep. Folding invKeep in HERE, once per layer, gives every consumer
                    // of hNErr (inter-layer propagation AND the weight/bias gradients that pair hNErr with
                    // the upstream activation) the correct δ_pre. The output-layer update's downstream
                    // activation (h4Act) carries its own invKeep independently (no double count). When
                    // dropout is inactive invKeep == 1.0, an exact no-op.
                    for (var k = 0; k < Hidden4Size; k++)
                    {
                        h4Err[k] = (h4Pre[k] > 0 && h4Mask[k] > 0)
                            ? outErr * _weightsH4O[k] * dropoutInvKeep
                            : 0.0;
                    }

                    // Hidden3 error (backprop through ReLU + inverted-dropout scale from hidden4).
                    // h4Err holds δ_pre for hidden4, so summing against the weights yields δ_a for
                    // hidden3; multiplying by invKeep converts that to δ_pre.
                    for (var k = 0; k < Hidden3Size; k++)
                    {
                        if (h3Pre[k] <= 0 || h3Mask[k] == 0.0)
                        {
                            h3Err[k] = 0.0;
                            continue;
                        }

                        var sum = 0.0;
                        for (var m = 0; m < Hidden4Size; m++)
                        {
                            sum += h4Err[m] * _weightsH3H4[(m * Hidden3Size) + k];
                        }

                        h3Err[k] = sum * dropoutInvKeep;
                    }

                    // Hidden2 layer error (backprop through ReLU + inverted-dropout scale from hidden3)
                    for (var k = 0; k < Hidden2Size; k++)
                    {
                        if (h2Pre[k] <= 0 || h2Mask[k] <= 0.0)
                        {
                            h2Err[k] = 0.0;
                            continue;
                        }

                        var sum = 0.0;
                        for (var l = 0; l < Hidden3Size; l++)
                        {
                            sum += h3Err[l] * _weightsH2H3[(l * Hidden2Size) + k];
                        }

                        h2Err[k] = sum * dropoutInvKeep;
                    }

                    // Hidden1 layer error (backprop through ReLU + inverted-dropout scale from hidden2)
                    for (var j = 0; j < Hidden1Size; j++)
                    {
                        if (h1Pre[j] <= 0 || h1Mask[j] <= 0.0)
                        {
                            h1Err[j] = 0.0;
                            continue;
                        }

                        var sum = 0.0;
                        for (var k = 0; k < Hidden2Size; k++)
                        {
                            sum += h2Err[k] * _weightsH1H2[(k * Hidden1Size) + j];
                        }

                        h1Err[j] = sum * dropoutInvKeep;
                    }

                    // === Now update all weights using the pre-computed error signals ===

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

                    {
                        var g = outErr;
                        _mBO = (AdamBeta1 * _mBO) + ((1 - AdamBeta1) * g);
                        _vBO = (AdamBeta2 * _vBO) + ((1 - AdamBeta2) * g * g);
                        _biasOutput -= DefaultLearningRate * (_mBO / bc1) / (Math.Sqrt(_vBO / bc2) + AdamEpsilon);
                        _biasOutput = Math.Clamp(_biasOutput, -WeightClamp, WeightClamp);
                    }

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

                        {
                            var g = h4Err[k];
                            _mBH4![k] = (AdamBeta1 * _mBH4[k]) + ((1 - AdamBeta1) * g);
                            _vBH4![k] = (AdamBeta2 * _vBH4[k]) + ((1 - AdamBeta2) * g * g);
                            _biasH4[k] -= DefaultLearningRate * (_mBH4[k] / bc1) /
                                          (Math.Sqrt(_vBH4[k] / bc2) + AdamEpsilon);
                            _biasH4[k] = Math.Clamp(_biasH4[k], -WeightClamp, WeightClamp);
                        }
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

                        {
                            var g = h3Err[k];
                            _mBH3![k] = (AdamBeta1 * _mBH3[k]) + ((1 - AdamBeta1) * g);
                            _vBH3![k] = (AdamBeta2 * _vBH3[k]) + ((1 - AdamBeta2) * g * g);
                            _biasH3[k] -= DefaultLearningRate * (_mBH3[k] / bc1) /
                                          (Math.Sqrt(_vBH3[k] / bc2) + AdamEpsilon);
                            _biasH3[k] = Math.Clamp(_biasH3[k], -WeightClamp, WeightClamp);
                        }
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

                        {
                            var g = h2Err[k];
                            _mBH2![k] = (AdamBeta1 * _mBH2[k]) + ((1 - AdamBeta1) * g);
                            _vBH2![k] = (AdamBeta2 * _vBH2[k]) + ((1 - AdamBeta2) * g * g);
                            _biasH2[k] -= DefaultLearningRate * (_mBH2[k] / bc1) /
                                          (Math.Sqrt(_vBH2[k] / bc2) + AdamEpsilon);
                            _biasH2[k] = Math.Clamp(_biasH2[k], -WeightClamp, WeightClamp);
                        }
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

                        {
                            var g = h1Err[j];
                            _mBH1![j] = (AdamBeta1 * _mBH1[j]) + ((1 - AdamBeta1) * g);
                            _vBH1![j] = (AdamBeta2 * _vBH1[j]) + ((1 - AdamBeta2) * g * g);
                            _biasH1[j] -= DefaultLearningRate * (_mBH1[j] / bc1) /
                                          (Math.Sqrt(_vBH1[j] / bc2) + AdamEpsilon);
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
                        Array.Copy(_weightsH3H4, bestWH3H4, _weightsH3H4.Length);
                        Array.Copy(_biasH4, bestBH4, _biasH4.Length);
                        Array.Copy(_weightsH4O, bestWH4O, _weightsH4O.Length);
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
                            Array.Copy(bestWH3H4, _weightsH3H4, _weightsH3H4.Length);
                            Array.Copy(bestBH4, _biasH4, _biasH4.Length);
                            Array.Copy(bestWH4O, _weightsH4O, _weightsH4O.Length);
                            _biasOutput = bestBO;
                            break;
                        }
                    }
                }
            }

            // Restore the best weights whenever early stopping observed an improvement, otherwise the
            // reported _lastValidationLoss won't match the persisted model. The restore in the
            // patience-break branch only fires on early stop; running to maxEpochs may leave the
            // last-epoch weights differing from the best-observed.
            if (useEarlyStopping && bestLoss < double.MaxValue)
            {
                Array.Copy(bestWIH, _weightsIH, _weightsIH.Length);
                Array.Copy(bestBH1, _biasH1, _biasH1.Length);
                Array.Copy(bestWH1H2, _weightsH1H2, _weightsH1H2.Length);
                Array.Copy(bestBH2, _biasH2, _biasH2.Length);
                Array.Copy(bestWH2H3, _weightsH2H3, _weightsH2H3.Length);
                Array.Copy(bestBH3, _biasH3, _biasH3.Length);
                Array.Copy(bestWH3H4, _weightsH3H4, _weightsH3H4.Length);
                Array.Copy(bestBH4, _biasH4, _biasH4.Length);
                Array.Copy(bestWH4O, _weightsH4O, _weightsH4O.Length);
                _biasOutput = bestBO;
            }

            // Report the early-stopping validation loss (a genuine held-out out-of-sample number) as the
            // generalization estimate for the ensemble quality gate. Three cases matter downstream:
            //   1. useEarlyStopping = false: no validation signal (dataset too small to split). NaN maps
            //      to qualityFactor = 0.5 in EnsembleScoringStrategy (half progression) - cold-start.
            //   2. useEarlyStopping = true, bestLoss < MaxValue: healthy run; report bestLoss.
            //   3. useEarlyStopping = true, bestLoss == MaxValue: ran full length but no epoch improved
            //      (model degrading, or initial weights beat every SGD step). Report the ceiling
            //      (2× threshold) - not NaN, which would hide a degrading model behind case 1's 0.5 - so
            //      the ensemble's soft-damping rolls alpha back to alphaMin.
            // Publish standardization stats while the write lock is still held so scorers (read lock) see
            // a consistent pair; moving this out of the lock would let a scorer read old _featureMeans
            // with already-updated _featureStdDevs (or vice-versa).
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

        // Persist weights and log feature importance outside the write lock so concurrent Score()
        // callers (read lock) are not blocked by disk I/O or logging. LogFeatureImportance only reads
        // _weightsIH, stable after the write lock is released (Train is the only writer, serialized by
        // the scheduled task).
        TrySaveWeights();
        LogFeatureImportance(inputSize);

        // Compute ranking metrics outside the write lock (Score() needs read lock). Prefer the
        // caller's held-out slice so P@K/R@K/NDCG are genuine out-of-sample numbers; fall back to the
        // training set only when no held-out slice was passed or it is too small.
        var metricsSource = heldOutForMetrics is { Count: >= 2 } ? heldOutForMetrics : examples;
        var (pAtK, rAtK, nAtK) = RankingMetrics.ComputeAll(metricsSource, this);
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
    ///     MLP forward pass: input -> hidden₁ (ReLU) -> hidden₂ (ReLU) -> hidden₃ (ReLU) -> hidden₄ (ReLU) -> output (Sigmoid).
    ///     Uses pre-allocated buffers for hidden activations to avoid allocation.
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

        // Hidden layer 2: hidden1 -> hidden2 (ReLU)
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

        // Hidden layer 3: hidden2 -> hidden3 (ReLU)
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

        // Hidden layer 4: hidden3 -> hidden4 (ReLU)
        for (var m = 0; m < Hidden4Size; m++)
        {
            var sum = bH4[m];
            var baseIdx = m * Hidden3Size;
            for (var l = 0; l < Hidden3Size; l++)
            {
                sum += wH3H4[baseIdx + l] * h3Act[l];
            }

            h4Pre[m] = sum;
            h4Act[m] = sum > 0 ? sum : 0.0;
        }

        // Output layer: hidden4 -> output (Sigmoid)
        var outputZ = bO;
        for (var m = 0; m < Hidden4Size; m++)
        {
            outputZ += wH4O[m] * h4Act[m];
        }

        return Sigmoid(outputZ);
    }

    /// <summary>
    ///     Training-time forward pass that additionally applies inverted Bernoulli dropout to each
    ///     hidden layer's activations. Numerically a superset of <see cref="ForwardPass"/>: with
    ///     <paramref name="keepProbability"/> >= 1.0 (or <paramref name="invKeepScale"/> = 1.0) the output
    ///     is bit-identical to <see cref="ForwardPass"/>, so tests can pin "dropout off" without a second
    ///     code path. Only the training loop calls this, so the per-neuron mask assignment / dropout-off
    ///     branch (a hair slower than pure <see cref="ForwardPass"/>) sits in the backprop budget, not the
    ///     scoring hot path.
    ///     <para>
    ///         Inverted-dropout convention: if <c>mask[k]=1</c>, act[k] = relu(pre[k]) ×
    ///         <paramref name="invKeepScale"/>; if <c>mask[k]=0</c>, act[k] = 0. This preserves E[act]
    ///         between train and inference so the mask-free <see cref="ForwardPass"/> works at scoring
    ///         time with the same weight magnitudes.
    ///     </para>
    ///     <para>
    ///         Dropping a neuron means (a) its downstream contribution is zero (<c>actX[k]=0</c>) and
    ///         (b) its upstream gradient must also be zero - the <c>maskX[k]==0</c> guard in backprop
    ///         enforces this. When <paramref name="keepProbability"/> >= 1.0 the method short-circuits the
    ///         RNG and fills the masks with 1.0 (mathematical parity with <see cref="ForwardPass"/>).
    ///     </para>
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

        // Hidden layer 1: input -> hidden1 (ReLU + optional dropout)
        for (var j = 0; j < Hidden1Size; j++)
        {
            var sum = bH1[j];
            var baseIdx = j * inputSize;
            for (var i = 0; i < inputSize; i++)
            {
                sum += wIH[baseIdx + i] * input[i];
            }

            h1Pre[j] = sum;
            var relu = sum > 0 ? sum : 0.0;

            if (dropoutOff)
            {
                h1Mask[j] = 1.0;
                h1Act[j] = relu;
            }
            else
            {
                var keep = rng.NextDouble() < keepProbability;
                h1Mask[j] = keep ? 1.0 : 0.0;
                h1Act[j] = keep ? relu * invKeepScale : 0.0;
            }
        }

        // Hidden layer 2: hidden1 -> hidden2 (ReLU + optional dropout)
        for (var k = 0; k < Hidden2Size; k++)
        {
            var sum = bH2[k];
            var baseIdx = k * Hidden1Size;
            for (var j = 0; j < Hidden1Size; j++)
            {
                sum += wH1H2[baseIdx + j] * h1Act[j];
            }

            h2Pre[k] = sum;
            var relu = sum > 0 ? sum : 0.0;

            if (dropoutOff)
            {
                h2Mask[k] = 1.0;
                h2Act[k] = relu;
            }
            else
            {
                var keep = rng.NextDouble() < keepProbability;
                h2Mask[k] = keep ? 1.0 : 0.0;
                h2Act[k] = keep ? relu * invKeepScale : 0.0;
            }
        }

        // Hidden layer 3: hidden2 -> hidden3 (ReLU + optional dropout)
        for (var l = 0; l < Hidden3Size; l++)
        {
            var sum = bH3[l];
            var baseIdx = l * Hidden2Size;
            for (var k = 0; k < Hidden2Size; k++)
            {
                sum += wH2H3[baseIdx + k] * h2Act[k];
            }

            h3Pre[l] = sum;
            var relu = sum > 0 ? sum : 0.0;

            if (dropoutOff)
            {
                h3Mask[l] = 1.0;
                h3Act[l] = relu;
            }
            else
            {
                var keep = rng.NextDouble() < keepProbability;
                h3Mask[l] = keep ? 1.0 : 0.0;
                h3Act[l] = keep ? relu * invKeepScale : 0.0;
            }
        }

        // Hidden layer 4: hidden3 -> hidden4 (ReLU + optional dropout)
        for (var m = 0; m < Hidden4Size; m++)
        {
            var sum = bH4[m];
            var baseIdx = m * Hidden3Size;
            for (var l = 0; l < Hidden3Size; l++)
            {
                sum += wH3H4[baseIdx + l] * h3Act[l];
            }

            h4Pre[m] = sum;
            var relu = sum > 0 ? sum : 0.0;

            if (dropoutOff)
            {
                h4Mask[m] = 1.0;
                h4Act[m] = relu;
            }
            else
            {
                var keep = rng.NextDouble() < keepProbability;
                h4Mask[m] = keep ? 1.0 : 0.0;
                h4Act[m] = keep ? relu * invKeepScale : 0.0;
            }
        }

        // Output layer: hidden4 -> output (Sigmoid, no dropout on the output neuron)
        var outputZ = bO;
        for (var m = 0; m < Hidden4Size; m++)
        {
            outputZ += wH4O[m] * h4Act[m];
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
    ///     Initializes weights using He/Kaiming uniform for hidden layers (ReLU)
    ///     and Xavier/Glorot uniform for the output layer (Sigmoid). He: limit = sqrt(6/fan_in), Xavier: limit = sqrt(6/(fan_in+fan_out)).
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
            // Validate standardization arrays: both null, or both FeatureCount length. Stale mismatched
            // lengths would crash StandardizeSingleVector at scoring time.
            var hasValidStandardization = data is null
                                          || (data.FeatureMeans is null && data.FeatureStdDevs is null)
                                          || data is
                                          {
                                              FeatureMeans: { Length: CandidateFeatures.FeatureCount },
                                              FeatureStdDevs.Length: CandidateFeatures.FeatureCount
                                          };

            if (data is not null
                && hasValidStandardization
                && data is
                {
                    Version: CurrentWeightsVersion, WeightsIH.Length: Hidden1Size * CandidateFeatures.FeatureCount,
                    BiasH1.Length: Hidden1Size, WeightsH1H2.Length: Hidden2Size * Hidden1Size,
                    BiasH2.Length: Hidden2Size, WeightsH2H3.Length: Hidden3Size * Hidden2Size,
                    BiasH3.Length: Hidden3Size, WeightsH3H4.Length: Hidden4Size * Hidden3Size,
                    BiasH4.Length: Hidden4Size, WeightsH4O.Length: Hidden4Size
                })
            {
                // Reject persisted weights containing NaN/Infinity values that would poison scoring.
                if (!AllFinite(data.WeightsIH) || !AllFinite(data.BiasH1)
                    || !AllFinite(data.WeightsH1H2) || !AllFinite(data.BiasH2)
                    || !AllFinite(data.WeightsH2H3) || !AllFinite(data.BiasH3)
                    || !AllFinite(data.WeightsH3H4) || !AllFinite(data.BiasH4)
                    || !AllFinite(data.WeightsH4O) || !double.IsFinite(data.BiasOutput)
                    || (data.FeatureMeans is not null && !AllFinite(data.FeatureMeans))
                    || (data.FeatureStdDevs is not null && !AllFinite(data.FeatureStdDevs)))
                {
                    _logger?.LogWarning(
                        "NeuralScoringStrategy: Discarding persisted weights containing NaN/Infinity values");
                }
                else
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
    ///     Persists current weights to disk atomically. Thread-safe: the snapshot is cloned under a Read
    ///     lock (so concurrent Score() Read-lock calls are not blocked by disk I/O outside the lock), and
    ///     a concurrent Train() Write lock is blocked only for the O(weights.Length) copy, never for
    ///     serialization/file I/O, so it cannot interleave partial weights.
    ///     <para>
    ///         Weight mutation paths (for maintainers):
    ///         <list type="bullet">
    ///             <item><see cref="InitializeWeights"/> - constructor only, before any consumer sees the
    ///                 instance. No lock needed.</item>
    ///             <item><see cref="Train(IReadOnlyList{TrainingExample},IReadOnlyList{TrainingExample}?)"/>
    ///                 - mutates all weight/bias fields and Adam moments under the write lock, serialized
    ///                 further by the scheduled task's <c>TrainGate</c>.</item>
    ///             <item><see cref="EnsureAdamState"/> - reassigns Adam moments; only from Train under the write lock.</item>
    ///             <item><see cref="TryLoadWeights"/> - constructor only, same ordering as InitializeWeights.</item>
    ///         </list>
    ///         No other writer path exists. Any new one MUST acquire the write lock before touching a weight array.
    ///     </para>
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
    ///     Logs per-feature importance from input->hidden1 weight L2 norms:
    ///     Importance[f] = sqrt(Σ_j weightsIH[j, f]²), measuring how strongly each input drives hidden
    ///     activations. Safe outside the write lock: reads only <c>_weightsIH</c>, stable after Train
    ///     releases the lock (Train is the sole writer).
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
    ///     Releases the read lock, tolerating disposal mid-scoring. <see cref="Dispose"/> can fire on
    ///     plugin unload while another thread is mid-<see cref="Score"/>; the scoring method's outer catch
    ///     has already returned the neutral 0.5, but the finally must still unwind, and a naked
    ///     <c>ExitReadLock()</c> would throw <see cref="ObjectDisposedException"/> up through
    ///     <c>Parallel.ForEach</c> as a spurious "Failed to generate recommendations" warning. Absorbing
    ///     that one exception keeps the batch loop healthy without hiding real bugs.
    ///     <para>
    ///         Not wrapped in try/finally: we own the whole critical section and nothing between the
    ///         <c>IsReadLockHeld</c> check and <c>ExitReadLock</c> can throw, so MT1013 would only add
    ///         noise. The caller's finally guarantees this runs exactly once per acquisition.
    ///     </para>
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