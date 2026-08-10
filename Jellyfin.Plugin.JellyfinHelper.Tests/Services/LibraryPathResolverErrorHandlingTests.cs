using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Covers how <see cref="LibraryPathResolver"/> reacts when a location cannot be canonicalized.
/// </summary>
public sealed class LibraryPathResolverErrorHandlingTests
{
    [Fact]
    public void GetDistinctLibraryLocations_LocationCannotBeFullyQualified_FallsBackToOriginalPath()
    {
        // An embedded null makes Path.GetFullPath throw on every OS; the resolver must pass the
        // location through untouched rather than dropping or mangling it.
        var badPath = "/media/mov\0ies";
        var mock = TestMockFactory.CreateLibraryManager();
        mock.Setup(lm => lm.GetVirtualFolders())
            .Returns([new VirtualFolderInfo
            {
                Name = "Movies", CollectionType = CollectionTypeOptions.movies, Locations = [badPath]
            }]);

        var result = LibraryPathResolver.GetDistinctLibraryLocations(mock.Object);

        Assert.Contains(badPath, result);
    }
}
