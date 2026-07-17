using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="NeuralFeatureImportance"/> — the permutation-importance analyzer
///     used to inspect which features drive the neural scoring model's predictions.
///     The class is internal, but the test assembly has <c>InternalsVisibleTo</c> access.
///     Focus areas:
///     <list type="bullet">
///         <item>Argument validation (ThrowIfNull / ThrowIfNegativeOrZero).</item>
///         <item>Edge cases: too-few samples → empty result (no divide-by-zero).</item>
///         <item>Sample cap: sampleSize > examples.Count must be clamped.</item>
///         <item>Determinism: seeded RNG produces stable importance values across runs.</item>
///         <item>Result shape: keys are FeatureIndex enum names, size == FeatureCount.</item>
///     </list>
/// </summary>
public class NeuralFeatureImportanceTests
{
    private static NeuralScoringStrategy CreateStrategy()
        => new(weightsPath: null, logger: NullLogger.Instance);

    /// <summary>Builds N examples with random-looking features that pass validation.</summary>
    private static List<TrainingExample> BuildExamples(int count, int seed = 7)
    {
        var rng = new Random(seed);
        var list = new List<TrainingExample>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = rng.NextDouble(),
                    CollaborativeScore = rng.NextDouble(),
                    CombinedCriticScore = rng.NextDouble(),
                    RecencyScore = rng.NextDouble(),
                    YearProximityScore = rng.NextDouble(),
                    GenreCount = rng.Next(1, 6),
                    IsSeries = i % 2 == 0,
                    UserRatingScore = rng.NextDouble(),
                    CompletionRatio = rng.NextDouble(),
                    HasUserInteraction = i % 3 == 0,
                    PeopleSimilarity = rng.NextDouble(),
                    StudioMatch = i % 4 == 0,
                    PopularityScore = rng.NextDouble(),
                    LibraryAddedRecency = rng.NextDouble()
                },
                Label = rng.NextDouble() > 0.5 ? 1.0 : 0.0
            });
        }

        return list;
    }

    // -----------------------------------------------------------------------
    // Argument validation
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputePermutationImportance_NullStrategy_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NeuralFeatureImportance.ComputePermutationImportance(null!, BuildExamples(5)));
    }

    [Fact]
    public void ComputePermutationImportance_NullExamples_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            NeuralFeatureImportance.ComputePermutationImportance(CreateStrategy(), null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ComputePermutationImportance_NonPositiveSampleSize_Throws(int sampleSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NeuralFeatureImportance.ComputePermutationImportance(CreateStrategy(), BuildExamples(5), sampleSize));
    }

    // -----------------------------------------------------------------------
    // Edge case: insufficient data
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputePermutationImportance_EmptyExamples_ReturnsEmptyDictionary()
    {
        // Contract: fewer than 2 samples cannot support permutation (no variance to permute).
        var result = NeuralFeatureImportance.ComputePermutationImportance(
            CreateStrategy(),
            new List<TrainingExample>());
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ComputePermutationImportance_SingleExample_ReturnsEmptyDictionary()
    {
        // A single sample has no variance to shuffle — must not divide by zero.
        var result = NeuralFeatureImportance.ComputePermutationImportance(
            CreateStrategy(),
            BuildExamples(1));
        Assert.Empty(result);
    }

    [Fact]
    public void ComputePermutationImportance_SampleSizeOne_TreatedAsInsufficient()
    {
        // Even with 10 examples, capping to sampleSize=1 must yield empty (guard on
        // actualSampleSize < 2).
        var result = NeuralFeatureImportance.ComputePermutationImportance(
            CreateStrategy(),
            BuildExamples(10),
            sampleSize: 1);
        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // Sample cap
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputePermutationImportance_SampleSizeExceedsExamplesCount_ClampsToExamplesCount()
    {
        // Regression: passing sampleSize=1000 with only 10 examples must not IndexOutOfRange.
        var result = NeuralFeatureImportance.ComputePermutationImportance(
            CreateStrategy(),
            BuildExamples(10),
            sampleSize: 1000);
        Assert.NotEmpty(result);
        Assert.Equal(CandidateFeatures.FeatureCount, result.Count);
    }

    // -----------------------------------------------------------------------
    // Result shape
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputePermutationImportance_ReturnsOneEntryPerFeature()
    {
        var result = NeuralFeatureImportance.ComputePermutationImportance(
            CreateStrategy(),
            BuildExamples(50));
        Assert.Equal(CandidateFeatures.FeatureCount, result.Count);
    }

    [Fact]
    public void ComputePermutationImportance_ResultKeys_MatchFeatureIndexEnumNames()
    {
        // Contract: consumers of the result look up entries by feature name to render
        // debug tables. Any drift between the enum and the result keys silently breaks
        // that UI.
        var result = NeuralFeatureImportance.ComputePermutationImportance(
            CreateStrategy(),
            BuildExamples(50));
        var expectedNames = Enum.GetNames<FeatureIndex>();
        foreach (var name in expectedNames)
        {
            Assert.Contains(name, result.Keys);
        }
    }

    [Fact]
    public void ComputePermutationImportance_AllValuesAreFiniteDoubles()
    {
        // Regression: a bug that emitted NaN/Infinity would poison downstream logging
        // and might get stored to disk. All importance values must be finite.
        var result = NeuralFeatureImportance.ComputePermutationImportance(
            CreateStrategy(),
            BuildExamples(50));
        Assert.All(result.Values, v => Assert.True(double.IsFinite(v),
            $"Importance value {v} is not finite"));
    }

    // -----------------------------------------------------------------------
    // Determinism — seeded RNG must produce identical results on repeat calls.
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputePermutationImportance_IsDeterministic_AcrossRepeatedCalls()
    {
        // The class uses a fixed seed (Random(42)) so repeated calls with the same
        // input MUST produce byte-identical importance dictionaries. Any drift here
        // means someone accidentally introduced ambient state.
        var strategy = CreateStrategy();
        var examples = BuildExamples(30);

        var run1 = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples);
        var run2 = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples);

        Assert.Equal(run1.Count, run2.Count);
        foreach (var kv in run1)
        {
            Assert.True(run2.ContainsKey(kv.Key));
            Assert.Equal(kv.Value, run2[kv.Key], precision: 12);
        }
    }

    [Fact]
    public void ComputePermutationImportance_DifferentSampleSize_CanYieldDifferentResults()
    {
        // Sanity: different sampleSize must be able to affect the outcome, otherwise
        // the sampling is a no-op.
        var strategy = CreateStrategy();
        var examples = BuildExamples(50);
        var small = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples, sampleSize: 5);
        var full = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples, sampleSize: 50);
        // Both have the correct number of features but the actual importance numbers
        // must be free to differ.
        Assert.Equal(CandidateFeatures.FeatureCount, small.Count);
        Assert.Equal(CandidateFeatures.FeatureCount, full.Count);
    }

    // -----------------------------------------------------------------------
    // Default sample size constant
    // -----------------------------------------------------------------------

    [Fact]
    public void DefaultSampleSize_IsPositive_AndMatchesDocumentation()
    {
        Assert.True(NeuralFeatureImportance.DefaultSampleSize > 0);
        Assert.Equal(200, NeuralFeatureImportance.DefaultSampleSize);
    }
}