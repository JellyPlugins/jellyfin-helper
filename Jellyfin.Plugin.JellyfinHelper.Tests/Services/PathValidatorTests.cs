using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

public class PathValidatorTests
{
    // ===== IsSafePath =====

    [Fact]
    public void IsSafePath_ReturnsFalse_WhenPathIsNull()
    {
        Assert.False(PathValidator.IsSafePath(null, "/base"));
    }

    [Fact]
    public void IsSafePath_ReturnsFalse_WhenPathIsEmpty()
    {
        Assert.False(PathValidator.IsSafePath("", "/base"));
    }

    [Fact]
    public void IsSafePath_ReturnsFalse_WhenPathIsWhitespace()
    {
        Assert.False(PathValidator.IsSafePath("   ", "/base"));
    }

    [Fact]
    public void IsSafePath_ReturnsFalse_WhenPathContainsTraversal()
    {
        Assert.False(PathValidator.IsSafePath("/base/../etc/passwd", "/base"));
    }

    [Fact]
    public void IsSafePath_ReturnsFalse_WhenPathContainsNullChar()
    {
        Assert.False(PathValidator.IsSafePath("/base/file\0.txt", "/base"));
    }

    [Fact]
    public void IsSafePath_ReturnsTrue_WhenPathIsWithinBase()
    {
        var basePath = System.IO.Path.GetTempPath();
        var safePath = System.IO.Path.Join(basePath, "subdir", "file.txt");
        Assert.True(PathValidator.IsSafePath(safePath, basePath));
    }

    [Fact]
    public void IsSafePath_ReturnsFalse_WhenPathIsOutsideBase()
    {
        var basePath = System.IO.Path.Join(System.IO.Path.GetTempPath(), "allowed");
        var outsidePath = System.IO.Path.Join(System.IO.Path.GetTempPath(), "outside", "file.txt");
        Assert.False(PathValidator.IsSafePath(outsidePath, basePath));
    }

    // ===== SanitizeFileName =====

    [Fact]
    public void SanitizeFileName_ReturnsExport_WhenNull()
    {
        Assert.Equal("export", PathValidator.SanitizeFileName(null!));
    }

    [Fact]
    public void SanitizeFileName_ReturnsExport_WhenEmpty()
    {
        Assert.Equal("export", PathValidator.SanitizeFileName(""));
    }

    [Fact]
    public void SanitizeFileName_ReturnsExport_WhenWhitespace()
    {
        Assert.Equal("export", PathValidator.SanitizeFileName("   "));
    }

    [Fact]
    public void SanitizeFileName_ReturnsSameName_WhenValid()
    {
        Assert.Equal("report.csv", PathValidator.SanitizeFileName("report.csv"));
    }

    [Fact]
    public void SanitizeFileName_StripsDirectoryComponents()
    {
        var result = PathValidator.SanitizeFileName("subdir/file.txt");
        Assert.Equal("file.txt", result);
    }

    [Fact]
    public void SanitizeFileName_StripsBackslashDirectoryComponents()
    {
        var result = PathValidator.SanitizeFileName("subdir\\file.txt");
        Assert.Equal("file.txt", result);
    }

    // TEST-5: IsSafePath(base, base) must return true — the path IS the allowed root.
    [Fact]
    public void IsSafePath_PathEqualsBase_ReturnsTrue()
    {
        var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.True(PathValidator.IsSafePath(dir, dir));
    }

    [Fact]
    public void IsSafePath_PathInsideBase_ReturnsTrue()
    {
        var dir = Path.GetTempPath();
        var child = Path.Combine(dir, "subdir");
        Assert.True(PathValidator.IsSafePath(child, dir));
    }

    [Fact]
    public void IsSafePath_PathOutsideBase_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "allowed");
        var outside = Path.Combine(Path.GetTempPath(), "other");
        Assert.False(PathValidator.IsSafePath(outside, dir));
    }

    [Fact]
    public void IsSafePath_TraversalInPath_ReturnsFalse()
    {
        var dir = Path.GetTempPath();
        Assert.False(PathValidator.IsSafePath(Path.Combine(dir, "..", "escape"), dir));
    }

    // ===== IsPathSafeForDeletion =====

    /// <summary>
    /// A path whose folder name contains ".." as a substring (but has no ".." segment)
    /// must not be rejected — that would be a false positive.
    /// </summary>
    [Fact]
    public void IsPathSafeForDeletion_PathWithDotDotInName_NotFalsePositive()
    {
        // "/media/my..folder/file" contains ".." as a substring but not as a path segment.
        var path = Path.Combine(Path.DirectorySeparatorChar.ToString(), "media", "my..folder", "file");
        Assert.True(PathValidator.IsPathSafeForDeletion(path, []));
    }

    /// <summary>
    /// A path that is a direct child of a library root must be rejected to prevent
    /// deleting content that lives inside a library.
    /// </summary>
    [Fact]
    public void IsPathSafeForDeletion_PathInsideLibraryRoot_Rejected()
    {
        var libraryRoot = Path.Combine(Path.DirectorySeparatorChar.ToString(), "media", "movies");
        var candidate   = Path.Combine(libraryRoot, "Inception");
        Assert.False(PathValidator.IsPathSafeForDeletion(candidate, [libraryRoot]));
    }

    /// <summary>
    /// IsSafePath must reject a path that contains a ".." segment, even when the
    /// segment-split logic is the primary guard (early-exit before GetFullPath).
    /// </summary>
    [Fact]
    public void IsSafePath_DotDotAsSegment_Rejected()
    {
        // "/media/../etc" contains ".." as an explicit path segment.
        var path = Path.Combine(
            Path.DirectorySeparatorChar.ToString(), "media", "..", "etc");
        Assert.False(PathValidator.IsSafePath(path, Path.Combine(Path.DirectorySeparatorChar.ToString(), "media")));
    }

    // ===== IsSensitiveSystemPath =====

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsSensitiveSystemPath_NullOrEmpty_ReturnsFalse(string? path)
        => Assert.False(PathValidator.IsSensitiveSystemPath(path));

    [Theory]
    [InlineData("/config")]
    [InlineData("/config/data/plugins")]
    [InlineData("/data")]
    [InlineData("/cache")]
    [InlineData("/etc")]
    [InlineData("/etc/ssl/private")]
    [InlineData("/var/log")]
    [InlineData("/proc")]
    [InlineData("/root")]
    public void IsSensitiveSystemPath_PosixSystemRoots_ReturnTrue(string path)
        => Assert.True(PathValidator.IsSensitiveSystemPath(path));

    [Theory]
    [InlineData("/media")]
    [InlineData("/media/Movies")]
    [InlineData("/mnt/library2")]
    [InlineData("/srv/media")]
    [InlineData("/configuration")] // NOT /config — must not false-match on a prefix
    [InlineData("/etcetera")] // NOT /etc
    public void IsSensitiveSystemPath_MediaAndLookalikes_ReturnFalse(string path)
        => Assert.False(PathValidator.IsSensitiveSystemPath(path));

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)\Foo")]
    [InlineData(@"C:\ProgramData")]
    public void IsSensitiveSystemPath_WindowsSystemRoots_ReturnTrue(string path)
    {
        // Windows roots are matched case-insensitively; the check is OS-agnostic on the
        // literal string, so this holds on any host runner.
        Assert.True(PathValidator.IsSensitiveSystemPath(path));
    }
}