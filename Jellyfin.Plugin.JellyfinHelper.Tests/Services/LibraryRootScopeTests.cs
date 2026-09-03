using System;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Covers the <see cref="LibraryRootScope"/> carrier: it stores the supplied allowed and excluded
///     roots verbatim and rejects null constructor arguments.
/// </summary>
public sealed class LibraryRootScopeTests
{
    [Fact]
    public void Constructor_StoresAllowedAndExcludedRoots()
    {
        var scope = new LibraryRootScope(["/media/movies"], ["/media/anime"]);

        Assert.Equal(["/media/movies"], scope.AllowedRoots);
        Assert.Equal(["/media/anime"], scope.ExcludedRoots);
    }

    [Fact]
    public void Constructor_NullAllowedRoots_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LibraryRootScope(null!, []));
    }

    [Fact]
    public void Constructor_NullExcludedRoots_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LibraryRootScope([], null!));
    }

    [Fact]
    public void Constructor_EmptyLists_ExposesEmptyCollections()
    {
        var scope = new LibraryRootScope([], []);

        Assert.Empty(scope.AllowedRoots);
        Assert.Empty(scope.ExcludedRoots);
    }
}
