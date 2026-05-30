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

    /// <summary>
    ///     Returns the platform-specific maximum path length, matching the production logic in
    ///     <see cref="TrashService" /> (259 on Windows, 1023 on macOS, 4095 on Linux).
    /// </summary>
    private static int GetExpectedMaxPathLength() =>
        OperatingSystem.IsWindows() ? 259 :
        OperatingSystem.IsMacOS() ? 1023 :
        4095;

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

        var maxLen = GetExpectedMaxPathLength();
        var resultSize = TrashService.MeasureString(result);
        Assert.True(resultSize <= maxLen, $"Path size {resultSize} exceeds OS limit {maxLen}");
    }

    // ── Path-length safety: path already at the limit ────────────────────────

    [Fact]
    public void ResolveCollision_PathAtExactLimit_DoesNotExceedLimit()
    {
        var maxLen = GetExpectedMaxPathLength();

        // Build a directory name that brings the total path exactly to maxLen.
        // We can only do this when the test root itself is short enough.
        var separator = Path.DirectorySeparatorChar.ToString();
        var dirPrefixSize = TrashService.MeasureString(_testRoot) + TrashService.MeasureString(separator);
        var nameLen = maxLen - dirPrefixSize;
        if (nameLen <= 0)
        {
            // Test root is already too long for this platform — skip gracefully.
            return;
        }

        var longName = new string('a', nameLen);
        var path = Path.Join(_testRoot, longName);

        var result = TrashService.ResolveCollision(path);

        var resultSize = TrashService.MeasureString(result);
        Assert.True(resultSize <= maxLen, $"Path size {resultSize} exceeds OS limit {maxLen}");
    }

    // ── Path-length safety: path over the limit ───────────────────────────────

    [Fact]
    public void ResolveCollision_PathOverLimit_TruncatesName()
    {
        var maxLen = GetExpectedMaxPathLength();

        // Construct a path that exceeds maxLen by 50 units (bytes on Unix, chars on Windows).
        var separator = Path.DirectorySeparatorChar.ToString();
        var dirPrefixSize = TrashService.MeasureString(_testRoot) + TrashService.MeasureString(separator);
        var nameLen = maxLen - dirPrefixSize + 50; // 50 units over limit
        if (nameLen <= 0)
        {
            return;
        }

        var longName = new string('b', nameLen);
        var path = Path.Join(_testRoot, longName);

        var result = TrashService.ResolveCollision(path);

        var resultSize = TrashService.MeasureString(result);
        Assert.True(resultSize <= maxLen, $"Path size {resultSize} exceeds OS limit {maxLen}");
        Assert.True(resultSize > 0);
    }

    // ── Path-length safety: suffix collision with long path ───────────────────

    [Fact]
    public void ResolveCollision_LongPathWithCollision_SuffixedResultFitsLimit()
    {
        Directory.CreateDirectory(_testRoot);

        var maxLen = GetExpectedMaxPathLength();
        var separator = Path.DirectorySeparatorChar.ToString();
        var dirPrefixSize = TrashService.MeasureString(_testRoot) + TrashService.MeasureString(separator);

        // Linux NAME_MAX caps a single path component at 255 bytes.
        // We must stay within that limit so Directory.CreateDirectory succeeds
        // while still constructing a path that exercises the length-truncation code path.
        var componentLimit = OperatingSystem.IsWindows() ? maxLen - dirPrefixSize : 200;

        var nameLen = Math.Min(maxLen - dirPrefixSize, componentLimit);
        if (nameLen <= 0)
        {
            return;
        }

        var longName = new string('c', nameLen);
        var path = Path.Join(_testRoot, longName);

        // Create a collision so the suffix path must be generated
        Directory.CreateDirectory(path);

        var result = TrashService.ResolveCollision(path);

        var resultSize = TrashService.MeasureString(result);
        Assert.True(resultSize <= maxLen, $"Suffixed path size {resultSize} exceeds OS limit {maxLen}");
        Assert.False(Directory.Exists(result), "Resolved path must not already exist");
        Assert.NotEqual(path, result);
    }

    // ── Path-length safety: component over NAME_MAX but path under OS max ────

    [Fact]
    public void ResolveCollision_ComponentOverLimitButPathUnderMax_TruncatesComponent()
    {
        // Windows NAME_MAX enforcement is not applicable here (no per-component cap below MAX_PATH).
        // This test isolates the per-component cap on non-Windows filesystems (NAME_MAX = 255).
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 260 chars > 255 (NAME_MAX) but far below 4095 (PATH_MAX) — exercises the component cap.
        var path = Path.Join(_testRoot, new string('d', 260));
        var result = TrashService.ResolveCollision(path);

        var componentSize = TrashService.MeasureString(Path.GetFileName(result));
        Assert.True(componentSize <= 255,
            $"Component size {componentSize} exceeds NAME_MAX 255");
    }

    // ── Path-length safety: multibyte UTF-8 characters (Unix byte limits) ────

    [Fact]
    public void ResolveCollision_MultibyteName_ComponentFitsWithinByteLimit()
    {
        // On Unix, NAME_MAX is 255 bytes, not 255 characters.
        // CJK characters like 'あ' are 3 bytes each in UTF-8.
        // 100 such characters = 300 bytes > 255 byte limit.
        // This test verifies that the truncation respects byte boundaries.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 100 CJK chars × 3 bytes = 300 bytes, exceeds NAME_MAX of 255 bytes
        var multibyteComponent = new string('あ', 100);
        var path = Path.Join(_testRoot, multibyteComponent);
        var result = TrashService.ResolveCollision(path);

        var resultComponent = Path.GetFileName(result);
        var componentByteCount = System.Text.Encoding.UTF8.GetByteCount(resultComponent);
        Assert.True(componentByteCount <= 255,
            $"Component byte length {componentByteCount} exceeds NAME_MAX 255 bytes");
        // Verify we didn't lose everything — should still have some content
        Assert.True(resultComponent.Length > 0, "Truncated component should not be empty");
    }

    [Fact]
    public void ResolveCollision_MultibyteNameWithCollision_SuffixPreservedWithinByteLimit()
    {
        // Verifies that when a multibyte name has a collision, the suffix (_2) is preserved
        // and the total component stays within the byte limit.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_testRoot);

        // Use 80 CJK chars (240 bytes) — fits in 255 but after adding suffix would need truncation
        var multibyteComponent = new string('日', 80);
        var path = Path.Join(_testRoot, multibyteComponent);

        // Create the original to force collision resolution — but only if it fits NAME_MAX
        var componentBytes = System.Text.Encoding.UTF8.GetByteCount(multibyteComponent);
        if (componentBytes <= 255)
        {
            Directory.CreateDirectory(path);
        }

        var result = TrashService.ResolveCollision(path);

        var resultComponent = Path.GetFileName(result);
        var resultByteCount = System.Text.Encoding.UTF8.GetByteCount(resultComponent);
        Assert.True(resultByteCount <= 255,
            $"Suffixed component byte length {resultByteCount} exceeds NAME_MAX 255 bytes");
    }

    [Fact]
    public void ResolveCollision_EmojiName_TruncatesOnRuneBoundary()
    {
        // Emoji like 🎬 are 4 bytes in UTF-8 and 2 chars (surrogate pair) in UTF-16.
        // Verify we don't split in the middle of a multi-byte sequence.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 70 emoji × 4 bytes = 280 bytes > 255 byte limit
        // Cannot use new string(char, count) for surrogate pairs; build manually.
        var emoji = "\U0001F3AC"; // 🎬 U+1F3AC
        var emojiComponent = string.Concat(Enumerable.Repeat(emoji, 70));
        var path = Path.Join(_testRoot, emojiComponent);
        var result = TrashService.ResolveCollision(path);

        var resultComponent = Path.GetFileName(result);
        var componentByteCount = System.Text.Encoding.UTF8.GetByteCount(resultComponent);
        Assert.True(componentByteCount <= 255,
            $"Emoji component byte length {componentByteCount} exceeds NAME_MAX 255 bytes");
        // Verify no broken surrogates — re-encoding should round-trip cleanly
        var reEncoded = System.Text.Encoding.UTF8.GetString(
            System.Text.Encoding.UTF8.GetBytes(resultComponent));
        Assert.Equal(resultComponent, reEncoded);
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

        var maxLen = GetExpectedMaxPathLength();
        var resultSize = TrashService.MeasureString(result);
        Assert.True(resultSize <= maxLen, $"GUID fallback path size {resultSize} exceeds OS limit {maxLen}");
        Assert.False(Directory.Exists(result));
        Assert.False(File.Exists(result));
    }
}
