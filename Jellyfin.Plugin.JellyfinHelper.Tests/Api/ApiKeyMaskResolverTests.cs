using System;
using System.Collections.Generic;
using System.Linq;
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
    public void ResolveArrKey_MaskSameUrlSameName_ResolvesToAStoredKeyDeterministically()
    {
        // Duplicate instances with identical URL and Name are indistinguishable to the resolver, so
        // it deterministically returns the first matching stored key. The security invariant still
        // holds: a real stored key for that URL comes back, never the mask and never a key from a
        // different URL. Names are display labels, not unique keys. This locks the behaviour so it
        // can't silently regress.
        var stored = new List<ArrInstanceConfig>
        {
            new() { Url = "http://localhost:7878", ApiKey = "key-first", Name = "Dup" },
            new() { Url = "http://localhost:7878", ApiKey = "key-second", Name = "Dup" }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://localhost:7878", "Dup", stored);

        Assert.Equal("key-first", result);
        Assert.NotEqual(ApiKeyMask, result);
    }

    [Fact]
    public void ResolveArrKey_MaskSameUrlEmptyNames_ResolvesToAStoredKey()
    {
        // Empty names collide the same way duplicate names do. Still resolves to a real stored key
        // for the matching URL rather than failing or leaking the mask.
        var stored = new List<ArrInstanceConfig>
        {
            new() { Url = "http://localhost:7878", ApiKey = "key-first", Name = string.Empty },
            new() { Url = "http://localhost:7878", ApiKey = "key-second", Name = string.Empty }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://localhost:7878", string.Empty, stored);

        Assert.Equal("key-first", result);
    }

    [Fact]
    public void ResolveArrKey_MaskNameMismatch_FallsBackToUrlOnly()
    {
        // Rename case: the stored Name differs because the admin renamed it, but the URL still
        // matches, so the URL-only fallback recovers the key rather than losing it.
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
    public void ResolveArrKey_MaskWithReadOnlyListStore_UsesFastPath_RecoversStoredKey()
    {
        // The resolver reuses the list directly when it already implements IReadOnlyList, with no
        // defensive copy. An array satisfies IReadOnlyList<T>, so this hits that fast path. Key
        // recovery must be identical whatever the collection shape.
        IReadOnlyList<ArrInstanceConfig> stored = new[]
        {
            new ArrInstanceConfig { Url = "http://localhost:7878", ApiKey = "stored-key", Name = "R" }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://localhost:7878", "R", stored);

        Assert.Equal("stored-key", result);
    }

    [Fact]
    public void ResolveArrKey_MaskWithDeferredEnumerableStore_MaterializesOnce_RecoversStoredKey()
    {
        // A lazy IEnumerable that isn't an IReadOnlyList forces the ToList() branch instead. The
        // Select projection guarantees it isn't already a list, so this covers the non-fast path.
        var stored = new[] { ("http://localhost:7878", "stored-key", "R") }
            .Select(t => new ArrInstanceConfig { Url = t.Item1, ApiKey = t.Item2, Name = t.Item3 });

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://localhost:7878", "R", stored);

        Assert.Equal("stored-key", result);
    }

    [Fact]
    public void ResolveArrKey_NullStored_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ApiKeyMaskResolver.ResolveArrKey("key", "http://localhost:7878", "R", null!));
    }

    [Fact]
    public void ResolveArrKey_StoredKeyLiterallyEqualsMask_TreatedAsUnchanged_RoundTrips()
    {
        // Edge case: the admin's real stored key happens to equal the mask sentinel. Incoming is
        // also the mask, so IsMask short-circuits and we recover the stored key by URL+Name.
        // The returned value is the recovered stored key, never a freshly minted mask leaked as
        // a new secret.
        var stored = new List<ArrInstanceConfig>
        {
            new() { Url = "http://x", ApiKey = ApiKeyMask, Name = "R" }
        };

        var result = ApiKeyMaskResolver.ResolveArrKey(ApiKeyMask, "http://x", "R", stored);

        Assert.Equal(ApiKeyMask, result);
    }

    [Fact]
    public void IsMask_TabAndNewlineWrappedSentinel_ReturnsTrue()
    {
        // Trim() drops all surrounding whitespace, not just spaces, so a tab/newline-padded copy
        // still can't dodge detection and get forwarded upstream as a "key".
        Assert.True(ApiKeyMaskResolver.IsMask("\t" + ApiKeyMask + "\n"));
    }
}
