using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Reverse-lookup contract for TmdbGenreMap: mapping Jellyfin genre strings back to TMDb movie/TV genre IDs.
/// </summary>
public class TmdbGenreMapReverseLookupTests
{
    [Theory]
    [InlineData("Action", 28)]
    [InlineData("Science Fiction", 878)]
    [InlineData("action", 28)] // OrdinalIgnoreCase: case must not affect the result
    [InlineData("SCIENCE FICTION", 878)]
    public void ToMovieTmdbId_KnownGenre_ReturnsMappedMovieId(string genre, int expected)
    {
        Assert.Equal(expected, TmdbGenreMap.ToMovieTmdbId(genre));
    }

    [Theory]
    [InlineData("Sci-Fi")]
    [InlineData("SciFi")]
    public void ToMovieTmdbId_SciFiAlias_MapsToScienceFiction(string alias)
    {
        // Jellyfin frequently stores these shorthand forms; both must resolve to Science Fiction.
        Assert.Equal(878, TmdbGenreMap.ToMovieTmdbId(alias));
    }

    [Fact]
    public void ToMovieTmdbId_UnmappedGenre_ReturnsNull()
    {
        Assert.Null(TmdbGenreMap.ToMovieTmdbId("Nonexistent"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToMovieTmdbId_NullOrWhitespace_ReturnsNull(string? genre)
    {
        Assert.Null(TmdbGenreMap.ToMovieTmdbId(genre!));
    }

    [Theory]
    [InlineData("Comedy", 35)]
    [InlineData("Sci-Fi & Fantasy", 10765)]
    [InlineData("comedy", 35)] // OrdinalIgnoreCase
    [InlineData("sci-fi & fantasy", 10765)]
    public void ToTvTmdbId_KnownGenre_ReturnsMappedTvId(string genre, int expected)
    {
        Assert.Equal(expected, TmdbGenreMap.ToTvTmdbId(genre));
    }

    [Theory]
    [InlineData("Action", 10759)]
    [InlineData("Adventure", 10759)]
    [InlineData("Science Fiction", 10765)]
    [InlineData("Sci-Fi", 10765)]
    public void ToTvTmdbId_ActionAndAdventureAliases_MapToCombinedTvGenre(string alias, int expected)
    {
        // Jellyfin stores Action/Adventure and Science Fiction separately; the map folds them onto
        // TMDb's combined TV genres so preference-driven TV discovery still returns results.
        Assert.Equal(expected, TmdbGenreMap.ToTvTmdbId(alias));
    }

    [Fact]
    public void ToTvTmdbId_UnmappedGenre_ReturnsNull()
    {
        Assert.Null(TmdbGenreMap.ToTvTmdbId("Nonexistent"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToTvTmdbId_NullOrWhitespace_ReturnsNull(string? genre)
    {
        Assert.Null(TmdbGenreMap.ToTvTmdbId(genre!));
    }
}
