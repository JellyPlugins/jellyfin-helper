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
    // Deterministic digest of Heuristic+Learned+Neural scores over 500 seeded feature vectors. If a behavior-preserving refactor changes this, the refactor changed behavior.
    private const string ExpectedDigest = "C92AFA7751C77E7FE07513C01094B4A0CB4650B67B2515C93C6C0B53E2FEF940";

    [Fact]
    public void ScoringStrategies_ProduceStableDigest_AcrossSeededFeatureBatch()
    {
        var digest = ComputeDigest();

        // If this fails, a change altered scoring output. For a deliberate scoring-math change, regenerate ExpectedDigest from the new value (after reviewing the diff); otherwise the refactor was not behavior-preserving and must be fixed.
        Assert.Equal(ExpectedDigest, digest);
    }

    internal static string ComputeDigest()
    {
        var heuristic = new HeuristicScoringStrategy();
        var learned = new LearnedScoringStrategy();
        using var neural = new NeuralScoringStrategy();

        var settableDoubles = Array.FindAll(
            typeof(CandidateFeatures).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static p => p.CanWrite);

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
