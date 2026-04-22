using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="TrainingExample"/>: ComputeEffectiveWeight(), ComputeTemporalWeight(),
///     Label/SampleWeight clamping, and temporal decay constants.
/// </summary>
public sealed class TrainingExampleTests
{
    // ============================================================
    // Constants Verification
    // ============================================================

    [Fact]
    public void TemporalDecayHalfLifeDays_Is90()
    {
        Assert.Equal(90.0, TrainingExample.TemporalDecayHalfLifeDays);
    }

    [Fact]
    public void TemporalDecayConstant_IsLn2OverHalfLife()
    {
        var expected = Math.Log(2.0) / 90.0;
        Assert.Equal(expected, TrainingExample.TemporalDecayConstant, 12);
    }

    // ============================================================
    // Label Clamping Tests
    // ============================================================

    [Fact]
    public void Label_DefaultIsZero()
    {
        var example = new TrainingExample { Features = new CandidateFeatures() };
        Assert.Equal(0.0, example.Label);
    }

    [Fact]
    public void Label_ClampsToZero_WhenNegative()
    {
        var example = new TrainingExample { Features = new CandidateFeatures(), Label = -0.5 };
        Assert.Equal(0.0, example.Label);
    }

    [Fact]
    public void Label_ClampsToOne_WhenAboveOne()
    {
        var example = new TrainingExample { Features = new CandidateFeatures(), Label = 1.5 };
        Assert.Equal(1.0, example.Label);
    }

    [Fact]
    public void Label_AcceptsValidRange()
    {
        var example = new TrainingExample { Features = new CandidateFeatures(), Label = 0.75 };
        Assert.Equal(0.75, example.Label);
    }

    // ============================================================
    // SampleWeight Clamping Tests
    // ============================================================

    [Fact]
    public void SampleWeight_DefaultIsOne()
    {
        var example = new TrainingExample { Features = new CandidateFeatures() };
        Assert.Equal(1.0, example.SampleWeight);
    }

    [Fact]
    public void SampleWeight_ClampsToZero_WhenNegative()
    {
        var example = new TrainingExample { Features = new CandidateFeatures(), SampleWeight = -0.5 };
        Assert.Equal(0.0, example.SampleWeight);
    }

    [Fact]
    public void SampleWeight_ClampsToOne_WhenAboveOne()
    {
        var example = new TrainingExample { Features = new CandidateFeatures(), SampleWeight = 2.0 };
        Assert.Equal(1.0, example.SampleWeight);
    }

    [Fact]
    public void SampleWeight_AcceptsValidRange()
    {
        var example = new TrainingExample { Features = new CandidateFeatures(), SampleWeight = 0.6 };
        Assert.Equal(0.6, example.SampleWeight);
    }

    // ============================================================
    // ComputeTemporalWeight Tests
    // ============================================================

    [Fact]
    public void ComputeTemporalWeight_BrandNew_ReturnsOne()
    {
        var now = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            GeneratedAtUtc = now
        };

        var weight = example.ComputeTemporalWeight(now);
        Assert.Equal(1.0, weight, 10);
    }

    [Fact]
    public void ComputeTemporalWeight_FutureExample_ReturnsOne()
    {
        // Example generated in the future relative to reference → ageDays <= 0 → 1.0
        var reference = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            GeneratedAtUtc = reference.AddDays(10)
        };

        var weight = example.ComputeTemporalWeight(reference);
        Assert.Equal(1.0, weight, 10);
    }

    [Fact]
    public void ComputeTemporalWeight_AtHalfLife_ReturnsHalf()
    {
        var reference = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            GeneratedAtUtc = reference.AddDays(-90) // exactly one half-life
        };

        var weight = example.ComputeTemporalWeight(reference);
        Assert.Equal(0.5, weight, 4);
    }

    [Fact]
    public void ComputeTemporalWeight_TwoHalfLives_ReturnsQuarter()
    {
        var reference = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            GeneratedAtUtc = reference.AddDays(-180) // two half-lives
        };

        var weight = example.ComputeTemporalWeight(reference);
        Assert.Equal(0.25, weight, 4);
    }

    [Fact]
    public void ComputeTemporalWeight_VeryOld_ApproachesZero()
    {
        var reference = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            GeneratedAtUtc = reference.AddDays(-900) // 10 half-lives
        };

        var weight = example.ComputeTemporalWeight(reference);
        Assert.True(weight < 0.001, $"Very old example should have near-zero weight, got {weight}");
        Assert.True(weight > 0.0, "Weight should never be exactly zero");
    }

    [Fact]
    public void ComputeTemporalWeight_WithoutReference_UsesUtcNow()
    {
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            GeneratedAtUtc = DateTime.UtcNow.AddDays(-1)
        };

        // Should not throw and should return a reasonable value
        var weight = example.ComputeTemporalWeight();
        Assert.InRange(weight, 0.99, 1.0); // 1 day old → very close to 1.0
    }

    [Fact]
    public void ComputeTemporalWeight_DecaysMonotonically()
    {
        var reference = DateTime.UtcNow;
        var weights = new double[5];

        for (var i = 0; i < 5; i++)
        {
            var example = new TrainingExample
            {
                Features = new CandidateFeatures(),
                GeneratedAtUtc = reference.AddDays(-i * 30) // 0, 30, 60, 90, 120 days
            };
            weights[i] = example.ComputeTemporalWeight(reference);
        }

        // Each subsequent weight should be strictly less than the previous
        for (var i = 1; i < weights.Length; i++)
        {
            Assert.True(weights[i] < weights[i - 1],
                $"Weight at {i * 30} days ({weights[i]:F6}) should be less than at {(i - 1) * 30} days ({weights[i - 1]:F6})");
        }
    }

    // ============================================================
    // ComputeEffectiveWeight Tests
    // ============================================================

    [Fact]
    public void ComputeEffectiveWeight_FullWeight_BrandNew_ReturnsOne()
    {
        var now = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            SampleWeight = 1.0,
            GeneratedAtUtc = now
        };

        var effective = example.ComputeEffectiveWeight(now);
        Assert.Equal(1.0, effective, 10);
    }

    [Fact]
    public void ComputeEffectiveWeight_HalfSampleWeight_BrandNew_ReturnsHalf()
    {
        var now = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            SampleWeight = 0.5,
            GeneratedAtUtc = now
        };

        var effective = example.ComputeEffectiveWeight(now);
        Assert.Equal(0.5, effective, 10);
    }

    [Fact]
    public void ComputeEffectiveWeight_FullWeight_AtHalfLife_ReturnsHalf()
    {
        var reference = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            SampleWeight = 1.0,
            GeneratedAtUtc = reference.AddDays(-90)
        };

        var effective = example.ComputeEffectiveWeight(reference);
        Assert.Equal(0.5, effective, 4);
    }

    [Fact]
    public void ComputeEffectiveWeight_HalfSampleWeight_AtHalfLife_ReturnsQuarter()
    {
        var reference = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            SampleWeight = 0.5,
            GeneratedAtUtc = reference.AddDays(-90)
        };

        var effective = example.ComputeEffectiveWeight(reference);
        Assert.Equal(0.25, effective, 4);
    }

    [Fact]
    public void ComputeEffectiveWeight_ZeroSampleWeight_ReturnsZero()
    {
        var now = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            SampleWeight = 0.0,
            GeneratedAtUtc = now
        };

        var effective = example.ComputeEffectiveWeight(now);
        Assert.Equal(0.0, effective, 10);
    }

    [Fact]
    public void ComputeEffectiveWeight_WithoutReference_UsesUtcNow()
    {
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            SampleWeight = 0.8,
            GeneratedAtUtc = DateTime.UtcNow
        };

        // Should not throw and should return a reasonable value
        var effective = example.ComputeEffectiveWeight();
        Assert.InRange(effective, 0.79, 0.81);
    }

    [Fact]
    public void ComputeEffectiveWeight_IsSampleWeightTimesTemporalWeight()
    {
        var reference = DateTime.UtcNow;
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            SampleWeight = 0.7,
            GeneratedAtUtc = reference.AddDays(-45)
        };

        var temporal = example.ComputeTemporalWeight(reference);
        var effective = example.ComputeEffectiveWeight(reference);

        Assert.Equal(0.7 * temporal, effective, 10);
    }

    // ============================================================
    // GeneratedAtUtc Tests
    // ============================================================

    [Fact]
    public void GeneratedAtUtc_DefaultsToApproximatelyNow()
    {
        var before = DateTime.UtcNow;
        var example = new TrainingExample { Features = new CandidateFeatures() };
        var after = DateTime.UtcNow;

        Assert.InRange(example.GeneratedAtUtc, before, after.AddSeconds(1));
    }

    [Fact]
    public void GeneratedAtUtc_CanBeSetExplicitly()
    {
        var timestamp = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var example = new TrainingExample
        {
            Features = new CandidateFeatures(),
            GeneratedAtUtc = timestamp
        };

        Assert.Equal(timestamp, example.GeneratedAtUtc);
    }
}