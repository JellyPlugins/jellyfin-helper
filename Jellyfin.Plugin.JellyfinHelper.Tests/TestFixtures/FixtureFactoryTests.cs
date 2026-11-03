using System.Text;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

public sealed class FixtureFactoryTests
{
    [Theory]
    [InlineData(CollectionTypeOptions.movies)]
    [InlineData(CollectionTypeOptions.tvshows)]
    [InlineData(CollectionTypeOptions.music)]
    public void CreateTypedLibrary_NullLocations_ThrowsArgumentNullException(CollectionTypeOptions type)
    {
        var exception = Assert.Throws<ArgumentNullException>(() => CreateTypedLibrary(type, null!));

        Assert.Equal("locations", exception.ParamName);
    }

    [Theory]
    [InlineData(CollectionTypeOptions.movies, "Movies", "movies")]
    [InlineData(CollectionTypeOptions.tvshows, "TV Shows", "tv")]
    [InlineData(CollectionTypeOptions.music, "Music", "music")]
    public void CreateTypedLibrary_EmptyLocations_PreservesDefaults(
        CollectionTypeOptions type, string expectedName, string expectedFolder)
    {
        var library = CreateTypedLibrary(type, []);

        Assert.Equal(type, library.CollectionType);
        Assert.Equal(expectedName, library.Name);
        Assert.Equal(TestDataGenerator.TestPath("media", expectedFolder), Assert.Single(library.Locations));
    }

    [Theory]
    [InlineData(CollectionTypeOptions.movies)]
    [InlineData(CollectionTypeOptions.tvshows)]
    [InlineData(CollectionTypeOptions.music)]
    public void CreateTypedLibrary_CustomLocations_PreservesArrayAndName(CollectionTypeOptions type)
    {
        string[] locations = [TestDataGenerator.TestPath("first"), TestDataGenerator.TestPath("second")];
        var library = type switch
        {
            CollectionTypeOptions.movies => TestDataGenerator.CreateMovieLibrary("Custom", locations),
            CollectionTypeOptions.tvshows => TestDataGenerator.CreateTvShowLibrary("Custom", locations),
            CollectionTypeOptions.music => TestDataGenerator.CreateMusicLibrary("Custom", locations),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        Assert.Equal("Custom", library.Name);
        Assert.Equal(type, library.CollectionType);
        Assert.Same(locations, library.Locations);
    }

    [Fact]
    public void AddJsonBodyToController_NullController_ThrowsBeforeReadingBody()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => ControllerTestFactory.AddJsonBodyToController(null!, null!, requestAborted: TestContext.Current.CancellationToken));

        Assert.Equal("controller", exception.ParamName);
    }

    [Fact]
    public async Task AddJsonBodyToController_DefaultLength_PreservesUtf8BodyAndController()
    {
        var controller = new TestController();
        const string json = "{\"name\":\"caf\u00e9\"}";

        var result = ControllerTestFactory.AddJsonBodyToController(controller, json, requestAborted: TestContext.Current.CancellationToken);
        using var body = controller.Request.Body;
        using var reader = new StreamReader(body, Encoding.UTF8);

        Assert.Same(controller, result);
        Assert.Equal("application/json", controller.Request.ContentType);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), controller.Request.ContentLength);
        Assert.Equal(json, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AddJsonBodyToController_ExplicitLengthAndCancellation_PreservesOverrides()
    {
        var controller = new TestController();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        ControllerTestFactory.AddJsonBodyToController(controller, "{}", 100, cancellation.Token);
        using var body = controller.Request.Body;

        Assert.Equal(100, controller.Request.ContentLength);
        Assert.Equal(cancellation.Token, controller.HttpContext.RequestAborted);
    }

    private static VirtualFolderInfo CreateTypedLibrary(CollectionTypeOptions type, string[] locations)
        => type switch
        {
            CollectionTypeOptions.movies => TestDataGenerator.CreateMovieLibrary(locations: locations),
            CollectionTypeOptions.tvshows => TestDataGenerator.CreateTvShowLibrary(locations: locations),
            CollectionTypeOptions.music => TestDataGenerator.CreateMusicLibrary(locations: locations),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

    private sealed class TestController : ControllerBase
    {
    }
}
