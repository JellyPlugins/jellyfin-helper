using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Adaptive ML scoring strategy using a linear model with learned weights.
///     Learns personalized feature weights from user watch history via mini-batch gradient descent.
///     Genre-mismatch penalties are NOT applied here — they are handled centrally by the
///     ensemble layer to avoid double-penalization.
///     No external ML dependencies required — pure C# implementation.
/// </summary>
/// <remarks>
///     Architecture: 11 input features → 11 weights + 1 bias → clamp(0,1) → score (0–1).
///     Features include 2 interaction terms (genre×rating, genre×collab).
///     Training uses mean squared error (MSE) loss with L2 regularization and early stopping.
///     Weights are persisted to disk so they survive server restarts.
/// </remarks>
public sealed class LearnedScoringStrategy : IScoringStrategy
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

    /// <summary>Minimum number of validation examples required for early stopping.</summary>
    internal const int MinValidationExamples = 2;

    /// <summary>Cached JSON serializer options for weight persistence.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly string? _weightsPath;
    private double _bias;
    private double[] _weights;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LearnedScoringStrategy" /> class
    ///     with default initial weights optimized for genre-driven recommendations.
    /// </summary>
    /// <param name="weightsPath">
    ///     Optional file path for persisting learned weights.
    ///     If null, weights are kept in memory only.
    /// </param>
    public LearnedScoringStrategy(string? weightsPath = null)
    {
        _weightsPath = weightsPath;

        // Initialize with genre-dominant weights — genre match is the strongest signal
        _weights = CreateDefaultWeights();
        _bias = 0.05; // slight positive bias so perfect matches approach 1.0

        // Try to load persisted weights
        TryLoadWeights();
    }

    /// <inheritdoc />
    public string Name => "Learned (Adaptive ML)";

    /// <inheritdoc />
    public string NameKey => "strategyLearned";

    /// <summary>
    ///     Gets a copy of the current weights (for testing/debugging).
    /// </summary>
    internal double[] CurrentWeights
    {
        get
        {
            lock (_lock)
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
            lock (_lock)
            {
                return _bias;
            }
        }
    }

    /// <inheritdoc />
    public double Score(CandidateFeatures features)
    {
        var vector = features.ToVector();
        ValidateVectorLength(vector);

        lock (_lock)
        {
            return Math.Clamp(ComputeRawScore(vector, _weights, _bias), 0.0, 1.0);
        }
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        var vector = features.ToVector();
        ValidateVectorLength(vector);

        double genreContrib, collabContrib, ratingContrib, recencyContrib;
        double yearProxContrib, userRatingContrib, interactionContrib;
        double rawScore;

        lock (_lock)
        {
            rawScore = _bias;

            // Compute individual contributions using named indices
            genreContrib = vector[(int)FeatureIndex.GenreSimilarity] * _weights[(int)FeatureIndex.GenreSimilarity];
            collabContrib = vector[(int)FeatureIndex.CollaborativeScore] * _weights[(int)FeatureIndex.CollaborativeScore];
            ratingContrib = vector[(int)FeatureIndex.RatingScore] * _weights[(int)FeatureIndex.RatingScore];
            recencyContrib = vector[(int)FeatureIndex.RecencyScore] * _weights[(int)FeatureIndex.RecencyScore];
            yearProxContrib = vector[(int)FeatureIndex.YearProximityScore] * _weights[(int)FeatureIndex.YearProximityScore];
            userRatingContrib = vector[(int)FeatureIndex.UserRatingScore] * _weights[(int)FeatureIndex.UserRatingScore];

            // Interaction + minor features (genreCount, isSeries, genre×rating, genre×collab, completionRatio)
            interactionContrib = 0;
            interactionContrib += vector[(int)FeatureIndex.GenreCountNormalized] * _weights[(int)FeatureIndex.GenreCountNormalized];
            interactionContrib += vector[(int)FeatureIndex.IsSeries] * _weights[(int)FeatureIndex.IsSeries];
            interactionContrib += vector[(int)FeatureIndex.GenreRatingInteraction] * _weights[(int)FeatureIndex.GenreRatingInteraction];
            interactionContrib += vector[(int)FeatureIndex.GenreCollabInteraction] * _weights[(int)FeatureIndex.GenreCollabInteraction];
            interactionContrib += vector[(int)FeatureIndex.CompletionRatio] * _weights[(int)FeatureIndex.CompletionRatio];

            rawScore += genreContrib + collabContrib + ratingContrib + recencyContrib
                + yearProxContrib + userRatingContrib + interactionContrib;
        }

        var score = Math.Clamp(rawScore, 0.0, 1.0);

        return new ScoreExplanation
        {
            FinalScore = score,
            GenreContribution = genreContrib,
            CollaborativeContribution = collabContrib,
            RatingContribution = ratingContrib,
            RecencyContribution = recencyContrib,
            YearProximityContribution = yearProxContrib,
            UserRatingContribution = userRatingContrib,
            InteractionContribution = interactionContrib,
            GenrePenaltyMultiplier = 1.0, // No penalty in Learned — applied in Ensemble
            DominantSignal = ScoreExplanation.DetermineDominantSignal(
                genreContrib, collabContrib, ratingContrib, userRatingContrib, recencyContrib, yearProxContrib),
            StrategyName = Name
        };
    }

    /// <inheritdoc />
    public bool Train(IReadOnlyList<TrainingExample> examples)
    {
        if (examples.Count < MinTrainingExamples)
        {
            return false;
        }

        lock (_lock)
        {
            // Split into training and validation sets for early stopping
            var validationCount = Math.Max(MinValidationExamples, (int)(examples.Count * ValidationSplitRatio));
            validationCount = Math.Min(validationCount, examples.Count - MinTrainingExamples);

            // If we can't split properly, train on all data without early stopping
            var useEarlyStopping = validationCount >= MinValidationExamples
                && examples.Count - validationCount >= MinTrainingExamples;

            var rng = new Random();

            // Create shuffled index array for split
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

            int[] trainIndices;
            int[] valIndices;

            if (useEarlyStopping)
            {
                trainIndices = allIndices[..^validationCount];
                valIndices = allIndices[^validationCount..];
            }
            else
            {
                trainIndices = allIndices;
                valIndices = [];
            }

            var bestLoss = double.MaxValue;
            var patienceCounter = 0;
            var bestWeights = (double[])_weights.Clone();
            var bestBias = _bias;

            var maxEpochs = useEarlyStopping ? MaxTrainingEpochs : Math.Min(MaxTrainingEpochs, 15);

            for (var epoch = 0; epoch < maxEpochs; epoch++)
            {
                // Fisher-Yates shuffle training indices each epoch
                for (var j = trainIndices.Length - 1; j > 0; j--)
                {
                    var k = rng.Next(j + 1);
                    (trainIndices[j], trainIndices[k]) = (trainIndices[k], trainIndices[j]);
                }

                foreach (var idx in trainIndices)
                {
                    var example = examples[idx];
                    var vector = example.Features.ToVector();

                    // Forward pass — linear model
                    var z = ComputeRawScore(vector, _weights, _bias);
                    var predicted = Math.Clamp(z, 0.0, 1.0);

                    // Error = predicted - label (gradient of MSE loss)
                    var error = predicted - example.Label;

                    // Only update if not clamped (sub-gradient: skip when at boundary moving wrong way)
                    if ((z <= 0 && error < 0) || (z >= 1 && error > 0))
                    {
                        continue;
                    }

                    // Update weights with gradient descent + L2 regularization
                    var len = Math.Min(vector.Length, _weights.Length);
                    for (var i = 0; i < len; i++)
                    {
                        var gradient = (error * vector[i]) + (L2Lambda * _weights[i]);
                        _weights[i] -= DefaultLearningRate * gradient;
                        _weights[i] = Math.Clamp(_weights[i], -2.0, 2.0);
                    }

                    // Update bias (no regularization on bias)
                    _bias -= DefaultLearningRate * error;
                    _bias = Math.Clamp(_bias, -1.0, 1.0);
                }

                // Early stopping: evaluate on validation set
                if (useEarlyStopping && valIndices.Length > 0)
                {
                    var valLoss = ComputeMseLoss(examples, valIndices, _weights, _bias);

                    if (valLoss < bestLoss - 1e-6)
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
                            // Restore best weights
                            Array.Copy(bestWeights, _weights, _weights.Length);
                            _bias = bestBias;
                            break;
                        }
                    }
                }
            }
        }

        // Persist updated weights
        TrySaveWeights();
        return true;
    }

    /// <summary>
    ///     Creates the default weight array with genre-dominant initial weights.
    /// </summary>
    private static double[] CreateDefaultWeights()
    {
        var weights = new double[CandidateFeatures.FeatureCount];
        weights[(int)FeatureIndex.GenreSimilarity] = 0.35;      // dominant signal
        weights[(int)FeatureIndex.CollaborativeScore] = 0.12;
        weights[(int)FeatureIndex.RatingScore] = 0.08;
        weights[(int)FeatureIndex.RecencyScore] = 0.05;
        weights[(int)FeatureIndex.YearProximityScore] = 0.05;
        weights[(int)FeatureIndex.GenreCountNormalized] = 0.05;
        weights[(int)FeatureIndex.IsSeries] = 0.00;              // neutral start
        weights[(int)FeatureIndex.GenreRatingInteraction] = 0.08;
        weights[(int)FeatureIndex.GenreCollabInteraction] = 0.08;
        weights[(int)FeatureIndex.UserRatingScore] = 0.10;
        weights[(int)FeatureIndex.CompletionRatio] = 0.04;
        return weights;
    }

    /// <summary>
    ///     Computes the raw linear score from a feature vector, weights, and bias.
    /// </summary>
    private static double ComputeRawScore(double[] vector, double[] weights, double bias)
    {
        var score = bias;
        var len = Math.Min(vector.Length, weights.Length);
        for (var i = 0; i < len; i++)
        {
            score += vector[i] * weights[i];
        }

        return score;
    }

    /// <summary>
    ///     Computes the mean squared error loss on a subset of examples.
    /// </summary>
    private static double ComputeMseLoss(
        IReadOnlyList<TrainingExample> examples,
        int[] indices,
        double[] weights,
        double bias)
    {
        var totalLoss = 0.0;
        foreach (var idx in indices)
        {
            var vector = examples[idx].Features.ToVector();
            var predicted = Math.Clamp(ComputeRawScore(vector, weights, bias), 0.0, 1.0);
            var error = predicted - examples[idx].Label;
            totalLoss += error * error;
        }

        return totalLoss / indices.Length;
    }

    /// <summary>
    ///     Validates that a feature vector has the expected length.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the vector length doesn't match the expected feature count.</exception>
    private static void ValidateVectorLength(double[] vector)
    {
        Debug.Assert(
            vector.Length == CandidateFeatures.FeatureCount,
            $"Feature vector length mismatch: expected {CandidateFeatures.FeatureCount}, got {vector.Length}");

        if (vector.Length != CandidateFeatures.FeatureCount)
        {
            throw new ArgumentException(
                $"Feature vector length mismatch: expected {CandidateFeatures.FeatureCount}, got {vector.Length}",
                nameof(vector));
        }
    }

    /// <summary>
    ///     Tries to load persisted weights from disk.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Graceful fallback to default weights on any I/O or parse error")]
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
            if (data?.Weights is { Length: CandidateFeatures.FeatureCount })
            {
                _weights = data.Weights;
                _bias = data.Bias;
            }
        }
        catch (Exception)
        {
            // Silently fall back to default weights
        }
    }

    /// <summary>
    ///     Tries to persist current weights to disk.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Non-critical persistence — silently ignore write failures")]
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

            var data = new WeightsData
            {
                Weights = (double[])_weights.Clone(),
                Bias = _bias,
                UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Version = 4
            };

            var json = JsonSerializer.Serialize(data, SerializerOptions);
            var tempPath = _weightsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _weightsPath, overwrite: true);
        }
        catch (Exception)
        {
            // Non-critical — silently ignore
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

        /// <summary>Gets or sets the ISO 8601 timestamp of the last update.</summary>
        public string UpdatedAt { get; set; } = string.Empty;

        /// <summary>Gets or sets the schema version.</summary>
        public int Version { get; set; }
    }
}