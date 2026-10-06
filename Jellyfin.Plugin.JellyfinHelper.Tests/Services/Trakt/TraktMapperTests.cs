using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Trakt;

/// <summary>
///     Verifies Trakt-to-candidate mapping: TMDb id is required (missing ones are dropped and counted),
///     media type is normalized, trending entries unwrap the correct nested member, and the rating carries.
/// </summary>
public sealed class TraktMapperTests
{
    private static TraktMediaItem Media(int? tmdb, string title = "T", double? rating = 8.0) => new()
    {
        Title = title,
        Year = 2021,
        Overview = "o",
        Rating = rating,
        Ids = tmdb.HasValue ? new TraktIds { Tmdb = tmdb, Slug = "slug", Trakt = 1 } : new TraktIds(),
    };

    [Fact]
    public void MapMediaItems_DropsItemsWithoutTmdbId()
    {
        var items = new List<TraktMediaItem> { Media(100), Media(null), Media(200) };

        var mapped = TraktMapper.MapMediaItems(items, "movie", out var dropped);

        Assert.Equal(1, dropped);
        Assert.Equal(2, mapped.Count);
        Assert.Equal(100, mapped[0].TmdbId);
        Assert.Equal(200, mapped[1].TmdbId);
    }

    [Fact]
    public void MapMediaItems_NormalizesMediaTypeAndCarriesFields()
    {
        var mapped = TraktMapper.MapMediaItems([Media(100, "Title", 7.5)], "TV", out _);

        var c = Assert.Single(mapped);
        Assert.Equal("tv", c.MediaType);
        Assert.Equal("Title", c.Title);
        Assert.Equal(2021, c.Year);
        Assert.Equal(7.5, c.VoteAverage);
        Assert.Equal("slug", c.TraktSlug);
    }

    [Fact]
    public void MapMediaItems_NullsBlankSlug()
    {
        var item = Media(100);
        item.Ids!.Slug = "  ";

        var c = Assert.Single(TraktMapper.MapMediaItems([item], "movie", out _));

        Assert.Null(c.TraktSlug);
    }

    [Fact]
    public void MapMediaItems_NullList_ReturnsEmpty()
    {
        var mapped = TraktMapper.MapMediaItems(null, "movie", out var dropped);
        Assert.Empty(mapped);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void MapMediaItems_MissingRating_DefaultsToZero()
    {
        var mapped = TraktMapper.MapMediaItems([Media(100, rating: null)], "movie", out _);
        Assert.Equal(0, Assert.Single(mapped).VoteAverage);
    }

    [Fact]
    public void MapTrendingItems_Movies_UnwrapsMovieMember()
    {
        var items = new List<TraktTrendingItem>
        {
            new() { Watchers = 50, Movie = Media(100) },
            new() { Watchers = 10, Movie = Media(null) },
        };

        var mapped = TraktMapper.MapTrendingItems(items, "movie", out var dropped);

        Assert.Equal(1, dropped);
        Assert.Equal(100, Assert.Single(mapped).TmdbId);
    }

    [Fact]
    public void MapTrendingItems_Shows_UnwrapsShowMember()
    {
        var items = new List<TraktTrendingItem>
        {
            new() { Watchers = 50, Show = Media(300) },
        };

        var mapped = TraktMapper.MapTrendingItems(items, "tv", out _);

        var c = Assert.Single(mapped);
        Assert.Equal(300, c.TmdbId);
        Assert.Equal("tv", c.MediaType);
    }

    [Fact]
    public void MapTrendingItems_WrongMemberForType_IsDropped()
    {
        // A /movies/trending entry asked for as "tv" has no Show, so it drops.
        var items = new List<TraktTrendingItem> { new() { Watchers = 5, Movie = Media(100) } };

        var mapped = TraktMapper.MapTrendingItems(items, "tv", out var dropped);

        Assert.Empty(mapped);
        Assert.Equal(1, dropped);
    }
}
