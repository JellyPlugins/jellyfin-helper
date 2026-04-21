using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Adaptive ML scoring strategy using a linear model with learned weights.
///     Learns personalized feature weights from user watch history via mini-batch gradient descent.
///     Includes a genre-mismatch penalty that strongly down-ranks items with no genre overlap.
///     No external ML dependencies required — pure C# implementation.
/// </summary>
/// <remarks>
///     Architecture: 7 input features → 7 weights + 1 bias → clamp(0,1) → genre penalty → score (0–1).
///     Training uses mean squared error (MSE) loss with L2 regularization.
///     Weights are persisted to disk so they survive server restarts.
/// </remarks>
public sealed class LearnedScoringStrategy : IScoringStrategy
{
    /// <summary>Number of input features (must match <see cref="CandidateFeatures.ToVector"/> length).</summary>
    internal const int FeatureCount = 7;

    /// <summary>Default learning rate for gradient descent.</summary>
    internal const double DefaultLearningRate = 0.02;

    /// <summary>L2 regularization strength (weight decay).</summary>
    internal const double L2Lambda = 0.001;

    /// <summary>Number of training epochs per <see cref="Train"/> call.</summary>
    internal const int TrainingEpochs = 15;

    /// <summary>Minimum number of training examples required before training runs.</summary>
    internal const int MinTrainingExamples = 5;

    /// <summary>
    ///     Genre similarity threshold below which the genre mismatch penalty is applied.
    ///     Items with genre similarity below this value are considered poor matches.
    /// </summary>
    internal const double GenreMismatchThreshold = 0.1;

    /// <summary>
    ///     Penalty multiplier for items below the genre mismatch threshold.
    ///     Applied as a post-scoring multiplier to strongly suppress mismatched items.
    /// </summary>
    internal const double GenreMismatchPenalty = 0.15;

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
        _weights =
        [
            0.50, // genre similarity (dominant signal)
            0.20, // collaborative
            0.10, // rating
            0.05, // recency
            0.05, // year proximity
            0.05, // genre count
            0.00 // isSeries (neutral start)
        ];
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
        double rawScore;

        lock (_lock)
        {
            rawScore = _bias;
            for (var i = 0; i < Math.Min(vector.Length, _weights.Length); i++)
            {
                rawScore += vector[i] * _weights[i];
            }
        }

        // Clamp to [0, 1] — linear model, no sigmoid compression
        var score = Math.Clamp(rawScore, 0.0, 1.0);

        // Apply genre-mismatch penalty: items with nearly zero genre overlap
        // get heavily penalized so they don't pollute recommendations
        if (features.GenreSimilarity < GenreMismatchThreshold)
        {
            score *= GenreMismatchPenalty;
        }

        return score;
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

                    // Forward pass — linear model (no sigmoid)
                    var z = _bias;
                    for (var i = 0; i < Math.Min(vector.Length, _weights.Length); i++)
                    {
                        z += vector[i] * _weights[i];
                    }

                    var predicted = Math.Clamp(z, 0.0, 1.0);

                    // Error = predicted - label (gradient of MSE loss)
                    var error = predicted - example.Label;

                    // Only update if not clamped (sub-gradient: skip when at boundary moving wrong way)
                    if ((z <= 0 && error < 0) || (z >= 1 && error > 0))
                    {
                        continue;
                    }

                    // Update weights with gradient descent + L2 regularization
                    for (var i = 0; i < Math.Min(vector.Length, _weights.Length); i++)
                    {
                        var gradient = (error * vector[i]) + (L2Lambda * _weights[i]);
                        _weights[i] -= DefaultLearningRate * gradient;

                        // Clamp weights to prevent extreme values from bad training data
                        _weights[i] = Math.Clamp(_weights[i], -2.0, 2.0);
                    }

                    // Update bias (no regularization on bias)
                    _bias -= DefaultLearningRate * error;
                    _bias = Math.Clamp(_bias, -1.0, 1.0);
                }
            }
        }

        // Persist updated weights
        TrySaveWeights();
        return true;
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
                Version = 2
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