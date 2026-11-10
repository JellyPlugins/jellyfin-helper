using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for NeuralFeatureImportance - the permutation-importance analyzer used to inspect which features drive the neural scoring model's predictions.
/// </summary>
public class NeuralFeatureImportanceTests
{
    private static NeuralScoringStrategy CreateStrategy()
        => new(weightsPath: null, logger: NullLogger.Instance);

    [Fact]
    public void PredictiveFeature_IsImportantEvenWhenPermutationPreservesMeanScore()
    {
        using var strategy = CreateSingleFeatureStrategy();
        var examples = BuildPredictableExamples(strategy);
        var result = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples);

        Assert.True(result[nameof(FeatureIndex.LibraryAddedRecency)] > 0.001);
        Assert.Equal(0.0, result[nameof(FeatureIndex.GenreSimilarity)]);
    }

    [Fact]
    public void HarmfulFeature_HasNegativeImportance()
    {
        using var strategy = CreateSingleFeatureStrategy();
        var examples = BuildPredictableExamples(strategy);
        foreach (var example in examples) example.Label = 1.0 - example.Label;
        var result = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples);

        Assert.True(result[nameof(FeatureIndex.LibraryAddedRecency)] < 0.0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IgnoredExamples_DoNotAffectSamplingOrImportance(bool temporallyExpired)
    {
        using var strategy = CreateSingleFeatureStrategy();
        var examples = BuildPredictableExamples(strategy);
        var reference = DateTime.UtcNow;
        var ignored = new TrainingExample
        {
            Features = new CandidateFeatures { LibraryAddedRecency = 1.0 },
            Label = 0.0,
            SampleWeight = temporallyExpired ? 1.0 : 0.0,
            GeneratedAtUtc = temporallyExpired ? reference.AddDays(-900) : reference
        };
        var baseline = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples, referenceTimeUtc: reference);
        var actual = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples.Prepend(ignored).ToArray(), referenceTimeUtc: reference);

        Assert.Equal(baseline.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void Importance_UsesSampleAndTemporalWeightsWithFixedLabels()
    {
        using var strategy = CreateSingleFeatureStrategy();
        var examples = BuildPredictableExamples(strategy);
        var reference = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < examples.Length; i++)
        {
            examples[i].SampleWeight = 0.1 + 0.9 * i / examples.Length;
            examples[i].GeneratedAtUtc = reference.AddDays(-10 * i);
        }

        var result = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples, referenceTimeUtc: reference);
        var expected = ExpectedPermutedLoss(strategy, examples, reference, (int)FeatureIndex.LibraryAddedRecency);
        Assert.Equal(expected, result[nameof(FeatureIndex.LibraryAddedRecency)], precision: 12);
    }

    [Fact]
    public void Normalization_DoesNotMutateSampleFeaturesOrModelWeights()
    {
        using var strategy = CreateSingleFeatureStrategy();
        typeof(NeuralScoringStrategy).GetField("_featureMeans", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(strategy, Enumerable.Repeat(0.2, CandidateFeatures.FeatureCount).ToArray());
        typeof(NeuralScoringStrategy).GetField("_featureStdDevs", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(strategy, Enumerable.Repeat(0.4, CandidateFeatures.FeatureCount).ToArray());
        var examples = BuildPredictableExamples(strategy);
        var vectors = examples.Select(example => example.Features.ToVector()).ToArray();
        var weights = strategy.GetCurrentWeightsHidden();
        var reference = DateTime.UtcNow;
        var first = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples, referenceTimeUtc: reference);
        var second = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples, referenceTimeUtc: reference);

        Assert.True(first[nameof(FeatureIndex.LibraryAddedRecency)] > 0.0);
        Assert.Equal(first.OrderBy(pair => pair.Key), second.OrderBy(pair => pair.Key));
        for (var i = 0; i < examples.Length; i++) Assert.Equal(vectors[i], examples[i].Features.ToVector());
        Assert.Equal(weights, strategy.GetCurrentWeightsHidden());
    }

    private static NeuralScoringStrategy CreateSingleFeatureStrategy()
    {
        var strategy = CreateStrategy();
        foreach (var name in new[] { "_weightsIH", "_weightsH1H2", "_weightsH2H3", "_weightsH3H4", "_weightsH4O", "_biasH1", "_biasH2", "_biasH3", "_biasH4" })
        {
            var values = (double[])typeof(NeuralScoringStrategy).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(strategy)!;
            Array.Clear(values);
            if (name.StartsWith("_weights", StringComparison.Ordinal)) values[name == "_weightsIH" ? (int)FeatureIndex.LibraryAddedRecency : 0] = 1.0;
        }

        typeof(NeuralScoringStrategy).GetField("_biasOutput", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(strategy, 0.0);
        return strategy;
    }

    private static TrainingExample[] BuildPredictableExamples(NeuralScoringStrategy strategy)
        => Enumerable.Range(0, 16).Select(i =>
        {
            var features = new CandidateFeatures { LibraryAddedRecency = i / 15.0 };
            return new TrainingExample { Features = features, Label = strategy.Score(features) };
        }).ToArray();

    private static double ExpectedPermutedLoss(NeuralScoringStrategy strategy, TrainingExample[] examples, DateTime reference, int feature)
    {
        var rng = new Random(42);
        var indices = Enumerable.Range(0, examples.Length).ToArray();
        for (var i = 0; i < indices.Length - 1; i++)
        {
            var j = rng.Next(i, indices.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var samples = indices.Select(index => examples[index]).ToArray();
        var values = Array.Empty<double>();
        for (var f = 0; f <= feature; f++)
        {
            values = samples.Select(sample => sample.Features.ToVector()[f]).ToArray();
            for (var i = values.Length - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (values[i], values[j]) = (values[j], values[i]);
            }
        }

        var loss = 0.0;
        var totalWeight = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var vector = samples[i].Features.ToVector();
            vector[feature] = values[i];
            var error = strategy.ScoreVector(vector) - samples[i].Label;
            var weight = samples[i].ComputeEffectiveWeight(reference);
            loss += weight * error * error;
            totalWeight += weight;
        }

        return loss / totalWeight;
    }

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

    // Argument validation

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

    // Edge case: insufficient data

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
        // A single sample has no variance to shuffle - must not divide by zero.
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

    // Sample cap

    [Fact]
    public void ComputePermutationImportance_SampleSizeExceedsExamplesCount_ClampsToExamplesCount()
    {
        var result = NeuralFeatureImportance.ComputePermutationImportance(
            CreateStrategy(),
            BuildExamples(10),
            sampleSize: 1000);
        Assert.NotEmpty(result);
        Assert.Equal(CandidateFeatures.FeatureCount, result.Count);
    }

    // Result shape

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

        // Every result key must be a declared FeatureIndex name. We compare against the set of all
        // enum names (a HashSet) rather than iterating them: FeatureIndex.SeriesAffinity and
        // FeatureIndex.SeriesProgressionBoost share the same underlying value (15), so Enum.ToString()
        // on that value may return either alias depending on the runtime. Asserting "each enum name is
        // a key" is therefore unreliable; asserting "each key is a valid enum name" is exact.
        var validNames = new HashSet<string>(Enum.GetNames<FeatureIndex>(), StringComparer.Ordinal);
        foreach (var key in result.Keys)
        {
            Assert.Contains(key, validNames);
        }

        // Explicit contract for the aliased enum value 15: the canonical "SeriesAffinity" key must
        // always appear in the result, and the legacy alias must never create a separate entry.
        Assert.Contains(nameof(FeatureIndex.SeriesAffinity), result.Keys);
        Assert.DoesNotContain(nameof(FeatureIndex.SeriesProgressionBoost), result.Keys);
    }

    [Fact]
    public void ComputePermutationImportance_AllValuesAreFiniteDoubles()
    {
        var result = NeuralFeatureImportance.ComputePermutationImportance(
            CreateStrategy(),
            BuildExamples(50));
        Assert.All(result.Values, v => Assert.True(double.IsFinite(v),
            $"Importance value {v} is not finite"));
    }

    // Determinism - seeded RNG must produce identical results on repeat calls.

    [Fact]
    public void ComputePermutationImportance_IsDeterministic_AcrossRepeatedCalls()
    {
        // The class uses a fixed seed (Random(42)) so repeated calls with the same input MUST produce byte-identical importance dictionaries.
        var strategy = CreateStrategy();
        var examples = BuildExamples(30);

        var run1 = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples);
        var run2 = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples);

        Assert.Equal(run1.Count, run2.Count);
        foreach (var kv in run1)
        {
            Assert.True(run2.TryGetValue(kv.Key, out var otherValue));
            Assert.Equal(kv.Value, otherValue, precision: 12);
        }
    }

    [Fact]
    public void ComputePermutationImportance_DifferentSampleSize_YieldsDifferentImportanceValues()
    {
        // Sanity: different sampleSize must be able to affect the outcome, otherwise the sampling is a no-op.
        var strategy = CreateStrategy();
        var examples = BuildExamples(50);
        var small = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples, sampleSize: 5);
        var full = NeuralFeatureImportance.ComputePermutationImportance(strategy, examples, sampleSize: 50);

        Assert.Equal(CandidateFeatures.FeatureCount, small.Count);
        Assert.Equal(CandidateFeatures.FeatureCount, full.Count);

        // Feature keys must be identical (deterministic layout across runs).
        Assert.Equal(small.Keys.OrderBy(k => k), full.Keys.OrderBy(k => k));

        // Assert that at least one importance value differs. With sampleSize=5 vs 50 over a permutation-based algorithm, statistical variance across even the most stable feature is essentially guaranteed.
        var anyDifferent = small.Any(kv => Math.Abs(kv.Value - full[kv.Key]) > 1e-9);
        Assert.True(
            anyDifferent,
            "sampleSize must influence permutation importance results; every value matched exactly, suggesting the sampling was a no-op.");
    }

    // Default sample size constant

    [Fact]
    public void DefaultSampleSize_IsPositive_AndMatchesDocumentation()
    {
        Assert.True(NeuralFeatureImportance.DefaultSampleSize > 0);
        Assert.Equal(200, NeuralFeatureImportance.DefaultSampleSize);
    }
}
