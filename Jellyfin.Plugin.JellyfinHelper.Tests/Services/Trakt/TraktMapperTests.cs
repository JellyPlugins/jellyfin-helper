using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    [Trait("Category", "Security")]
    public void MapMediaItems_Slug_Trimmed_NullWhenBlank()
    {
        var withSpaces = new TraktMediaItem { Title = "T", Ids = new TraktIds { Tmdb = 1, Slug = "  my-slug  " } };
        var withBlank = new TraktMediaItem { Title = "T", Ids = new TraktIds { Tmdb = 2, Slug = "   " } };

        var mapped = TraktMapper.MapMediaItems([withSpaces, withBlank], "movie", out _);

        Assert.Equal("my-slug", mapped[0].TraktSlug);
        Assert.Null(mapped[1].TraktSlug);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MapMediaItems_GenreIds_Empty_FailClosedForUnenriched()
    {
        // Trakt candidates start unenriched (no genres). Downstream must enrich before the
        // parental filter; an empty genre list here is the fail-closed signal, not an allow.
        var mapped = TraktMapper.MapMediaItems([Media(100)], "movie", out _);

        var c = Assert.Single(mapped);
        Assert.NotNull(c.GenreIds);
        Assert.Empty(c.GenreIds);
        Assert.False(c.Adult);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MapMediaItems_NullAndZeroTmdb_DroppedAndCounted()
    {
        var items = new List<TraktMediaItem> { Media(0), Media(null), null! };

        var mapped = TraktMapper.MapMediaItems(items, "movie", out var dropped);

        Assert.Empty(mapped);
        Assert.Equal(3, dropped);
    }

    [Fact]
    public void MapMediaItems_AssignsSourceRankInListOrder()
    {
        // The incoming list order is Trakt's own ranking; it must be carried as SourceRank 0,1,2...
        var mapped = TraktMapper.MapMediaItems([Media(100), Media(200), Media(300)], "movie", out _);

        Assert.Equal([0, 1, 2], mapped.Select(m => m.SourceRank).ToList());
    }

    [Fact]
    public void MapMediaItems_DroppedItem_DoesNotShiftLaterRanks()
    {
        // The index spans dropped items, so a kept item keeps the rank matching its true Trakt position.
        var mapped = TraktMapper.MapMediaItems([Media(100), Media(null), Media(300)], "movie", out _);

        Assert.Equal(100, mapped[0].TmdbId);
        Assert.Equal(0, mapped[0].SourceRank);
        Assert.Equal(300, mapped[1].TmdbId);
        Assert.Equal(2, mapped[1].SourceRank);
    }

    [Fact]
    public void MapMediaItems_RankOffset_ContinuesNumbering()
    {
        // Shows mapped after movies continue the ranking from the movies' count.
        var mapped = TraktMapper.MapMediaItems([Media(100), Media(200)], "tv", out _, rankOffset: 5);

        Assert.Equal([5, 6], mapped.Select(m => m.SourceRank).ToList());
    }

    [Fact]
    public void MapTrendingItems_AssignsSourceRankWithOffset()
    {
        var items = new List<TraktTrendingItem>
        {
            new() { Watchers = 50, Movie = Media(100) },
            new() { Watchers = 10, Movie = Media(200) },
        };

        var mapped = TraktMapper.MapTrendingItems(items, "movie", out _, rankOffset: 3);

        Assert.Equal([3, 4], mapped.Select(m => m.SourceRank).ToList());
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("../evil")]
    [InlineData("java script:")]
    [InlineData("Upper-Case")]
    [InlineData("has_underscore")]
    public void MapMediaItems_IllegalSlugChars_MapToNull(string slug)
    {
        // The whitelist is ^[a-z0-9-]+$, so a slug carrying traversal, scheme, uppercase or any other
        // byte must drop to null rather than reach the frontend deep-link that concatenates it.
        var item = Media(100);
        item.Ids!.Slug = slug;

        var c = Assert.Single(TraktMapper.MapMediaItems([item], "movie", out _));

        Assert.Null(c.TraktSlug);
    }

    [Fact]
    public void MapMediaItems_CleanSlug_IsPreserved()
    {
        var item = Media(100);
        item.Ids!.Slug = "the-matrix-1999";

        var c = Assert.Single(TraktMapper.MapMediaItems([item], "movie", out _));

        Assert.Equal("the-matrix-1999", c.TraktSlug);
    }

    [Theory]
    [InlineData(-5d, 0d)]
    [InlineData(50d, 10d)]
    public void MapMediaItems_RatingOutOfRange_ClampsToZeroTen(double rating, double expected)
    {
        // A malformed Trakt rating must clamp into the 0-10 band before it reaches the scorer.
        var c = Assert.Single(TraktMapper.MapMediaItems([Media(100, rating: rating)], "movie", out _));

        Assert.Equal(expected, c.VoteAverage);
    }
}
