using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="ContentScoring"/> static helper methods
///     and <see cref="TrainingDataBuilder.ComputeCollectionProgressionBoostFromCache"/>.
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

    // ============================================================
    // ComputeCollectionProgressionBoostFromCache Tests
    // (via reflection since it's private static — tested through internal access)
    // ============================================================

    [Fact]
    public void CollectionProgressionBoost_EmptyBoxSetIds_ReturnsZero()
    {
        var boxSetIds = new List<Guid>();
        var watchedIds = new HashSet<Guid> { Guid.NewGuid() };

        var result = InvokeComputeCollectionProgressionBoostFromCache(boxSetIds, watchedIds);
        Assert.Equal(0.0, result, 10);
    }

    [Fact]
    public void CollectionProgressionBoost_BoxSetIdInWatchedIds_ReturnsHalf()
    {
        var boxSetId = Guid.NewGuid();
        var boxSetIds = new List<Guid> { boxSetId };
        var watchedIds = new HashSet<Guid> { boxSetId }; // User watched the BoxSet itself

        var result = InvokeComputeCollectionProgressionBoostFromCache(boxSetIds, watchedIds);
        Assert.Equal(0.5, result, 10);
    }

    [Fact]
    public void CollectionProgressionBoost_BoxSetIdNotInWatchedIds_ReturnsBaseBoost()
    {
        var boxSetId = Guid.NewGuid();
        var boxSetIds = new List<Guid> { boxSetId };
        var watchedIds = new HashSet<Guid> { Guid.NewGuid() }; // Different item

        var result = InvokeComputeCollectionProgressionBoostFromCache(boxSetIds, watchedIds);
        Assert.Equal(0.3, result, 10);
    }

    [Fact]
    public void CollectionProgressionBoost_MultipleBoxSets_FirstMatchWins()
    {
        var boxSet1 = Guid.NewGuid();
        var boxSet2 = Guid.NewGuid();
        var boxSetIds = new List<Guid> { boxSet1, boxSet2 };
        var watchedIds = new HashSet<Guid> { boxSet2 }; // Second BoxSet is watched

        var result = InvokeComputeCollectionProgressionBoostFromCache(boxSetIds, watchedIds);
        Assert.Equal(0.5, result, 10);
    }

    [Fact]
    public void CollectionProgressionBoost_MultipleBoxSets_NoneWatched_ReturnsBaseBoost()
    {
        var boxSetIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var watchedIds = new HashSet<Guid> { Guid.NewGuid() };

        var result = InvokeComputeCollectionProgressionBoostFromCache(boxSetIds, watchedIds);
        Assert.Equal(0.3, result, 10);
    }

    [Fact]
    public void CollectionProgressionBoost_EmptyWatchedIds_ReturnsBaseBoost()
    {
        var boxSetIds = new List<Guid> { Guid.NewGuid() };
        var watchedIds = new HashSet<Guid>();

        var result = InvokeComputeCollectionProgressionBoostFromCache(boxSetIds, watchedIds);
        Assert.Equal(0.3, result, 10);
    }

    /// <summary>
    ///     Invokes the private static method via reflection for testing.
    ///     The method is private to TrainingDataBuilder but accessible via InternalsVisibleTo + reflection.
    /// </summary>
    private static double InvokeComputeCollectionProgressionBoostFromCache(
        IReadOnlyList<Guid> boxSetIds,
        HashSet<Guid> watchedIds)
    {
        var method = typeof(TrainingDataBuilder).GetMethod(
            "ComputeCollectionProgressionBoostFromCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [boxSetIds, watchedIds]);
        Assert.NotNull(result);

        return (double)result!;
    }
}