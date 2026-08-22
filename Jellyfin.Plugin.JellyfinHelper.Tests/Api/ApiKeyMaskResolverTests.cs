using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Unit tests for <see cref="ApiKeyMaskResolver"/>, the shared logic that decides whether an
///     incoming API key is the masked sentinel and, if so, recovers the real stored key. The class
///     is <c>internal</c> but reachable via <c>InternalsVisibleTo</c>. These tests lock the exact
///     semantics the save path (<c>ConfigurationController.ResolveApiKey</c>) previously implemented
///     inline, plus the security invariant that an unresolvable mask yields an empty string (never a
///     literal copy of the mask) so callers know not to forward it upstream.
/// </summary>
public class ApiKeyMaskResolverTests
{
    private const string ApiKeyMask = "********";

    // ---------- IsMask ----------

    [Fact]
    public void IsMask_ExactSentinel_ReturnsTrue()
    {
        Assert.True(ApiKeyMaskResolver.IsMask(ApiKeyMask));
    }

    [Fact]
    public void IsMask_PaddedSentinel_ReturnsTrue()
    {
        // Trimmed before comparison so a padded copy can't dodge detection and get forwarded.
        Assert.True(ApiKeyMaskResolver.IsMask("  " + ApiKeyMask + "  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("real-key")]
    [InlineData("*******")]   // 7 stars, not the 8-star sentinel
    [InlineData("*********")]  // 9 stars
    public void IsMask_NonSentinelValues_ReturnFalse(string? candidate)
    {
        Assert.False(ApiKeyMaskResolver.IsMask(candidate));
    }

    // ---------- ResolveArrKey: non-mask passthrough ----------

    [Fact]
    public void ResolveArrKey_RealKey_ReturnedUnchanged()
    {
        var stored = new List<ArrInstanceConfig>
        {
            new() { Url = "http://localhost:7878", ApiKey = "stored-key", Name = "R" }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey("new-key", "http://localhost:7878", "R", stored);

        Assert.Equal("new-key", result);
    }

    [Fact]
    public void ResolveArrKey_NullRealKey_ReturnsEmptyString()
    {
        var result = ApiKeyMaskResolver.ResolveArrKey(null, "http://localhost:7878", "R", new List<ArrInstanceConfig>());

        Assert.Equal(string.Empty, result);
    }

    // ---------- ResolveArrKey: mask resolution ----------

    [Fact]
    public void ResolveArrKey_MaskMatchesByUrl_RecoversStoredKey()
    {
        var stored = new List<ArrInstanceConfig>
        {
            new() { Url = "http://localhost:7878", ApiKey = "stored-key", Name = "R" }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://localhost:7878", "R", stored);

        Assert.Equal("stored-key", result);
    }

    [Fact]
    public void ResolveArrKey_MaskSameUrlDifferentNames_SelectsByName()
    {
        var stored = new List<ArrInstanceConfig>
        {
            new() { Url = "http://localhost:7878", ApiKey = "key-A", Name = "A" },
            new() { Url = "http://localhost:7878", ApiKey = "key-B", Name = "B" }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://localhost:7878", "B", stored);

        Assert.Equal("key-B", result);
    }

    [Fact]
    public void ResolveArrKey_MaskNameMismatch_FallsBackToUrlOnly()
    {
        // Rename case: the stored Name differs (admin renamed), but the URL still matches, so the
        // URL-only fallback recovers the key rather than losing it.
        var stored = new List<ArrInstanceConfig>
        {
            new() { Url = "http://localhost:7878", ApiKey = "stored-key", Name = "OldName" }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://localhost:7878", "NewName", stored);

        Assert.Equal("stored-key", result);
    }

    [Fact]
    public void ResolveArrKey_MaskUrlComparisonIsCaseInsensitiveAndTrimmed()
    {
        var stored = new List<ArrInstanceConfig>
        {
            new() { Url = "http://Localhost:7878", ApiKey = "stored-key", Name = "R" }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "  http://localhost:7878  ", "R", stored);

        Assert.Equal("stored-key", result);
    }

    [Fact]
    public void ResolveArrKey_MaskNoMatch_ReturnsEmptyString()
    {
        // Security invariant: an unresolvable mask must NOT return a literal copy of the mask.
        var stored = new List<ArrInstanceConfig>
        {
            new() { Url = "http://other:7878", ApiKey = "stored-key", Name = "R" }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://localhost:7878", "R", stored);

        Assert.Equal(string.Empty, result);
        Assert.NotEqual(ApiKeyMask, result);
    }

    [Fact]
    public void ResolveArrKey_MaskEmptyStore_ReturnsEmptyString()
    {
        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://localhost:7878", "R", new List<ArrInstanceConfig>());

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ResolveArrKey_NullStored_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ApiKeyMaskResolver.ResolveArrKey("key", "http://localhost:7878", "R", null!));
    }
}
