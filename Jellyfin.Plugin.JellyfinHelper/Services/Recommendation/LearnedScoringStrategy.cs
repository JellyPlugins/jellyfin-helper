using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Lightweight ML scoring strategy using a single-layer perceptron with sigmoid activation.
///     Learns personalized feature weights from user watch history via mini-batch gradient descent.
///     No external ML dependencies required — pure C# implementation.
/// </summary>
/// <remarks>
///     Architecture: 7 input features → 7 weights + 1 bias → sigmoid → score (0–1).
///     Training uses binary cross-entropy loss with L2 regularization.
///     Weights are persisted to disk so they survive server restarts.
/// </remarks>
public sealed class LearnedScoringStrategy : IScoringStrategy
{
    /// <summary>Number of input features (must match <see cref="CandidateFeatures.ToVector"/> length).</summary>
    internal const int FeatureCount = 7;

    /// <summary>Default learning rate for gradient descent.</summary>
    internal const double DefaultLearningRate = 0.05;

    /// <summary>L2 regularization strength (weight decay).</summary>
    internal const double L2Lambda = 0.001;

    /// <summary>Number of training epochs per <see cref="Train"/> call.</summary>
    internal const int TrainingEpochs = 10;

    /// <summary>Minimum number of training examples required before training runs.</summary>
    internal const int MinTrainingExamples = 5;

    /// <summary>Cached JSON serializer options for weight persistence.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string? _weightsPath;
    private double _bias;
    private double[] _weights;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LearnedScoringStrategy" /> class
    ///     with default initial weights that match the heuristic strategy.
    /// </summary>
    /// <param name="weightsPath">
    ///     Optional file path for persisting learned weights.
    ///     If null, weights are kept in memory only.
    /// </param>
    public LearnedScoringStrategy(string? weightsPath = null)
    {
        _weightsPath = weightsPath;

        // Initialize with heuristic-like weights as a warm start
        _weights =
        [
            0.40, // genre similarity
            0.25, // collaborative
            0.15, // rating
            0.10, // recency
            0.10, // year proximity
            0.05, // genre count
            0.00 // isSeries (neutral start)
        ];
        _bias = -0.3; // slight negative bias (sigmoid(0) ≈ 0.43)

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
        double z;

        lock (_lock)
        {
            z = _bias;
            for (var i = 0; i < Math.Min(vector.Length, _weights.Length); i++)
            {
                z += vector[i] * _weights[i];
            }
        }

        return Sigmoid(z);
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
            for (var epoch = 0; epoch < TrainingEpochs; epoch++)
            {
                foreach (var example in examples)
                {
                    var vector = example.Features.ToVector();

                    // Forward pass
                    var z = _bias;
                    for (var i = 0; i < Math.Min(vector.Length, _weights.Length); i++)
                    {
                        z += vector[i] * _weights[i];
                    }

                    var predicted = Sigmoid(z);

                    // Error = predicted - label (gradient of BCE loss)
                    var error = predicted - example.Label;

                    // Update weights with gradient descent + L2 regularization
                    for (var i = 0; i < Math.Min(vector.Length, _weights.Length); i++)
                    {
                        var gradient = (error * vector[i]) + (L2Lambda * _weights[i]);
                        _weights[i] -= DefaultLearningRate * gradient;
                    }

                    // Update bias (no regularization on bias)
                    _bias -= DefaultLearningRate * error;
                }
            }
        }

        // Persist updated weights
        TrySaveWeights();
        return true;
    }

    /// <summary>
    ///     Sigmoid activation function: maps any real number to (0, 1).
    /// </summary>
    /// <param name="z">The linear combination input.</param>
    /// <returns>A value between 0 and 1.</returns>
    internal static double Sigmoid(double z)
    {
        // Clamp to prevent overflow
        if (z > 20)
        {
            return 1.0;
        }

        if (z < -20)
        {
            return 0.0;
        }

        return 1.0 / (1.0 + Math.Exp(-z));
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
            if (data?.Weights is { Length: FeatureCount })
            {
                _weights = data.Weights;
                _bias = data.Bias;
            }
        }
        catch
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
                Version = 1
            };

            var json = JsonSerializer.Serialize(data, SerializerOptions);
            File.WriteAllText(_weightsPath, json);
        }
        catch
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