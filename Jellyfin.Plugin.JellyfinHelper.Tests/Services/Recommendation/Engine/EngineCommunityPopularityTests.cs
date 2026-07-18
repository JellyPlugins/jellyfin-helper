using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <c>Engine.BuildCommunityPopularityMap</c> — the shared cold-start
///     community-popularity computation used by both the batch path and the live
///     cold-start path.
///     <para>
///         This helper is the single source of truth for the "two-user gate" that
///         prevents a single-user deployment from turning its own watch history
///         into "the community" (which would degenerate cold-start into a
///         self-fulfilling prophecy). Historically these two loops were duplicated
///         inline and drifted at least once during refactoring, so pinning the
///         behaviour with a golden set of test cases is a proper bug-guard against
///         re-duplication.
///     </para>
///     <para>
///         The helper is <c>private static</c> so we reach it via reflection — the
///         test project already has <c>InternalsVisibleTo</c> but private members
///         still need reflection. The alternative (making it internal) would leak
///         an implementation detail; keeping the surface tight and testing via
///         reflection is the right trade-off here.
///     </para>
/// </summary>
public sealed class EngineCommunityPopularityTests
{
    // ================================================================================================
    // Two-user gate
    // ================================================================================================

    [Fact]
    public void BuildCommunityPopularityMap_EmptyDictionary_ReturnsNull()
    {
        // No users at all: obviously no community signal to compute.
        var input = new Dictionary<Guid, HashSet<Guid>>();
        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_SingleUserWithNoHistory_ReturnsNull()
    {
        // One user in the map but they have no watch data — no community signal.
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = []
        };
        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_SingleUserWithHistory_ReturnsNull()
    {
        // BUG GUARD: one user with real watch data must NOT be treated as "the community".
        // The two-user gate is the whole point — if we return a map here, cold-start
        // recommendations degenerate into "the only user's own preferences" and defeat
        // the "wisdom of the crowd" premise.
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]
        };
        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_TwoUsers_OneEmpty_ReturnsNull()
    {
        // The gate requires TWO users with actual watch history — one active + one empty
        // still counts as "essentially a single-user deployment" from a community-signal POV.
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [Guid.NewGuid(), Guid.NewGuid()],
            [Guid.NewGuid()] = [] // empty profile
        };
        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_TwoUsersWithHistory_ReturnsMap()
    {
        // The minimum viable case: two active users unlock the community signal.
        var item = Guid.NewGuid();
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [item],
            [Guid.NewGuid()] = [item]
        };
        var result = Invoke(input);

        Assert.NotNull(result);
        Assert.Equal(2, result![item]);
    }

    // ================================================================================================
    // Counting semantics
    // ================================================================================================

    [Fact]
    public void BuildCommunityPopularityMap_ItemCountsReflectPerUserWatches()
    {
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var itemC = Guid.NewGuid();

        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [itemA, itemB],       // watched A + B
            [Guid.NewGuid()] = [itemA, itemC],       // watched A + C
            [Guid.NewGuid()] = [itemA]               // watched A only
        };

        var result = Invoke(input);

        Assert.NotNull(result);
        Assert.Equal(3, result![itemA]);   // seen by 3 users
        Assert.Equal(1, result[itemB]);    // seen by 1 user
        Assert.Equal(1, result[itemC]);    // seen by 1 user
    }

    [Fact]
    public void BuildCommunityPopularityMap_UsersWithDisjointHistories_YieldsSingleCounts()
    {
        // Users with entirely disjoint watch lists → every item has count 1.
        // BUG GUARD: if the counting logic ever gets an off-by-one that doubles counts
        // across users, this test detects it via the strict equality assertion.
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [Guid.NewGuid(), Guid.NewGuid()],
            [Guid.NewGuid()] = [Guid.NewGuid(), Guid.NewGuid()]
        };

        var result = Invoke(input);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Count); // 4 distinct items
        Assert.All(result.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void BuildCommunityPopularityMap_ThreeEmptyUsers_ReturnsNull()
    {
        // The gate iterates until it finds >= 2 users with non-empty sets.
        // 3 empty users must not accidentally unlock the map because .Count == 3 > 1.
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [],
            [Guid.NewGuid()] = [],
            [Guid.NewGuid()] = []
        };

        var result = Invoke(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildCommunityPopularityMap_ExactlyTwoActiveUsersAmongMany_UnlocksMap()
    {
        // Mixed deployment: many users but only two with actual history. The gate must
        // count users with history rather than dictionary size.
        var item = Guid.NewGuid();
        var input = new Dictionary<Guid, HashSet<Guid>>
        {
            [Guid.NewGuid()] = [],
            [Guid.NewGuid()] = [],
            [Guid.NewGuid()] = [item],
            [Guid.NewGuid()] = [item],
            [Guid.NewGuid()] = []
        };

        var result = Invoke(input);
        Assert.NotNull(result);
        Assert.Equal(2, result![item]);
    }

    [Fact]
    public void BuildCommunityPopularityMap_SameItemAcrossManyUsers_CountsAccurately()
    {
        // 10 users all watched the same 3 items. Every item must show count 10.
        // Verifies the outer/inner-loop composition doesn't accidentally short-circuit.
        var items = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var input = new Dictionary<Guid, HashSet<Guid>>();
        for (var i = 0; i < 10; i++)
        {
            input[Guid.NewGuid()] = new HashSet<Guid>(items);
        }

        var result = Invoke(input);

        Assert.NotNull(result);
        foreach (var item in items)
        {
            Assert.Equal(10, result![item]);
        }
    }

    // ================================================================================================
    // Reflection glue
    // ================================================================================================

    private static Dictionary<Guid, int>? Invoke(IReadOnlyDictionary<Guid, HashSet<Guid>> userSets)
    {
        var method = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine)
            .GetMethod(
                "BuildCommunityPopularityMap",
                BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (Dictionary<Guid, int>?)method!.Invoke(null, [userSets]);
    }
}