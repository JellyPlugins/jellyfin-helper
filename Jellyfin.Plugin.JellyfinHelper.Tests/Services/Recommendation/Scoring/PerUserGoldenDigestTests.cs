using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Parity ("golden digest") test for the per-user recommendation model: a single user whose per-user
///     learned model is warm-started from the global fit and then trained on the same examples reproduces the
///     global model's scores exactly. Cold-start parity (a user below the per-user threshold gets the exact
///     global instance) is covered separately in PerUserEnsembleRegistryTests.
/// </summary>
public sealed class PerUserGoldenDigestTests
{
    [Fact]
    public void SingleUserPerUserModel_MatchesGlobalModel()
    {
        var examples = GenerateExamples(200);

        // The shared global fit both models warm-start from.
        var global = new LearnedScoringStrategy();
        Assert.True(global.Train(examples));

        // Path A: continue training the global model on the identical example set.
        var globalContinued = new LearnedScoringStrategy();
        globalContinued.SeedFrom(global);
        Assert.True(globalContinued.Train(examples));

        // Path B: a per-user model warm-started from the SAME global fit, trained on the SAME data.
        var perUser = new LearnedScoringStrategy();
        perUser.SeedFrom(global);
        Assert.True(perUser.Train(examples));

        // One user fed identical data from the identical warm start must reproduce global behaviour exactly.
        var digestA = ComputeScoreDigest(globalContinued);
        var digestB = ComputeScoreDigest(perUser);
        Assert.Equal(digestA, digestB);
    }

    private static string ComputeScoreDigest(LearnedScoringStrategy strategy)
    {
        var settableDoubles = Array.FindAll(
            typeof(CandidateFeatures).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static p => p.CanWrite);

        // Reflection does NOT guarantee a stable GetProperties() order across runtimes, so sort by name to
        // make the seed-to-feature assignment identical on Windows and the Linux CI runner.
        Array.Sort(settableDoubles, static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        var sb = new StringBuilder();
        for (var seed = 1; seed <= 50; seed++)
        {
            var features = BuildDeterministicFeatures(settableDoubles, seed);
            var score = strategy.Score(features);
            sb.Append(score.ToString("F9", CultureInfo.InvariantCulture)).Append(';');
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "Seeded System.Random is required here for REPRODUCIBLE test fixtures - the parity digest must be identical on every run. A cryptographic RNG would be non-deterministic and defeat the test.")]
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "Seeded System.Random is required here for REPRODUCIBLE training fixtures - the parity test needs the identical example set on every run.")]
    private static List<TrainingExample> GenerateExamples(int count)
    {
        var rng = new Random(42);
        var examples = new List<TrainingExample>(count);

        // Pin GeneratedAtUtc to a fixed future date so ComputeTemporalWeight hits its ageDays <= 0 branch
        // (weight exactly 1.0) on every run. Otherwise the two Train() calls read DateTime.UtcNow microseconds
        // apart and produce infinitesimally different temporal weights, breaking exact digest equality.
        var generatedAt = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
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
                    IsSeries = rng.NextDouble() > 0.5
                },
                Label = genreSim > 0.5 ? 1.0 : 0.0,
                GeneratedAtUtc = generatedAt
            });
        }

        return examples;
    }
}
