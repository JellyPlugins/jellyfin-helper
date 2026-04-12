using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
/// Tests for <see cref="PathValidator"/>.
/// </summary>
[Collection("PluginLogService")]
public class PathValidatorTests : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PathValidatorTests"/> class.
    /// Clears the plugin log buffer before each test.
    /// </summary>
    public PathValidatorTests()
    {
        PluginLogService.TestMinLevelOverride = "DEBUG";
        PluginLogService.Clear();
    }

    /// <summary>
    /// Cleans up after each test.
    /// </summary>
    public void Dispose()
    {
        PluginLogService.Clear();
        PluginLogService.TestMinLevelOverride = null;
    }

    // === IsSafePath ===

    [Fact]
    public void IsSafePath_NullPath_ReturnsFalse()
    {
        Assert.False(PathValidator.IsSafePath(null, "/base"));
    }

    [Fact]
    public void IsSafePath_EmptyPath_ReturnsFalse()
    {
        Assert.False(PathValidator.IsSafePath(string.Empty, "/base"));
    }

    [Fact]
    public void IsSafePath_WhitespacePath_ReturnsFalse()
    {
        Assert.False(PathValidator.IsSafePath("   ", "/base"));
    }

    [Fact]
    public void IsSafePath_PathWithTraversal_ReturnsFalse()
    {
        Assert.False(PathValidator.IsSafePath("/base/../etc/passwd", "/base"));
    }

    [Fact]
    public void IsSafePath_PathWithNullChar_ReturnsFalse()
    {
        Assert.False(PathValidator.IsSafePath("/base/file\0.txt", "/base"));
    }

    [Fact]
    public void IsSafePath_ValidChildPath_ReturnsTrue()
    {
        // Use a temp directory to ensure the path resolves correctly on this OS
        var baseDir = Path.GetTempPath();
        var childPath = Path.Combine(baseDir, "subdir", "file.txt");

        Assert.True(PathValidator.IsSafePath(childPath, baseDir));
    }

    [Fact]
    public void IsSafePath_PathOutsideBase_ReturnsFalse()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "specific-base");
        var outsidePath = Path.Combine(Path.GetTempPath(), "other-dir", "file.txt");

        Assert.False(PathValidator.IsSafePath(outsidePath, baseDir));
    }

    // === SanitizeFileName ===

    [Fact]
    public void SanitizeFileName_NullInput_ReturnsDefault()
    {
        Assert.Equal("export", PathValidator.SanitizeFileName(null!));
    }

    [Fact]
    public void SanitizeFileName_EmptyInput_ReturnsDefault()
    {
        Assert.Equal("export", PathValidator.SanitizeFileName(string.Empty));
    }

    [Fact]
    public void SanitizeFileName_ValidName_ReturnsSameName()
    {
        Assert.Equal("report.json", PathValidator.SanitizeFileName("report.json"));
    }

    [Fact]
    public void SanitizeFileName_StripsDirectorySeparators()
    {
        var result = PathValidator.SanitizeFileName("some/path/file.txt");
        Assert.Equal("file.txt", result);
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidChars()
    {
        // Use characters that are invalid on ALL platforms (null char is always invalid)
        var invalidChars = Path.GetInvalidFileNameChars();
        if (invalidChars.Length == 0)
        {
            return; // Nothing to test on this platform
        }

        var testChar = invalidChars[0];
        var input = $"file{testChar}name.txt";
        var result = PathValidator.SanitizeFileName(input);

        // The invalid char should be replaced with '_'.
        // Use ordinal comparison because the null character ('\0') has zero sort-weight
        // in culture-sensitive comparisons, causing DoesNotContain to report a false match.
        Assert.DoesNotContain(testChar.ToString(), result, StringComparison.Ordinal);
        Assert.Contains("name.txt", result);
    }

    // === Logging integration tests ===

    [Fact]
    public void IsSafePath_EmptyPath_LogsDebugEntry()
    {
        PathValidator.IsSafePath(string.Empty, "/base");

        var entries = PluginLogService.GetEntries(minLevel: "DEBUG", source: "PathValidator");
        Assert.Contains(entries, e => e.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsSafePath_TraversalPath_LogsWarningEntry()
    {
        PathValidator.IsSafePath("/base/../etc/passwd", "/base");

        var entries = PluginLogService.GetEntries(minLevel: "WARN", source: "PathValidator");
        Assert.Contains(entries, e => e.Message.Contains("traversal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsSafePath_NullCharPath_LogsWarningEntry()
    {
        PathValidator.IsSafePath("/base/file\0.txt", "/base");

        var entries = PluginLogService.GetEntries(minLevel: "WARN", source: "PathValidator");
        Assert.Contains(entries, e => e.Message.Contains("traversal", StringComparison.OrdinalIgnoreCase));
    }
}