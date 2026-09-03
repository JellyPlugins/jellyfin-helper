using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Covers <see cref="LibraryPathResolver.IsUnderAllowedRoot"/> boundary and platform behavior,
///     and the scoped <see cref="LibraryPathResolver.IsAllowed(string?, LibraryRootScope)"/> /
///     <see cref="LibraryPathResolver.GetLibraryRootScope"/> nested-exclusion handling.
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

    [Fact]
    public void IsAllowed_ItemUnderAllowedOnly_ReturnsTrue()
    {
        var scope = new LibraryRootScope(["/media/movies"], []);
        Assert.True(LibraryPathResolver.IsAllowed("/media/movies/film.mkv", scope));
    }

    [Fact]
    public void IsAllowed_ItemUnderNeither_ReturnsFalse()
    {
        var scope = new LibraryRootScope(["/media/movies"], ["/media/anime"]);
        Assert.False(LibraryPathResolver.IsAllowed("/media/home-videos/clip.mp4", scope));
    }

    [Fact]
    public void IsAllowed_ExcludedNestedUnderAllowed_DeniesNestedButKeepsSibling()
    {
        // Allowed "/media" with excluded "/media/anime": an item under the deeper excluded root is
        // denied even though it also sits under the allowed root, while a sibling under the allowed
        // root is kept. This is the leak the name-based skip alone could not close.
        var scope = new LibraryRootScope(["/media"], ["/media/anime"]);

        Assert.False(LibraryPathResolver.IsAllowed("/media/anime/show/ep.mkv", scope));
        Assert.True(LibraryPathResolver.IsAllowed("/media/movies/film.mkv", scope));
    }

    [Fact]
    public void IsAllowed_AllowedNestedUnderExcluded_KeepsNested()
    {
        // The inverse nesting: allowed "/media/anime" under an excluded "/media". The more specific
        // allowed root wins, so its own items are kept while the rest of the excluded tree is denied.
        var scope = new LibraryRootScope(["/media/anime"], ["/media"]);

        Assert.True(LibraryPathResolver.IsAllowed("/media/anime/show/ep.mkv", scope));
        Assert.False(LibraryPathResolver.IsAllowed("/media/movies/film.mkv", scope));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsAllowed_NullOrEmptyItemPath_ReturnsFalse(string? itemPath)
    {
        var scope = new LibraryRootScope(["/media/movies"], []);
        Assert.False(LibraryPathResolver.IsAllowed(itemPath, scope));
    }

    [Fact]
    public void IsAllowed_EmptyScope_ReturnsFalse()
    {
        var scope = new LibraryRootScope([], []);
        Assert.False(LibraryPathResolver.IsAllowed("/media/movies/film.mkv", scope));
    }

    [Fact]
    public void IsAllowed_NullScope_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LibraryPathResolver.IsAllowed("/media/movies/film.mkv", null!));
    }

    [Fact]
    public void IsAllowed_CasingFollowsPlatformConvention()
    {
        var scope = new LibraryRootScope(["/media/movies"], []);
        var result = LibraryPathResolver.IsAllowed("/media/MOVIES/film.mkv", scope);

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
    public void GetLibraryRootScope_PartitionsLocationsByNameExclusion()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(m => m.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Media", Locations = ["/media"] },
                new VirtualFolderInfo { Name = "Anime", Locations = ["/media/anime"] }
            ]);

        var scope = LibraryPathResolver.GetLibraryRootScope(
            libraryManager.Object,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Anime" });

        Assert.Equal(["/media"], scope.AllowedRoots);
        Assert.Equal(["/media/anime"], scope.ExcludedRoots);
    }

    [Fact]
    public void GetLibraryRootScope_NullLibraryManager_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => LibraryPathResolver.GetLibraryRootScope(null!, new HashSet<string>()));
    }
}
