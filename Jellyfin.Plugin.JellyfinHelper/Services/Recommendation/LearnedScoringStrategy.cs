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
///     Genre-mismatch penalties are NOT applied here — they are handled centrally by the
///     ensemble layer to avoid double-penalization.
///     No external ML dependencies required — pure C# implementation.
/// </summary>
/// <remarks>
///     Architecture: 9 input features → 9 weights + 1 bias → clamp(0,1) → score (0–1).
///     Features include 2 interaction terms (genre×rating, genre×collab).
///     Training uses mean squared error (MSE) loss with L2 regularization.
///     Weights are persisted to disk so they survive server restarts.
/// </remarks>
public sealed class LearnedScoringStrategy : IScoringStrategy
{
    /// <summary>Number of input features (must match <see cref="CandidateFeatures.ToVector"/> length).</summary>
    internal const int FeatureCount = 11;

    /// <summary>Default learning rate for gradient descent.</summary>
    internal const double DefaultLearningRate = 0.02;

    /// <summary>L2 regularization strength (weight decay).</summary>
    internal const double L2Lambda = 0.001;

    /// <summary>Number of training epochs per <see cref="Train"/> call.</summary>
    internal const int TrainingEpochs = 15;

    /// <summary>Minimum number of training examples required before training runs.</summary>
    internal const int MinTrainingExamples = 5;

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
            0.35, // genre similarity (dominant signal)
            0.12, // collaborative
            0.08, // community rating
            0.05, // recency
            0.05, // year proximity
            0.05, // genre count
            0.00, // isSeries (neutral start — no inherent preference)
            0.08, // genre × rating interaction
            0.08, // genre × collaborative interaction
            0.10, // user personal rating (stronger than community rating)
            0.04 // completion ratio (penalizes abandoned items)
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
        return ScoreWithExplanation(features).FinalScore;
    }

    /// <inheritdoc />
    public ScoreExplanation ScoreWithExplanation(CandidateFeatures features)
    {
        var vector = features.ToVector();
        double rawScore;
        double genreContrib, collabContrib, ratingContrib, recencyContrib;
        double yearProxContrib, userRatingContrib, interactionContrib;

        lock (_lock)
        {
            rawScore = _bias;
            var len = Math.Min(vector.Length, _weights.Length);

            // Compute individual contributions
            genreContrib = len > 0 ? vector[0] * _weights[0] : 0;
            collabContrib = len > 1 ? vector[1] * _weights[1] : 0;
            ratingContrib = len > 2 ? vector[2] * _weights[2] : 0;
            recencyContrib = len > 3 ? vector[3] * _weights[3] : 0;
            yearProxContrib = len > 4 ? vector[4] * _weights[4] : 0;
            userRatingContrib = len > 9 ? vector[9] * _weights[9] : 0;

            // Interaction + minor features (genreCount, isSeries, genre×rating, genre×collab, completionRatio)
            interactionContrib = 0;
            for (var i = 5; i < len; i++)
            {
                if (i == 9)
                {
                    continue; // userRating already counted separately
                }

                interactionContrib += vector[i] * _weights[i];
            }

            rawScore += genreContrib + collabContrib + ratingContrib + recencyContrib
                + yearProxContrib + userRatingContrib + interactionContrib;
        }

        var score = Math.Clamp(rawScore, 0.0, 1.0);

        // Determine dominant signal
        var dominant = "genre";
        var maxContrib = genreContrib;
        if (collabContrib > maxContrib) { dominant = "collaborative"; maxContrib = collabContrib; }
        if (ratingContrib > maxContrib) { dominant = "communityRating"; maxContrib = ratingContrib; }
        if (userRatingContrib > maxContrib) { dominant = "userRating"; maxContrib = userRatingContrib; }
        if (recencyContrib > maxContrib) { dominant = "recency"; maxContrib = recencyContrib; }
        if (yearProxContrib > maxContrib) { dominant = "yearProximity"; }

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
            DominantSignal = dominant,
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
            // Create a mutable index list for Fisher-Yates shuffling each epoch.
            // Shuffling prevents order-dependent bias in stochastic gradient descent
            // (last examples in a fixed order would disproportionately influence weights).
            var indices = new int[examples.Count];
            for (var j = 0; j < indices.Length; j++)
            {
                indices[j] = j;
            }

            var rng = new Random();

            for (var epoch = 0; epoch < TrainingEpochs; epoch++)
            {
                // Fisher-Yates shuffle for this epoch
                for (var j = indices.Length - 1; j > 0; j--)
                {
                    var k = rng.Next(j + 1);
                    (indices[j], indices[k]) = (indices[k], indices[j]);
                }

                foreach (var idx in indices)
                {
                    var example = examples[idx];
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
                Version = 3
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