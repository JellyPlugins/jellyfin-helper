using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Behavior-lock ("golden") test that pins the exact numeric output of the deterministic
///     scoring strategies. It fills a large batch of <see cref="CandidateFeatures" /> with
///     seed-deterministic values (via reflection over every public settable property, so it
///     covers all 38 features regardless of which one a refactor touches), scores each with the
///     Heuristic, Learned and Neural strategies, and reduces every score to a single digest
///     string. Any change in scoring math — the kind a pure structural S3776 refactor must NOT
///     introduce — changes the digest and fails the test. This guards behavior independently of
///     whatever the feature-specific unit tests happen to cover.
/// </summary>
public class ScoringGoldenLockTests
{
    // Deterministic digest of Heuristic+Learned+Neural scores over 500 seeded feature vectors.
    // If a behavior-preserving refactor changes this, the refactor changed behavior. Regenerate
    // ONLY when an intentional scoring-math change is made (and review the diff carefully).
    private const string ExpectedDigest = "119409E262B97799940A15D0C0165FDA23EEFAF64DFF110BBE9480225AB52BED";

    [Fact]
    public void ScoringStrategies_ProduceStableDigest_AcrossSeededFeatureBatch()
    {
        var digest = ComputeDigest();

        if (ExpectedDigest == "PENDING")
        {
            // First run prints the baseline so it can be pinned; treat as informational.
#pragma warning disable CS0162 // Unreachable code detected
            Assert.Fail($"Golden digest baseline = {digest}");
#pragma warning restore CS0162 // Unreachable code detected
        }

        Assert.Equal(ExpectedDigest, digest);
    }

    internal static string ComputeDigest()
    {
        var heuristic = new HeuristicScoringStrategy();
        var learned = new LearnedScoringStrategy();
        var neural = new NeuralScoringStrategy();

        var settableDoubles = Array.FindAll(
            typeof(CandidateFeatures).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static p => p.CanWrite);

        var sb = new StringBuilder();
        for (var seed = 1; seed <= 500; seed++)
        {
            var features = BuildDeterministicFeatures(settableDoubles, seed);
            var h = heuristic.Score(features);
            var l = learned.Score(features);
            var n = neural.Score(features);
            sb.Append(h.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(l.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(n.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

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
