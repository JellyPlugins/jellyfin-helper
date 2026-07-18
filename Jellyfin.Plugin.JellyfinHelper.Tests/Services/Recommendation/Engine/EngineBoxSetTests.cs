using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the BoxSet-related pure-static helpers on <see cref="Engine"/>:
///     <c>BuildWatchedBoxSetCounts</c> and <c>ComputeCollectionProgressionBoostLive</c>.
///     <para>
///         These helpers implement the "you already watched half of this trilogy" boost signal
///         that surfaces the next installment of a collection to the user. The formula is shared
///         with the training-time <c>ComputeCollectionProgressionBoostWithCounts</c> in
///         <c>TrainingDataBuilder</c>, but both call into the same <see cref="EngineConstants.ComputeCollectionProgressionBoost"/>
///         helper — so the golden vectors below (which pin the formula shape at inference time)
///         also protect train/serve parity by construction.
///     </para>
/// </summary>
public sealed class EngineBoxSetTests
{
    // ============================================================================
    // BuildWatchedBoxSetCounts — inverts (item → boxSets) into (boxSet → watchedCount).
    // ============================================================================

    [Fact]
    public void BuildWatchedBoxSetCounts_EmptyWatched_ReturnsEmpty()
    {
        var lookup = new Dictionary<Guid, List<Guid>>
        {
            [Guid.NewGuid()] = [Guid.NewGuid()]
        };
        var result = InvokeBuildWatchedBoxSetCounts([], lookup);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildWatchedBoxSetCounts_EmptyLookup_ReturnsEmpty()
    {
        var watched = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var result = InvokeBuildWatchedBoxSetCounts(watched, []);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildWatchedBoxSetCounts_WatchedItemNotInLookup_ContributesNothing()
    {
        // Watched items outside any BoxSet must not create phantom entries.
        var boxSet = Guid.NewGuid();
        var watchedItem = Guid.NewGuid();
        var otherItem = Guid.NewGuid();
        var lookup = new Dictionary<Guid, List<Guid>>
        {
            [otherItem] = [boxSet] // only OTHER item is in the box set
        };
        var result = InvokeBuildWatchedBoxSetCounts([watchedItem], lookup);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildWatchedBoxSetCounts_SingleWatched_ContributesOne()
    {
        var boxSet = Guid.NewGuid();
        var item = Guid.NewGuid();
        var lookup = new Dictionary<Guid, List<Guid>>
        {
            [item] = [boxSet]
        };
        var result = InvokeBuildWatchedBoxSetCounts([item], lookup);
        Assert.Single(result);
        Assert.Equal(1, result[boxSet]);
    }

    [Fact]
    public void BuildWatchedBoxSetCounts_MultipleWatchedInSameBoxSet_Accumulates()
    {
        var boxSet = Guid.NewGuid();
        var item1 = Guid.NewGuid();
        var item2 = Guid.NewGuid();
        var item3 = Guid.NewGuid();
        var lookup = new Dictionary<Guid, List<Guid>>
        {
            [item1] = [boxSet],
            [item2] = [boxSet],
            [item3] = [boxSet]
        };
        var result = InvokeBuildWatchedBoxSetCounts([item1, item2, item3], lookup);
        Assert.Single(result);
        Assert.Equal(3, result[boxSet]);
    }

    [Fact]
    public void BuildWatchedBoxSetCounts_ItemInMultipleBoxSets_ContributesToEach()
    {
        // A movie can belong to more than one BoxSet (e.g. "MCU" AND "Avengers Saga").
        // Both BoxSets must get the increment.
        var mcu = Guid.NewGuid();
        var avengersSaga = Guid.NewGuid();
        var infinityWar = Guid.NewGuid();
        var lookup = new Dictionary<Guid, List<Guid>>
        {
            [infinityWar] = [mcu, avengersSaga]
        };
        var result = InvokeBuildWatchedBoxSetCounts([infinityWar], lookup);
        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[mcu]);
        Assert.Equal(1, result[avengersSaga]);
    }

    [Fact]
    public void BuildWatchedBoxSetCounts_MixedScenario_CountsCorrectly()
    {
        // Realistic: user watched 3 items across 2 BoxSets, one shared.
        var trilogyA = Guid.NewGuid();
        var trilogyB = Guid.NewGuid();
        var itemA1 = Guid.NewGuid();
        var itemA2 = Guid.NewGuid();
        var itemBoth = Guid.NewGuid(); // belongs to both

        var lookup = new Dictionary<Guid, List<Guid>>
        {
            [itemA1] = [trilogyA],
            [itemA2] = [trilogyA],
            [itemBoth] = [trilogyA, trilogyB]
        };

        var result = InvokeBuildWatchedBoxSetCounts([itemA1, itemA2, itemBoth], lookup);
        Assert.Equal(2, result.Count);
        Assert.Equal(3, result[trilogyA]);
        Assert.Equal(1, result[trilogyB]);
    }

    // ============================================================================
    // ComputeCollectionProgressionBoostLive — reads the counts and produces a boost.
    // Delegates the FORMULA to EngineConstants.ComputeCollectionProgressionBoost.
    // ============================================================================

    [Fact]
    public void ComputeCollectionProgressionBoostLive_EmptyWatchedBoxSetCounts_ReturnsZero()
    {
        // No watched history → no boost signal (this is different from "0 boost from the formula"
        // and lives in the wrapper's short-circuit).
        var boost = InvokeComputeCollectionProgressionBoostLive(
            [Guid.NewGuid()],
            []);
        Assert.Equal(0.0, boost);
    }

    [Fact]
    public void ComputeCollectionProgressionBoostLive_EmptyCandidateBoxSets_ReturnsZero()
    {
        // The candidate isn't in ANY BoxSet — no signal applies.
        var boost = InvokeComputeCollectionProgressionBoostLive(
            [],
            new Dictionary<Guid, int> { [Guid.NewGuid()] = 5 });
        Assert.Equal(0.0, boost);
    }

    [Fact]
    public void ComputeCollectionProgressionBoostLive_CandidateBoxSetNotInCounts_ReturnsZero()
    {
        // Candidate is in a BoxSet the user has never touched.
        var candidateBox = Guid.NewGuid();
        var otherBox = Guid.NewGuid();
        var boost = InvokeComputeCollectionProgressionBoostLive(
            [candidateBox],
            new Dictionary<Guid, int> { [otherBox] = 5 });
        Assert.Equal(0.0, boost);
    }

    [Fact]
    public void ComputeCollectionProgressionBoostLive_SingleWatched_MatchesFormula()
    {
        // The wrapper must forward to EngineConstants.ComputeCollectionProgressionBoost for
        // count=1. Any inline duplication of the formula (that would break train/serve parity)
        // is caught here by comparing the wrapper output against the direct formula call.
        var boxSet = Guid.NewGuid();
        var expected = EngineConstants.ComputeCollectionProgressionBoost(1);

        var boost = InvokeComputeCollectionProgressionBoostLive(
            [boxSet],
            new Dictionary<Guid, int> { [boxSet] = 1 });
        Assert.Equal(expected, boost);
    }

    [Fact]
    public void ComputeCollectionProgressionBoostLive_MonotonicWithCount_UpToClamp()
    {
        // As watched-count grows, the boost must weakly increase — the underlying formula
        // is `0.3 + (n-1) × 0.2, clamped [0,1]`, so growth stops at some finite n.
        var boxSet = Guid.NewGuid();
        var boost1 = InvokeComputeCollectionProgressionBoostLive(
            [boxSet], new Dictionary<Guid, int> { [boxSet] = 1 });
        var boost3 = InvokeComputeCollectionProgressionBoostLive(
            [boxSet], new Dictionary<Guid, int> { [boxSet] = 3 });
        var boost10 = InvokeComputeCollectionProgressionBoostLive(
            [boxSet], new Dictionary<Guid, int> { [boxSet] = 10 });

        Assert.True(boost3 >= boost1);
        Assert.True(boost10 >= boost3);
        Assert.InRange(boost10, 0.0, 1.0); // must remain clamped even at large counts
    }

    [Fact]
    public void ComputeCollectionProgressionBoostLive_MultipleBoxSets_PicksHighestBoost()
    {
        // Candidate lives in two BoxSets — user has watched 1 item in one and 5 in the other.
        // The wrapper must return the HIGHER of the two individual boosts (best-progression rule).
        var lowBox = Guid.NewGuid();
        var highBox = Guid.NewGuid();
        var expectedHigh = EngineConstants.ComputeCollectionProgressionBoost(5);

        var boost = InvokeComputeCollectionProgressionBoostLive(
            [lowBox, highBox],
            new Dictionary<Guid, int>
            {
                [lowBox] = 1,
                [highBox] = 5
            });

        Assert.Equal(expectedHigh, boost);
    }

    [Fact]
    public void ComputeCollectionProgressionBoostLive_ClampedAtOne_ForVeryHighCounts()
    {
        // BUG GUARD: the underlying formula clamps to 1.0. If a maintainer removed the clamp
        // in a refactor, a user with 100 watched items in a mega-BoxSet would produce a
        // boost > 1.0 and dominate every other feature — the ensemble would then always
        // recommend the same tail-end of the collection. The clamp is what keeps the boost
        // a "signal", not a "verdict".
        var boxSet = Guid.NewGuid();
        var boost = InvokeComputeCollectionProgressionBoostLive(
            [boxSet],
            new Dictionary<Guid, int> { [boxSet] = 100 });
        Assert.InRange(boost, 0.0, 1.0);
    }

    // ============================================================================
    // Reflection glue
    // ============================================================================

    private static Dictionary<Guid, int> InvokeBuildWatchedBoxSetCounts(
        HashSet<Guid> watchedIds,
        Dictionary<Guid, List<Guid>> candidateBoxSetLookup)
    {
        var method = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine)
            .GetMethod(
                "BuildWatchedBoxSetCounts",
                BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Dictionary<Guid, int>)method!.Invoke(null, [watchedIds, candidateBoxSetLookup])!;
    }

    private static double InvokeComputeCollectionProgressionBoostLive(
        List<Guid> candidateBoxSetIds,
        Dictionary<Guid, int> watchedBoxSetCounts)
    {
        var method = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine)
            .GetMethod(
                "ComputeCollectionProgressionBoostLive",
                BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (double)method!.Invoke(null, [candidateBoxSetIds, watchedBoxSetCounts])!;
    }
}
