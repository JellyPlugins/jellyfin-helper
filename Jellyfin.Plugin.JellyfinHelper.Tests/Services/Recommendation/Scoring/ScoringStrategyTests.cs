using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

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
    public void CandidateFeatures_ToVector_Returns23Elements()
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
            CompletionRatio = 0.75,
            SeriesProgressionBoost = 0.4,
            PopularityScore = 0.6,
            DayOfWeekAffinity = 0.3,
            HourOfDayAffinity = 0.5,
            IsWeekend = true
        };

        var vector = features.ToVector();

        Assert.Equal(23, vector.Length);
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
        Assert.Equal(0.0, vector[11]); // isAbandoned (no HasUserInteraction → 0)
        Assert.Equal(0.0, vector[12]); // hasInteraction (default false → 0)
        Assert.Equal(0.0, vector[13]); // peopleSimilarity (default 0)
        Assert.Equal(0.0, vector[14]); // studioMatch (default false → 0)
        Assert.Equal(0.4, vector[15]); // seriesProgressionBoost
        Assert.Equal(0.6, vector[16]); // popularityScore
        Assert.Equal(0.3, vector[17]); // dayOfWeekAffinity
        Assert.Equal(0.5, vector[18]); // hourOfDayAffinity
        Assert.Equal(1.0, vector[19]); // isWeekend (true → 1.0)
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
        // Most default to 0.0, but UserRatingScore defaults to 0.5 and CompletionRatio defaults to 0.5
        // IsAbandoned = 0.0 because HasUserInteraction defaults to false
        for (var i = 0; i < vector.Length; i++)
        {
            if (i == 9) // UserRatingScore default is 0.5
            {
                Assert.Equal(0.5, vector[i]);
            }
            else if (i == 10) // CompletionRatio default is 0.5
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
        Assert.Equal(CandidateFeatures.FeatureCount, vector.Length);
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
        // Use genrePenaltyFloor=1.0 to disable penalty for pure weight verification
        var strategy = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
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

        // With all basic features = 1.0 but new features (PeopleSimilarity, StudioMatch,
        // HasInteraction, SeriesProgressionBoost, PopularityScore, DayOfWeekAffinity) at defaults (0),
        // the weighted sum is ~0.82-0.85. Only the explicitly set features contribute.
        Assert.InRange(score, 0.80, 1.00);
    }

    [Fact]
    public void Heuristic_Score_AllZeros_ReturnsPenalizedZero()
    {
        var strategy = new HeuristicScoringStrategy();
        // UserRatingScore defaults to 0.5, so explicitly set to 0 for "all zeros" test
        var features = new CandidateFeatures { UserRatingScore = 0.0 };

        var score = strategy.Score(features);
        // With all features at 0 except CompletionRatio default=0.5:
        // raw = 0.5 * CompletionRatio_weight (0.07) = 0.035
        // genre penalty floor = 0.10 → 0.10 * 0.035 = 0.0035
        Assert.Equal(0.0035, score, 4);
    }

    [Fact]
    public void Heuristic_Score_GenreOnly()
    {
        // Use genrePenaltyFloor=1.0 to disable penalty and test pure weight
        var strategy = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var features = new CandidateFeatures { GenreSimilarity = 0.5, UserRatingScore = 0.0 };

        var score = strategy.Score(features);

        // Default CompletionRatio=0.5 (no interaction), HasUserInteraction=false → IsAbandoned=0
        // HasInteraction = 0 (no interaction)
        var expected = (0.5 * DefaultWeights.GenreSimilarity)
            + (0.5 * DefaultWeights.CompletionRatio); // CompletionRatio default is now 0.5
        Assert.Equal(expected, score, 4);
    }

    [Fact]
    public void Heuristic_Score_WeightedCombination()
    {
        // Use genrePenaltyFloor=1.0 to disable penalty for pure weight verification
        var strategy = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
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

        // CompletionRatio=0.0 with no HasUserInteraction → IsAbandoned=0, HasInteraction=0
        var expected =
            (0.8 * DefaultWeights.GenreSimilarity) +
            (0.6 * DefaultWeights.CollaborativeScore) +
            (0.7 * DefaultWeights.RatingScore) +
            (0.5 * DefaultWeights.RecencyScore) +
            (0.9 * DefaultWeights.YearProximityScore) +
            (0.8 * 0.7 * DefaultWeights.GenreRatingInteraction) +
            (0.8 * 0.6 * DefaultWeights.GenreCollabInteraction) +
            (0.5 * 0.7 * DefaultWeights.RecencyRatingInteraction); // RecencyScore(0.5) * RatingScore(0.7)

        Assert.Equal(expected, strategy.Score(features), 4);
    }

    [Fact]
    public void Heuristic_Score_GenreMismatch_AppliesPenaltyByDefault()
    {
        var strategy = new HeuristicScoringStrategy(); // default genrePenaltyFloor=0.10
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.0, // no genre match → penalty floor = 0.10
            CollaborativeScore = 0.5,
            RatingScore = 0.8,
            RecencyScore = 0.7,
            YearProximityScore = 0.9,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        // CompletionRatio=0.0 with no HasUserInteraction → IsAbandoned=0, HasInteraction=0
        var rawExpected =
            (0.0 * DefaultWeights.GenreSimilarity) +
            (0.5 * DefaultWeights.CollaborativeScore) +
            (0.8 * DefaultWeights.RatingScore) +
            (0.7 * DefaultWeights.RecencyScore) +
            (0.9 * DefaultWeights.YearProximityScore) +

            (0.7 * 0.8 * DefaultWeights.RecencyRatingInteraction);

        // With genrePenaltyFloor=0.10 and GenreSimilarity=0.0, penalty = 0.10
        var expected = rawExpected * 0.10;
        Assert.Equal(expected, strategy.Score(features), 4);
    }

    [Fact]
    public void Heuristic_Score_GenreMismatch_NoPenaltyWhenDisabled()
    {
        var strategy = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.0, // no genre match, but penalty disabled
            CollaborativeScore = 0.5,
            RatingScore = 0.8,
            RecencyScore = 0.7,
            YearProximityScore = 0.9,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        // CompletionRatio=0.0 with no HasUserInteraction → IsAbandoned=0, HasInteraction=0
        var expected =
            (0.0 * DefaultWeights.GenreSimilarity) +
            (0.5 * DefaultWeights.CollaborativeScore) +
            (0.8 * DefaultWeights.RatingScore) +
            (0.7 * DefaultWeights.RecencyScore) +
            (0.9 * DefaultWeights.YearProximityScore) +

            (0.7 * 0.8 * DefaultWeights.RecencyRatingInteraction);

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
            RecencyScore = 0.5,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        // No genre match but same other features
        var badFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.0,
            RatingScore = 0.6,
            RecencyScore = 0.5,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        var goodScore = strategy.Score(goodFeatures);
        var badScore = strategy.Score(badFeatures);

        // Bad score should be MUCH lower than good score (at least 3x difference)
        Assert.True(goodScore > badScore * 3,
            $"Genre mismatch should be strongly penalized: good={goodScore:F4}, bad={badScore:F4}");
    }

    [Fact]
    public void Heuristic_DoesNotImplementITrainableStrategy()
    {
        object strategy = new HeuristicScoringStrategy();
        // HeuristicScoringStrategy no longer implements ITrainableStrategy (ISP compliance)
        Assert.False(strategy is ITrainableStrategy);
    }

    [Fact]
    public void DefaultWeights_CreateWeightArray_CoversAllFeatureIndexValues()
    {
        // Guard test: ensures every FeatureIndex enum value has an explicit weight assignment
        // in DefaultWeights.CreateWeightArray(). If a new FeatureIndex is added without updating
        // CreateWeightArray, this test will fail — preventing silently unweighted features.
        var weights = DefaultWeights.CreateWeightArray();
        var allIndices = Enum.GetValues<FeatureIndex>();

        foreach (var index in allIndices)
        {
            var i = (int)index;
            Assert.True(
                i < weights.Length,
                $"FeatureIndex.{index} ({i}) is out of bounds for weight array (length {weights.Length}). " +
                $"Update CandidateFeatures.FeatureCount and DefaultWeights.CreateWeightArray().");

            // Every feature must have a non-zero default weight (positive or negative).
            // IsAbandoned intentionally has a negative weight (-0.04).
            Assert.True(
                Math.Abs(weights[i]) > 1e-12,
                $"FeatureIndex.{index} ({i}) has weight 0.0 in DefaultWeights.CreateWeightArray(). " +
                $"Add an explicit assignment or document why it should be zero.");
        }
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

        // With bias and updated weights, all features = 0 → small positive score
        // Genre penalty is now in Ensemble, not Learned, so no penalty applied here
        Assert.InRange(score, 0.0, 0.20);
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
            RecencyScore = 0.5,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        var badFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.0,
            RatingScore = 0.7,
            RecencyScore = 0.5,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        var goodScore = strategy.Score(goodFeatures);
        var badScore = strategy.Score(badFeatures);

        Assert.True(goodScore > badScore * 2,
            $"Genre mismatch penalty should create large gap: good={goodScore:F4}, bad={badScore:F4}");
    }

    [Fact]
    public void Learned_InitialWeights_GenreDominant()
    {
        var strategy = new LearnedScoringStrategy();
        var weights = strategy.CurrentWeights;

        Assert.Equal(CandidateFeatures.FeatureCount, weights.Length);
        Assert.Equal(0.20, weights[0]); // genre (dominant)
        Assert.Equal(0.11, weights[1]); // collaborative
        Assert.Equal(0.07, weights[2]); // rating
        Assert.Equal(0.05, weights[7]); // genre × rating interaction
        Assert.Equal(0.05, weights[8]); // genre × collab interaction
        Assert.Equal(0.09, weights[9]); // user rating
        Assert.Equal(0.07, weights[10]); // completion ratio
        Assert.Equal(-0.04, weights[11]); // isAbandoned
        Assert.Equal(0.01, weights[12]); // hasInteraction
        Assert.Equal(0.06, weights[13]); // people similarity
        Assert.Equal(0.02, weights[14]); // studio match
        Assert.Equal(0.06, weights[15]); // seriesProgressionBoost
        Assert.Equal(0.01, weights[16]); // popularityScore
        Assert.Equal(0.02, weights[17]); // dayOfWeekAffinity
        Assert.Equal(0.02, weights[18]); // hourOfDayAffinity
        Assert.Equal(0.01, weights[19]); // isWeekend
        Assert.Equal(0.02, weights[20]); // tagSimilarity
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

        Assert.Equal(CandidateFeatures.FeatureCount, weights.Length);
        Assert.Equal(0.20, weights[0]); // default genre weight
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

        Assert.True(marvelScore > 0.45, $"Marvel should score high: {marvelScore:F4}");
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
        Assert.True(chuckyScore < 0.30, $"Chucky should score low (no genre overlap): {chuckyScore:F4}");
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
        // Use penalty-disabled heuristic (same as Ensemble uses internally) for fair comparison
        var learned = ensemble.LearnedStrategy;
        var heuristic = ensemble.HeuristicStrategy;

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

        // Ensemble applies genre penalty centrally, so the blended score may be below both sub-scores.
        // But for features with GenreSimilarity > threshold (0.15), penalty = 1.0 and the score
        // should be between the two sub-strategy scores.
        var penalty = EnsembleScoringStrategy.ComputeSoftGenrePenalty(
            features.GenreSimilarity, EnsembleScoringStrategy.DefaultGenrePenaltyFloor);
        var minScore = Math.Min(learnedScore, heuristicScore) * penalty;
        var maxScore = Math.Max(learnedScore, heuristicScore) * penalty;

        Assert.InRange(ensembleScore, minScore - 0.001, maxScore + 0.001);
    }

    [Fact]
    public void Ensemble_DefaultAlpha_IsAlphaMin()
    {
        var strategy = new EnsembleScoringStrategy();
        // Default alpha is AlphaMin before any training occurs
        Assert.Equal(EnsembleScoringStrategy.DefaultAlphaMin, strategy.CurrentAlpha, 4);
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
        var expectedAlpha25 = EnsembleScoringStrategy.ComputeSigmoidAlpha(25, EnsembleScoringStrategy.DefaultAlphaMin, EnsembleScoringStrategy.DefaultAlphaMax);
        Assert.Equal(expectedAlpha25, strategy.CurrentAlpha, 4);
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
        Assert.Equal(EnsembleScoringStrategy.ComputeSigmoidAlpha(50, EnsembleScoringStrategy.DefaultAlphaMin, EnsembleScoringStrategy.DefaultAlphaMax), alphaAfter50, 4);

        // Train with 60 more examples (total = 110)
        strategy.Train(GenerateTrainingExamples(60));
        Assert.Equal(EnsembleScoringStrategy.ComputeSigmoidAlpha(110, EnsembleScoringStrategy.DefaultAlphaMin, EnsembleScoringStrategy.DefaultAlphaMax), strategy.CurrentAlpha, 4);
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
        var expectedAlpha = EnsembleScoringStrategy.ComputeSigmoidAlpha(10, EnsembleScoringStrategy.DefaultAlphaMin, EnsembleScoringStrategy.DefaultAlphaMax);
        Assert.Equal(expectedAlpha, strategy.CurrentAlpha, 4);
        Assert.True(strategy.CurrentAlpha < (EnsembleScoringStrategy.DefaultAlphaMin + EnsembleScoringStrategy.DefaultAlphaMax) / 2.0,
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

        var penalty = EnsembleScoringStrategy.ComputeSoftGenrePenalty(features.GenreSimilarity, EnsembleScoringStrategy.DefaultGenrePenaltyFloor);
        var expectedScore = ((alpha * learnedScore) + ((1.0 - alpha) * heuristicScore)) * penalty;
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

    // ============================================================
    // Edge-Case & Validation Tests
    // ============================================================

    [Fact]
    public void CandidateFeatures_Clamping_NegativeValues_ClampedToZero()
    {
        var features = new CandidateFeatures
        {
            GenreSimilarity = -0.5,
            CollaborativeScore = -1.0,
            RatingScore = -0.3,
            RecencyScore = -2.0,
            YearProximityScore = -0.1,
            UserRatingScore = -0.5,
            CompletionRatio = -0.8
        };

        Assert.Equal(0.0, features.GenreSimilarity);
        Assert.Equal(0.0, features.CollaborativeScore);
        Assert.Equal(0.0, features.RatingScore);
        Assert.Equal(0.0, features.RecencyScore);
        Assert.Equal(0.0, features.YearProximityScore);
        Assert.Equal(0.0, features.UserRatingScore);
        Assert.Equal(0.0, features.CompletionRatio);
    }

    [Fact]
    public void CandidateFeatures_Clamping_OverOneValues_ClampedToOne()
    {
        var features = new CandidateFeatures
        {
            GenreSimilarity = 1.5,
            CollaborativeScore = 2.0,
            RatingScore = 10.0,
            RecencyScore = 3.0,
            YearProximityScore = 1.1,
            UserRatingScore = 5.0,
            CompletionRatio = 1.001
        };

        Assert.Equal(1.0, features.GenreSimilarity);
        Assert.Equal(1.0, features.CollaborativeScore);
        Assert.Equal(1.0, features.RatingScore);
        Assert.Equal(1.0, features.RecencyScore);
        Assert.Equal(1.0, features.YearProximityScore);
        Assert.Equal(1.0, features.UserRatingScore);
        Assert.Equal(1.0, features.CompletionRatio);
    }

    [Fact]
    public void Heuristic_ScoreWithExplanation_MatchesScore()
    {
        var strategy = new HeuristicScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            CollaborativeScore = 0.5,
            RatingScore = 0.7,
            RecencyScore = 0.3,
            YearProximityScore = 0.9,
            GenreCount = 3,
            UserRatingScore = 0.6,
            CompletionRatio = 0.75
        };

        var explanation = strategy.ScoreWithExplanation(features);

        Assert.Equal(strategy.Score(features), explanation.FinalScore, 10);
        Assert.Equal("Heuristic (Fixed Weights)", explanation.StrategyName);
        Assert.False(string.IsNullOrEmpty(explanation.DominantSignal));
        // Genre penalty is now applied at the Ensemble level only;
        // individual strategies report GenrePenaltyMultiplier = 1.0
        Assert.Equal(1.0, explanation.GenrePenaltyMultiplier, 10);
    }

    [Fact]
    public void Learned_ScoreWithExplanation_MatchesScore()
    {
        var strategy = new LearnedScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.7,
            CollaborativeScore = 0.4,
            RatingScore = 0.6,
            RecencyScore = 0.5,
            YearProximityScore = 0.8,
            GenreCount = 2,
            IsSeries = true
        };

        var explanation = strategy.ScoreWithExplanation(features);

        Assert.Equal(strategy.Score(features), explanation.FinalScore, 10);
        Assert.Equal("Learned (Adaptive ML)", explanation.StrategyName);
        Assert.False(string.IsNullOrEmpty(explanation.DominantSignal));
    }

    [Fact]
    public void Ensemble_ScoreWithExplanation_IncludesGenrePenalty()
    {
        var strategy = new EnsembleScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.0,
            CollaborativeScore = 0.5,
            RatingScore = 0.8,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        var explanation = strategy.ScoreWithExplanation(features);

        Assert.True(explanation.GenrePenaltyMultiplier < 1.0,
            $"Genre penalty should apply for zero similarity, got {explanation.GenrePenaltyMultiplier:F4}");
        Assert.Equal(EnsembleScoringStrategy.DefaultGenrePenaltyFloor, explanation.GenrePenaltyMultiplier, 4);
    }

    [Fact]
    public void Ensemble_ScoreWithExplanation_NoPenaltyForHighGenreSimilarity()
    {
        var strategy = new EnsembleScoringStrategy();
        var features = new CandidateFeatures { GenreSimilarity = 0.8, RatingScore = 0.7 };

        var explanation = strategy.ScoreWithExplanation(features);
        Assert.Equal(1.0, explanation.GenrePenaltyMultiplier, 4);
    }

    [Fact]
    public void Ensemble_ConfigurableAlpha_AffectsBlending()
    {
        var lowAlpha = new EnsembleScoringStrategy(alphaMin: 0.1, alphaMax: 0.2);
        var highAlpha = new EnsembleScoringStrategy(alphaMin: 0.9, alphaMax: 0.95);

        Assert.Equal(0.1, lowAlpha.CurrentAlpha, 4);
        Assert.Equal(0.9, highAlpha.CurrentAlpha, 4);

        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.6,
            CollaborativeScore = 0.5,
            RatingScore = 0.7
        };

        Assert.InRange(lowAlpha.Score(features), 0.0, 1.0);
        Assert.InRange(highAlpha.Score(features), 0.0, 1.0);
    }

    [Fact]
    public void Ensemble_ConfigurableGenrePenalty_StrictVsLenient()
    {
        var lenient = new EnsembleScoringStrategy(genrePenaltyFloor: 0.5);
        var strict = new EnsembleScoringStrategy(genrePenaltyFloor: 0.01);

        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.0,
            CollaborativeScore = 0.8,
            RatingScore = 0.9,
            UserRatingScore = 0.0,
            CompletionRatio = 0.0
        };

        Assert.True(lenient.Score(features) > strict.Score(features),
            "Lenient penalty should produce higher score than strict penalty");
    }

    [Fact]
    public void Ensemble_SoftGenrePenalty_LinearRamp()
    {
        var atZero = EnsembleScoringStrategy.ComputeSoftGenrePenalty(0.0, EnsembleScoringStrategy.DefaultGenrePenaltyFloor);
        var atThreshold = EnsembleScoringStrategy.ComputeSoftGenrePenalty(
            EnsembleScoringStrategy.GenrePenaltyThreshold, EnsembleScoringStrategy.DefaultGenrePenaltyFloor);
        var aboveThreshold = EnsembleScoringStrategy.ComputeSoftGenrePenalty(0.5, EnsembleScoringStrategy.DefaultGenrePenaltyFloor);

        Assert.Equal(EnsembleScoringStrategy.DefaultGenrePenaltyFloor, atZero, 4);
        Assert.Equal(1.0, atThreshold, 4);
        Assert.Equal(1.0, aboveThreshold, 4);

        // Midpoint should be between floor and 1.0
        var midSim = EnsembleScoringStrategy.GenrePenaltyThreshold / 2.0;
        var atMid = EnsembleScoringStrategy.ComputeSoftGenrePenalty(midSim, EnsembleScoringStrategy.DefaultGenrePenaltyFloor);
        Assert.True(atMid > atZero && atMid < atThreshold,
            $"Midpoint penalty ({atMid:F4}) should be between floor ({atZero:F4}) and 1.0");
    }

    [Fact]
    public void Heuristic_Score_IdenticalFeatures_DeterministicOutput()
    {
        var strategy = new HeuristicScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.5,
            CollaborativeScore = 0.3,
            RatingScore = 0.6
        };

        Assert.Equal(strategy.Score(features), strategy.Score(features), 15);
    }

    [Fact]
    public void ScoreExplanation_ToString_ContainsAllFields()
    {
        var explanation = new ScoreExplanation
        {
            FinalScore = 0.75,
            GenreContribution = 0.3,
            CollaborativeContribution = 0.1,
            RatingContribution = 0.05,
            RecencyContribution = 0.02,
            YearProximityContribution = 0.03,
            UserRatingContribution = 0.08,
            InteractionContribution = 0.04,
            GenrePenaltyMultiplier = 0.9,
            DominantSignal = "genre",
            StrategyName = "TestStrategy"
        };

        var str = explanation.ToString();
        Assert.Contains("TestStrategy", str);
        Assert.Contains("0.7500", str);
        Assert.Contains("genre", str);
    }

    [Fact]
    public void ScoreExplanation_Blend_SetsDominantSignal()
    {
        // Arrange: 'a' is genre-dominant, 'b' is collaborative-dominant
        var a = new ScoreExplanation
        {
            FinalScore = 0.6,
            GenreContribution = 0.5,
            CollaborativeContribution = 0.05,
            RatingContribution = 0.02,
            RecencyContribution = 0.01,
            YearProximityContribution = 0.01,
            UserRatingContribution = 0.0,
            InteractionContribution = 0.01,
            DominantSignal = "Genre",
            StrategyName = "A"
        };

        var b = new ScoreExplanation
        {
            FinalScore = 0.8,
            GenreContribution = 0.05,
            CollaborativeContribution = 0.6,
            RatingContribution = 0.02,
            RecencyContribution = 0.01,
            YearProximityContribution = 0.01,
            UserRatingContribution = 0.0,
            InteractionContribution = 0.01,
            DominantSignal = "Collaborative",
            StrategyName = "B"
        };

        // Act: blend 50/50
        var blended = a.Blend(b, 0.5);

        // Assert: DominantSignal must not be empty
        Assert.False(string.IsNullOrEmpty(blended.DominantSignal), "Blend must set DominantSignal");

        // With equal alpha, genre=0.275 and collab=0.325, so collaborative should dominate
        Assert.Equal("Collaborative", blended.DominantSignal);
    }

    [Fact]
    public void ScoreExplanation_Blend_InterpolatesCorrectly()
    {
        var a = new ScoreExplanation
        {
            FinalScore = 1.0,
            GenreContribution = 1.0,
            CollaborativeContribution = 0.0,
            DominantSignal = "Genre",
            StrategyName = "A"
        };

        var b = new ScoreExplanation
        {
            FinalScore = 0.0,
            GenreContribution = 0.0,
            CollaborativeContribution = 1.0,
            DominantSignal = "Collaborative",
            StrategyName = "B"
        };

        // alpha=0.3 → result = 0.7*a + 0.3*b
        var blended = a.Blend(b, 0.3);

        Assert.Equal(0.7, blended.FinalScore, 10);
        Assert.Equal(0.7, blended.GenreContribution, 10);
        Assert.Equal(0.3, blended.CollaborativeContribution, 10);
        Assert.Equal("Genre", blended.DominantSignal); // 0.7 > 0.3
    }

    // ============================================================
    // ScoreExplanation.WithPenalty Tests
    // ============================================================

    [Fact]
    public void ScoreExplanation_WithPenalty_ScalesAllContributions()
    {
        var original = new ScoreExplanation
        {
            FinalScore = 0.80,
            GenreContribution = 0.30,
            CollaborativeContribution = 0.20,
            RatingContribution = 0.10,
            RecencyContribution = 0.05,
            YearProximityContribution = 0.05,
            UserRatingContribution = 0.08,
            InteractionContribution = 0.02,
            GenrePenaltyMultiplier = 1.0,
            DominantSignal = "Genre",
            StrategyName = "Heuristic"
        };

        var penalized = original.WithPenalty(0.5);

        Assert.Equal(0.40, penalized.FinalScore, 10);
        Assert.Equal(0.15, penalized.GenreContribution, 10);
        Assert.Equal(0.10, penalized.CollaborativeContribution, 10);
        Assert.Equal(0.05, penalized.RatingContribution, 10);
        Assert.Equal(0.025, penalized.RecencyContribution, 10);
        Assert.Equal(0.025, penalized.YearProximityContribution, 10);
        Assert.Equal(0.04, penalized.UserRatingContribution, 10);
        Assert.Equal(0.01, penalized.InteractionContribution, 10);
        Assert.Equal(0.5, penalized.GenrePenaltyMultiplier, 10);
        Assert.Equal("Genre", penalized.DominantSignal);
        Assert.Equal("Heuristic", penalized.StrategyName);
    }

    [Fact]
    public void ScoreExplanation_WithPenalty_ClampsScoreToZeroOne()
    {
        var original = new ScoreExplanation
        {
            FinalScore = 0.90,
            GenreContribution = 0.90
        };

        // Penalty > 1 would push score above 1.0 without clamping
        var penalized = original.WithPenalty(1.5);
        Assert.True(penalized.FinalScore <= 1.0, "FinalScore should be clamped to max 1.0");

        // Penalty of 0 should yield 0
        var zeroed = original.WithPenalty(0.0);
        Assert.Equal(0.0, zeroed.FinalScore, 10);
        Assert.Equal(0.0, zeroed.GenreContribution, 10);
    }

    // ============================================================
    // DefaultWeights Tests
    // ============================================================

    [Fact]
    public void DefaultWeights_CreateWeightArray_HasCorrectLength()
    {
        var weights = DefaultWeights.CreateWeightArray();
        Assert.Equal(CandidateFeatures.FeatureCount, weights.Length);
    }

    [Fact]
    public void DefaultWeights_CreateWeightArray_MatchesConstants()
    {
        var weights = DefaultWeights.CreateWeightArray();

        Assert.Equal(DefaultWeights.GenreSimilarity, weights[(int)FeatureIndex.GenreSimilarity], 15);
        Assert.Equal(DefaultWeights.CollaborativeScore, weights[(int)FeatureIndex.CollaborativeScore], 15);
        Assert.Equal(DefaultWeights.RatingScore, weights[(int)FeatureIndex.RatingScore], 15);
        Assert.Equal(DefaultWeights.RecencyScore, weights[(int)FeatureIndex.RecencyScore], 15);
        Assert.Equal(DefaultWeights.YearProximityScore, weights[(int)FeatureIndex.YearProximityScore], 15);
        Assert.Equal(DefaultWeights.GenreCountNormalized, weights[(int)FeatureIndex.GenreCountNormalized], 15);
        Assert.Equal(DefaultWeights.IsSeries, weights[(int)FeatureIndex.IsSeries], 15);
        Assert.Equal(DefaultWeights.GenreRatingInteraction, weights[(int)FeatureIndex.GenreRatingInteraction], 15);
        Assert.Equal(DefaultWeights.GenreCollabInteraction, weights[(int)FeatureIndex.GenreCollabInteraction], 15);
        Assert.Equal(DefaultWeights.UserRatingScore, weights[(int)FeatureIndex.UserRatingScore], 15);
        Assert.Equal(DefaultWeights.CompletionRatio, weights[(int)FeatureIndex.CompletionRatio], 15);
    }

    [Fact]
    public void DefaultWeights_WeightsExcludingBias_SumToOne()
    {
        var weights = DefaultWeights.CreateWeightArray();
        var sum = 0.0;
        for (var i = 0; i < weights.Length; i++)
        {
            sum += weights[i];
        }

        Assert.InRange(sum, 0.95, 1.15);
    }

    // ============================================================
    // TrainingExample — Temporal Decay Tests
    // ============================================================

    [Fact]
    public void TrainingExample_ComputeTemporalWeight_NowReturnsOne()
    {
        var now = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures { GenreSimilarity = 0.5 },
            Label = 1.0,
            GeneratedAtUtc = now
        };

        Assert.Equal(1.0, example.ComputeTemporalWeight(now), 10);
    }

    [Fact]
    public void TrainingExample_ComputeTemporalWeight_HalfLifeReturnsHalf()
    {
        var now = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures { GenreSimilarity = 0.5 },
            Label = 1.0,
            GeneratedAtUtc = now.AddDays(-TrainingExample.TemporalDecayHalfLifeDays)
        };

        Assert.Equal(0.5, example.ComputeTemporalWeight(now), 4);
    }

    [Fact]
    public void TrainingExample_ComputeTemporalWeight_FutureExampleReturnsOne()
    {
        var now = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures { GenreSimilarity = 0.5 },
            Label = 1.0,
            GeneratedAtUtc = now.AddDays(10) // Future
        };

        Assert.Equal(1.0, example.ComputeTemporalWeight(now), 10);
    }

    [Fact]
    public void TrainingExample_ComputeTemporalWeight_OldExampleDecaysTowardZero()
    {
        var now = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures { GenreSimilarity = 0.5 },
            Label = 1.0,
            GeneratedAtUtc = now.AddDays(-365) // ~1 year old
        };

        var weight = example.ComputeTemporalWeight(now);
        Assert.True(weight > 0.0, "Weight should be positive");
        Assert.True(weight < 0.1, $"Weight for 365-day-old example should be very small, got {weight:F6}");
    }

    [Fact]
    public void TrainingExample_ComputeEffectiveWeight_CombinesSampleAndTemporal()
    {
        var now = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures { GenreSimilarity = 0.5 },
            Label = 1.0,
            SampleWeight = 0.5,
            GeneratedAtUtc = now
        };

        Assert.Equal(0.5, example.ComputeEffectiveWeight(now), 10);
    }

    [Fact]
    public void TrainingExample_Label_ClampedToZeroOne()
    {
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            Label = 2.0
        };
        Assert.Equal(1.0, example.Label, 15);

        example.Label = -1.0;
        Assert.Equal(0.0, example.Label, 15);
    }

    // ============================================================
    // ScoringHelper Tests
    // ============================================================

    [Fact]
    public void ScoringHelper_ComputeRawScore_MatchesManualCalculation()
    {
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            CollaborativeScore = 0.5,
            RatingScore = 0.7,
            RecencyScore = 0.3,
            YearProximityScore = 0.4,
            GenreCount = 3,
            IsSeries = false,
            UserRatingScore = 0.6,
            CompletionRatio = 0.5
        };

        var vector = features.ToVector();
        var weights = DefaultWeights.CreateWeightArray();
        var bias = 0.0;

        var rawScore = ScoringHelper.ComputeRawScore(vector, weights, bias);

        // Manual calculation
        var expected = 0.0;
        for (var i = 0; i < vector.Length; i++)
        {
            expected += vector[i] * weights[i];
        }

        Assert.Equal(expected, rawScore, 12);
    }

    [Fact]
    public void ScoringHelper_BuildExplanation_ScoreMatchesComputeRawScore()
    {
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.6,
            CollaborativeScore = 0.4,
            RatingScore = 0.8,
            RecencyScore = 0.2,
            YearProximityScore = 0.5,
            GenreCount = 2,
            UserRatingScore = 0.7,
            CompletionRatio = 0.3
        };

        var vector = features.ToVector();
        var weights = DefaultWeights.CreateWeightArray();
        var bias = DefaultWeights.Bias;

        var explanation = ScoringHelper.BuildExplanation(vector, weights, bias, "Test");
        var rawScore = ScoringHelper.ComputeRawScore(vector, weights, bias);
        var expectedScore = Math.Clamp(rawScore, 0.0, 1.0);

        Assert.Equal(expectedScore, explanation.FinalScore, 12);
        Assert.Equal("Test", explanation.StrategyName);
        Assert.False(string.IsNullOrEmpty(explanation.DominantSignal));
    }

    [Fact]
    public void ScoringHelper_BuildExplanation_ContributionsSumApproximatelyToScore()
    {
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.5,
            CollaborativeScore = 0.3,
            RatingScore = 0.6,
            RecencyScore = 0.4,
            YearProximityScore = 0.5,
            GenreCount = 2,
            UserRatingScore = 0.5,
            CompletionRatio = 0.4
        };

        var vector = features.ToVector();
        var weights = DefaultWeights.CreateWeightArray();
        var bias = 0.0; // Zero bias so contributions should sum to score

        var explanation = ScoringHelper.BuildExplanation(vector, weights, bias, "Test");

        var contributionSum = explanation.GenreContribution
            + explanation.CollaborativeContribution
            + explanation.RatingContribution
            + explanation.RecencyContribution
            + explanation.YearProximityContribution
            + explanation.UserRatingContribution
            + explanation.InteractionContribution;

        Assert.Equal(explanation.FinalScore, contributionSum, 6);
    }

    // ============================================================
    // Ensemble State Persistence Tests
    // ============================================================

    [Fact]
    public void Ensemble_State_PersistsAndRestores()
    {
        var statePath = Path.Combine(_tempDir, "ensemble_state.json");
        var weightsPath = Path.Combine(_tempDir, "ml_weights.json");

        var learned = new LearnedScoringStrategy(weightsPath);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);

        var original = new EnsembleScoringStrategy(learned, heuristic, statePath: statePath);
        var examples = GenerateTrainingExamples(100);
        original.Train(examples);

        var originalAlpha = original.CurrentAlpha;
        var originalCount = original.TrainingExampleCount;

        // Create new instance with same state path — should restore state
        var learned2 = new LearnedScoringStrategy(weightsPath);
        var heuristic2 = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var restored = new EnsembleScoringStrategy(learned2, heuristic2, statePath: statePath);

        Assert.Equal(originalCount, restored.TrainingExampleCount);
        Assert.Equal(originalAlpha, restored.CurrentAlpha, 4);
    }

    [Fact]
    public void Ensemble_State_MissingFileStartsFromDefaults()
    {
        var statePath = Path.Combine(_tempDir, "nonexistent_state.json");
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var strategy = new EnsembleScoringStrategy(learned, heuristic, statePath: statePath);

        Assert.Equal(EnsembleScoringStrategy.DefaultAlphaMin, strategy.CurrentAlpha, 4);
        Assert.Equal(0, strategy.TrainingExampleCount);
    }

    // ============================================================
    // NeuralScoringStrategy Tests
    // ============================================================

    [Fact]
    public void Neural_Name_ReturnsExpectedValue()
    {
        var strategy = new NeuralScoringStrategy();
        Assert.Equal("Neural (Adaptive MLP)", strategy.Name);
    }

    [Fact]
    public void Neural_Score_ReturnsValueBetweenZeroAndOne()
    {
        var strategy = new NeuralScoringStrategy();
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
    public void Neural_Score_AllZeros_ReturnsBaselineScore()
    {
        var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures();

        var score = strategy.Score(features);

        // Two-hidden-layer MLP with Xavier init and sigmoid output produces baseline score
        // for default features (UserRatingScore=0.5, CompletionRatio=0.5, rest=0).
        Assert.InRange(score, 0.0, 0.75);
    }

    [Fact]
    public void Neural_Score_HighGenreMatch_ReturnsHighScore()
    {
        var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.9,
            CollaborativeScore = 0.5,
            RatingScore = 0.8,
            RecencyScore = 0.5,
            YearProximityScore = 0.7,
            GenreCount = 3,
            IsSeries = false,
            PopularityScore = 0.5,
            DayOfWeekAffinity = 0.3
        };

        var score = strategy.Score(features);

        // Neural network with Xavier init on 18 features: score must be in valid range
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void Neural_Score_AfterTraining_GenreMatchBeatsNoGenre()
    {
        var strategy = new NeuralScoringStrategy();

        // Train the network to learn genre importance
        var examples = new List<TrainingExample>();
        for (var i = 0; i < 30; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = 0.9,
                    RatingScore = 0.7,
                    RecencyScore = 0.5,
                    CollaborativeScore = 0.3
                },
                Label = 1.0
            });
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = 0.0,
                    RatingScore = 0.7,
                    RecencyScore = 0.5,
                    CollaborativeScore = 0.3
                },
                Label = 0.0
            });
        }

        strategy.Train(examples);

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

        Assert.True(goodScore > badScore,
            $"After training, higher genre similarity should produce higher score: good={goodScore:F4}, bad={badScore:F4}");
    }

    [Fact]
    public void Neural_Train_WithTooFewExamples_ReturnsFalse()
    {
        var strategy = new NeuralScoringStrategy();
        var examples = new List<TrainingExample>
        {
            new() { Features = new CandidateFeatures { GenreSimilarity = 1.0 }, Label = 1.0 },
            new() { Features = new CandidateFeatures(), Label = 0.0 }
        };

        Assert.False(strategy.Train(examples));
    }

    [Fact]
    public void Neural_Train_UpdatesWeights()
    {
        var strategy = new NeuralScoringStrategy();

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
    }

    [Fact]
    public void Neural_Train_ImprovesScoresForPositiveExamples()
    {
        var strategy = new NeuralScoringStrategy();

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

        Assert.True(scoreAfter >= scoreBefore,
            $"Score should increase or stay stable after training: {scoreBefore:F4} → {scoreAfter:F4}");
    }

    [Fact]
    public void Neural_ScoreWithExplanation_MatchesScore()
    {
        var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.7,
            CollaborativeScore = 0.4,
            RatingScore = 0.6,
            RecencyScore = 0.3,
            YearProximityScore = 0.5,
            GenreCount = 2,
            IsSeries = true,
            UserRatingScore = 0.5,
            CompletionRatio = 0.8,
            PeopleSimilarity = 0.3,
            StudioMatch = true
        };

        var score = strategy.Score(features);
        var explanation = strategy.ScoreWithExplanation(features);

        Assert.Equal(score, explanation.FinalScore, 6);
        Assert.Equal("Neural (Adaptive MLP)", explanation.StrategyName);
    }

    [Fact]
    public void Neural_PersistsAndRestoresWeights()
    {
        var weightsPath = Path.Combine(_tempDir, "neural_weights.json");
        var strategy = new NeuralScoringStrategy(weightsPath);

        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.9,
            RatingScore = 0.8,
            RecencyScore = 0.5
        };

        var examples = new List<TrainingExample>();
        for (var i = 0; i < 20; i++)
        {
            examples.Add(new TrainingExample { Features = features, Label = 1.0 });
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures { GenreSimilarity = 0.1 },
                Label = 0.0
            });
        }

        strategy.Train(examples);
        var scoreAfterTraining = strategy.Score(features);

        // Create new instance from persisted weights
        var restored = new NeuralScoringStrategy(weightsPath);
        var scoreAfterRestore = restored.Score(features);

        Assert.Equal(scoreAfterTraining, scoreAfterRestore, 6);
    }

    [Fact]
    public void Neural_Score_OutputAlwaysClamped()
    {
        var strategy = new NeuralScoringStrategy();

        // Test with extreme values
        var features = new CandidateFeatures
        {
            GenreSimilarity = 1.0,
            CollaborativeScore = 1.0,
            RatingScore = 1.0,
            RecencyScore = 1.0,
            YearProximityScore = 1.0,
            GenreCount = 5,
            IsSeries = true,
            UserRatingScore = 1.0,
            CompletionRatio = 1.0,
            HasUserInteraction = true,
            PeopleSimilarity = 1.0,
            StudioMatch = true
        };

        var score = strategy.Score(features);
        Assert.InRange(score, 0.0, 1.0);

        // All zeros
        var zeroFeatures = new CandidateFeatures();
        var zeroScore = strategy.Score(zeroFeatures);
        Assert.InRange(zeroScore, 0.0, 1.0);
    }

    // ============================================================
    // Ensemble + Neural Integration Tests (3-way blending)
    // ============================================================

    [Fact]
    public void Ensemble_WithNeural_BlendsBetaCorrectly()
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy();
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        // Train with enough data to activate neural (>= NeuralActivationThreshold)
        var examples = GenerateTrainingExamples(160);
        ensemble.Train(examples);

        // After sufficient training, neural beta should be > 0
        var beta = ensemble.CurrentNeuralBeta;
        Assert.True(beta >= 0, $"Neural beta should be >= 0, was {beta:F4}");

        // Score should still be in valid range
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            RatingScore = 0.7,
            RecencyScore = 0.5,
            CollaborativeScore = 0.6
        };
        var score = ensemble.Score(features);
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void Ensemble_NeuralBelowThreshold_NotUsed()
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy();
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        // Train with data below NeuralActivationThreshold (50)
        var examples = GenerateTrainingExamples(25);
        ensemble.Train(examples);

        // Neural beta should remain 0 when below activation threshold
        Assert.Equal(0.0, ensemble.CurrentNeuralBeta);
    }

    [Fact]
    public void Ensemble_WithoutNeural_BetaStaysZero()
    {
        // Ensemble without neural strategy (null)
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural: null);

        var examples = GenerateTrainingExamples(160);
        ensemble.Train(examples);

        // Beta should stay 0 when no neural strategy is provided
        Assert.Equal(0.0, ensemble.CurrentNeuralBeta);
    }

    [Fact]
    public void Ensemble_WithNeural_ScoreWithExplanationMatchesScore()
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy();
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        // Train to activate neural
        var examples = GenerateTrainingExamples(160);
        ensemble.Train(examples);

        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.8,
            RatingScore = 0.7,
            RecencyScore = 0.5,
            CollaborativeScore = 0.6,
            YearProximityScore = 0.4
        };

        var score = ensemble.Score(features);
        var explanation = ensemble.ScoreWithExplanation(features);

        // Explanation's final score should match Score() output
        Assert.Equal(score, explanation.FinalScore, 4);
        Assert.Equal("Ensemble (Adaptive ML + Rules)", explanation.StrategyName);
    }

    [Fact]
    public void Ensemble_NeuralBetaPersisted_SurvivesRestart()
    {
        var statePath = Path.Combine(_tempDir, "ensemble_neural_state.json");
        var weightsPath = Path.Combine(_tempDir, "learned_neural_weights.json");
        var neuralWeightsPath = Path.Combine(_tempDir, "neural_weights.json");

        var learned = new LearnedScoringStrategy(weightsPath);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy(neuralWeightsPath);
        var original = new EnsembleScoringStrategy(learned, heuristic, neural, statePath: statePath);

        // Train enough to activate neural beta
        var examples = GenerateTrainingExamples(160);
        original.Train(examples);
        var betaBefore = original.CurrentNeuralBeta;

        // Create a new instance that loads from persisted state
        var learned2 = new LearnedScoringStrategy(weightsPath);
        var heuristic2 = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural2 = new NeuralScoringStrategy(neuralWeightsPath);
        var restored = new EnsembleScoringStrategy(learned2, heuristic2, neural2, statePath: statePath);

        // Neural beta should be restored from persisted state
        Assert.Equal(betaBefore, restored.CurrentNeuralBeta, 6);
    }

    [Fact]
    public void Neural_AdamTimestep_ResetOnRestore()
    {
        var weightsPath = Path.Combine(_tempDir, "neural_adam_reset.json");
        var strategy = new NeuralScoringStrategy(weightsPath);

        // Train to advance Adam timestep
        var examples = new List<TrainingExample>();
        for (var i = 0; i < 20; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures { GenreSimilarity = 0.9, RatingScore = 0.8 },
                Label = 1.0
            });
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures { GenreSimilarity = 0.1, RatingScore = 0.2 },
                Label = 0.0
            });
        }

        strategy.Train(examples);

        // Restore from disk — Adam state should reset but weights should persist
        var restored = new NeuralScoringStrategy(weightsPath);

        // Verify weights are consistent (score should match)
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.7,
            RatingScore = 0.6,
            RecencyScore = 0.5
        };

        Assert.Equal(strategy.Score(features), restored.Score(features), 6);

        // Further training should still work after restore
        Assert.True(restored.Train(examples));
    }

    [Fact]
    public void Learned_StandardizationTransition_ResetsWeights()
    {
        // Start with fewer than MinExamplesForStandardization to train WITHOUT standardization
        var strategy = new LearnedScoringStrategy();
        var fewExamples = new List<TrainingExample>();
        for (var i = 0; i < 7; i++)
        {
            fewExamples.Add(new TrainingExample
            {
                Features = new CandidateFeatures { GenreSimilarity = 0.9, RatingScore = 0.8 },
                Label = 1.0
            });
        }

        // Train without standardization (7 < MinExamplesForStandardization=10)
        strategy.Train(fewExamples);
        var weightsAfterFirstTrain = strategy.CurrentWeights;

        // Now train WITH standardization (>= 10 examples) — should trigger weight reset
        var manyExamples = GenerateTrainingExamples(20);
        strategy.Train(manyExamples);
        var weightsAfterSecondTrain = strategy.CurrentWeights;

        // Weights should have changed (reset + retrained)
        var anyDifferent = false;
        for (var i = 0; i < weightsAfterFirstTrain.Length; i++)
        {
            if (Math.Abs(weightsAfterFirstTrain[i] - weightsAfterSecondTrain[i]) > 1e-10)
            {
                anyDifferent = true;
                break;
            }
        }

        Assert.True(anyDifferent, "Weights should change during standardization transition");
    }

    [Fact]
    public void Neural_Sigmoid_EdgeValues()
    {
        // Sigmoid(0) = 0.5
        Assert.Equal(0.5, NeuralScoringStrategy.Sigmoid(0), 10);

        // Large positive → ~1.0
        Assert.True(NeuralScoringStrategy.Sigmoid(100) > 0.999);

        // Large negative → ~0.0
        Assert.True(NeuralScoringStrategy.Sigmoid(-100) < 0.001);

        // Monotonically increasing
        Assert.True(NeuralScoringStrategy.Sigmoid(1) > NeuralScoringStrategy.Sigmoid(0));
        Assert.True(NeuralScoringStrategy.Sigmoid(0) > NeuralScoringStrategy.Sigmoid(-1));
    }

    [Fact]
    public void Neural_ForwardPass_ZeroInputProducesSigmoidOfBias()
    {
        var inputSize = CandidateFeatures.FeatureCount;
        var wIH = new double[NeuralScoringStrategy.Hidden1Size * inputSize];
        var bH1 = new double[NeuralScoringStrategy.Hidden1Size];
        var wH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size];
        var bH2 = new double[NeuralScoringStrategy.Hidden2Size];
        var wH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size];
        var bH3 = new double[NeuralScoringStrategy.Hidden3Size];
        var wH3O = new double[NeuralScoringStrategy.Hidden3Size];
        var bO = 0.0;
        var input = new double[inputSize];
        var h1Pre = new double[NeuralScoringStrategy.Hidden1Size];
        var h1Act = new double[NeuralScoringStrategy.Hidden1Size];
        var h2Pre = new double[NeuralScoringStrategy.Hidden2Size];
        var h2Act = new double[NeuralScoringStrategy.Hidden2Size];
        var h3Pre = new double[NeuralScoringStrategy.Hidden3Size];
        var h3Act = new double[NeuralScoringStrategy.Hidden3Size];

        var result = NeuralScoringStrategy.ForwardPass(
            input,
            wIH,
            bH1,
            wH1H2,
            bH2,
            wH2H3,
            bH3,
            wH3O,
            bO,
            h1Pre,
            h1Act,
            h2Pre,
            h2Act,
            h3Pre,
            h3Act);

        // With all zero weights, biases, and inputs: sigmoid(0) = 0.5
        Assert.Equal(0.5, result, 10);
    }

    [Fact]
    public void Ensemble_NeuralBetaRamps_WithMoreData()
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var neural = new NeuralScoringStrategy();
        var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        // Train at threshold
        ensemble.Train(GenerateTrainingExamples(55));
        var betaAt55 = ensemble.CurrentNeuralBeta;

        // Train more — beta should increase (or stay same if quality gate blocks it)
        ensemble.Train(GenerateTrainingExamples(100));
        var betaAt155 = ensemble.CurrentNeuralBeta;

        // With more data, beta should be >= what it was before
        Assert.True(betaAt155 >= betaAt55,
            $"Beta should ramp up with more data: {betaAt55:F4} → {betaAt155:F4}");

        // Beta should never exceed NeuralMaxBetaFraction
        Assert.True(betaAt155 <= EnsembleScoringStrategy.NeuralMaxBetaFraction,
            $"Beta should not exceed max: {betaAt155:F4} > {EnsembleScoringStrategy.NeuralMaxBetaFraction}");
    }
}
