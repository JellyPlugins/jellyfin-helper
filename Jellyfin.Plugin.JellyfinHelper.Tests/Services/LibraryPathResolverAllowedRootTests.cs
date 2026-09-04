using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Covers the scoped <see cref="LibraryPathResolver.IsAllowed(string?, LibraryRootScope)"/>
///     boundary/platform behavior and <see cref="LibraryPathResolver.GetLibraryRootScope"/>
///     partitioning, including nested-exclusion handling.
/// </summary>
public sealed class LibraryPathResolverAllowedRootTests
{
    // Allowed-only scope helper: the boundary cases below do not involve exclusions, so they use a
    // scope whose excluded set is empty and assert the allowed-root matching directly.
    private static LibraryRootScope Allow(params string[] allowedRoots) => new(allowedRoots, []);

    [Fact]
    public void IsAllowed_ChildOfRoot_ReturnsTrue()
    {
        Assert.True(LibraryPathResolver.IsAllowed("/media/movies/Film (2020)/film.mkv", Allow("/media/movies")));
    }

    [Fact]
    public void IsAllowed_ExactRoot_ReturnsTrue()
    {
        Assert.True(LibraryPathResolver.IsAllowed("/media/movies", Allow("/media/movies")));
    }

    [Fact]
    public void IsAllowed_SiblingPrefix_ReturnsFalse()
    {
        // /media/movies must not match /media/movies2 — the directory boundary guards this.
        Assert.False(LibraryPathResolver.IsAllowed("/media/movies2/film.mkv", Allow("/media/movies")));
    }

    [Fact]
    public void IsAllowed_TrailingSeparatorOnRoot_StillMatches()
    {
        Assert.True(LibraryPathResolver.IsAllowed("/media/movies/film.mkv", Allow("/media/movies/")));
    }

    [Fact]
    public void IsAllowed_MixedSeparators_Normalized()
    {
        Assert.True(LibraryPathResolver.IsAllowed(@"\media\movies\film.mkv", Allow("/media/movies")));
    }

    [Fact]
    public void IsAllowed_EmptyRootEntry_Skipped()
    {
        // A blank root entry must not act as a wildcard that matches everything.
        Assert.False(LibraryPathResolver.IsAllowed("/media/movies/film.mkv", Allow(string.Empty)));
    }

    [Fact]
    public void IsAllowed_FilesystemRootAllowed_ChildMatches()
    {
        // A virtual folder rooted at "/" must treat every path as a descendant; the child prefix
        // collapses to "/" itself rather than "//", which would reject everything.
        Assert.True(LibraryPathResolver.IsAllowed("/media/movies/film.mkv", Allow("/")));
    }

    [Fact]
    public void IsAllowed_FilesystemRootAllowed_RootItselfMatches()
    {
        Assert.True(LibraryPathResolver.IsAllowed("/", Allow("/")));
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
    public void GetLibraryRootScope_CaseSensitiveExclusionSet_StillMatchesCaseInsensitively()
    {
        // The XML contract promises a case-insensitive name match. A caller passing a default
        // (ordinal, case-sensitive) set with "anime" must still exclude a folder named "Anime";
        // GetLibraryRootScope rebuilds an ordinal-ignore-case lookup internally to guarantee this.
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
            new HashSet<string>(StringComparer.Ordinal) { "anime" });

        Assert.Equal(["/media"], scope.AllowedRoots);
        Assert.Equal(["/media/anime"], scope.ExcludedRoots);
    }

    [Fact]
    public void GetLibraryRootScope_NullLibraryManager_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => LibraryPathResolver.GetLibraryRootScope(null!, new HashSet<string>()));
    }

    [Fact]
    public void GetLibraryRootScope_TrailingSeparatorDuplicate_CollapsedToOneRoot()
    {
        // "/media/movies" and "/media/movies/" normalize to the same path, so they must not each
        // occupy a slot. The first spelling is kept.
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(m => m.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Movies", Locations = ["/media/movies", "/media/movies/"] }
            ]);

        var scope = LibraryPathResolver.GetLibraryRootScope(
            libraryManager.Object,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["/media/movies"], scope.AllowedRoots);
    }

    [Fact]
    public void GetAllowedLibraryRootIds_NoExclusions_ReturnsEmpty()
    {
        // An empty exclusion set means nothing is scoped out, so callers should leave their query
        // unrestricted rather than filtering to a subset of roots.
        var libraryManager = new Mock<ILibraryManager>();

        var ids = LibraryPathResolver.GetAllowedLibraryRootIds(
            libraryManager.Object,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Empty(ids);
        libraryManager.Verify(m => m.GetVirtualFolders(), Times.Never);
    }

    [Fact]
    public void GetAllowedLibraryRootIds_WithExclusion_ReturnsOnlyAllowedRootIds()
    {
        var allowedId = Guid.NewGuid();
        var excludedId = Guid.NewGuid();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(m => m.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Movies", ItemId = allowedId.ToString("N"), Locations = ["/media/movies"] },
                new VirtualFolderInfo { Name = "Anime", ItemId = excludedId.ToString("N"), Locations = ["/media/anime"] }
            ]);

        var ids = LibraryPathResolver.GetAllowedLibraryRootIds(
            libraryManager.Object,
            new HashSet<string>(StringComparer.Ordinal) { "anime" });

        Assert.Equal([allowedId], ids);
    }

    [Fact]
    public void GetAllowedLibraryRootIds_UnparsableOrEmptyItemId_Skipped()
    {
        // A virtual folder whose item id is missing or malformed cannot scope a query, so it is left
        // out rather than silently widening the query.
        var allowedId = Guid.NewGuid();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(m => m.GetVirtualFolders())
            .Returns(
            [
                new VirtualFolderInfo { Name = "Movies", ItemId = allowedId.ToString("N"), Locations = ["/media/movies"] },
                new VirtualFolderInfo { Name = "Broken", ItemId = "not-a-guid", Locations = ["/media/broken"] },
                new VirtualFolderInfo { Name = "NoId", ItemId = null, Locations = ["/media/noid"] },
                new VirtualFolderInfo { Name = "Anime", ItemId = Guid.NewGuid().ToString("N"), Locations = ["/media/anime"] }
            ]);

        var ids = LibraryPathResolver.GetAllowedLibraryRootIds(
            libraryManager.Object,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Anime" });

        Assert.Equal([allowedId], ids);
    }

    [Fact]
    public void GetAllowedLibraryRootIds_NullLibraryManager_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => LibraryPathResolver.GetAllowedLibraryRootIds(
                null!,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Anime" }));
    }
}
