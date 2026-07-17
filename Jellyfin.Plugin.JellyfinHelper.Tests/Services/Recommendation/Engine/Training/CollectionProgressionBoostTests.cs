using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>
///     Tests for <see cref="TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts"/>.
///     <para>
///         The legacy <c>ComputeCollectionProgressionBoostFromCache</c> was
///         removed and the surviving method has been promoted to <c>internal</c> so these tests
///         can call it directly (no more reflection). They lock in the diminishing-returns
///         scale <c>0.3 + (n-1) × 0.2, clamped [0,1]</c> that is shared with the inference-time
///         <c>Engine.ComputeCollectionProgressionBoostLive</c>, protecting the ML feature's
///         train/serve parity should either copy drift in the future.
///     </para>
/// </summary>
public sealed class CollectionProgressionBoostTests
{
    // ============================================================
    // Empty / zero-input guards
    // ============================================================

    [Fact]
    public void WithCounts_NullBoxSetIds_ReturnsZero()
    {
        var counts = new Dictionary<Guid, int> { { Guid.NewGuid(), 5 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            boxSetIds: null,
            watchedBoxSetCounts: counts);

        Assert.Equal(0.0, result, 10);
    }

    [Fact]
    public void WithCounts_EmptyBoxSetIds_ReturnsZero()
    {
        var counts = new Dictionary<Guid, int> { { Guid.NewGuid(), 5 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            boxSetIds: Array.Empty<Guid>(),
            watchedBoxSetCounts: counts);

        Assert.Equal(0.0, result, 10);
    }

    [Fact]
    public void WithCounts_EmptyCountsMap_ReturnsZero()
    {
        // Candidate belongs to BoxSets but the user has not watched any siblings yet.
        var boxSetIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var counts = new Dictionary<Guid, int>();

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            boxSetIds,
            counts);

        Assert.Equal(0.0, result, 10);
    }

    [Fact]
    public void WithCounts_BoxSetsAbsentFromCounts_ReturnsZero()
    {
        // Candidate BoxSet IDs do not appear in the counts map (user watched siblings from
        // other collections). No progression signal.
        var candidateBoxSet = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { Guid.NewGuid(), 3 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { candidateBoxSet },
            counts);

        Assert.Equal(0.0, result, 10);
    }

    // ============================================================
    // Diminishing-returns scale contract (must stay in lockstep with
    // Engine.ComputeCollectionProgressionBoostLive to preserve train/serve parity)
    // ============================================================

    [Fact]
    public void WithCounts_OneWatchedSibling_ReturnsBaseBoost()
    {
        // 1 watched sibling → 0.3 + (1-1) × 0.2 = 0.3
        var boxSetId = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { boxSetId, 1 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { boxSetId },
            counts);

        Assert.Equal(0.3, result, 10);
    }

    [Fact]
    public void WithCounts_TwoWatchedSiblings_ReturnsHalf()
    {
        // 2 watched siblings → 0.3 + (2-1) × 0.2 = 0.5
        var boxSetId = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { boxSetId, 2 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { boxSetId },
            counts);

        Assert.Equal(0.5, result, 10);
    }

    [Fact]
    public void WithCounts_ThreeWatchedSiblings_ReturnsSevenTenths()
    {
        // 3 watched siblings → 0.3 + (3-1) × 0.2 = 0.7
        var boxSetId = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { boxSetId, 3 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { boxSetId },
            counts);

        Assert.Equal(0.7, result, 10);
    }

    [Fact]
    public void WithCounts_FourWatchedSiblings_ReturnsNineTenths()
    {
        // 4 watched siblings → 0.3 + (4-1) × 0.2 = 0.9
        var boxSetId = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { boxSetId, 4 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { boxSetId },
            counts);

        Assert.Equal(0.9, result, 10);
    }

    [Fact]
    public void WithCounts_FiveWatchedSiblings_ClampsToOne()
    {
        // 5 watched siblings → 0.3 + (5-1) × 0.2 = 1.1, must clamp to 1.0.
        var boxSetId = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { boxSetId, 5 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { boxSetId },
            counts);

        Assert.Equal(1.0, result, 10);
    }

    [Fact]
    public void WithCounts_LargeWatchedCount_StillClampsToOne()
    {
        // Pathological case: user has watched 500 siblings of a mega-collection.
        // The clamp must keep the output well-defined and bounded.
        var boxSetId = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { boxSetId, 500 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { boxSetId },
            counts);

        Assert.Equal(1.0, result, 10);
    }

    // ============================================================
    // Multi-BoxSet candidate selection: the method must pick the
    // BEST (highest progression) BoxSet a candidate belongs to
    // ============================================================

    [Fact]
    public void WithCounts_MultipleBoxSets_UsesHighestProgression()
    {
        var lowProgressionBoxSet = Guid.NewGuid();  // 1 watched sibling → 0.3
        var highProgressionBoxSet = Guid.NewGuid(); // 4 watched siblings → 0.9
        var counts = new Dictionary<Guid, int>
        {
            { lowProgressionBoxSet, 1 },
            { highProgressionBoxSet, 4 }
        };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { lowProgressionBoxSet, highProgressionBoxSet },
            counts);

        // Must return 0.9 (the best of the two), not the first hit or the average.
        Assert.Equal(0.9, result, 10);
    }

    [Fact]
    public void WithCounts_MultipleBoxSets_OrderIndependent()
    {
        // Regression: verify the pick-the-best behaviour does not depend on iteration order.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var counts = new Dictionary<Guid, int>
        {
            { a, 1 },
            { b, 4 }
        };

        var forward = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(new[] { a, b }, counts);
        var reversed = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(new[] { b, a }, counts);

        Assert.Equal(forward, reversed, 10);
        Assert.Equal(0.9, forward, 10);
    }

    [Fact]
    public void WithCounts_MultipleBoxSets_OnlyOneWatched_UsesThatOne()
    {
        // Only one of the candidate's BoxSets has any watched siblings.
        // The un-watched BoxSet (missing from counts) must not contribute; the
        // method must fall back to the watched one and NOT return 0.
        var watched = Guid.NewGuid();
        var unwatched = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { watched, 3 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { unwatched, watched },
            counts);

        Assert.Equal(0.7, result, 10);
    }

    [Fact]
    public void WithCounts_ZeroWatchedCountInMap_IsTreatedAsAbsent()
    {
        // Defensive edge case: a BoxSet ID is present in the counts map but its
        // count is zero (should not happen in practice, but the method must not
        // emit a 0.3 boost for it — the "n > 0" guard in the code must hold).
        var boxSetId = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { boxSetId, 0 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { boxSetId },
            counts);

        Assert.Equal(0.0, result, 10);
    }

    [Fact]
    public void WithCounts_NegativeWatchedCountInMap_IsTreatedAsAbsent()
    {
        // Extra-defensive: a negative count (corruption) must not accidentally
        // slip through and produce a nonsense boost.
        var boxSetId = Guid.NewGuid();
        var counts = new Dictionary<Guid, int> { { boxSetId, -5 } };

        var result = TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts(
            new[] { boxSetId },
            counts);

        Assert.Equal(0.0, result, 10);
    }
}
