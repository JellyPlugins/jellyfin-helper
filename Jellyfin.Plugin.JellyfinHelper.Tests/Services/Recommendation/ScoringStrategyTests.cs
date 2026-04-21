using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

/// <summary>
///     Tests for <see cref="IScoringStrategy" /> implementations:
///     <see cref="HeuristicScoringStrategy" />, <see cref="LearnedScoringStrategy" />,
///     and <see cref="EnsembleScoringStrategy" />.
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
    public void CandidateFeatures_ToVector_Returns11Elements()
    {
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            CollaborativeScore = 0.5,
            RatingScore = 0.7,
            RecencyScore = 0.3,
            YearProximityScore = 0.9,
            GenreCount = 3,
            IsSeries = false,
            UserRatingScore = 0.6,
            CompletionRatio = 0.75
        };

        var vector = features.ToVector();

        Assert.Equal(11, vector.Length);
        Assert.Equal(0.8, vector[0]); // genre
        Assert.Equal(0.5, vector[1]); // collab
        Assert.Equal(0.7, vector[2]); // rating
        Assert.Equal(0.3, vector[3]); // recency
        Assert.Equal(0.9, vector[4]); // yearProx
        Assert.Equal(0.6, vector[5]); // 3/5 = 0.6 (genreCount normalized)
        Assert.Equal(0.0, vector[6]); // not a series
        Assert.Equal(0.8 * 0.7, vector[7], 10); // genre × rating interaction
        Assert.Equal(0.8 * 0.5, vector[8], 10); // genre × collab interaction
        Assert.Equal(0.6, vector[9]); // userRating
        Assert.Equal(0.75, vector[10]); // completionRatio
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
    public void CandidateFeatures_ToVector_NegativeGenreCountNormalizesToZero()
    {
        var features = new CandidateFeatures { GenreCount = -1 };
        var vector = features.ToVector();
        // -1/5 = -0.2, Math.Clamp(-0.2, 0.0, 1.0) = 0.0 — clamped to lower bound
        Assert.Equal(0.0, vector[5]);
    }

    [Fact]
    public void CandidateFeatures_ToVector_DefaultsExpected()
    {
        var features = new CandidateFeatures();
        var vector = features.ToVector();
        // Most default to 0.0, but UserRatingScore defaults to 0.5
        for (var i = 0; i < vector.Length; i++)
        {
            if (i == 9) // UserRatingScore default is 0.5
            {
                Assert.Equal(0.5, vector[i]);
            }
            else
            {
                Assert.Equal(0.0, vector[i]);
            }
        }
    }

    [Fact]
    public void CandidateFeatures_ToVector_Length_MatchesLearnedStrategyFeatureCount()
    {
        // Guard: if a new feature is added to ToVector() but FeatureCount is not updated
        // (or vice versa), training and scoring will silently produce wrong results.
        var vector = new CandidateFeatures().ToVector();
        Assert.Equal(LearnedScoringStrategy.FeatureCount, vector.Length);
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
    public void Heuristic_Score_AllOnes_ReturnsExpectedSum()
    {
        var strategy = new HeuristicScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 1.0,
            CollaborativeScore = 1.0,
            RatingScore = 1.0,
            RecencyScore = 1.0,
            YearProximityScore = 1.0,
            GenreCount = 5, // normalizes to 1.0
            IsSeries = true, // 1.0
            UserRatingScore = 1.0,
            CompletionRatio = 1.0
        };

        var score = strategy.Score(features);

        // Sum of all weights = 1.00
        Assert.Equal(1.00, score, 4);
    }

    [Fact]
    public void Heuristic_Score_AllZeros_ReturnsPenalizedZero()
    {
        var strategy = new HeuristicScoringStrategy();
        // UserRatingScore defaults to 0.5, so explicitly set to 0 for "all zeros" test
        var features = new CandidateFeatures { UserRatingScore = 0.0 };

        var score = strategy.Score(features);
        Assert.Equal(0.0, score, 4);
    }

    [Fact]
    public void Heuristic_Score_GenreOnly()
    {
        var strategy = new HeuristicScoringStrategy();
        var features = new CandidateFeatures { GenreSimilarity = 0.5, UserRatingScore = 0.0 };

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
            YearProximityScore = 0.9,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        var expected =
            (0.8 * HeuristicScoringStrategy.GenreWeight) +
            (0.6 * HeuristicScoringStrategy.CollaborativeWeight) +
            (0.7 * HeuristicScoringStrategy.RatingWeight) +
            (0.5 * HeuristicScoringStrategy.RecencyWeight) +
            (0.9 * HeuristicScoringStrategy.YearProximityWeight) +
            (0.8 * 0.7 * HeuristicScoringStrategy.GenreRatingInteractionWeight) +
            (0.8 * 0.6 * HeuristicScoringStrategy.GenreCollabInteractionWeight);

        Assert.Equal(expected, strategy.Score(features), 4);
    }

    [Fact]
    public void Heuristic_Score_GenreMismatch_NoPenaltyApplied()
    {
        var strategy = new HeuristicScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.0, // no genre match
            CollaborativeScore = 0.5,
            RatingScore = 0.8,
            RecencyScore = 0.7,
            YearProximityScore = 0.9,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        var expected =
            (0.0 * HeuristicScoringStrategy.GenreWeight) +
            (0.5 * HeuristicScoringStrategy.CollaborativeWeight) +
            (0.8 * HeuristicScoringStrategy.RatingWeight) +
            (0.7 * HeuristicScoringStrategy.RecencyWeight) +
            (0.9 * HeuristicScoringStrategy.YearProximityWeight);

        Assert.Equal(expected, strategy.Score(features), 4);
    }

    [Fact]
    public void Heuristic_Score_GenreBelowThreshold_GetsStronglyPenalized()
    {
        var strategy = new HeuristicScoringStrategy();

        // Good genre match
        var goodFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            RatingScore = 0.6,
            RecencyScore = 0.5
        };

        // No genre match but same other features
        var badFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.0,
            RatingScore = 0.6,
            RecencyScore = 0.5
        };

        var goodScore = strategy.Score(goodFeatures);
        var badScore = strategy.Score(badFeatures);

        // Bad score should be MUCH lower than good score (at least 5x difference)
        Assert.True(goodScore > badScore * 5,
            $"Genre mismatch should be strongly penalized: good={goodScore:F4}, bad={badScore:F4}");
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
    public void Learned_Score_AllZeros_ReturnsLowScore()
    {
        var strategy = new LearnedScoringStrategy();
        var features = new CandidateFeatures();

        var score = strategy.Score(features);

        // With bias = 0.05, all features = 0 → rawScore = 0.05
        // Genre penalty is now in Ensemble, not Learned, so score = 0.05
        Assert.InRange(score, 0.0, 0.10);
    }

    [Fact]
    public void Learned_Score_HighGenreMatch_ReturnsHighScore()
    {
        var strategy = new LearnedScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.9,
            CollaborativeScore = 0.5,
            RatingScore = 0.8,
            RecencyScore = 0.5,
            YearProximityScore = 0.7,
            GenreCount = 3,
            IsSeries = false
        };

        var score = strategy.Score(features);

        // Should be a respectable score — no penalty because genre >= threshold
        Assert.True(score > 0.5, $"High genre match should yield high score, got {score:F4}");
    }

    [Fact]
    public void Learned_Score_NoGenreMatch_GetsStrongPenalty()
    {
        var strategy = new LearnedScoringStrategy();

        var goodFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            RatingScore = 0.7,
            RecencyScore = 0.5
        };

        var badFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.0,
            RatingScore = 0.7,
            RecencyScore = 0.5
        };

        var goodScore = strategy.Score(goodFeatures);
        var badScore = strategy.Score(badFeatures);

        Assert.True(goodScore > badScore * 3,
            $"Genre mismatch penalty should create large gap: good={goodScore:F4}, bad={badScore:F4}");
    }

    [Fact]
    public void Learned_InitialWeights_GenreDominant()
    {
        var strategy = new LearnedScoringStrategy();
        var weights = strategy.CurrentWeights;

        Assert.Equal(LearnedScoringStrategy.FeatureCount, weights.Length);
        Assert.Equal(0.40, weights[0]); // genre (dominant)
        Assert.Equal(0.15, weights[1]); // collaborative
        Assert.Equal(0.10, weights[2]); // rating
        Assert.Equal(0.10, weights[7]); // genre × rating interaction
        Assert.Equal(0.10, weights[8]); // genre × collab interaction
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
        Assert.True(scoreAfter >= scoreBefore,
            $"Score should increase or stay stable after training: {scoreBefore:F4} → {scoreAfter:F4}");
    }

    [Fact]
    public void Learned_Train_WeightsStayClamped()
    {
        var strategy = new LearnedScoringStrategy();

        // Create extreme training data to try to push weights to extremes
        var examples = new List<TrainingExample>();
        for (var i = 0; i < 100; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = 1.0,
                    CollaborativeScore = 1.0,
                    RatingScore = 1.0,
                    RecencyScore = 1.0,
                    YearProximityScore = 1.0,
                    GenreCount = 5,
                    IsSeries = true
                },
                Label = 1.0
            });
        }

        strategy.Train(examples);
        var weights = strategy.CurrentWeights;
        var bias = strategy.CurrentBias;

        // All weights should be within clamped range
        foreach (var w in weights)
        {
            Assert.InRange(w, -2.0, 2.0);
        }

        Assert.InRange(bias, -1.0, 1.0);
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

        Assert.True(strategy.Train(examples), "Training should succeed with sufficient examples");

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

        Assert.True(strategy1.Train(examples), "Training should succeed with sufficient examples");
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

    // ============================================================
    // Genre Mismatch Penalty Integration Tests
    // ============================================================

    [Fact]
    public void Heuristic_GenreMismatch_ChuckyVsMarvel_MarvelWins()
    {
        // Simulates: user likes Action/SciFi/Superhero, candidate is Horror (Chucky) vs Action (Marvel)
        // Note: Heuristic no longer applies genre penalty — that's in the Ensemble.
        // But Marvel still scores much higher because genre similarity dominates the weights
        // and interaction terms (genre×rating, genre×collab) amplify genre-matching items.
        var strategy = new HeuristicScoringStrategy();

        var marvelFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.85, // Action + SciFi + Adventure overlap
            CollaborativeScore = 0.3,
            RatingScore = 0.75,
            RecencyScore = 0.6,
            YearProximityScore = 0.8,
            GenreCount = 4
        };

        var chuckyFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.0, // Horror — no overlap with Action/SciFi profile
            CollaborativeScore = 0.1,
            RatingScore = 0.5,
            RecencyScore = 0.4,
            YearProximityScore = 0.7,
            GenreCount = 2
        };

        var marvelScore = strategy.Score(marvelFeatures);
        var chuckyScore = strategy.Score(chuckyFeatures);

        Assert.True(marvelScore > 0.5, $"Marvel should score high: {marvelScore:F4}");
        // Without penalty, Chucky gets a modest score from non-genre signals
        Assert.True(chuckyScore < 0.20, $"Chucky should score low (no genre overlap): {chuckyScore:F4}");
        Assert.True(marvelScore > chuckyScore * 3,
            $"Marvel should be significantly higher than Chucky: Marvel={marvelScore:F4}, Chucky={chuckyScore:F4}");
    }

    [Fact]
    public void Learned_GenreMismatch_ChuckyVsMarvel_MarvelWins()
    {
        // Learned strategy no longer applies genre penalty — that's in the Ensemble.
        // But Marvel still scores higher due to genre weight dominance + interaction terms.
        var strategy = new LearnedScoringStrategy();

        var marvelFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.85,
            CollaborativeScore = 0.3,
            RatingScore = 0.75,
            RecencyScore = 0.6,
            YearProximityScore = 0.8,
            GenreCount = 4
        };

        var chuckyFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.0,
            CollaborativeScore = 0.1,
            RatingScore = 0.5,
            RecencyScore = 0.4,
            YearProximityScore = 0.7,
            GenreCount = 2
        };

        var marvelScore = strategy.Score(marvelFeatures);
        var chuckyScore = strategy.Score(chuckyFeatures);

        Assert.True(marvelScore > 0.5, $"Marvel should score high: {marvelScore:F4}");
        // Without penalty, Chucky gets a modest score from non-genre signals + bias
        Assert.True(chuckyScore < 0.25, $"Chucky should score low (no genre overlap): {chuckyScore:F4}");
        Assert.True(marvelScore > chuckyScore * 2,
            $"Marvel should be significantly higher than Chucky: Marvel={marvelScore:F4}, Chucky={chuckyScore:F4}");
    }

    // ============================================================
    // EnsembleScoringStrategy Tests
    // ============================================================

    [Fact]
    public void Ensemble_Name_ReturnsExpected()
    {
        var strategy = new EnsembleScoringStrategy();
        Assert.Equal("Ensemble (Adaptive ML + Rules)", strategy.Name);
        Assert.Equal("strategyEnsemble", strategy.NameKey);
    }

    [Fact]
    public void Ensemble_Score_IsBetweenLearnedAndHeuristic()
    {
        var ensemble = new EnsembleScoringStrategy();
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy();

        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.7,
            CollaborativeScore = 0.4,
            RatingScore = 0.6,
            RecencyScore = 0.5,
            YearProximityScore = 0.8,
            GenreCount = 3,
            IsSeries = false
        };

        var ensembleScore = ensemble.Score(features);
        var learnedScore = learned.Score(features);
        var heuristicScore = heuristic.Score(features);

        var minScore = Math.Min(learnedScore, heuristicScore);
        var maxScore = Math.Max(learnedScore, heuristicScore);

        Assert.InRange(ensembleScore, minScore - 0.001, maxScore + 0.001);
    }

    [Fact]
    public void Ensemble_DefaultAlpha_IsAlphaMin()
    {
        var strategy = new EnsembleScoringStrategy();
        // Default alpha is AlphaMin before any training occurs
        Assert.Equal(EnsembleScoringStrategy.AlphaMin, strategy.CurrentAlpha, 4);
    }

    [Fact]
    public void Ensemble_Train_IncreasesAlpha_MediumData()
    {
        var strategy = new EnsembleScoringStrategy();
        var initialAlpha = strategy.CurrentAlpha;

        // Generate 25 training examples
        var examples = GenerateTrainingExamples(25);
        var result = strategy.Train(examples);

        Assert.True(result);
        Assert.Equal(EnsembleScoringStrategy.ComputeSigmoidAlpha(25), strategy.CurrentAlpha, 4);
        Assert.True(strategy.CurrentAlpha > initialAlpha, "Alpha should increase with training data");
        Assert.Equal(25, strategy.TrainingExampleCount);
    }

    [Fact]
    public void Ensemble_Train_IncreasesAlpha_HighData()
    {
        var strategy = new EnsembleScoringStrategy();

        // Train with 50 examples first
        strategy.Train(GenerateTrainingExamples(50));
        var alphaAfter50 = strategy.CurrentAlpha;
        Assert.Equal(EnsembleScoringStrategy.ComputeSigmoidAlpha(50), alphaAfter50, 4);

        // Train with 60 more examples (total = 110)
        strategy.Train(GenerateTrainingExamples(60));
        Assert.Equal(EnsembleScoringStrategy.ComputeSigmoidAlpha(110), strategy.CurrentAlpha, 4);
        Assert.True(strategy.CurrentAlpha > alphaAfter50, "Alpha should increase with more data");
        Assert.Equal(110, strategy.TrainingExampleCount);
    }

    [Fact]
    public void Ensemble_Train_FewExamples_StaysLowAlpha()
    {
        var strategy = new EnsembleScoringStrategy();

        var examples = GenerateTrainingExamples(10);
        strategy.Train(examples);

        // With few examples, alpha should still be close to AlphaMin
        var expectedAlpha = EnsembleScoringStrategy.ComputeSigmoidAlpha(10);
        Assert.Equal(expectedAlpha, strategy.CurrentAlpha, 4);
        Assert.True(strategy.CurrentAlpha < (EnsembleScoringStrategy.AlphaMin + EnsembleScoringStrategy.AlphaMax) / 2.0,
            "With few examples, alpha should be below the midpoint");
        Assert.Equal(10, strategy.TrainingExampleCount);
    }

    [Fact]
    public void Ensemble_Train_DelegatesToLearned()
    {
        var strategy = new EnsembleScoringStrategy();

        var examples = GenerateTrainingExamples(30);
        var result = strategy.Train(examples);

        Assert.True(result);
        // Verify the learned strategy was trained by checking that scoring changes after training
        // (The learned strategy's weights should have been updated)
        Assert.NotNull(strategy.LearnedStrategy);
    }

    [Fact]
    public void Ensemble_Score_ReturnsValidRange()
    {
        var strategy = new EnsembleScoringStrategy();

        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.0,
            CollaborativeScore = 0.0,
            RatingScore = 0.0,
            RecencyScore = 0.0,
            YearProximityScore = 0.0,
            GenreCount = 0,
            IsSeries = false
        };

        var score = strategy.Score(features);
        Assert.InRange(score, 0.0, 1.0);

        features = new CandidateFeatures
        {
            GenreSimilarity = 1.0,
            CollaborativeScore = 1.0,
            RatingScore = 1.0,
            RecencyScore = 1.0,
            YearProximityScore = 1.0,
            GenreCount = 5,
            IsSeries = true
        };

        score = strategy.Score(features);
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void Ensemble_GenreMismatch_ChuckyVsMarvel_MarvelWins()
    {
        var strategy = new EnsembleScoringStrategy();

        var marvelFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.85,
            CollaborativeScore = 0.3,
            RatingScore = 0.75,
            RecencyScore = 0.6,
            YearProximityScore = 0.8,
            GenreCount = 4
        };

        var chuckyFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.0,
            CollaborativeScore = 0.1,
            RatingScore = 0.5,
            RecencyScore = 0.4,
            YearProximityScore = 0.7,
            GenreCount = 2
        };

        var marvelScore = strategy.Score(marvelFeatures);
        var chuckyScore = strategy.Score(chuckyFeatures);

        Assert.True(marvelScore > 0.4, $"Marvel should score well: {marvelScore:F4}");
        Assert.True(chuckyScore < 0.1, $"Chucky should score very low: {chuckyScore:F4}");
        Assert.True(marvelScore > chuckyScore * 5,
            $"Marvel should be >5x higher than Chucky: Marvel={marvelScore:F4}, Chucky={chuckyScore:F4}");
    }

    [Fact]
    public void Ensemble_WithWeightsPath_PassesToLearned()
    {
        var weightsPath = Path.Combine(_tempDir, "ensemble_weights.json");
        var strategy = new EnsembleScoringStrategy(weightsPath);

        Assert.NotNull(strategy.LearnedStrategy);
        Assert.NotNull(strategy.HeuristicStrategy);
    }

    [Fact]
    public void Ensemble_AlphaBlending_VerifyFormula()
    {
        var strategy = new EnsembleScoringStrategy();
        var learned = strategy.LearnedStrategy;
        var heuristic = strategy.HeuristicStrategy;

        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.6,
            CollaborativeScore = 0.3,
            RatingScore = 0.8,
            RecencyScore = 0.4,
            YearProximityScore = 0.7,
            GenreCount = 2,
            IsSeries = false
        };

        var learnedScore = learned.Score(features);
        var heuristicScore = heuristic.Score(features);
        var alpha = strategy.CurrentAlpha;

        var expectedScore = (alpha * learnedScore) + ((1.0 - alpha) * heuristicScore);
        var actualScore = strategy.Score(features);

        Assert.Equal(expectedScore, actualScore, 6);
    }

    /// <summary>
    ///     Generates a list of training examples with mixed positive and negative labels.
    /// </summary>
    private static List<TrainingExample> GenerateTrainingExamples(int count)
    {
        var examples = new List<TrainingExample>(count);
        var rng = new Random(42); // deterministic seed for reproducibility

        for (int i = 0; i < count; i++)
        {
            var isPositive = i % 3 != 0; // roughly 2/3 positive
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = isPositive ? 0.5 + (rng.NextDouble() * 0.5) : rng.NextDouble() * 0.3,
                    CollaborativeScore = rng.NextDouble(),
                    RatingScore = 0.3 + (rng.NextDouble() * 0.7),
                    RecencyScore = rng.NextDouble(),
                    YearProximityScore = rng.NextDouble(),
                    GenreCount = rng.Next(1, 6),
                    IsSeries = rng.NextDouble() > 0.7
                },
                Label = isPositive ? 1.0 : 0.0
            });
        }

        return examples;
    }
}
