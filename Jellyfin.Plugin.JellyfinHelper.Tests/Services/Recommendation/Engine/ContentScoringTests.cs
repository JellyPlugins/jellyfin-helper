using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="ContentScoring"/> static helper methods.
///     <para>
///         Roadmap v3 (C3): the previously-tested
///         <c>TrainingDataBuilder.ComputeCollectionProgressionBoostFromCache</c> legacy method
///         was removed because it was dead code — only reflection-based tests referenced it and
///         its 0.0/0.3/0.5 flat heuristic had already been superseded by the diminishing-returns
///         <c>ComputeCollectionProgressionBoostWithCounts</c> used in both Phase 1 and Phase 3
///         of <c>TrainingDataBuilder</c>. The remaining formula is covered end-to-end via the
///         Phase 1 / Phase 3 training paths, which exercise the same math with real BoxSet inputs.
///     </para>
/// </summary>
public sealed class ContentScoringTests
{
    // ============================================================
    // ComputePopularityScore Tests
    // ============================================================

    [Fact]
    public void ComputePopularityScore_CollaborativePositive_ScalesBy08()
    {
        var result = ContentScoring.ComputePopularityScore(0.5, 0.8);
        Assert.Equal(0.4, result, 10); // 0.5 * 0.8 = 0.4
    }

    [Fact]
    public void ComputePopularityScore_CollaborativeZero_UsesCriticFallback()
    {
        var result = ContentScoring.ComputePopularityScore(0.0, 0.8);
        Assert.Equal(0.24, result, 10); // 0.8 * 0.3 = 0.24
    }

    [Fact]
    public void ComputePopularityScore_CollaborativeHigh_ClampsToOne()
    {
        // collaborativeScore * 0.8 > 1.0 when collaborativeScore > 1.25
        // Since collaborative scores are normalized to [0,1], this shouldn't happen in practice,
        // but the Clamp guarantees contract compliance.
        var result = ContentScoring.ComputePopularityScore(1.5, 0.0);
        Assert.Equal(1.0, result, 10); // Clamped
    }

    [Fact]
    public void ComputePopularityScore_FallbackPath_ClampsToOne()
    {
        // Edge case: combinedCriticScore * 0.3 should be clamped
        // Even though combinedCriticScore is normally [0,1], defensive clamping is verified.
        var result = ContentScoring.ComputePopularityScore(0.0, 4.0);
        Assert.Equal(1.0, result, 10); // 4.0 * 0.3 = 1.2, clamped to 1.0
    }

    [Fact]
    public void ComputePopularityScore_FallbackPath_ClampsToZero()
    {
        // Negative input (defensive)
        var result = ContentScoring.ComputePopularityScore(0.0, -1.0);
        Assert.Equal(0.0, result, 10); // -1.0 * 0.3 = -0.3, clamped to 0.0
    }

    [Fact]
    public void ComputePopularityScore_BothZero_ReturnsZero()
    {
        var result = ContentScoring.ComputePopularityScore(0.0, 0.0);
        Assert.Equal(0.0, result, 10);
    }

    [Fact]
    public void ComputePopularityScore_CollaborativeNegative_UsesFallback()
    {
        // Negative collaborative score → falls through to critic fallback (since !(neg > 0))
        var result = ContentScoring.ComputePopularityScore(-0.1, 0.5);
        Assert.Equal(0.15, result, 10); // 0.5 * 0.3 = 0.15
    }

    [Fact]
    public void ComputePopularityScore_ResultAlwaysInZeroOneRange()
    {
        // Exhaustive boundary check
        var testCases = new (double collab, double critic)[]
        {
            (0.0, 0.0), (1.0, 1.0), (0.0, 1.0), (1.0, 0.0),
            (0.5, 0.5), (0.01, 0.99), (0.99, 0.01)
        };

        foreach (var (collab, critic) in testCases)
        {
            var result = ContentScoring.ComputePopularityScore(collab, critic);
            Assert.InRange(result, 0.0, 1.0);
        }
    }
}
