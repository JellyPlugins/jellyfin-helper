using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Exercises the internal Z-score helpers of <see cref="LearnedScoringStrategy"/>:
///     <see cref="LearnedScoringStrategy.ComputeFeatureStatistics"/> (mean / Bessel-corrected
///     std-dev, empty-input guard, malformed-vector guard) and
///     <see cref="LearnedScoringStrategy.StandardizeSingleVector"/> (near-zero-std-dev passthrough).
/// </summary>
public sealed class LearnedScoringStrategyStandardizationTests
{
    [Fact]
    public void ComputeFeatureStatistics_EmptyVectorArray_ReturnsZeroFilledStatsWithoutDivideByZero()
    {
        // n == 0 must short-circuit before the mean divide-by-n, otherwise every feature
        // would be NaN. Contract: full-length, all-zero stats and no exception.
        var (means, stdDevs) = LearnedScoringStrategy.ComputeFeatureStatistics([]);

        Assert.Equal(CandidateFeatures.FeatureCount, means.Length);
        Assert.Equal(CandidateFeatures.FeatureCount, stdDevs.Length);
        Assert.All(means, m => Assert.Equal(0.0, m));
        Assert.All(stdDevs, s => Assert.Equal(0.0, s));
    }

    [Fact]
    public void ComputeFeatureStatistics_VectorShorterThanFeatureCount_ThrowsArgumentException()
    {
        // A row shorter than FeatureCount would read out of bounds; the guard must reject it
        // and identify the offending index/lengths so the caller can diagnose the corruption.
        var good = new double[CandidateFeatures.FeatureCount];
        var shortRow = new double[1];

        var ex = Assert.Throws<ArgumentException>(
            () => LearnedScoringStrategy.ComputeFeatureStatistics([good, shortRow]));

        Assert.Equal("vectors", ex.ParamName);
        Assert.Contains("index 1", ex.Message);
        Assert.Contains("length 1", ex.Message);
    }

    [Fact]
    public void ComputeFeatureStatistics_KnownValues_ComputesMeanAndBesselCorrectedStdDev()
    {
        // Two vectors differing only in GenreSimilarity (0.0 and 1.0). Mean = 0.5 and the
        // sample std-dev uses the n-1 denominator: sqrt(((0-.5)^2 + (1-.5)^2)/1) = sqrt(0.5).
        // A population (n) denominator would instead give 0.5, so this pins the Bessel path.
        var genreIdx = (int)FeatureIndex.GenreSimilarity;
        var a = new double[CandidateFeatures.FeatureCount];
        var b = new double[CandidateFeatures.FeatureCount];
        a[genreIdx] = 0.0;
        b[genreIdx] = 1.0;

        var (means, stdDevs) = LearnedScoringStrategy.ComputeFeatureStatistics([a, b]);

        Assert.Equal(0.5, means[genreIdx], 12);
        Assert.Equal(Math.Sqrt(0.5), stdDevs[genreIdx], 12);
    }

    [Fact]
    public void StandardizeSingleVector_ZeroStdDevFeature_LeavesValueUnchanged()
    {
        // Features whose std-dev is <= 1e-8 must pass through untouched (dividing would yield
        // NaN/huge values); features with a real std-dev must be transformed to (x-mean)/stddev.
        var genreIdx = (int)FeatureIndex.GenreSimilarity;
        var collabIdx = (int)FeatureIndex.CollaborativeScore;

        var means = new double[CandidateFeatures.FeatureCount];
        var stdDevs = new double[CandidateFeatures.FeatureCount];
        means[genreIdx] = 0.5;
        stdDevs[genreIdx] = 0.0;   // near-zero -> passthrough
        means[collabIdx] = 0.2;
        stdDevs[collabIdx] = 0.5;  // real std-dev -> transform

        var vector = new double[CandidateFeatures.FeatureCount];
        vector[genreIdx] = 0.9;
        vector[collabIdx] = 0.7;

        LearnedScoringStrategy.StandardizeSingleVector(vector, means, stdDevs);

        Assert.Equal(0.9, vector[genreIdx], 12);
        Assert.Equal((0.7 - 0.2) / 0.5, vector[collabIdx], 12);
    }
}
