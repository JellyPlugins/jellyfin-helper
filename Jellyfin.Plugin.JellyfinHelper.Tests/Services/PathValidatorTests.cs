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
        var safePath = System.IO.Path.Combine(basePath, "subdir", "file.txt");
        Assert.True(PathValidator.IsSafePath(safePath, basePath));
    }

    [Fact]
    public void IsSafePath_ReturnsFalse_WhenPathIsOutsideBase()
    {
        var basePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "allowed");
        var outsidePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "outside", "file.txt");
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
}