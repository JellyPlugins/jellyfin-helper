using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="NeuralScoringStrategy"/>: Forward-Pass, Backprop/Training,
///     Adam optimizer, Weight Persistence, Xavier initialization, Sigmoid, Dropout.
///     Architecture
///     <see cref="CandidateFeatures.FeatureCount"/> inputs → 76 hidden₁ → 96 hidden₂ →
///     48 hidden₃ → 24 hidden₄ → 1 output.
/// </summary>
public sealed class NeuralScoringStrategyTests : IDisposable
{
    /// <summary>
    ///     Epsilon for dropout-mask equality checks. Mask entries are assigned via
    ///     <c>keep ? 1.0 : 0.0</c> in <see cref="NeuralScoringStrategy"/> (no arithmetic),
    ///     so they are literally the double constants 0.0 and 1.0. Using
    ///     <c>Math.Abs(m - target) &lt;= MaskEpsilon</c> is semantically identical to a
    ///     bit-exact <c>==</c> comparison at this scale but silences the static-analyzer
    ///     "equality on floating-point" warning uniformly across the file. Single named
    ///     constant here so the test-wide tolerance never drifts and future readers see
    ///     the intent (exact 0/1, tolerance is only there for the analyzer).
    /// </summary>
    private const double MaskEpsilon = 1e-12;

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

    [Fact]
    public void Score_AfterDispose_ReturnsSafeScore()
    {
        var strategy = new NeuralScoringStrategy(null);
        strategy.Dispose();
        var score = strategy.Score(new CandidateFeatures());
        Assert.Equal(0.5, score, 10);
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
        Assert.Contains("FeatureMeans", json);
        Assert.Contains("FeatureStdDevs", json);
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
    public void GracefulFallback_OnNullJsonFile_ReturnsSafeScore()
    {
        var path = Path.Combine(_tempDir, "null-weights.json");
        File.WriteAllText(path, "null");
        var strategy = new NeuralScoringStrategy(path);
        var score = strategy.Score(new CandidateFeatures());
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
    public void Hidden1Size_MatchesExpansionFactor()
    {
        // 76 ≈ 2× InputSize (38) - expansion factor for tabular MLPs.
        Assert.Equal(76, NeuralScoringStrategy.Hidden1Size);
    }

    [Fact]
    public void Hidden2Size_IsV3Value()
    {
        // 24 → 96 (widest layer; feature-interaction composition capacity).
        Assert.Equal(96, NeuralScoringStrategy.Hidden2Size);
    }

    [Fact]
    public void Hidden3Size_IsV3Value()
    {
        // 12 → 48 (half of Hidden2; compression stage).
        Assert.Equal(48, NeuralScoringStrategy.Hidden3Size);
    }

    [Fact]
    public void Hidden4Size_IsV3Value()
    {
        // 6 → 24 (final layer feeding the sigmoid output).
        Assert.Equal(24, NeuralScoringStrategy.Hidden4Size);
    }

    [Fact]
    public void CurrentWeightsVersion_IsV3()
    {
        // Version bump 2 → 3 signals to persistence-loaders that the
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
    // Dropout Tests
    // ============================================================

    [Fact]
    public void Train_WithDropoutActive_ConvergesAndStaysFinite()
    {
        // Dropout-on backprop must be numerically correct AND stable. With inverted dropout the
        // activation is a = mask · relu(pre) · invKeep, so the error propagated into each hidden
        // layer's pre-activation carries exactly one invKeep factor. A missing OR doubled invKeep
        // would mis-scale the hidden gradients and either stall learning or diverge. This test
        // trains well above MinExamplesForDropout on a learnable signal (label = genreSim > 0.5)
        // and asserts the model both (a) produces a finite validation loss and (b) actually learns
        // the signal - the strongest black-box guard that the per-layer invKeep scaling is right.
        var trainCount = NeuralScoringStrategy.MinExamplesForDropout * 6; // comfortably dropout-active
        Assert.True(trainCount >= NeuralScoringStrategy.MinExamplesForDropout);

        var strategy = new NeuralScoringStrategy();
        var examples = GenerateExamples(trainCount);

        Assert.True(strategy.Train(examples));

        // (a) Finite, non-negative validation loss - no NaN/Inf from mis-scaled gradients.
        Assert.False(double.IsNaN(strategy.LastValidationLoss));
        Assert.False(double.IsInfinity(strategy.LastValidationLoss));
        Assert.True(strategy.LastValidationLoss >= 0.0);

        // (b) The signal was actually learned: a strong-genre-match candidate must score higher
        // than a weak-genre-match one. Divergent or wrongly-signed gradients would break this.
        var strong = strategy.Score(new CandidateFeatures { GenreSimilarity = 1.0 });
        var weak = strategy.Score(new CandidateFeatures { GenreSimilarity = 0.0 });
        Assert.True(double.IsFinite(strong) && double.IsFinite(weak));
        Assert.True(
            strong > weak,
            $"Dropout-active training should learn the genre signal (strong={strong:F4} should exceed weak={weak:F4})");
    }

    [Fact]
    public void DropoutKeepProbability_Is080()
    {
        // Mid-range 20% drop rate for small tabular MLPs.
        Assert.Equal(0.8, NeuralScoringStrategy.DropoutKeepProbability);
    }

    [Fact]
    public void MinExamplesForDropout_ExceedsMinTrainingExamples()
    {
        // Contract: dropout only kicks in when there are enough examples that
        // per-sample gradient starvation is unlikely. Requiring it to be strictly
        // greater than MinTrainingExamples ensures a training run that JUST hits
        // MinTrainingExamples runs WITHOUT dropout - a safer default for cold start.
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

        // Bit-identical output, and every mask value must be 1.0. See MaskEpsilon comment
        // for why we use approximate comparison on values that are literally assigned as 1.0
        // in production code - the epsilon is a static-analyzer accommodation, not a
        // tolerance for numerical drift.
        Assert.Equal(expected, actual, 15);
        Assert.All(h1Mask, m => Assert.True(Math.Abs(m - 1.0) <= MaskEpsilon));
        Assert.All(h2Mask, m => Assert.True(Math.Abs(m - 1.0) <= MaskEpsilon));
        Assert.All(h3Mask, m => Assert.True(Math.Abs(m - 1.0) <= MaskEpsilon));
        Assert.All(h4Mask, m => Assert.True(Math.Abs(m - 1.0) <= MaskEpsilon));

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
    public void ForwardPassTraining_KeepProbabilityZero_ProducesBiasOnlyOutput()
    {
        // With keepProbability=0.0 every unit is dropped; output = Sigmoid(biasOutput).
        // With all-zero biases biasOutput=0 so Sigmoid(0)=0.5.
        var inputSize = CandidateFeatures.FeatureCount;
        var input = new double[inputSize]; // all zeros
        var wIH = new double[NeuralScoringStrategy.Hidden1Size * inputSize]; // all zeros
        var bH1 = new double[NeuralScoringStrategy.Hidden1Size]; // all zeros
        var wH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size]; // all zeros
        var bH2 = new double[NeuralScoringStrategy.Hidden2Size]; // all zeros
        var wH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size]; // all zeros
        var bH3 = new double[NeuralScoringStrategy.Hidden3Size]; // all zeros
        var wH3H4 = new double[NeuralScoringStrategy.Hidden4Size * NeuralScoringStrategy.Hidden3Size]; // all zeros
        var bH4 = new double[NeuralScoringStrategy.Hidden4Size]; // all zeros
        var wH4O = new double[NeuralScoringStrategy.Hidden4Size]; // all zeros

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

        var result = NeuralScoringStrategy.ForwardPassTraining(
            input, wIH, bH1, wH1H2, bH2, wH2H3, bH3, wH3H4, bH4, wH4O, 0.0,
            h1Pre, h1Act, h2Pre, h2Act, h3Pre, h3Act, h4Pre, h4Act,
            h1Mask, h2Mask, h3Mask, h4Mask,
            new Random(0),
            keepProbability: 0.0,
            invKeepScale: 1.0);

        Assert.Equal(0.5, result, 6);
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

        // With keep-p = 0.5 across 24 + 48 + 96 + 76 = 244 Bernoulli draws, the probability
        // of getting all-ones OR all-zeros is astronomically small (~ 2^-244). We assert the
        // strictly weaker property that AT LEAST ONE neuron in Hidden4 is dropped AND at
        // least one is kept. The chance of this failing due to bad luck is ~ 2 × (0.5)^24
        // ≈ 1.2 × 10^-7 (still deterministic here thanks to the fixed seed).
        // MaskEpsilon: see the class-level constant comment for why the approximate
        // comparison is used on values that production code assigns as exact 0/1.
        Assert.Contains(h4Mask, m => Math.Abs(m - 0.0) <= MaskEpsilon);
        Assert.Contains(h4Mask, m => Math.Abs(m - 1.0) <= MaskEpsilon);
        // Each mask entry must be exactly 0.0 or 1.0 - never in between.
        Assert.All(h4Mask, m => Assert.True(
            Math.Abs(m - 0.0) <= MaskEpsilon || Math.Abs(m - 1.0) <= MaskEpsilon,
            $"Mask entry {m} is neither 0 nor 1"));
        Assert.All(h1Mask, m => Assert.True(
            Math.Abs(m - 0.0) <= MaskEpsilon || Math.Abs(m - 1.0) <= MaskEpsilon));
        Assert.All(h2Mask, m => Assert.True(
            Math.Abs(m - 0.0) <= MaskEpsilon || Math.Abs(m - 1.0) <= MaskEpsilon));
        Assert.All(h3Mask, m => Assert.True(
            Math.Abs(m - 0.0) <= MaskEpsilon || Math.Abs(m - 1.0) <= MaskEpsilon));

        // Where a neuron was dropped, its activation must be exactly 0 regardless of
        // pre-activation magnitude. Where it was kept, activation = relu(pre) * invKeepScale.
        // Verified for EVERY hidden layer so a regression that records the mask but forgets
        // to apply zeroing/scaling in an earlier layer still fails the test (a previous
        // version only checked Hidden4 and would have missed a Hidden1/2/3-only regression).
        //
        // The local function is `static` so it cannot capture the class-level MaskEpsilon
        // directly; we hardcode the same 1e-12 value here as a bit-exact mirror. Sharing
        // the value via a parameter was tried but rejected - passing a wrong epsilon at a
        // call site would silently invert the "dropped" / "kept" decision without any test
        // catching it. Keeping the value literal here + a comment cross-reference to
        // MaskEpsilon makes both call sites trivially auditable and eliminates the class
        // of "wrong-argument-passed" bugs entirely.
        static void AssertDropoutApplied(double[] pre, double[] act, double[] mask, double invKeepScale)
        {
            // Mirror of MaskEpsilon (see class-level constant) - kept literal because static
            // locals can't capture instance/class members. Bit-exact equality would work but
            // triggers the same static-analyzer warning MaskEpsilon exists to silence.
            const double dropMaskEpsilon = 1e-12;
            Assert.Equal(pre.Length, act.Length);
            Assert.Equal(pre.Length, mask.Length);
            for (var k = 0; k < mask.Length; k++)
            {
                if (Math.Abs(mask[k] - 0.0) <= dropMaskEpsilon)
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

    [Fact]
    public void Train_WithDropout_WeightsConvergeWithoutNaN()
    {
        // Dropout is activated when trainIdx.Length >= MinExamplesForDropout (30).
        // We use exactly MinExamplesForDropout examples so that the training split
        // (which is ~80% of examples after the val split) just reaches the threshold.
        // The test verifies that the dropout gradient path (h4Err no longer multiplied
        // by dropoutInvKeep - the bug that was fixed) does not produce NaN/Infinity and
        // that weights do actually change from their initial Xavier values.
        var strategy = new NeuralScoringStrategy();
        var initialWH = strategy.CurrentWeightsHidden.ToArray();
        var initialWO = strategy.CurrentWeightsOutput.ToArray();

        // >= MinExamplesForDropout examples to guarantee dropout is active during training.
        var examples = GenerateExamples(NeuralScoringStrategy.MinExamplesForDropout);
        var trained = strategy.Train(examples);

        Assert.True(trained, "Train should return true with enough examples");

        var updatedWH = strategy.CurrentWeightsHidden;
        var updatedWO = strategy.CurrentWeightsOutput;

        // No weight must be NaN or Infinity.
        Assert.All(updatedWH, w => Assert.True(
            double.IsFinite(w),
            $"Hidden weight became non-finite ({w}) after training with dropout"));
        Assert.All(updatedWO, w => Assert.True(
            double.IsFinite(w),
            $"Output weight became non-finite ({w}) after training with dropout"));

        // At least some weights must have changed from their initial Xavier values.
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

        Assert.True(anyHiddenChanged, "Training with dropout should modify hidden weights");
        Assert.True(anyOutputChanged, "Training with dropout should modify output weights");

        // Validation loss must be finite.
        Assert.True(double.IsFinite(strategy.LastValidationLoss),
            $"Validation loss is not finite ({strategy.LastValidationLoss}) after training with dropout");
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

    [Fact]
    public async Task Score_ConcurrentWithTrain_NoRaceCondition()
    {
        // Verifies the _featureMeans thread-safety fix: Train() writes _featureMeans
        // while Score() reads it.  Before the fix a torn write could produce NaN or
        // cause an exception; this test runs both concurrently and asserts that every
        // score is finite and no exception escapes.
        var strategy = new NeuralScoringStrategy();

        // Use enough examples so that _featureMeans is computed (and therefore the
        // read/write interleaving is exercised under real conditions).
        var examples = GenerateExamples(NeuralScoringStrategy.MinExamplesForDropout);
        var features = new CandidateFeatures
        {
            GenreSimilarity = 0.6,
            CombinedCriticScore = 0.5,
            CollaborativeScore = 0.4,
            RecencyScore = 0.7,
            YearProximityScore = 0.8
        };

        var scores = new System.Collections.Concurrent.ConcurrentBag<double>();
        var exceptions = new ConcurrentBag<Exception>();

        // Run several consecutive Train() calls while hammering Score() in parallel.
        const int trainRounds = 5;
        const int scorersPerRound = 20;

        for (var round = 0; round < trainRounds; round++)
        {
            var trainTask = Task.Run(() =>
            {
                try
                {
                    strategy.Train(examples);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            var scoreTasks = Enumerable.Range(0, scorersPerRound).Select(_ => Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 10; i++)
                    {
                        scores.Add(strategy.Score(features));
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })).ToArray();

            await Task.WhenAll(new[] { trainTask }.Concat(scoreTasks));
        }

        Assert.Empty(exceptions);

        // Every score collected must be a finite value in [0, 1].
        Assert.All(scores, s => Assert.True(
            double.IsFinite(s) && s >= 0.0 && s <= 1.0,
            $"Score {s} is not in the expected finite [0,1] range"));
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

// TEST-2: A structurally valid weights JSON containing NaN or Infinity in a single weight
// value must be rejected - the strategy must fall back to defaults and still return a
// finite score rather than silently propagating NaN through forward-pass arithmetic.
// TEST-3: ScoreVector() called concurrently with Train() must not observe partially-updated
// weights and must always return a finite score.
public sealed class NeuralScoringStrategyRobustnessTests : IDisposable
{
    private readonly string _tempDir;

    public NeuralScoringStrategyRobustnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"neural_robust_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void TryLoadWeights_NaNInSingleWeight_FallsBackToDefaults()
    {
        // Train a strategy to produce a valid weights file, then corrupt one weight with NaN.
        var weightsPath = Path.Join(_tempDir, "nan_weight.json");
        var strategy1 = new NeuralScoringStrategy(weightsPath);
        var rng = new Random(42);
        var examples = BuildExamples(rng, 20);
        strategy1.Train(examples);

        var json = File.ReadAllText(weightsPath);
        // Replace the first numeric weight value with NaN - structurally valid JSON.
        var corrupted = System.Text.RegularExpressions.Regex.Replace(
            json, @"""WeightsIH""\s*:\s*\[([^,\]]+)", m =>
                m.Value.Replace(m.Groups[1].Value, "NaN"),
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(2));
        File.WriteAllText(weightsPath, corrupted);

        // Load into a fresh strategy - must reject the NaN file and not crash.
        var strategy2 = new NeuralScoringStrategy(weightsPath);
        var score = strategy2.Score(new CandidateFeatures { GenreSimilarity = 0.7 });

        Assert.True(double.IsFinite(score), $"Score must be finite after NaN-weight rejection, got {score}");
        Assert.InRange(score, 0.0, 1.0);
        var freshStrategy = new NeuralScoringStrategy(null);
        Assert.Equal(freshStrategy.Score(new CandidateFeatures { GenreSimilarity = 0.7 }), score, 10);
    }

    [Fact]
    public async Task ScoreVector_ConcurrentWithTrain_AlwaysReturnsFiniteScore()
    {
        var strategy = new NeuralScoringStrategy(null);
        var rng = new Random(1);
        var examples = BuildExamples(rng, 40);

        // Prime the strategy with one train pass so ScoreVector has non-default weights.
        strategy.Train(examples);

        var vector = new double[CandidateFeatures.FeatureCount];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = rng.NextDouble();
        }

        var scores = new System.Collections.Concurrent.ConcurrentBag<double>();
        var trainTask = Task.Run(() =>
        {
            for (var i = 0; i < 5; i++)
            {
                strategy.Train(examples);
            }
        });

        var scoreTask = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var v = (double[])vector.Clone();
                scores.Add(strategy.ScoreVector(v));
            }
        });

        await Task.WhenAll(trainTask, scoreTask);

        foreach (var s in scores)
        {
            Assert.True(double.IsFinite(s), $"ScoreVector returned non-finite value {s} during concurrent Train");
            Assert.InRange(s, 0.0, 1.0);
        }
    }

    private static List<TrainingExample> BuildExamples(Random rng, int count)
    {
        var examples = new List<TrainingExample>(count);
        for (var i = 0; i < count; i++)
        {
            var g = rng.NextDouble();
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = g,
                    CollaborativeScore = rng.NextDouble(),
                    CombinedCriticScore = rng.NextDouble(),
                    RecencyScore = rng.NextDouble(),
                    YearProximityScore = rng.NextDouble(),
                    PopularityScore = rng.NextDouble(),
                    LanguageAffinity = rng.NextDouble(),
                    LibraryAddedRecency = rng.NextDouble()
                },
                Label = g > 0.5 ? 1.0 : 0.0
            });
        }

        return examples;
    }

    // ============================================================
    // Data race: _featureMeans / _featureStdDevs consistency
    // ============================================================

    /// <summary>
    ///     Concurrent Train() + Score() must never produce NaN and scores must stay in [0,1].
    ///     Covers the fix that moved _featureMeans/_featureStdDevs writes inside the _rwLock
    ///     write block so scorers holding the read lock always observe a coherent pair.
    /// </summary>
    [Fact]
    public async Task ConcurrentTrainAndScore_NoNanAndScoresInRange()
    {
        const int exampleCount = 60;
        const int scoreThreads = 8;
        const int scoresPerThread = 200;

        var rng = new Random(77);
        var examples = BuildExamples(rng, exampleCount);
        var strategy = new NeuralScoringStrategy();

        // Prime with one round so _featureMeans is non-null before concurrent access starts.
        strategy.Train(examples);

        var scores = new System.Collections.Concurrent.ConcurrentBag<double>();
        var cts = new CancellationTokenSource();

        var scoreTasks = Enumerable.Range(0, scoreThreads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < scoresPerThread && !cts.Token.IsCancellationRequested; i++)
            {
                var features = new CandidateFeatures
                {
                    GenreSimilarity = 0.6,
                    CollaborativeScore = 0.4,
                    CombinedCriticScore = 0.7,
                    RecencyScore = 0.5
                };
                scores.Add(strategy.Score(features));
            }
        })).ToArray();

        var trainTask = Task.Run(() =>
        {
            for (var round = 0; round < 5; round++)
            {
                strategy.Train(examples);
            }

            cts.Cancel();
        });

        await Task.WhenAll(scoreTasks.Append(trainTask));

        foreach (var s in scores)
        {
            Assert.True(double.IsFinite(s), $"Score() returned non-finite {s} during concurrent Train()");
            Assert.InRange(s, 0.0, 1.0);
        }
    }

    // ============================================================
    // Backprop: dropout invKeep must not compound across layers
    // ============================================================

    /// <summary>
    ///     Verifies that removing the spurious * dropoutInvKeep from h3Err/h2Err/h1Err
    ///     eliminates the (1/keep)^(L-1) compound gradient inflation.
    ///     Strategy: compare normalised input-layer weight-delta magnitudes between a run
    ///     with dropout active (>= MinExamplesForDropout examples) and one with dropout
    ///     inactive (fewer examples). The bug inflated the active-dropout run by ~(1/0.8)^3
    ///     ≈ 1.95×; after the fix the ratio (per-example) stays well below 2.0.
    /// </summary>
    [Fact]
    public void Backprop_DropoutInvKeep_NotCompoundedAcrossLayers()
    {
        var rngOff = new Random(42);
        var examplesOff = BuildExamples(rngOff, NeuralScoringStrategy.MinExamplesForDropout - 1);

        var rngOn = new Random(42);
        var examplesOn = BuildExamples(rngOn, NeuralScoringStrategy.MinExamplesForDropout + 5);

        var strategyOff = new NeuralScoringStrategy();
        var wBefore = (double[])strategyOff.CurrentWeightsHidden.Clone();
        strategyOff.Train(examplesOff);
        var wAfterOff = strategyOff.CurrentWeightsHidden;

        var deltaOff = 0.0;
        for (var i = 0; i < wBefore.Length; i++)
        {
            deltaOff += Math.Abs(wAfterOff[i] - wBefore[i]);
        }

        var strategyOn = new NeuralScoringStrategy();
        var wBeforeOn = (double[])strategyOn.CurrentWeightsHidden.Clone();
        strategyOn.Train(examplesOn);
        var wAfterOn = strategyOn.CurrentWeightsHidden;

        var deltaOn = 0.0;
        for (var i = 0; i < wBeforeOn.Length; i++)
        {
            deltaOn += Math.Abs(wAfterOn[i] - wBeforeOn[i]);
        }

        Assert.True(double.IsFinite(deltaOff) && deltaOff > 0,
            $"dropout-off delta should be finite and positive, got {deltaOff}");
        Assert.True(double.IsFinite(deltaOn) && deltaOn > 0,
            $"dropout-on delta should be finite and positive, got {deltaOn}");

        // Normalise by example count so the comparison is per-example gradient magnitude.
        // With the compound-scaling bug the ratio would be ~1.95*(35/29) ≈ 2.35.
        // After the fix it must stay below 2.0.
        var normOff = deltaOff / examplesOff.Count;
        var normOn = deltaOn / examplesOn.Count;
        var ratio = normOn / normOff;
        Assert.True(ratio < 2.0,
            $"Normalised gradient ratio (dropout-on/dropout-off) = {ratio:F4} exceeds 2.0 - compound dropoutInvKeep scaling bug likely present");
    }
}
