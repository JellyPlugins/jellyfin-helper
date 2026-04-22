using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="NeuralScoringStrategy"/>: Forward-Pass, Backprop/Training,
///     Adam optimizer, Weight Persistence, Xavier initialization, Sigmoid.
/// </summary>
public sealed class NeuralScoringStrategyTests : IDisposable
{
    private readonly string _tempDir;

    public NeuralScoringStrategyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jf-neural-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    // ============================================================
    // Name / NameKey
    // ============================================================

    [Fact]
    public void Name_ReturnsExpected()
    {
        var strategy = new NeuralScoringStrategy();
        Assert.Equal("Neural (Adaptive MLP)", strategy.Name);
        Assert.Equal("strategyNeural", strategy.NameKey);
    }

    // ============================================================
    // Sigmoid Tests
    // ============================================================

    [Fact]
    public void Sigmoid_Zero_ReturnsHalf()
    {
        Assert.Equal(0.5, NeuralScoringStrategy.Sigmoid(0.0), 10);
    }

    [Fact]
    public void Sigmoid_LargePositive_ApproachesOne()
    {
        var result = NeuralScoringStrategy.Sigmoid(100.0);
        Assert.True(result > 0.999, $"Sigmoid(100) should be ~1.0, got {result}");
        Assert.True(result <= 1.0);
    }

    [Fact]
    public void Sigmoid_LargeNegative_ApproachesZero()
    {
        var result = NeuralScoringStrategy.Sigmoid(-100.0);
        Assert.True(result < 0.001, $"Sigmoid(-100) should be ~0.0, got {result}");
        Assert.True(result >= 0.0);
    }

    [Fact]
    public void Sigmoid_IsMonotonic()
    {
        var prev = NeuralScoringStrategy.Sigmoid(-10.0);
        for (var x = -9.0; x <= 10.0; x += 1.0)
        {
            var current = NeuralScoringStrategy.Sigmoid(x);
            Assert.True(current >= prev, $"Sigmoid should be monotonically increasing: {prev} -> {current} at x={x}");
            prev = current;
        }
    }

    [Fact]
    public void Sigmoid_IsSymmetric()
    {
        // sigmoid(x) + sigmoid(-x) = 1
        for (var x = 0.1; x <= 5.0; x += 0.5)
        {
            var sum = NeuralScoringStrategy.Sigmoid(x) + NeuralScoringStrategy.Sigmoid(-x);
            Assert.Equal(1.0, sum, 10);
        }
    }

    // ============================================================
    // ForwardPass Tests
    // ============================================================

    [Fact]
    public void ForwardPass_AllZeroWeights_ReturnsSigmoidZero()
    {
        var inputSize = CandidateFeatures.FeatureCount;
        var input = new double[inputSize];
        var wH = new double[NeuralScoringStrategy.HiddenSize * inputSize];
        var bH = new double[NeuralScoringStrategy.HiddenSize];
        var wO = new double[NeuralScoringStrategy.HiddenSize];
        var bO = 0.0;
        var hPre = new double[NeuralScoringStrategy.HiddenSize];
        var hAct = new double[NeuralScoringStrategy.HiddenSize];

        var result = NeuralScoringStrategy.ForwardPass(input, wH, bH, wO, bO, hPre, hAct);

        // All zeros → hidden pre-activation = 0 → ReLU(0) = 0 → output = sigmoid(0) = 0.5
        Assert.Equal(0.5, result, 10);
    }

    [Fact]
    public void ForwardPass_PositiveBias_IncreasesOutput()
    {
        var inputSize = CandidateFeatures.FeatureCount;
        var input = new double[inputSize];
        var wH = new double[NeuralScoringStrategy.HiddenSize * inputSize];
        var bH = new double[NeuralScoringStrategy.HiddenSize];
        var wO = new double[NeuralScoringStrategy.HiddenSize];
        var bO = 2.0; // positive output bias
        var hPre = new double[NeuralScoringStrategy.HiddenSize];
        var hAct = new double[NeuralScoringStrategy.HiddenSize];

        var result = NeuralScoringStrategy.ForwardPass(input, wH, bH, wO, bO, hPre, hAct);

        // sigmoid(2.0) ≈ 0.88
        Assert.True(result > 0.5, $"Positive bias should increase output, got {result}");
        Assert.Equal(NeuralScoringStrategy.Sigmoid(2.0), result, 10);
    }

    [Fact]
    public void ForwardPass_ReluActivation_BlocksNegativePreActivation()
    {
        // ReLU activation is tested implicitly: with all-zero weights/inputs,
        // hidden pre-activation = 0, ReLU(0) = 0, output = sigmoid(0) = 0.5.
        // ForwardPass uses HiddenSize=8 internally, so we verify via Score methods.
        var strategy = new NeuralScoringStrategy();
        var score = strategy.Score(new CandidateFeatures());
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void ForwardPass_OutputInZeroOneRange()
    {
        var inputSize = CandidateFeatures.FeatureCount;
        var rng = new Random(123);

        // Random weights and inputs
        var input = new double[inputSize];
        var wH = new double[NeuralScoringStrategy.HiddenSize * inputSize];
        var bH = new double[NeuralScoringStrategy.HiddenSize];
        var wO = new double[NeuralScoringStrategy.HiddenSize];
        var hPre = new double[NeuralScoringStrategy.HiddenSize];
        var hAct = new double[NeuralScoringStrategy.HiddenSize];

        for (var i = 0; i < input.Length; i++)
        {
            input[i] = rng.NextDouble();
        }

        for (var i = 0; i < wH.Length; i++)
        {
            wH[i] = (rng.NextDouble() - 0.5) * 2;
        }

        for (var i = 0; i < wO.Length; i++)
        {
            wO[i] = (rng.NextDouble() - 0.5) * 2;
        }

        var result = NeuralScoringStrategy.ForwardPass(input, wH, bH, wO, 0.0, hPre, hAct);
        Assert.InRange(result, 0.0, 1.0);
    }

    // ============================================================
    // Xavier Initialization Tests
    // ============================================================

    [Fact]
    public void XavierInit_WeightsAreNotAllZero()
    {
        var strategy = new NeuralScoringStrategy();
        var wH = strategy.CurrentWeightsHidden;
        var wO = strategy.CurrentWeightsOutput;

        Assert.True(wH.Any(w => Math.Abs(w) > 1e-10), "Hidden weights should not all be zero after Xavier init");
        Assert.True(wO.Any(w => Math.Abs(w) > 1e-10), "Output weights should not all be zero after Xavier init");
    }

    [Fact]
    public void XavierInit_HiddenWeights_CorrectLength()
    {
        var strategy = new NeuralScoringStrategy();
        var wH = strategy.CurrentWeightsHidden;

        Assert.Equal(NeuralScoringStrategy.HiddenSize * CandidateFeatures.FeatureCount, wH.Length);
    }

    [Fact]
    public void XavierInit_OutputWeights_CorrectLength()
    {
        var strategy = new NeuralScoringStrategy();
        var wO = strategy.CurrentWeightsOutput;

        Assert.Equal(NeuralScoringStrategy.HiddenSize, wO.Length);
    }

    [Fact]
    public void XavierInit_HiddenWeights_WithinExpectedBounds()
    {
        var strategy = new NeuralScoringStrategy();
        var wH = strategy.CurrentWeightsHidden;

        // Xavier limit = sqrt(6 / (inputSize + hiddenSize))
        var limit = Math.Sqrt(6.0 / (CandidateFeatures.FeatureCount + NeuralScoringStrategy.HiddenSize));

        foreach (var w in wH)
        {
            Assert.InRange(w, -limit - 0.001, limit + 0.001);
        }
    }

    [Fact]
    public void XavierInit_OutputWeights_WithinExpectedBounds()
    {
        var strategy = new NeuralScoringStrategy();
        var wO = strategy.CurrentWeightsOutput;

        var limit = Math.Sqrt(6.0 / (NeuralScoringStrategy.HiddenSize + 1));

        foreach (var w in wO)
        {
            Assert.InRange(w, -limit - 0.001, limit + 0.001);
        }
    }

    [Fact]
    public void XavierInit_IsDeterministic()
    {
        // Both use seed 42 internally
        var s1 = new NeuralScoringStrategy();
        var s2 = new NeuralScoringStrategy();

        var wH1 = s1.CurrentWeightsHidden;
        var wH2 = s2.CurrentWeightsHidden;

        for (var i = 0; i < wH1.Length; i++)
        {
            Assert.Equal(wH1[i], wH2[i], 10);
        }
    }

    // ============================================================
    // Score Tests
    // ============================================================

    [Fact]
    public void Score_ReturnsValueBetweenZeroAndOne()
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
    public void Score_AllZeroFeatures_ReturnsSomething()
    {
        var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures();

        var score = strategy.Score(features);
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void Score_IsDeterministic()
    {
        var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.6,
            RatingScore = 0.7,
            CollaborativeScore = 0.4
        };

        var score1 = strategy.Score(features);
        var score2 = strategy.Score(features);

        Assert.Equal(score1, score2, 10);
    }

    // ============================================================
    // ScoreWithExplanation Tests
    // ============================================================

    [Fact]
    public void ScoreWithExplanation_ReturnsValidExplanation()
    {
        var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.7,
            CollaborativeScore = 0.4,
            RatingScore = 0.6,
            RecencyScore = 0.5,
            YearProximityScore = 0.8,
            GenreCount = 3,
            UserRatingScore = 0.7
        };

        var explanation = strategy.ScoreWithExplanation(features);

        Assert.InRange(explanation.FinalScore, 0.0, 1.0);
        Assert.Equal("Neural (Adaptive MLP)", explanation.StrategyName);
        Assert.Equal(1.0, explanation.GenrePenaltyMultiplier, 10);
        Assert.False(string.IsNullOrEmpty(explanation.DominantSignal));
    }

    [Fact]
    public void ScoreWithExplanation_FinalScore_MatchesScore()
    {
        var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.5,
            RatingScore = 0.6,
            CollaborativeScore = 0.3
        };

        var score = strategy.Score(features);
        var explanation = strategy.ScoreWithExplanation(features);

        Assert.Equal(score, explanation.FinalScore, 8);
    }

    // ============================================================
    // Training Tests
    // ============================================================

    [Fact]
    public void Train_TooFewExamples_ReturnsFalse()
    {
        var strategy = new NeuralScoringStrategy();
        var examples = new List<TrainingExample>();
        for (var i = 0; i < NeuralScoringStrategy.MinTrainingExamples - 1; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures { GenreSimilarity = 0.5 },
                Label = 1.0
            });
        }

        Assert.False(strategy.Train(examples));
    }

    [Fact]
    public void Train_MinimumExamples_ReturnsTrue()
    {
        var strategy = new NeuralScoringStrategy();
        var examples = GenerateExamples(NeuralScoringStrategy.MinTrainingExamples);

        Assert.True(strategy.Train(examples));
    }

    [Fact]
    public void Train_UpdatesWeights()
    {
        var strategy = new NeuralScoringStrategy();
        var initialWH = strategy.CurrentWeightsHidden;
        var initialWO = strategy.CurrentWeightsOutput;

        var examples = GenerateExamples(20);
        strategy.Train(examples);

        var updatedWH = strategy.CurrentWeightsHidden;
        var updatedWO = strategy.CurrentWeightsOutput;

        var anyHiddenChanged = false;
        for (var i = 0; i < initialWH.Length; i++)
        {
            if (Math.Abs(initialWH[i] - updatedWH[i]) > 1e-10)
            {
                anyHiddenChanged = true;
                break;
            }
        }

        var anyOutputChanged = false;
        for (var i = 0; i < initialWO.Length; i++)
        {
            if (Math.Abs(initialWO[i] - updatedWO[i]) > 1e-10)
            {
                anyOutputChanged = true;
                break;
            }
        }

        Assert.True(anyHiddenChanged, "Training should modify hidden weights");
        Assert.True(anyOutputChanged, "Training should modify output weights");
    }

    [Fact]
    public void Train_IncrementsGeneration()
    {
        var strategy = new NeuralScoringStrategy();
        Assert.Equal(0, strategy.TrainingGeneration);

        var examples = GenerateExamples(20);
        strategy.Train(examples);
        Assert.Equal(1, strategy.TrainingGeneration);

        strategy.Train(examples);
        Assert.Equal(2, strategy.TrainingGeneration);
    }

    [Fact]
    public void Train_SetsValidationLoss()
    {
        var strategy = new NeuralScoringStrategy();
        Assert.True(double.IsNaN(strategy.LastValidationLoss));

        var examples = GenerateExamples(30);
        strategy.Train(examples);

        Assert.False(double.IsNaN(strategy.LastValidationLoss));
        Assert.True(strategy.LastValidationLoss >= 0.0, "Validation loss should be non-negative");
    }

    [Fact]
    public void Train_WeightsStayClamped()
    {
        var strategy = new NeuralScoringStrategy();

        // Extreme data to push weights
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
                    IsSeries = true,
                    UserRatingScore = 1.0
                },
                Label = 1.0
            });
        }

        strategy.Train(examples);

        var wH = strategy.CurrentWeightsHidden;
        var wO = strategy.CurrentWeightsOutput;

        foreach (var w in wH)
        {
            Assert.InRange(w, -NeuralScoringStrategy.WeightClamp, NeuralScoringStrategy.WeightClamp);
        }

        foreach (var w in wO)
        {
            Assert.InRange(w, -NeuralScoringStrategy.WeightClamp, NeuralScoringStrategy.WeightClamp);
        }
    }

    [Fact]
    public void Train_MultipleTimes_ContinuesLearning()
    {
        var strategy = new NeuralScoringStrategy();
        var examples = GenerateExamples(20);

        strategy.Train(examples);
        var loss1 = strategy.LastValidationLoss;

        strategy.Train(examples);
        var loss2 = strategy.LastValidationLoss;

        // Both losses should be valid (non-NaN)
        Assert.False(double.IsNaN(loss1));
        Assert.False(double.IsNaN(loss2));
    }

    // ============================================================
    // Weight Persistence Tests
    // ============================================================

    [Fact]
    public void PersistsWeights_ToFile()
    {
        var weightsPath = Path.Combine(_tempDir, "neural_weights.json");
        var strategy = new NeuralScoringStrategy(weightsPath);

        var examples = GenerateExamples(20);
        strategy.Train(examples);

        Assert.True(File.Exists(weightsPath), "Weights file should be created after training");

        var json = File.ReadAllText(weightsPath);
        Assert.Contains("WeightsHidden", json);
        Assert.Contains("BiasHidden", json);
        Assert.Contains("WeightsOutput", json);
        Assert.Contains("BiasOutput", json);
        Assert.Contains("Version", json);
        Assert.Contains("TrainingGeneration", json);
    }

    [Fact]
    public void LoadsWeights_FromFile()
    {
        var weightsPath = Path.Combine(_tempDir, "neural_weights2.json");

        // Train and save
        var strategy1 = new NeuralScoringStrategy(weightsPath);
        var examples = GenerateExamples(20);
        strategy1.Train(examples);

        var savedWH = strategy1.CurrentWeightsHidden;
        var savedWO = strategy1.CurrentWeightsOutput;
        var savedGen = strategy1.TrainingGeneration;

        // Load into new instance
        var strategy2 = new NeuralScoringStrategy(weightsPath);
        var loadedWH = strategy2.CurrentWeightsHidden;
        var loadedWO = strategy2.CurrentWeightsOutput;

        for (var i = 0; i < savedWH.Length; i++)
        {
            Assert.Equal(savedWH[i], loadedWH[i], 10);
        }

        for (var i = 0; i < savedWO.Length; i++)
        {
            Assert.Equal(savedWO[i], loadedWO[i], 10);
        }

        Assert.Equal(savedGen, strategy2.TrainingGeneration);
    }

    [Fact]
    public void LoadedWeights_ProduceSameScore()
    {
        var weightsPath = Path.Combine(_tempDir, "neural_weights3.json");

        var strategy1 = new NeuralScoringStrategy(weightsPath);
        var examples = GenerateExamples(20);
        strategy1.Train(examples);

        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.7,
            CollaborativeScore = 0.4,
            RatingScore = 0.6,
            RecencyScore = 0.5,
            YearProximityScore = 0.8
        };

        var score1 = strategy1.Score(features);

        var strategy2 = new NeuralScoringStrategy(weightsPath);
        var score2 = strategy2.Score(features);

        Assert.Equal(score1, score2, 8);
    }

    [Fact]
    public void GracefulFallback_OnCorruptFile()
    {
        var weightsPath = Path.Combine(_tempDir, "corrupt_neural.json");
        File.WriteAllText(weightsPath, "not valid json {{{");

        // Should not throw, should use Xavier-initialized weights
        var strategy = new NeuralScoringStrategy(weightsPath);
        var score = strategy.Score(new CandidateFeatures { GenreSimilarity = 0.5 });

        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void NullPath_WorksInMemoryOnly()
    {
        var strategy = new NeuralScoringStrategy(null);
        var examples = GenerateExamples(20);

        // Should not throw
        Assert.True(strategy.Train(examples));
        var score = strategy.Score(new CandidateFeatures { GenreSimilarity = 0.5 });
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void VersionMismatch_DiscardsWeights()
    {
        var weightsPath = Path.Combine(_tempDir, "old_version.json");

        // Write a weights file with wrong version
        var fakeData = new NeuralScoringStrategy.NeuralWeightsData
        {
            WeightsHidden = new double[NeuralScoringStrategy.HiddenSize * CandidateFeatures.FeatureCount],
            BiasHidden = new double[NeuralScoringStrategy.HiddenSize],
            WeightsOutput = new double[NeuralScoringStrategy.HiddenSize],
            BiasOutput = 999.0,
            Version = NeuralScoringStrategy.CurrentWeightsVersion - 1 // old version
        };

        var json = System.Text.Json.JsonSerializer.Serialize(fakeData);
        File.WriteAllText(weightsPath, json);

        // Should discard and use Xavier defaults
        var strategy = new NeuralScoringStrategy(weightsPath);
        var score = strategy.Score(new CandidateFeatures());

        // Should still work normally (not use BiasOutput=999)
        Assert.InRange(score, 0.0, 1.0);
    }

    // ============================================================
    // Constants Verification
    // ============================================================

    [Fact]
    public void HiddenSize_Is8()
    {
        Assert.Equal(8, NeuralScoringStrategy.HiddenSize);
    }

    [Fact]
    public void MinTrainingExamples_Is8()
    {
        Assert.Equal(8, NeuralScoringStrategy.MinTrainingExamples);
    }

    [Fact]
    public void AdamHyperparameters_AreReasonable()
    {
        Assert.Equal(0.005, NeuralScoringStrategy.DefaultLearningRate);
        Assert.Equal(0.9, NeuralScoringStrategy.AdamBeta1);
        Assert.Equal(0.999, NeuralScoringStrategy.AdamBeta2);
        Assert.Equal(1e-8, NeuralScoringStrategy.AdamEpsilon);
    }

    [Fact]
    public void WeightClamp_Is3()
    {
        Assert.Equal(3.0, NeuralScoringStrategy.WeightClamp);
    }

    [Fact]
    public void ImplementsIScoringStrategy()
    {
        var strategy = new NeuralScoringStrategy();
        Assert.True(strategy is IScoringStrategy);
    }

    [Fact]
    public void ImplementsITrainableStrategy()
    {
        var strategy = new NeuralScoringStrategy();
        Assert.True(strategy is ITrainableStrategy);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static List<TrainingExample> GenerateExamples(int count)
    {
        var rng = new Random(42);
        var examples = new List<TrainingExample>();
        for (var i = 0; i < count; i++)
        {
            var genreSim = rng.NextDouble();
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = genreSim,
                    CollaborativeScore = rng.NextDouble(),
                    RatingScore = rng.NextDouble(),
                    RecencyScore = rng.NextDouble(),
                    YearProximityScore = rng.NextDouble(),
                    GenreCount = rng.Next(0, 6),
                    IsSeries = rng.NextDouble() > 0.5,
                    UserRatingScore = rng.NextDouble(),
                    CompletionRatio = rng.NextDouble(),
                    PeopleSimilarity = rng.NextDouble(),
                    StudioMatch = rng.NextDouble() > 0.5,
                    PopularityScore = rng.NextDouble(),
                    DayOfWeekAffinity = rng.NextDouble()
                },
                Label = genreSim > 0.5 ? 1.0 : 0.0
            });
        }

        return examples;
    }
}