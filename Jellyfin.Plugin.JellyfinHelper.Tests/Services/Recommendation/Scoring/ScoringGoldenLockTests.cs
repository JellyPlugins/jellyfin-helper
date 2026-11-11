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
    // Rebaselined for the deliberate BoxSet-over-franchise prior: collection 0.045 -> 0.150,
    // franchise 0.040 -> 0.025. The seed-to-feature mapping and all three scoring algorithms stay locked.
    private const string ExpectedDigest = "9825A32ABD237D833B8FB460EFFB42B4CA0A6C0486A37C1588605317BFEF3EC7";

    [Fact]
    public void ScoringStrategies_ProduceStableDigest_AcrossSeededFeatureBatch()
    {
        var digest = ComputeDigest();
        TestContext.Current.SendDiagnosticMessage("Current scoring digest: {0}", digest);

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
