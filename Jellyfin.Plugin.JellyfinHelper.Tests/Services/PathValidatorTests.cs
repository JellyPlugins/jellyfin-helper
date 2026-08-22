using System;
using System.IO;
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
        var basePath = Path.GetTempPath();
        var safePath = Path.Join(basePath, "subdir", "file.txt");
        Assert.True(PathValidator.IsSafePath(safePath, basePath));
    }

    [Fact]
    public void IsSafePath_ReturnsFalse_WhenPathIsOutsideBase()
    {
        var basePath = Path.Join(Path.GetTempPath(), "allowed");
        var outsidePath = Path.Join(Path.GetTempPath(), "outside", "file.txt");
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

    // IsSafePath(base, base) must return true. The path is the allowed root itself.
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
    /// A path whose folder name contains ".." as a substring, but has no ".." segment,
    /// must not be rejected. That would be a false positive.
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
    [InlineData("/configuration")] // NOT /config, must not false-match on a prefix
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

    // An empty base means there is no directory anything could be inside of, so the
    // empty-base guard must short-circuit to false before any path comparison runs.
    [Fact]
    public void IsSafePath_ReturnsFalse_WhenAllowedBaseDirectoryEmpty()
    {
        Assert.False(PathValidator.IsSafePath("/media/file.txt", ""));
    }

    // Deletion may only ever act on absolute paths. A relative path is ambiguous because
    // it resolves against an unknown cwd, so it must be refused outright.
    [Fact]
    public void IsPathSafeForDeletion_RelativePath_Rejected()
    {
        Assert.False(PathValidator.IsPathSafeForDeletion(Path.Combine("relative", "dir"), []));
    }

    // Deleting a whole drive or filesystem root must be refused regardless of library folders.
    [Fact]
    public void IsPathSafeForDeletion_FilesystemRoot_Rejected()
    {
        Assert.False(PathValidator.IsPathSafeForDeletion(Path.GetPathRoot(Path.GetTempPath())!, []));
    }

    // The candidate equals a configured library root exactly. Deleting the library root
    // itself must be refused, which is a different branch from child-of-root.
    [Fact]
    public void IsPathSafeForDeletion_PathEqualsLibraryRoot_Rejected()
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        var libraryRoot = Path.Combine(sep, "media", "movies");
        Assert.False(PathValidator.IsPathSafeForDeletion(libraryRoot, [libraryRoot]));
    }

    // The candidate is a parent of a library root. Deleting it would take the library root
    // down with it, so an ancestor must be refused too.
    [Fact]
    public void IsPathSafeForDeletion_PathIsAncestorOfLibraryRoot_Rejected()
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        Assert.False(PathValidator.IsPathSafeForDeletion(
            Path.Combine(sep, "media"),
            [Path.Combine(sep, "media", "movies")]));
    }

    // A name that reduces to a dot-segment survives char sanitization and GetFileName,
    // but is not a usable filename, so the contract falls back to the safe default.
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public void SanitizeFileName_ReturnsExport_WhenNameReducesToDotSegments(string name)
    {
        Assert.Equal("export", PathValidator.SanitizeFileName(name));
    }

    // A path that clears the null-byte and ".."-segment guards but is still malformed
    // enough that Path.GetFullPath throws, like a mid-string colon on Windows, must be
    // refused via the exception filter instead of surfacing as an exception to the caller.
    [Fact]
    public void IsSafePath_MalformedPath_ReturnsFalse()
    {
        var baseDir = Path.GetTempPath();
        var malformed = "C:mid:colon:seg";

        var result = Record.Exception(() =>
            Assert.False(PathValidator.IsSafePath(malformed, baseDir)));

        Assert.Null(result);
    }

    // Backslash traversal. On Windows '\' is a real separator, so "\..\" walks up and out and
    // the path is rejected. On Linux '\' is an ordinary filename char, so "/media\..\etc" is a
    // single leaf directly under "/", not under "/media/": there is no '/' after "media", so the
    // base-prefix check fails and the path is rejected there too. Refused on both platforms, for
    // different reasons.
    [Fact]
    public void IsSafePath_BackslashTraversal_Rejected()
    {
        Assert.False(PathValidator.IsSafePath("/media\\..\\etc", "/media"));
    }

    // Mixed separators split by platform. The path keeps a real '/' after "/media", so on Linux
    // it resolves to "/media/sub\..\..\etc", a leaf that still starts with "/media/" (backslashes
    // are ordinary chars), so it is allowed. On Windows the '\..\' segments are real traversal that
    // escapes "/media", so it is refused.
    [Fact]
    public void IsSafePath_MixedSeparatorTraversal()
    {
        var expectedSafe = !OperatingSystem.IsWindows();
        Assert.Equal(expectedSafe, PathValidator.IsSafePath("/media/sub\\..\\..\\etc", "/media"));
    }

    // A null byte can truncate the path in downstream syscalls (injection vector).
    // It must never survive into a real file operation.
    [Fact]
    public void SanitizeFileName_StripsNullByte()
    {
        var result = PathValidator.SanitizeFileName("evil\0.csv");
        Assert.DoesNotContain('\0', result);
        Assert.NotEmpty(result);
    }

    // A "filename" that is really an absolute path must be reduced to its leaf so it
    // cannot keep directory components and escape the export directory.
    [Fact]
    public void SanitizeFileName_AbsolutePosixPath_ReducedToLeaf()
    {
        var result = PathValidator.SanitizeFileName("/etc/passwd");
        Assert.Equal("passwd", result);
        Assert.DoesNotContain('/', result);
    }

    // Same for a Windows path. No separators of either kind may survive, only the leaf.
    [Fact]
    public void SanitizeFileName_AbsoluteWindowsPath_ReducedToLeaf()
    {
        var result = PathValidator.SanitizeFileName("C:\\Windows\\system32\\evil.dll");
        Assert.DoesNotContain('\\', result);
        Assert.DoesNotContain('/', result);
        Assert.Equal("evil.dll", result);
    }

    // Wildcards and reserved chars ('<', '>', '?') are invalid filenames on Windows and get
    // replaced with '_'. On Linux they are legal chars, so SanitizeFileName leaves them intact.
    // Either way the result is a single inert leaf with no path separators.
    [Fact]
    public void SanitizeFileName_ReplacesInvalidChars()
    {
        var result = PathValidator.SanitizeFileName("a<b>c?.txt");

        var expected = OperatingSystem.IsWindows() ? "a_b_c_.txt" : "a<b>c?.txt";
        Assert.Equal(expected, result);
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
    }

    // Boundary contract: this helper guards only filesystem and library roots, not system
    // paths, so "/etc" returns true here by design. Callers pair it with
    // IsSensitiveSystemPath (see TrashController), which is what actually refuses /etc.
    // Nothing in production treats this method alone as a full guard.
    [Fact]
    public void IsPathSafeForDeletion_DoesNotGuardSystemRoots_ByDesign()
    {
        Assert.True(PathValidator.IsPathSafeForDeletion("/etc", Array.Empty<string>()));
    }
}