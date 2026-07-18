using System;
using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the pure-static internal helper methods on <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine"/>.
///     These helpers cannot be exercised end-to-end without spinning up the full recommendation
///     pipeline (which requires a live Jellyfin <c>ILibraryManager</c> + a valid plugin instance),
///     so we hit them directly through their <c>internal</c> surface via <c>InternalsVisibleTo</c>.
///     <para>
///         The methods under test are deterministic, side-effect free, and encode contracts that
///         the rest of the engine relies on (exploration seed stability, cohort seeding, etc.).
///         Regressions here silently corrupt the daily-seed contract that keeps user-facing
///         recommendations stable across process restarts.
///     </para>
/// </summary>
public sealed class EngineHelperTests
{
    // ================================================================================================
    // ComputeStableSeed — deterministic, process-independent seed for the exploration RNG.
    // The whole point of this helper is that a Jellyfin restart within the same (userId, day) tuple
    // must produce IDENTICAL seeds; System.HashCode.Combine is randomised per-process and would
    // reshuffle exploration outcomes on every restart, which is exactly the bug this helper prevents.
    // ================================================================================================

    [Fact]
    public void ComputeStableSeed_SameInputs_ReturnsIdenticalSeed()
    {
        // BUG GUARD: the contract is deterministic in-process. If any refactor accidentally reintroduces
        // System.HashCode.Combine (which is per-process randomised) this test still passes in-process,
        // so it is complemented by ComputeStableSeed_KnownInputVector_MatchesGoldenValue below which
        // pins the exact hash algorithm to a fixed byte sequence.
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var a = InvokeComputeStableSeed(id, 42);
        var b = InvokeComputeStableSeed(id, 42);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeStableSeed_DifferentSuffix_ProducesDifferentSeed()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var a = InvokeComputeStableSeed(id, 1);
        var b = InvokeComputeStableSeed(id, 2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeStableSeed_DifferentGuid_SameSuffix_ProducesDifferentSeed()
    {
        // Cohort exploration relies on per-user seed divergence for the same (batch, day).
        // Without this a "batch" would deliver the same exploration slot to every user.
        var g1 = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var g2 = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var a = InvokeComputeStableSeed(g1, 42);
        var b = InvokeComputeStableSeed(g2, 42);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeStableSeed_EmptyGuid_ProducesDeterministicSeed()
    {
        // Guid.Empty is a legitimate value (all-zero user id) — the helper must not throw
        // and must still produce a stable seed.
        var a = InvokeComputeStableSeed(Guid.Empty, 0);
        var b = InvokeComputeStableSeed(Guid.Empty, 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeStableSeed_NegativeSuffix_ProducesDeterministicSeed()
    {
        // Suffix comes from int operators (e.g. hash folding) and can be negative — must not throw.
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var a = InvokeComputeStableSeed(id, -1);
        var b = InvokeComputeStableSeed(id, -1);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeStableSeed_MaxAndMinSuffix_DoNotThrow_AndAreDistinct()
    {
        // Verifies the `unchecked` block truly wraps as intended — this used to be a subtle bug
        // when the multiplier crossed int.MaxValue in a `checked` context.
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var minSeed = InvokeComputeStableSeed(id, int.MinValue);
        var maxSeed = InvokeComputeStableSeed(id, int.MaxValue);
        Assert.NotEqual(minSeed, maxSeed);
    }

    [Fact]
    public void ComputeStableSeed_KnownInputVector_MatchesGoldenValue()
    {
        // GOLDEN VECTOR: this test pins the exact algorithm ((guidHash * 397) ^ suffix).
        // If a maintainer swaps in a different hash without noticing, the entire installed
        // base gets a one-time reshuffle of exploration seeds — potentially very visible
        // to users if the new seed lands them in a different diversity cohort.
        //
        // The Guid GetHashCode() output is stable across .NET versions for a given byte layout,
        // so the golden value is safe against future runtimes.
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var seed = InvokeComputeStableSeed(id, 0);
        unchecked
        {
            var expected = (id.GetHashCode() * 397) ^ 0;
            Assert.Equal(expected, seed);
        }
    }

    [Fact]
    public void ComputeStableSeed_SuffixZero_YieldsGuidHashCodeTimes397()
    {
        // XOR with 0 is identity, so seed with suffix=0 must equal (GuidHash * 397).
        // Verifies the fold order (multiplier applies to the Guid, not the suffix).
        var id = Guid.Parse("aabbccdd-0011-2233-4455-66778899aabb");
        var seed = InvokeComputeStableSeed(id, 0);
        unchecked
        {
            Assert.Equal(id.GetHashCode() * 397, seed);
        }
    }

    // ================================================================================================
    // Reflection helpers — Engine.ComputeStableSeed is `internal static` and the test project has
    // InternalsVisibleTo, so we call it directly. Using MethodInfo instead of a direct call avoids
    // hard-coding a dependency on the exact signature if it ever needs a defensive rename.
    // ================================================================================================

    private static int InvokeComputeStableSeed(Guid id, int suffix)
    {
        var method = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine)
            .GetMethod(
                "ComputeStableSeed",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(Guid), typeof(int)],
                modifiers: null);

        Assert.NotNull(method);
        return (int)method!.Invoke(null, [id, suffix])!;
    }
}