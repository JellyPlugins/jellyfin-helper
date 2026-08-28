using System;
using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the pure-static internal helper methods on Engine.
/// </summary>
public sealed class EngineHelperTests
{
    // ComputeStableSeed - deterministic, process-independent seed for the exploration RNG.

    [Fact]
    public void ComputeStableSeed_SameInputs_ReturnsIdenticalSeed()
    {
        // BUG GUARD: the contract is deterministic in-process.
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
        // Guid.Empty is a legitimate value (all-zero user id) - the helper must not throw
        // and must still produce a stable seed.
        var a = InvokeComputeStableSeed(Guid.Empty, 0);
        var b = InvokeComputeStableSeed(Guid.Empty, 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeStableSeed_NegativeSuffix_ProducesDeterministicSeed()
    {
        // Suffix comes from int operators (e.g. hash folding) and can be negative - must not throw.
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var a = InvokeComputeStableSeed(id, -1);
        var b = InvokeComputeStableSeed(id, -1);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeStableSeed_MaxAndMinSuffix_DoNotThrow_AndAreDistinct()
    {
        // Verifies the `unchecked` block truly wraps as intended - this used to be a subtle bug
        // when the multiplier crossed int.MaxValue in a `checked` context.
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var minSeed = InvokeComputeStableSeed(id, int.MinValue);
        var maxSeed = InvokeComputeStableSeed(id, int.MaxValue);
        Assert.NotEqual(minSeed, maxSeed);
    }

    [Fact]
    public void ComputeStableSeed_KnownInputVector_IsSelfConsistent_WithSuffixAsIdentity()
    {
        // BEHAVIOUR PIN, not a golden literal: recomputing (guidHash * 397) here and comparing to the SUT is a tautology if the SUT ever silently changes to a different formula (both sides would move together).
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var baseSeed = InvokeComputeStableSeed(id, 0);

        // Sample a spread of suffix values (positive, negative, prime, power-of-two) to make
        // sure the invariant holds across the full int32 range - not just an easy corner.
        foreach (var suffix in new[] { 1, -1, 42, 1 << 16, int.MinValue, int.MaxValue })
        {
            var seeded = InvokeComputeStableSeed(id, suffix);
            unchecked
            {
                Assert.Equal(baseSeed ^ suffix, seeded);
            }
        }
    }

    [Fact]
    public void ComputeStableSeed_SuffixZero_YieldsFnv1aHash()
    {
        // The implementation uses FNV-1a over the raw Guid bytes (process-stable, no hash randomisation). With suffix=0 (XOR identity) the result must equal the FNV-1a hash of the Guid's byte representation.
        var id = Guid.Parse("aabbccdd-0011-2233-4455-66778899aabb");
        var seed = InvokeComputeStableSeed(id, 0);
        unchecked
        {
            // Recompute inline so the test is self-documenting and not a naked literal.
            var bytes = id.ToByteArray();
            var expected = (int)2166136261u;
            foreach (var b in bytes)
            {
                expected ^= b;
                expected *= 16777619;
            }
            Assert.Equal(expected, seed);
        }
    }

    // Reflection helpers - Engine.ComputeStableSeed is `internal static` and the test project has InternalsVisibleTo, so we call it directly.

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