using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="NeuralScoringStrategy"/>: Forward-Pass, Backprop/Training,
///     Adam optimizer, Weight Persistence, Xavier initialization, Sigmoid, Dropout.
///     Architecture (roadmap v3 A1, WeightsVersion 3):
///     <see cref="CandidateFeatures.FeatureCount"/> inputs → 62 hidden₁ → 96 hidden₂ →
///     48 hidden₃ → 24 hidden₄ → 1 output.
/// </summary>
public sealed class NeuralScoringStrategyTests : IDisposable
{
    private readonly string _tempDir;

    public NeuralScoringStrategyTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jf-neural-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (DirectoryNotFoundException)
        {
            // best-effort cleanup - directory may already be gone
        }
        catch (IOException)
        {
            // best-effort cleanup - file may be locked on CI
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup - permission edge cases on CI
        }

        GC.SuppressFinalize(this);
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
        var wIH = new double[NeuralScoringStrategy.Hidden1Size * inputSize];
        var bH1 = new double[NeuralScoringStrategy.Hidden1Size];
        var wH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size];
        var bH2 = new double[NeuralScoringStrategy.Hidden2Size];
        var wH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size];
        var bH3 = new double[NeuralScoringStrategy.Hidden3Size];
        var wH3H4 = new double[NeuralScoringStrategy.Hidden4Size * NeuralScoringStrategy.Hidden3Size];
        var bH4 = new double[NeuralScoringStrategy.Hidden4Size];
        var wH4O = new double[NeuralScoringStrategy.Hidden4Size];
        var bO = 0.0;
        var h1Pre = new double[NeuralScoringStrategy.Hidden1Size];
        var h1Act = new double[NeuralScoringStrategy.Hidden1Size];
        var h2Pre = new double[NeuralScoringStrategy.Hidden2Size];
        var h2Act = new double[NeuralScoringStrategy.Hidden2Size];
        var h3Pre = new double[NeuralScoringStrategy.Hidden3Size];
        var h3Act = new double[NeuralScoringStrategy.Hidden3Size];
        var h4Pre = new double[NeuralScoringStrategy.Hidden4Size];
        var h4Act = new double[NeuralScoringStrategy.Hidden4Size];

        var result = NeuralScoringStrategy.ForwardPass(
            input, wIH, bH1, wH1H2, bH2, wH2H3, bH3, wH3H4, bH4, wH4O, bO,
            h1Pre, h1Act, h2Pre, h2Act, h3Pre, h3Act, h4Pre, h4Act);

        // All zeros → hidden pre-activation = 0 → ReLU(0) = 0 → output = sigmoid(0) = 0.5
        Assert.Equal(0.5, result, 10);
    }

    [Fact]
    public void ForwardPass_PositiveBias_IncreasesOutput()
    {
        var inputSize = CandidateFeatures.FeatureCount;
        var input = new double[inputSize];
        var wIH = new double[NeuralScoringStrategy.Hidden1Size * inputSize];
        var bH1 = new double[NeuralScoringStrategy.Hidden1Size];
        var wH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size];
        var bH2 = new double[NeuralScoringStrategy.Hidden2Size];
        var wH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size];
        var bH3 = new double[NeuralScoringStrategy.Hidden3Size];
        var wH3H4 = new double[NeuralScoringStrategy.Hidden4Size * NeuralScoringStrategy.Hidden3Size];
        var bH4 = new double[NeuralScoringStrategy.Hidden4Size];
        var wH4O = new double[NeuralScoringStrategy.Hidden4Size];
        var bO = 2.0; // positive output bias
        var h1Pre = new double[NeuralScoringStrategy.Hidden1Size];
        var h1Act = new double[NeuralScoringStrategy.Hidden1Size];
        var h2Pre = new double[NeuralScoringStrategy.Hidden2Size];
        var h2Act = new double[NeuralScoringStrategy.Hidden2Size];
        var h3Pre = new double[NeuralScoringStrategy.Hidden3Size];
        var h3Act = new double[NeuralScoringStrategy.Hidden3Size];
        var h4Pre = new double[NeuralScoringStrategy.Hidden4Size];
        var h4Act = new double[NeuralScoringStrategy.Hidden4Size];

        var result = NeuralScoringStrategy.ForwardPass(
            input, wIH, bH1, wH1H2, bH2, wH2H3, bH3, wH3H4, bH4, wH4O, bO,
            h1Pre, h1Act, h2Pre, h2Act, h3Pre, h3Act, h4Pre, h4Act);

        // sigmoid(2.0) ≈ 0.88
        Assert.True(result > 0.5, $"Positive bias should increase output, got {result}");
        Assert.Equal(NeuralScoringStrategy.Sigmoid(2.0), result, 10);
    }

    [Fact]
    public void Score_DefaultFeatures_ReturnsScoreInRange()
    {
        var strategy = new NeuralScoringStrategy();
        var score = strategy.Score(new CandidateFeatures());
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void ForwardPass_OutputInZeroOneRange()
    {
        var inputSize = CandidateFeatures.FeatureCount;
        var rng = new Random(123);

        var input = new double[inputSize];
        var wIH = new double[NeuralScoringStrategy.Hidden1Size * inputSize];
        var bH1 = new double[NeuralScoringStrategy.Hidden1Size];
        var wH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size];
        var bH2 = new double[NeuralScoringStrategy.Hidden2Size];
        var wH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size];
        var bH3 = new double[NeuralScoringStrategy.Hidden3Size];
        var wH3H4 = new double[NeuralScoringStrategy.Hidden4Size * NeuralScoringStrategy.Hidden3Size];
        var bH4 = new double[NeuralScoringStrategy.Hidden4Size];
        var wH4O = new double[NeuralScoringStrategy.Hidden4Size];
        var h1Pre = new double[NeuralScoringStrategy.Hidden1Size];
        var h1Act = new double[NeuralScoringStrategy.Hidden1Size];
        var h2Pre = new double[NeuralScoringStrategy.Hidden2Size];
        var h2Act = new double[NeuralScoringStrategy.Hidden2Size];
        var h3Pre = new double[NeuralScoringStrategy.Hidden3Size];
        var h3Act = new double[NeuralScoringStrategy.Hidden3Size];
        var h4Pre = new double[NeuralScoringStrategy.Hidden4Size];
        var h4Act = new double[NeuralScoringStrategy.Hidden4Size];

        for (var i = 0; i < input.Length; i++)
        {
            input[i] = rng.NextDouble();
        }

        for (var i = 0; i < wIH.Length; i++)
        {
            wIH[i] = (rng.NextDouble() - 0.5) * 2;
        }

        for (var i = 0; i < wH1H2.Length; i++)
        {
            wH1H2[i] = (rng.NextDouble() - 0.5) * 2;
        }

        for (var i = 0; i < wH2H3.Length; i++)
        {
            wH2H3[i] = (rng.NextDouble() - 0.5) * 2;
        }

        for (var i = 0; i < wH3H4.Length; i++)
        {
            wH3H4[i] = (rng.NextDouble() - 0.5) * 2;
        }

        for (var i = 0; i < wH4O.Length; i++)
        {
            wH4O[i] = (rng.NextDouble() - 0.5) * 2;
        }

        var result = NeuralScoringStrategy.ForwardPass(
            input, wIH, bH1, wH1H2, bH2, wH2H3, bH3, wH3H4, bH4, wH4O, 0.0,
            h1Pre, h1Act, h2Pre, h2Act, h3Pre, h3Act, h4Pre, h4Act);
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
    public void XavierInit_IsDeterministic()
    {
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
            CombinedCriticScore = 0.7,
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
            CombinedCriticScore = 0.7,
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
            CombinedCriticScore = 0.6,
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
            CombinedCriticScore = 0.6,
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

        var examples = new List<TrainingExample>();
        for (var i = 0; i < 100; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = 1.0,
                    CollaborativeScore = 1.0,
                    CombinedCriticScore = 1.0,
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
    public void Train_MultipleTimes_ProducesFiniteLoss()
    {
        var strategy = new NeuralScoringStrategy();
        var examples = GenerateExamples(20);

        strategy.Train(examples);
        var loss1 = strategy.LastValidationLoss;

        strategy.Train(examples);
        var loss2 = strategy.LastValidationLoss;

        Assert.False(double.IsNaN(loss1));
        Assert.False(double.IsNaN(loss2));

        // Second training pass should not regress significantly
        // (allowing a small epsilon for stochastic variation)
        Assert.True(loss2 <= loss1 + 0.05,
            $"Second training pass regressed: loss1={loss1:F6}, loss2={loss2:F6}");
    }

    // ============================================================
    // Weight Persistence Tests
    // ============================================================

    [Fact]
    public void PersistsWeights_ToFile()
    {
        var weightsPath = Path.Join(_tempDir, "neural_weights.json");
        var strategy = new NeuralScoringStrategy(weightsPath);

        var examples = GenerateExamples(20);
        strategy.Train(examples);

        Assert.True(File.Exists(weightsPath), "Weights file should be created after training");

        var json = File.ReadAllText(weightsPath);
        Assert.Contains("WeightsIH", json);
        Assert.Contains("BiasH1", json);
        Assert.Contains("WeightsH1H2", json);
        Assert.Contains("BiasH2", json);
        Assert.Contains("WeightsH2H3", json);
        Assert.Contains("BiasH3", json);
        Assert.Contains("WeightsH3H4", json);
        Assert.Contains("BiasH4", json);
        Assert.Contains("WeightsH4O", json);
        Assert.Contains("BiasOutput", json);
        Assert.Contains("Version", json);
        Assert.Contains("TrainingGeneration", json);
    }

    [Fact]
    public void LoadsWeights_FromFile()
    {
        var weightsPath = Path.Join(_tempDir, "neural_weights2.json");

        var strategy1 = new NeuralScoringStrategy(weightsPath);
        var examples = GenerateExamples(20);
        strategy1.Train(examples);

        var savedWH = strategy1.CurrentWeightsHidden;
        var savedWH1H2 = strategy1.CurrentWeightsH1H2;
        var savedWH2H3 = strategy1.CurrentWeightsH2H3;
        var savedWH3H4 = strategy1.CurrentWeightsH3H4;
        var savedWO = strategy1.CurrentWeightsOutput;
        var savedGen = strategy1.TrainingGeneration;

        var strategy2 = new NeuralScoringStrategy(weightsPath);
        var loadedWH = strategy2.CurrentWeightsHidden;
        var loadedWH1H2 = strategy2.CurrentWeightsH1H2;
        var loadedWH2H3 = strategy2.CurrentWeightsH2H3;
        var loadedWH3H4 = strategy2.CurrentWeightsH3H4;
        var loadedWO = strategy2.CurrentWeightsOutput;

        for (var i = 0; i < savedWH.Length; i++)
        {
            Assert.Equal(savedWH[i], loadedWH[i], 10);
        }

        for (var i = 0; i < savedWH1H2.Length; i++)
        {
            Assert.Equal(savedWH1H2[i], loadedWH1H2[i], 10);
        }

        for (var i = 0; i < savedWH2H3.Length; i++)
        {
            Assert.Equal(savedWH2H3[i], loadedWH2H3[i], 10);
        }

        for (var i = 0; i < savedWH3H4.Length; i++)
        {
            Assert.Equal(savedWH3H4[i], loadedWH3H4[i], 10);
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
        var weightsPath = Path.Join(_tempDir, "neural_weights3.json");

        var strategy1 = new NeuralScoringStrategy(weightsPath);
        var examples = GenerateExamples(20);
        strategy1.Train(examples);

        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.7,
            CollaborativeScore = 0.4,
            CombinedCriticScore = 0.6,
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
        var weightsPath = Path.Join(_tempDir, "corrupt_neural.json");
        File.WriteAllText(weightsPath, "not valid json {{{");

        var strategy = new NeuralScoringStrategy(weightsPath);
        var score = strategy.Score(new CandidateFeatures { GenreSimilarity = 0.5 });

        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void NullPath_WorksInMemoryOnly()
    {
        var strategy = new NeuralScoringStrategy(null);
        var examples = GenerateExamples(20);

        Assert.True(strategy.Train(examples));
        var score = strategy.Score(new CandidateFeatures { GenreSimilarity = 0.5 });
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void VersionMismatch_DiscardsWeights()
    {
        var weightsPath = Path.Join(_tempDir, "old_version.json");

        var fakeData = new NeuralScoringStrategy.NeuralWeightsData
        {
            WeightsIH = new double[NeuralScoringStrategy.Hidden1Size * CandidateFeatures.FeatureCount],
            BiasH1 = new double[NeuralScoringStrategy.Hidden1Size],
            WeightsH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size],
            BiasH2 = new double[NeuralScoringStrategy.Hidden2Size],
            WeightsH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size],
            BiasH3 = new double[NeuralScoringStrategy.Hidden3Size],
            WeightsH3H4 = new double[NeuralScoringStrategy.Hidden4Size * NeuralScoringStrategy.Hidden3Size],
            BiasH4 = new double[NeuralScoringStrategy.Hidden4Size],
            WeightsH4O = new double[NeuralScoringStrategy.Hidden4Size],
            BiasOutput = 999.0,
            Version = NeuralScoringStrategy.CurrentWeightsVersion - 1
        };

        var json = System.Text.Json.JsonSerializer.Serialize(fakeData);
        File.WriteAllText(weightsPath, json);

        var strategy = new NeuralScoringStrategy(weightsPath);
        var features = new CandidateFeatures();
        var score = strategy.Score(features);
        var expectedFreshScore = new NeuralScoringStrategy(null).Score(features);

        // Old-version weights should be discarded; score must match a freshly initialized strategy
        Assert.Equal(expectedFreshScore, score, 10);
    }

    // ============================================================
    // Constants Verification
    // ============================================================

    [Fact]
    public void HiddenSize_MatchesFinalHiddenLayer()
    {
        // Roadmap v3 A1: legacy HiddenSize alias now tracks Hidden4Size (the last hidden layer)
        // which is 24 in the wider v3 architecture (was 6 in v2). Keeping the alias in place
        // means older external references keep compiling — they now report the correct final
        // hidden width rather than the outdated v2 value.
        Assert.Equal(NeuralScoringStrategy.Hidden4Size, NeuralScoringStrategy.HiddenSize);
        Assert.Equal(24, NeuralScoringStrategy.HiddenSize);
    }

    [Fact]
    public void MinTrainingExamples_Is12()
    {
        Assert.Equal(12, NeuralScoringStrategy.MinTrainingExamples);
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
    public void XavierInit_InputHiddenWeights_CorrectLength()
    {
        var strategy = new NeuralScoringStrategy();
        var wIH = strategy.CurrentWeightsHidden;

        Assert.Equal(NeuralScoringStrategy.Hidden1Size * CandidateFeatures.FeatureCount, wIH.Length);
    }

    [Fact]
    public void XavierInit_H1H2Weights_CorrectLength()
    {
        var strategy = new NeuralScoringStrategy();
        var wH1H2 = strategy.CurrentWeightsH1H2;

        Assert.Equal(NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size, wH1H2.Length);
    }

    [Fact]
    public void XavierInit_H2H3Weights_CorrectLength()
    {
        var strategy = new NeuralScoringStrategy();
        var wH2H3 = strategy.CurrentWeightsH2H3;

        Assert.Equal(NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size, wH2H3.Length);
    }

    [Fact]
    public void XavierInit_OutputWeights_CorrectLength()
    {
        var strategy = new NeuralScoringStrategy();
        var wO = strategy.CurrentWeightsOutput;

        Assert.Equal(NeuralScoringStrategy.Hidden4Size, wO.Length);
    }

    [Fact]
    public void HeInit_InputHiddenWeights_WithinExpectedBounds()
    {
        var strategy = new NeuralScoringStrategy();
        var wIH = strategy.CurrentWeightsHidden;

        // He/Kaiming uniform for ReLU: limit = sqrt(6 / fan_in)
        var limit = Math.Sqrt(6.0 / CandidateFeatures.FeatureCount);

        foreach (var w in wIH)
        {
            Assert.InRange(w, -limit - 0.001, limit + 0.001);
        }
    }

    [Fact]
    public void XavierInit_OutputWeights_WithinExpectedBounds()
    {
        var strategy = new NeuralScoringStrategy();
        var wO = strategy.CurrentWeightsOutput;

        var limit = Math.Sqrt(6.0 / (NeuralScoringStrategy.Hidden4Size + 1));

        foreach (var w in wO)
        {
            Assert.InRange(w, -limit - 0.001, limit + 0.001);
        }
    }

    [Fact]
    public void ImplementsIScoringStrategy()
    {
        var strategy = new NeuralScoringStrategy();
        Assert.IsAssignableFrom<IScoringStrategy>(strategy);
    }

    [Fact]
    public void ImplementsITrainableStrategy()
    {
        var strategy = new NeuralScoringStrategy();
        Assert.IsAssignableFrom<ITrainableStrategy>(strategy);
    }

    // ============================================================
    // Four-Hidden-Layer Architecture Tests
    // ============================================================

    [Fact]
    public void Hidden1Size_IsV3Value()
    {
        // Roadmap v3 A1: 48 → 62 (≈ 2× InputSize expansion factor for tabular MLPs).
        Assert.Equal(62, NeuralScoringStrategy.Hidden1Size);
    }

    [Fact]
    public void Hidden2Size_IsV3Value()
    {
        // Roadmap v3 A1: 24 → 96 (widest layer; feature-interaction composition capacity).
        Assert.Equal(96, NeuralScoringStrategy.Hidden2Size);
    }

    [Fact]
    public void Hidden3Size_IsV3Value()
    {
        // Roadmap v3 A1: 12 → 48 (half of Hidden2; compression stage).
        Assert.Equal(48, NeuralScoringStrategy.Hidden3Size);
    }

    [Fact]
    public void Hidden4Size_IsV3Value()
    {
        // Roadmap v3 A1: 6 → 24 (final layer feeding the sigmoid output).
        Assert.Equal(24, NeuralScoringStrategy.Hidden4Size);
    }

    [Fact]
    public void CurrentWeightsVersion_IsV3()
    {
        // Roadmap v3 A1: version bump 2 → 3 signals to persistence-loaders that the
        // stored array shapes no longer match; a v2 file will be discarded on load.
        Assert.Equal(3, NeuralScoringStrategy.CurrentWeightsVersion);
    }

    [Fact]
    public void H1H2Weights_AreNotAllZero()
    {
        var strategy = new NeuralScoringStrategy();
        var wH1H2 = strategy.CurrentWeightsH1H2;

        Assert.True(wH1H2.Any(w => Math.Abs(w) > 1e-10), "H1→H2 weights should not all be zero after Xavier init");
    }

    [Fact]
    public void H2H3Weights_AreNotAllZero()
    {
        var strategy = new NeuralScoringStrategy();
        var wH2H3 = strategy.CurrentWeightsH2H3;

        Assert.True(wH2H3.Any(w => Math.Abs(w) > 1e-10), "H2→H3 weights should not all be zero after Xavier init");
    }

    [Fact]
    public void L2Lambda_Is0002()
    {
        Assert.Equal(0.002, NeuralScoringStrategy.L2Lambda);
    }

    [Fact]
    public void EarlyStoppingPatience_Is6()
    {
        Assert.Equal(6, NeuralScoringStrategy.EarlyStoppingPatience);
    }

    // ============================================================
    // Dropout Tests (Roadmap v3 A2)
    // ============================================================

    [Fact]
    public void DropoutKeepProbability_Is080()
    {
        // Roadmap v3 A2: mid-range 20% drop rate for small tabular MLPs.
        Assert.Equal(0.8, NeuralScoringStrategy.DropoutKeepProbability);
    }

    [Fact]
    public void MinExamplesForDropout_ExceedsMinTrainingExamples()
    {
        // Contract: dropout only kicks in when there are enough examples that
        // per-sample gradient starvation is unlikely. Requiring it to be strictly
        // greater than MinTrainingExamples ensures a training run that JUST hits
        // MinTrainingExamples runs WITHOUT dropout — a safer default for cold start.
        Assert.True(
            NeuralScoringStrategy.MinExamplesForDropout > NeuralScoringStrategy.MinTrainingExamples,
            $"MinExamplesForDropout ({NeuralScoringStrategy.MinExamplesForDropout}) must be greater than "
            + $"MinTrainingExamples ({NeuralScoringStrategy.MinTrainingExamples}) so the minimum-training case is dropout-free");
    }

    [Fact]
    public void ForwardPassTraining_KeepProbabilityOne_MatchesForwardPass()
    {
        // Contract: with keep-probability >= 1.0 the training-time forward pass must be
        // bit-identical to the deterministic inference-time forward pass. This is the
        // safety net that lets tests / diagnostics compare training vs. serving paths
        // without any tolerance windows.
        var inputSize = CandidateFeatures.FeatureCount;
        var rng = new Random(7);
        var input = new double[inputSize];
        for (var i = 0; i < inputSize; i++)
        {
            input[i] = rng.NextDouble();
        }

        // Realistic non-zero weights so the ReLU path is actually exercised in both directions.
        var wIH = new double[NeuralScoringStrategy.Hidden1Size * inputSize];
        for (var i = 0; i < wIH.Length; i++)
        {
            wIH[i] = (rng.NextDouble() - 0.5) * 2.0;
        }

        var bH1 = new double[NeuralScoringStrategy.Hidden1Size];
        var wH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size];
        for (var i = 0; i < wH1H2.Length; i++)
        {
            wH1H2[i] = (rng.NextDouble() - 0.5) * 2.0;
        }

        var bH2 = new double[NeuralScoringStrategy.Hidden2Size];
        var wH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size];
        for (var i = 0; i < wH2H3.Length; i++)
        {
            wH2H3[i] = (rng.NextDouble() - 0.5) * 2.0;
        }

        var bH3 = new double[NeuralScoringStrategy.Hidden3Size];
        var wH3H4 = new double[NeuralScoringStrategy.Hidden4Size * NeuralScoringStrategy.Hidden3Size];
        for (var i = 0; i < wH3H4.Length; i++)
        {
            wH3H4[i] = (rng.NextDouble() - 0.5) * 2.0;
        }

        var bH4 = new double[NeuralScoringStrategy.Hidden4Size];
        var wH4O = new double[NeuralScoringStrategy.Hidden4Size];
        for (var i = 0; i < wH4O.Length; i++)
        {
            wH4O[i] = (rng.NextDouble() - 0.5) * 2.0;
        }

        var h1PreA = new double[NeuralScoringStrategy.Hidden1Size];
        var h1ActA = new double[NeuralScoringStrategy.Hidden1Size];
        var h2PreA = new double[NeuralScoringStrategy.Hidden2Size];
        var h2ActA = new double[NeuralScoringStrategy.Hidden2Size];
        var h3PreA = new double[NeuralScoringStrategy.Hidden3Size];
        var h3ActA = new double[NeuralScoringStrategy.Hidden3Size];
        var h4PreA = new double[NeuralScoringStrategy.Hidden4Size];
        var h4ActA = new double[NeuralScoringStrategy.Hidden4Size];

        var expected = NeuralScoringStrategy.ForwardPass(
            input, wIH, bH1, wH1H2, bH2, wH2H3, bH3, wH3H4, bH4, wH4O, 0.0,
            h1PreA, h1ActA, h2PreA, h2ActA, h3PreA, h3ActA, h4PreA, h4ActA);

        var h1PreB = new double[NeuralScoringStrategy.Hidden1Size];
        var h1ActB = new double[NeuralScoringStrategy.Hidden1Size];
        var h2PreB = new double[NeuralScoringStrategy.Hidden2Size];
        var h2ActB = new double[NeuralScoringStrategy.Hidden2Size];
        var h3PreB = new double[NeuralScoringStrategy.Hidden3Size];
        var h3ActB = new double[NeuralScoringStrategy.Hidden3Size];
        var h4PreB = new double[NeuralScoringStrategy.Hidden4Size];
        var h4ActB = new double[NeuralScoringStrategy.Hidden4Size];
        var h1Mask = new double[NeuralScoringStrategy.Hidden1Size];
        var h2Mask = new double[NeuralScoringStrategy.Hidden2Size];
        var h3Mask = new double[NeuralScoringStrategy.Hidden3Size];
        var h4Mask = new double[NeuralScoringStrategy.Hidden4Size];

        var actual = NeuralScoringStrategy.ForwardPassTraining(
            input, wIH, bH1, wH1H2, bH2, wH2H3, bH3, wH3H4, bH4, wH4O, 0.0,
            h1PreB, h1ActB, h2PreB, h2ActB, h3PreB, h3ActB, h4PreB, h4ActB,
            h1Mask, h2Mask, h3Mask, h4Mask,
            new Random(0), // seed irrelevant when dropout is off
            keepProbability: 1.0,
            invKeepScale: 1.0);

        // Bit-identical output, and every mask value must be 1.0
        Assert.Equal(expected, actual, 15);
        Assert.All(h1Mask, m => Assert.Equal(1.0, m));
        Assert.All(h2Mask, m => Assert.Equal(1.0, m));
        Assert.All(h3Mask, m => Assert.Equal(1.0, m));
        Assert.All(h4Mask, m => Assert.Equal(1.0, m));

        // And every buffer must match ForwardPass exactly (activations, pre-activations).
        // We check every hidden layer, not just the endpoints, because middle-layer skew
        // would silently corrupt backpropagation while an endpoint-only test still passes
        // (Hidden2/Hidden3 feed the gradient chain and MUST match bit-for-bit with dropout off).
        for (var j = 0; j < NeuralScoringStrategy.Hidden1Size; j++)
        {
            Assert.Equal(h1PreA[j], h1PreB[j], 15);
            Assert.Equal(h1ActA[j], h1ActB[j], 15);
        }

        for (var j = 0; j < NeuralScoringStrategy.Hidden2Size; j++)
        {
            Assert.Equal(h2PreA[j], h2PreB[j], 15);
            Assert.Equal(h2ActA[j], h2ActB[j], 15);
        }

        for (var j = 0; j < NeuralScoringStrategy.Hidden3Size; j++)
        {
            Assert.Equal(h3PreA[j], h3PreB[j], 15);
            Assert.Equal(h3ActA[j], h3ActB[j], 15);
        }

        for (var j = 0; j < NeuralScoringStrategy.Hidden4Size; j++)
        {
            Assert.Equal(h4PreA[j], h4PreB[j], 15);
            Assert.Equal(h4ActA[j], h4ActB[j], 15);
        }
    }

    [Fact]
    public void ForwardPassTraining_DropoutActive_ProducesZeroMaskEntries()
    {
        // Contract: with keep-p = 0.5 and a fair RNG, over Hidden2Size draws we expect
        // roughly half the neurons to be dropped. We assert only the weaker claim that
        // *some* neurons are dropped so the test is not flaky.
        var inputSize = CandidateFeatures.FeatureCount;
        var input = new double[inputSize];
        for (var i = 0; i < inputSize; i++)
        {
            input[i] = 1.0; // uniform input so every neuron would otherwise fire
        }

        var wIH = new double[NeuralScoringStrategy.Hidden1Size * inputSize];
        // Positive weights so every ReLU pre-activation is positive → dropout is the only zero source.
        for (var i = 0; i < wIH.Length; i++)
        {
            wIH[i] = 0.1;
        }

        var bH1 = new double[NeuralScoringStrategy.Hidden1Size];
        var wH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size];
        for (var i = 0; i < wH1H2.Length; i++)
        {
            wH1H2[i] = 0.1;
        }

        var bH2 = new double[NeuralScoringStrategy.Hidden2Size];
        var wH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size];
        for (var i = 0; i < wH2H3.Length; i++)
        {
            wH2H3[i] = 0.1;
        }

        var bH3 = new double[NeuralScoringStrategy.Hidden3Size];
        var wH3H4 = new double[NeuralScoringStrategy.Hidden4Size * NeuralScoringStrategy.Hidden3Size];
        for (var i = 0; i < wH3H4.Length; i++)
        {
            wH3H4[i] = 0.1;
        }

        var bH4 = new double[NeuralScoringStrategy.Hidden4Size];
        var wH4O = new double[NeuralScoringStrategy.Hidden4Size];

        var h1Pre = new double[NeuralScoringStrategy.Hidden1Size];
        var h1Act = new double[NeuralScoringStrategy.Hidden1Size];
        var h2Pre = new double[NeuralScoringStrategy.Hidden2Size];
        var h2Act = new double[NeuralScoringStrategy.Hidden2Size];
        var h3Pre = new double[NeuralScoringStrategy.Hidden3Size];
        var h3Act = new double[NeuralScoringStrategy.Hidden3Size];
        var h4Pre = new double[NeuralScoringStrategy.Hidden4Size];
        var h4Act = new double[NeuralScoringStrategy.Hidden4Size];
        var h1Mask = new double[NeuralScoringStrategy.Hidden1Size];
        var h2Mask = new double[NeuralScoringStrategy.Hidden2Size];
        var h3Mask = new double[NeuralScoringStrategy.Hidden3Size];
        var h4Mask = new double[NeuralScoringStrategy.Hidden4Size];

        // Fixed seed so the test is deterministic. Random(0) with keep-p=0.5 produces
        // a well-mixed sequence over Hidden4Size=24 draws; empirically several neurons
        // ARE dropped and several are kept.
        var result = NeuralScoringStrategy.ForwardPassTraining(
            input, wIH, bH1, wH1H2, bH2, wH2H3, bH3, wH3H4, bH4, wH4O, 0.0,
            h1Pre, h1Act, h2Pre, h2Act, h3Pre, h3Act, h4Pre, h4Act,
            h1Mask, h2Mask, h3Mask, h4Mask,
            new Random(0),
            keepProbability: 0.5,
            invKeepScale: 2.0);

        Assert.InRange(result, 0.0, 1.0);

        // With keep-p = 0.5 across 24 + 48 + 96 + 62 = 230 Bernoulli draws, the probability
        // of getting all-ones OR all-zeros is astronomically small (~ 2^-230). We assert the
        // strictly weaker property that AT LEAST ONE neuron in Hidden4 is dropped AND at
        // least one is kept. The chance of this failing due to bad luck is ~ 2 × (0.5)^24
        // ≈ 1.2 × 10^-7 (still deterministic here thanks to the fixed seed).
        Assert.Contains(h4Mask, m => m == 0.0);
        Assert.Contains(h4Mask, m => m == 1.0);
        // Each mask entry must be exactly 0.0 or 1.0 — never in between.
        Assert.All(h4Mask, m => Assert.True(m == 0.0 || m == 1.0, $"Mask entry {m} is neither 0 nor 1"));
        Assert.All(h1Mask, m => Assert.True(m == 0.0 || m == 1.0));
        Assert.All(h2Mask, m => Assert.True(m == 0.0 || m == 1.0));
        Assert.All(h3Mask, m => Assert.True(m == 0.0 || m == 1.0));

        // Where a neuron was dropped, its activation must be exactly 0 regardless of
        // pre-activation magnitude. Where it was kept, activation = relu(pre) * invKeepScale.
        // Verified for EVERY hidden layer so a regression that records the mask but forgets
        // to apply zeroing/scaling in an earlier layer still fails the test (a previous
        // version only checked Hidden4 and would have missed a Hidden1/2/3-only regression).
        static void AssertDropoutApplied(double[] pre, double[] act, double[] mask, double invKeepScale)
        {
            Assert.Equal(pre.Length, act.Length);
            Assert.Equal(pre.Length, mask.Length);
            for (var k = 0; k < mask.Length; k++)
            {
                if (mask[k] == 0.0)
                {
                    Assert.Equal(0.0, act[k]);
                }
                else
                {
                    var expectedRelu = pre[k] > 0 ? pre[k] : 0.0;
                    Assert.Equal(expectedRelu * invKeepScale, act[k], 10);
                }
            }
        }

        AssertDropoutApplied(h1Pre, h1Act, h1Mask, 2.0);
        AssertDropoutApplied(h2Pre, h2Act, h2Mask, 2.0);
        AssertDropoutApplied(h3Pre, h3Act, h3Mask, 2.0);
        AssertDropoutApplied(h4Pre, h4Act, h4Mask, 2.0);
    }

    [Fact]
    public void ForwardPassTraining_ExpectedActivationMagnitudeMatchesForwardPass()
    {
        // Contract (weakened, mathematically correct): inverted dropout preserves
        // E[pre-activation] at each linear layer. It does NOT preserve the final
        // sigmoid output, because ReLU (piecewise-linear) and sigmoid (non-linear)
        // compositions mean E[sigmoid(f(X))] ≠ sigmoid(f(E[X])) in general. What we
        // CAN assert is that a large-sample mean of dropout-ON outputs is close to
        // the deterministic reference within a broad band, provided the weights are
        // small enough to keep every neuron in the ~linear regime of the sigmoid
        // (|z| < ~1 → local slope ≈ 0.20 - 0.25, low curvature). All weights below
        // are drawn from ±0.25 for exactly this reason.
        //
        // The tolerance is 0.10 (± 10 percentage points on the [0, 1] output): tight
        // enough to catch the concrete bug the test guards against (dropout code that
        // forgets the 1/p rescaling on a whole layer would drift the mean by 20-50%),
        // loose enough to survive the ~5% jensen-gap this construction inherits from
        // the sigmoid non-linearity. A tighter band would produce false negatives.
        var inputSize = CandidateFeatures.FeatureCount;
        var input = new double[inputSize];
        var rng = new Random(101);
        for (var i = 0; i < inputSize; i++)
        {
            input[i] = rng.NextDouble();
        }

        var wIH = new double[NeuralScoringStrategy.Hidden1Size * inputSize];
        for (var i = 0; i < wIH.Length; i++)
        {
            wIH[i] = (rng.NextDouble() - 0.5) * 0.5;
        }

        var bH1 = new double[NeuralScoringStrategy.Hidden1Size];
        var wH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size];
        for (var i = 0; i < wH1H2.Length; i++)
        {
            wH1H2[i] = (rng.NextDouble() - 0.5) * 0.5;
        }

        var bH2 = new double[NeuralScoringStrategy.Hidden2Size];
        var wH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size];
        for (var i = 0; i < wH2H3.Length; i++)
        {
            wH2H3[i] = (rng.NextDouble() - 0.5) * 0.5;
        }

        var bH3 = new double[NeuralScoringStrategy.Hidden3Size];
        var wH3H4 = new double[NeuralScoringStrategy.Hidden4Size * NeuralScoringStrategy.Hidden3Size];
        for (var i = 0; i < wH3H4.Length; i++)
        {
            wH3H4[i] = (rng.NextDouble() - 0.5) * 0.5;
        }

        var bH4 = new double[NeuralScoringStrategy.Hidden4Size];
        var wH4O = new double[NeuralScoringStrategy.Hidden4Size];
        for (var i = 0; i < wH4O.Length; i++)
        {
            wH4O[i] = (rng.NextDouble() - 0.5) * 0.5;
        }

        // Deterministic reference (dropout OFF).
        var h1PreRef = new double[NeuralScoringStrategy.Hidden1Size];
        var h1ActRef = new double[NeuralScoringStrategy.Hidden1Size];
        var h2PreRef = new double[NeuralScoringStrategy.Hidden2Size];
        var h2ActRef = new double[NeuralScoringStrategy.Hidden2Size];
        var h3PreRef = new double[NeuralScoringStrategy.Hidden3Size];
        var h3ActRef = new double[NeuralScoringStrategy.Hidden3Size];
        var h4PreRef = new double[NeuralScoringStrategy.Hidden4Size];
        var h4ActRef = new double[NeuralScoringStrategy.Hidden4Size];
        var reference = NeuralScoringStrategy.ForwardPass(
            input, wIH, bH1, wH1H2, bH2, wH2H3, bH3, wH3H4, bH4, wH4O, 0.0,
            h1PreRef, h1ActRef, h2PreRef, h2ActRef, h3PreRef, h3ActRef, h4PreRef, h4ActRef);

        // Now average many dropout-ON runs.
        var dropoutRng = new Random(777);
        var sum = 0.0;
        const int samples = 4000;
        var h1Pre = new double[NeuralScoringStrategy.Hidden1Size];
        var h1Act = new double[NeuralScoringStrategy.Hidden1Size];
        var h2Pre = new double[NeuralScoringStrategy.Hidden2Size];
        var h2Act = new double[NeuralScoringStrategy.Hidden2Size];
        var h3Pre = new double[NeuralScoringStrategy.Hidden3Size];
        var h3Act = new double[NeuralScoringStrategy.Hidden3Size];
        var h4Pre = new double[NeuralScoringStrategy.Hidden4Size];
        var h4Act = new double[NeuralScoringStrategy.Hidden4Size];
        var h1Mask = new double[NeuralScoringStrategy.Hidden1Size];
        var h2Mask = new double[NeuralScoringStrategy.Hidden2Size];
        var h3Mask = new double[NeuralScoringStrategy.Hidden3Size];
        var h4Mask = new double[NeuralScoringStrategy.Hidden4Size];

        for (var s = 0; s < samples; s++)
        {
            sum += NeuralScoringStrategy.ForwardPassTraining(
                input, wIH, bH1, wH1H2, bH2, wH2H3, bH3, wH3H4, bH4, wH4O, 0.0,
                h1Pre, h1Act, h2Pre, h2Act, h3Pre, h3Act, h4Pre, h4Act,
                h1Mask, h2Mask, h3Mask, h4Mask,
                dropoutRng,
                keepProbability: 0.8,
                invKeepScale: 1.25);
        }

        var mean = sum / samples;

        // 0.10 tolerance matches the contract documented above: catches broken 1/p rescaling
        // (which would drift the mean by 20-50%) while surviving the sigmoid's Jensen-gap.
        Assert.InRange(mean, reference - 0.10, reference + 0.10);
    }

    // ============================================================
    // Concurrency Tests
    // ============================================================

    [Fact]
    public void Score_ConcurrentCalls_DoNotThrow()
    {
        var strategy = new NeuralScoringStrategy();
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.7,
            CombinedCriticScore = 0.6,
            CollaborativeScore = 0.4
        };

        Parallel.For(0, 100, _ =>
        {
            var score = strategy.Score(features);
            Assert.InRange(score, 0.0, 1.0);
        });
    }

    [Fact]
    public async Task Score_DuringTraining_DoesNotThrow()
    {
        var strategy = new NeuralScoringStrategy();
        var examples = GenerateExamples(20);
        var features = new CandidateFeatures { GenreSimilarity = 0.5, CombinedCriticScore = 0.6 };

        var trainTask = Task.Run(() => strategy.Train(examples));
        var scoreTask = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                var score = strategy.Score(features);
                Assert.InRange(score, 0.0, 1.0);
            }
        });

        await Task.WhenAll(trainTask, scoreTask);
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
                    CombinedCriticScore = rng.NextDouble(),
                    RecencyScore = rng.NextDouble(),
                    YearProximityScore = rng.NextDouble(),
                    GenreCount = rng.Next(0, 6),
                    IsSeries = rng.NextDouble() > 0.5,
                    UserRatingScore = rng.NextDouble(),
                    CompletionRatio = rng.NextDouble(),
                    PeopleSimilarity = rng.NextDouble(),
                    StudioMatch = rng.NextDouble() > 0.5,
                    PopularityScore = rng.NextDouble(),
                    DayOfWeekAffinity = rng.NextDouble(),
                    LibraryAddedRecency = rng.NextDouble(),
                    LanguageAffinity = rng.NextDouble(),
                    CollectionProgressionBoost = rng.NextDouble(),
                    SubtitleLanguageAffinity = rng.NextDouble()
                },
                Label = genreSim > 0.5 ? 1.0 : 0.0
            });
        }

        return examples;
    }
}
