using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Tests for path-length safety in <see cref="TrashService.ResolveCollision" />.
///     Verifies that neither numeric suffixes nor the GUID fallback produce paths
///     that exceed the OS limit, even on deeply nested directories with long names.
/// </summary>
public class TrashServicePathLengthTests : IDisposable
{
    private readonly string _testRoot = Path.Join(Path.GetTempPath(), $"TrashPathLen-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    // ── ResolveCollision: no collision ───────────────────────────────────────

    [Fact]
    public void ResolveCollision_NoCollision_ReturnsDesiredPath()
    {
        var path = Path.Join(_testRoot, "20260601-120000_Movie");
        var result = TrashService.ResolveCollision(path);
        Assert.Equal(path, result);
    }

    // ── ResolveCollision: numeric suffix ─────────────────────────────────────

    [Fact]
    public void ResolveCollision_SingleCollision_ReturnsSuffix2()
    {
        Directory.CreateDirectory(_testRoot);
        var baseName = Path.Join(_testRoot, "20260601-120000_Movie");
        Directory.CreateDirectory(baseName);

        var result = TrashService.ResolveCollision(baseName);

        Assert.EndsWith("_2", result, StringComparison.Ordinal);
        Assert.False(Directory.Exists(result));
    }

    [Fact]
    public void ResolveCollision_MultipleCollisions_ReturnsNextAvailableSuffix()
    {
        Directory.CreateDirectory(_testRoot);
        var baseName = Path.Join(_testRoot, "20260601-120000_Movie");
        Directory.CreateDirectory(baseName);
        Directory.CreateDirectory($"{baseName}_2");
        Directory.CreateDirectory($"{baseName}_3");

        var result = TrashService.ResolveCollision(baseName);

        Assert.EndsWith("_4", result, StringComparison.Ordinal);
    }

    // ── Path-length safety: normal path ──────────────────────────────────────

    [Fact]
    public void ResolveCollision_ShortPath_FitsWithinLimit()
    {
        var path = Path.Join(_testRoot, "20260601-120000_NormalMovieName");
        var result = TrashService.ResolveCollision(path);

        var maxLen = OperatingSystem.IsWindows() ? 259 : 4095;
        Assert.True(result.Length <= maxLen, $"Path length {result.Length} exceeds OS limit {maxLen}");
    }

    // ── Path-length safety: path already at the limit ────────────────────────

    [Fact]
    public void ResolveCollision_PathAtExactLimit_DoesNotExceedLimit()
    {
        var maxLen = OperatingSystem.IsWindows() ? 259 : 4095;

        // Build a directory name that brings the total path exactly to maxLen.
        // We can only do this when the test root itself is short enough.
        var separator = Path.DirectorySeparatorChar.ToString();
        var dirPrefixLen = _testRoot.Length + separator.Length;
        var nameLen = maxLen - dirPrefixLen;
        if (nameLen <= 0)
        {
            // Test root is already too long for this platform — skip gracefully.
            return;
        }

        var longName = new string('a', nameLen);
        var path = Path.Join(_testRoot, longName);

        var result = TrashService.ResolveCollision(path);

        Assert.True(result.Length <= maxLen, $"Path length {result.Length} exceeds OS limit {maxLen}");
    }

    // ── Path-length safety: path over the limit ───────────────────────────────

    [Fact]
    public void ResolveCollision_PathOverLimit_TruncatesName()
    {
        var maxLen = OperatingSystem.IsWindows() ? 259 : 4095;

        // Construct a path that is maxLen + 50 characters long.
        var separator = Path.DirectorySeparatorChar.ToString();
        var dirPrefixLen = _testRoot.Length + separator.Length;
        var nameLen = maxLen - dirPrefixLen + 50; // 50 chars over limit
        if (nameLen <= 0)
        {
            return;
        }

        var longName = new string('b', nameLen);
        var path = Path.Join(_testRoot, longName);

        var result = TrashService.ResolveCollision(path);

        Assert.True(result.Length <= maxLen, $"Path length {result.Length} exceeds OS limit {maxLen}");
        Assert.True(result.Length > 0);
    }

    // ── Path-length safety: suffix collision with long path ───────────────────

    [Fact]
    public void ResolveCollision_LongPathWithCollision_SuffixedResultFitsLimit()
    {
        Directory.CreateDirectory(_testRoot);

        var maxLen = OperatingSystem.IsWindows() ? 259 : 4095;
        var separator = Path.DirectorySeparatorChar.ToString();
        var dirPrefixLen = _testRoot.Length + separator.Length;

        // Linux NAME_MAX caps a single path component at 255 bytes.
        // We must stay within that limit so Directory.CreateDirectory succeeds
        // while still constructing a path that exercises the length-truncation code path.
        var componentLimit = OperatingSystem.IsWindows() ? maxLen - dirPrefixLen : 200;

        var nameLen = Math.Min(maxLen - dirPrefixLen, componentLimit);
        if (nameLen <= 0)
        {
            return;
        }

        var longName = new string('c', nameLen);
        var path = Path.Join(_testRoot, longName);

        // Create a collision so the suffix path must be generated
        Directory.CreateDirectory(path);

        var result = TrashService.ResolveCollision(path);

        Assert.True(result.Length <= maxLen, $"Suffixed path length {result.Length} exceeds OS limit {maxLen}");
        Assert.False(Directory.Exists(result), "Resolved path must not already exist");
        Assert.NotEqual(path, result);
    }

    // ── GUID fallback stays within limit ─────────────────────────────────────

    [Fact]
    public void ResolveCollision_GuidFallback_StaysWithinPathLimit()
    {
        // This test exercises the GUID fallback by filling 999 numeric suffixes.
        // We use a short name to avoid filesystem path issues while still
        // verifying that the returned path respects the length constraint.
        Directory.CreateDirectory(_testRoot);

        var baseName = Path.Join(_testRoot, "x");
        Directory.CreateDirectory(baseName);
        for (var i = 2; i < 1000; i++)
        {
            Directory.CreateDirectory($"{baseName}_{i}");
        }

        var result = TrashService.ResolveCollision(baseName);

        var maxLen = OperatingSystem.IsWindows() ? 259 : 4095;
        Assert.True(result.Length <= maxLen, $"GUID fallback path length {result.Length} exceeds OS limit {maxLen}");
        Assert.False(Directory.Exists(result));
        Assert.False(File.Exists(result));
    }
}
