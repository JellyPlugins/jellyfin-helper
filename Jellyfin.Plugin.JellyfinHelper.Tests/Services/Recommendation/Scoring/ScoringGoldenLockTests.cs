using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Behavior-lock ("golden") test that pins the exact numeric output of the deterministic scoring strategies.
/// </summary>
public class ScoringGoldenLockTests
{
    // User-authorized v6: 41 shared coordinates, Neural 41→82→96→48→24→1 and a rebalanced
    // one-unit positive heuristic budget. The independent layer oracle below checks all 500
    // seeded Neural outputs; weight-budget and thematic tests check the deliberate prior changes.
    private const string ExpectedDigest = "A8D3F78126E9D75C704271C94116073B16B6E38C43FCF8A6E8C61BAAD5FD2C41";

    [Fact]
    public void ScoringStrategies_ProduceStableDigest_AcrossSeededFeatureBatch()
    {
        var digest = ComputeDigest();
        TestContext.Current.SendDiagnosticMessage("Current scoring digest: {0}", digest);

        // If this fails, a change altered scoring output. For a deliberate scoring-math change, regenerate ExpectedDigest from the new value (after reviewing the diff); otherwise the refactor was not behavior-preserving and must be fixed.
        Assert.Equal(ExpectedDigest, digest);
    }

    [Fact]
    public void V6DefaultNeuralMatchesIndependent41By82LayerEvaluation()
    {
        var random = new Random(42);
        double[] Weights(int input, int output, bool final = false)
        {
            var limit = Math.Sqrt(6.0 / (final ? input + output : input));
            return Enumerable.Range(0, input * output).Select(_ => (random.NextDouble() * 2.0 * limit) - limit).ToArray();
        }

        var first = Weights(41, 82);
        var second = Weights(82, 96);
        var third = Weights(96, 48);
        var fourth = Weights(48, 24);
        var last = Weights(24, 1, final: true);
        using var model = new NeuralScoringStrategy();
        var properties = typeof(CandidateFeatures).GetProperties().Where(property => property.CanWrite && property.Name != nameof(CandidateFeatures.IsExternalCandidate)
            && property.Name != nameof(CandidateFeatures.HasGenreEvidence)).OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        for (var seed = 1; seed <= 500; seed++)
        {
            var features = BuildDeterministicFeatures(properties, seed);
            var hidden = Relu(features.ToVector(), first, 82);
            hidden = Relu(hidden, second, 96);
            hidden = Relu(hidden, third, 48);
            hidden = Relu(hidden, fourth, 24);
            var logit = 0.0;
            for (var index = 0; index < last.Length; index++) logit += last[index] * hidden[index];
            var exponent = Math.Exp(-Math.Abs(logit));
            var expected = logit >= 0 ? 1 / (1 + exponent) : exponent / (1 + exponent);
            Assert.Equal(expected, model.Score(features), 12);
        }
    }

    private static double[] Relu(double[] input, double[] weights, int outputs)
    {
        var result = new double[outputs];
        for (var row = 0; row < outputs; row++)
        {
            var value = 0.0;
            for (var column = 0; column < input.Length; column++) value += weights[row * input.Length + column] * input[column];
            result[row] = Math.Max(0, value);
        }

        return result;
    }

    internal static string ComputeDigest()
    {
        var heuristic = new HeuristicScoringStrategy();
        var learned = new LearnedScoringStrategy();
        using var neural = new NeuralScoringStrategy();

        var settableDoubles = Array.FindAll(
            typeof(CandidateFeatures).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static p => p.CanWrite && p.Name != nameof(CandidateFeatures.IsExternalCandidate)
                && p.Name != nameof(CandidateFeatures.HasGenreEvidence));

        // Availability is policy metadata, not a numerical signal: adding its boolean must not consume
        // another RNG value and silently change the seed-to-feature mapping. The original known-genre
        // outputs remain locked; missing-genre behaviour has separate observable regression tests.

        // Reflection does NOT guarantee a stable GetProperties() order across runtimes/platforms, so sort by name to make the seed-to-feature assignment (and thus the digest) identical on Windows and the Linux CI runner.
        Array.Sort(settableDoubles, static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        var sb = new StringBuilder();
        for (var seed = 1; seed <= 500; seed++)
        {
            var features = BuildDeterministicFeatures(settableDoubles, seed);
            var h = heuristic.Score(features);
            var l = learned.Score(features);
            var n = neural.Score(features);

            // Round to 9 decimals before hashing.
            sb.Append(h.ToString("F9", CultureInfo.InvariantCulture)).Append('|')
              .Append(l.ToString("F9", CultureInfo.InvariantCulture)).Append('|')
              .Append(n.ToString("F9", CultureInfo.InvariantCulture)).Append(';');
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "Seeded System.Random is required here for REPRODUCIBLE test fixtures — the golden digest must be identical on every run. A cryptographic RNG would be non-deterministic and defeat the test.")]
    private static CandidateFeatures BuildDeterministicFeatures(PropertyInfo[] props, int seed)
    {
        var rng = new Random(seed);
        var features = new CandidateFeatures();
        foreach (var p in props)
        {
            object? value = null;
            if (p.PropertyType == typeof(double))
            {
                value = rng.NextDouble();
            }
            else if (p.PropertyType == typeof(int))
            {
                value = rng.Next(0, 10);
            }
            else if (p.PropertyType == typeof(bool))
            {
                value = rng.Next(0, 2) == 1;
            }

            if (value is not null)
            {
                p.SetValue(features, value);
            }
        }

        return features;
    }
}
