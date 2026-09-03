using System;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Covers <see cref="LibraryPathResolver.IsUnderAllowedRoot"/> boundary and platform behavior.
/// </summary>
public sealed class LibraryPathResolverAllowedRootTests
{
    [Fact]
    public void IsUnderAllowedRoot_ChildOfRoot_ReturnsTrue()
    {
        Assert.True(LibraryPathResolver.IsUnderAllowedRoot("/media/movies/Film (2020)/film.mkv", ["/media/movies"]));
    }

    [Fact]
    public void IsUnderAllowedRoot_ExactRoot_ReturnsTrue()
    {
        Assert.True(LibraryPathResolver.IsUnderAllowedRoot("/media/movies", ["/media/movies"]));
    }

    [Fact]
    public void IsUnderAllowedRoot_SiblingPrefix_ReturnsFalse()
    {
        // /media/movies must not match /media/movies2 — the directory boundary guards this.
        Assert.False(LibraryPathResolver.IsUnderAllowedRoot("/media/movies2/film.mkv", ["/media/movies"]));
    }

    [Fact]
    public void IsUnderAllowedRoot_NotUnderAnyRoot_ReturnsFalse()
    {
        Assert.False(LibraryPathResolver.IsUnderAllowedRoot("/media/home-videos/clip.mp4", ["/media/movies", "/media/shows"]));
    }

    [Fact]
    public void IsUnderAllowedRoot_TrailingSeparatorOnRoot_StillMatches()
    {
        Assert.True(LibraryPathResolver.IsUnderAllowedRoot("/media/movies/film.mkv", ["/media/movies/"]));
    }

    [Fact]
    public void IsUnderAllowedRoot_MixedSeparators_Normalized()
    {
        Assert.True(LibraryPathResolver.IsUnderAllowedRoot(@"\media\movies\film.mkv", ["/media/movies"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsUnderAllowedRoot_NullOrEmptyItemPath_ReturnsFalse(string? itemPath)
    {
        Assert.False(LibraryPathResolver.IsUnderAllowedRoot(itemPath, ["/media/movies"]));
    }

    [Fact]
    public void IsUnderAllowedRoot_EmptyRootSet_ReturnsFalse()
    {
        Assert.False(LibraryPathResolver.IsUnderAllowedRoot("/media/movies/film.mkv", []));
    }

    [Fact]
    public void IsUnderAllowedRoot_NullRootSet_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LibraryPathResolver.IsUnderAllowedRoot("/media/movies/film.mkv", null!));
    }

    [Fact]
    public void IsUnderAllowedRoot_EmptyRootEntry_Skipped()
    {
        // A blank root entry must not act as a wildcard that matches everything.
        Assert.False(LibraryPathResolver.IsUnderAllowedRoot("/media/movies/film.mkv", [string.Empty]));
    }

    [Fact]
    public void IsUnderAllowedRoot_CasingFollowsPlatformConvention()
    {
        // Linux is case-sensitive (ordinal); other platforms fold case, matching GetDistinctLibraryLocations.
        var result = LibraryPathResolver.IsUnderAllowedRoot("/media/MOVIES/film.mkv", ["/media/movies"]);

        if (OperatingSystem.IsLinux())
        {
            Assert.False(result);
        }
        else
        {
            Assert.True(result);
        }
    }

    [Fact]
    public void IsUnderAllowedRoot_FilesystemRootAllowed_ChildMatches()
    {
        // A virtual folder rooted at "/" must treat every path as a descendant; the child prefix
        // collapses to "/" itself rather than "//", which would reject everything.
        Assert.True(LibraryPathResolver.IsUnderAllowedRoot("/media/movies/film.mkv", ["/"]));
    }

    [Fact]
    public void IsUnderAllowedRoot_FilesystemRootAllowed_RootItselfMatches()
    {
        Assert.True(LibraryPathResolver.IsUnderAllowedRoot("/", ["/"]));
    }
}
