using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

/// <summary>
///     Tests for <see cref="IScoringStrategy" /> implementations:
///     <see cref="HeuristicScoringStrategy" /> and <see cref="LearnedScoringStrategy" />.
/// </summary>
public sealed class ScoringStrategyTests : IDisposable
{
    private readonly string _tempDir;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ScoringStrategyTests" /> class.
    /// </summary>
    public ScoringStrategyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jf-helper-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    // ============================================================
    // CandidateFeatures Tests
    // ============================================================

    [Fact]
    public void CandidateFeatures_ToVector_Returns7Elements()
    {
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            CollaborativeScore = 0.5,
            RatingScore = 0.7,
            RecencyScore = 0.3,
            YearProximityScore = 0.9,
            GenreCount = 3,
            IsSeries = false
        };

        var vector = features.ToVector();

        Assert.Equal(7, vector.Length);
        Assert.Equal(0.8, vector[0]);
        Assert.Equal(0.5, vector[1]);
        Assert.Equal(0.7, vector[2]);
        Assert.Equal(0.3, vector[3]);
        Assert.Equal(0.9, vector[4]);
        Assert.Equal(0.6, vector[5]); // 3/5 = 0.6
        Assert.Equal(0.0, vector[6]); // not a series
    }

    [Fact]
    public void CandidateFeatures_ToVector_SeriesFlag()
    {
        var features = new CandidateFeatures { IsSeries = true };
        var vector = features.ToVector();
        Assert.Equal(1.0, vector[6]);
    }

    [Fact]
    public void CandidateFeatures_ToVector_GenreCountClampsToOne()
    {
        var features = new CandidateFeatures { GenreCount = 10 };
        var vector = features.ToVector();
        Assert.Equal(1.0, vector[5]); // 10/5 = 2.0, clamped to 1.0
    }

    [Fact]
    public void CandidateFeatures_ToVector_DefaultsAllZero()
    {
        var features = new CandidateFeatures();
        var vector = features.ToVector();
        foreach (var v in vector)
        {
            Assert.Equal(0.0, v);
        }
    }

    // ============================================================
    // HeuristicScoringStrategy Tests
    // ============================================================

    [Fact]
    public void Heuristic_Name_ReturnsExpected()
    {
        var strategy = new HeuristicScoringStrategy();
        Assert.Equal("Heuristic (Fixed Weights)", strategy.Name);
        Assert.Equal("strategyHeuristic", strategy.NameKey);
    }

    [Fact]
    public void Heuristic_Score_AllOnes_ReturnsOne()
    {
        var strategy = new HeuristicScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 1.0,
            CollaborativeScore = 1.0,
            RatingScore = 1.0,
            RecencyScore = 1.0,
            YearProximityScore = 1.0
        };

        var score = strategy.Score(features);

        // 0.40 + 0.25 + 0.15 + 0.10 + 0.10 = 1.0
        Assert.Equal(1.0, score, 4);
    }

    [Fact]
    public void Heuristic_Score_AllZeros_ReturnsZero()
    {
        var strategy = new HeuristicScoringStrategy();
        var features = new CandidateFeatures();

        var score = strategy.Score(features);
        Assert.Equal(0.0, score, 4);
    }

    [Fact]
    public void Heuristic_Score_GenreOnly()
    {
        var strategy = new HeuristicScoringStrategy();
        var features = new CandidateFeatures { GenreSimilarity = 0.5 };

        var score = strategy.Score(features);

        Assert.Equal(0.5 * HeuristicScoringStrategy.GenreWeight, score, 4);
    }

    [Fact]
    public void Heuristic_Score_WeightedCombination()
    {
        var strategy = new HeuristicScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            CollaborativeScore = 0.6,
            RatingScore = 0.7,
            RecencyScore = 0.5,
            YearProximityScore = 0.9
        };

        var expected =
            (0.8 * 0.40) +
            (0.6 * 0.25) +
            (0.7 * 0.15) +
            (0.5 * 0.10) +
            (0.9 * 0.10);

        Assert.Equal(expected, strategy.Score(features), 4);
    }

    [Fact]
    public void Heuristic_Train_ReturnsFalse()
    {
        var strategy = new HeuristicScoringStrategy();
        var examples = new List<TrainingExample>
        {
            new() { Features = new CandidateFeatures(), Label = 1.0 },
            new() { Features = new CandidateFeatures(), Label = 0.0 },
            new() { Features = new CandidateFeatures(), Label = 1.0 },
            new() { Features = new CandidateFeatures(), Label = 0.0 },
            new() { Features = new CandidateFeatures(), Label = 1.0 }
        };

        Assert.False(strategy.Train(examples));
    }

    // ============================================================
    // LearnedScoringStrategy Tests
    // ============================================================

    [Fact]
    public void Learned_Name_ReturnsExpected()
    {
        var strategy = new LearnedScoringStrategy();
        Assert.Equal("Learned (Adaptive ML)", strategy.Name);
        Assert.Equal("strategyLearned", strategy.NameKey);
    }

    [Fact]
    public void Learned_Score_ReturnsValueBetweenZeroAndOne()
    {
        var strategy = new LearnedScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            CollaborativeScore = 0.5,
            RatingScore = 0.7,
            RecencyScore = 0.3,
            YearProximityScore = 0.9,
            GenreCount = 3,
            IsSeries = true
        };

        var score = strategy.Score(features);
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void Learned_Score_AllZeros_ReturnsNearSigmoidOfBias()
    {
        var strategy = new LearnedScoringStrategy();
        var features = new CandidateFeatures();

        var score = strategy.Score(features);

        // With bias = -0.3, sigmoid(-0.3) ≈ 0.4256
        Assert.InRange(score, 0.35, 0.50);
    }

    [Fact]
    public void Learned_InitialWeights_MatchesHeuristic()
    {
        var strategy = new LearnedScoringStrategy();
        var weights = strategy.CurrentWeights;

        Assert.Equal(LearnedScoringStrategy.FeatureCount, weights.Length);
        Assert.Equal(0.40, weights[0]); // genre
        Assert.Equal(0.25, weights[1]); // collaborative
        Assert.Equal(0.15, weights[2]); // rating
    }

    [Fact]
    public void Learned_Train_WithTooFewExamples_ReturnsFalse()
    {
        var strategy = new LearnedScoringStrategy();
        var examples = new List<TrainingExample>
        {
            new() { Features = new CandidateFeatures { GenreSimilarity = 1.0 }, Label = 1.0 },
            new() { Features = new CandidateFeatures(), Label = 0.0 }
        };

        Assert.False(strategy.Train(examples));
    }

    [Fact]
    public void Learned_Train_UpdatesWeights()
    {
        var strategy = new LearnedScoringStrategy();
        var initialWeights = strategy.CurrentWeights;
        var initialBias = strategy.CurrentBias;

        // Create training data: positive examples with high genre similarity
        var examples = new List<TrainingExample>();
        for (var i = 0; i < 10; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = 0.9,
                    CollaborativeScore = 0.1,
                    RatingScore = 0.8,
                    RecencyScore = 0.5,
                    YearProximityScore = 0.7,
                    GenreCount = 3,
                    IsSeries = false
                },
                Label = 1.0
            });
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = 0.1,
                    CollaborativeScore = 0.9,
                    RatingScore = 0.2,
                    RecencyScore = 0.8,
                    YearProximityScore = 0.3,
                    GenreCount = 1,
                    IsSeries = true
                },
                Label = 0.0
            });
        }

        var trained = strategy.Train(examples);

        Assert.True(trained);

        var updatedWeights = strategy.CurrentWeights;
        var updatedBias = strategy.CurrentBias;

        // Weights should have changed
        var anyChanged = false;
        for (var i = 0; i < initialWeights.Length; i++)
        {
            if (Math.Abs(initialWeights[i] - updatedWeights[i]) > 1e-10)
            {
                anyChanged = true;
                break;
            }
        }

        Assert.True(anyChanged, "Training should modify at least some weights");
        Assert.NotEqual(initialBias, updatedBias);
    }

    [Fact]
    public void Learned_Train_ImprovesScoresForPositiveExamples()
    {
        var strategy = new LearnedScoringStrategy();

        var positiveFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.9,
            RatingScore = 0.8,
            RecencyScore = 0.7,
            YearProximityScore = 0.8,
            GenreCount = 4
        };

        var scoreBefore = strategy.Score(positiveFeatures);

        var examples = new List<TrainingExample>();
        for (var i = 0; i < 20; i++)
        {
            examples.Add(new TrainingExample { Features = positiveFeatures, Label = 1.0 });
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures { GenreSimilarity = 0.1, RatingScore = 0.2 },
                Label = 0.0
            });
        }

        strategy.Train(examples);
        var scoreAfter = strategy.Score(positiveFeatures);

        // Score for positive examples should increase after training
        Assert.True(scoreAfter > scoreBefore,
            $"Score should increase after training: {scoreBefore:F4} → {scoreAfter:F4}");
    }

    // ============================================================
    // Sigmoid Tests
    // ============================================================

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(25.0, 1.0)]
    [InlineData(-25.0, 0.0)]
    public void Sigmoid_KnownValues(double input, double expected)
    {
        Assert.Equal(expected, LearnedScoringStrategy.Sigmoid(input), 4);
    }

    [Fact]
    public void Sigmoid_PositiveInput_GreaterThanHalf()
    {
        Assert.True(LearnedScoringStrategy.Sigmoid(1.0) > 0.5);
    }

    [Fact]
    public void Sigmoid_NegativeInput_LessThanHalf()
    {
        Assert.True(LearnedScoringStrategy.Sigmoid(-1.0) < 0.5);
    }

    // ============================================================
    // Weight Persistence Tests
    // ============================================================

    [Fact]
    public void Learned_PersistsWeights_ToFile()
    {
        var weightsPath = Path.Combine(_tempDir, "weights.json");
        var strategy = new LearnedScoringStrategy(weightsPath);

        var examples = new List<TrainingExample>();
        for (var i = 0; i < 10; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = 0.9,
                    RatingScore = 0.8,
                    GenreCount = 3
                },
                Label = 1.0
            });
        }

        strategy.Train(examples);

        Assert.True(File.Exists(weightsPath), "Weights file should be created after training");

        var json = File.ReadAllText(weightsPath);
        Assert.Contains("Weights", json);
        Assert.Contains("Bias", json);
        Assert.Contains("Version", json);
    }

    [Fact]
    public void Learned_LoadsWeights_FromFile()
    {
        var weightsPath = Path.Combine(_tempDir, "weights2.json");

        // Train and save
        var strategy1 = new LearnedScoringStrategy(weightsPath);
        var examples = new List<TrainingExample>();
        for (var i = 0; i < 10; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = 0.9,
                    CollaborativeScore = 0.8,
                    RatingScore = 0.7,
                    GenreCount = 2
                },
                Label = 1.0
            });
        }

        strategy1.Train(examples);
        var savedWeights = strategy1.CurrentWeights;
        var savedBias = strategy1.CurrentBias;

        // Load into new instance
        var strategy2 = new LearnedScoringStrategy(weightsPath);
        var loadedWeights = strategy2.CurrentWeights;
        var loadedBias = strategy2.CurrentBias;

        for (var i = 0; i < savedWeights.Length; i++)
        {
            Assert.Equal(savedWeights[i], loadedWeights[i], 10);
        }

        Assert.Equal(savedBias, loadedBias, 10);
    }

    [Fact]
    public void Learned_GracefulFallback_OnCorruptFile()
    {
        var weightsPath = Path.Combine(_tempDir, "corrupt.json");
        File.WriteAllText(weightsPath, "not valid json {{{");

        // Should not throw, should use default weights
        var strategy = new LearnedScoringStrategy(weightsPath);
        var weights = strategy.CurrentWeights;

        Assert.Equal(LearnedScoringStrategy.FeatureCount, weights.Length);
        Assert.Equal(0.40, weights[0]); // default genre weight
    }

    [Fact]
    public void Learned_NullPath_WorksInMemoryOnly()
    {
        var strategy = new LearnedScoringStrategy(null);

        var examples = new List<TrainingExample>();
        for (var i = 0; i < 10; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures { GenreSimilarity = 0.9 },
                Label = 1.0
            });
        }

        // Should not throw
        Assert.True(strategy.Train(examples));
    }

    // ============================================================
    // TrainingExample Tests
    // ============================================================

    [Fact]
    public void TrainingExample_DefaultLabel_IsZero()
    {
        var example = new TrainingExample { Features = new CandidateFeatures() };
        Assert.Equal(0.0, example.Label);
    }
}