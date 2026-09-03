using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Covers <see cref="TmdbLibraryMapper"/> TMDb-id extraction and media-type keying.
/// </summary>
public sealed class TmdbLibraryMapperTests
{
    private static Movie MovieWithTmdb(string? tmdb)
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        if (tmdb is not null)
        {
            movie.ProviderIds["Tmdb"] = tmdb;
        }

        return movie;
    }

    private static Series SeriesWithTmdb(string? tmdb)
    {
        var series = new Series { Id = Guid.NewGuid() };
        if (tmdb is not null)
        {
            series.ProviderIds["Tmdb"] = tmdb;
        }

        return series;
    }

    [Fact]
    public void BuildTmdbKeySet_MovieAndSeries_KeyedByMediaType()
    {
        var items = new List<BaseItem> { MovieWithTmdb("550"), SeriesWithTmdb("1399") };

        var set = TmdbLibraryMapper.BuildTmdbKeySet(items);

        Assert.Contains((550, "movie"), set);
        Assert.Contains((1399, "tv"), set);
    }

    [Fact]
    public void BuildTmdbKeySet_ItemWithoutTmdb_Ignored()
    {
        var set = TmdbLibraryMapper.BuildTmdbKeySet([MovieWithTmdb(null)]);

        Assert.Empty(set);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("notanumber")]
    public void BuildTmdbKeySet_InvalidOrNonPositiveTmdb_Ignored(string tmdb)
    {
        var set = TmdbLibraryMapper.BuildTmdbKeySet([MovieWithTmdb(tmdb)]);

        Assert.Empty(set);
    }

    [Fact]
    public void BuildTmdbKeySet_DuplicateSameKey_Deduplicated()
    {
        var set = TmdbLibraryMapper.BuildTmdbKeySet([MovieWithTmdb("550"), MovieWithTmdb("550")]);

        Assert.Single(set);
    }

    [Fact]
    public void BuildTmdbKeySet_EmptyInput_EmptySet()
    {
        Assert.Empty(TmdbLibraryMapper.BuildTmdbKeySet([]));
    }

    [Fact]
    public void BuildTmdbKeySet_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TmdbLibraryMapper.BuildTmdbKeySet(null!));
    }

    [Fact]
    public void TryGetTmdbId_ValidId_ReturnsTrue()
    {
        Assert.True(TmdbLibraryMapper.TryGetTmdbId(MovieWithTmdb("603"), out var id));
        Assert.Equal(603, id);
    }

    [Fact]
    public void TryGetTmdbId_Missing_ReturnsFalseAndZero()
    {
        Assert.False(TmdbLibraryMapper.TryGetTmdbId(MovieWithTmdb(null), out var id));
        Assert.Equal(0, id);
    }

    [Fact]
    public void TryGetTmdbId_NullItem_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TmdbLibraryMapper.TryGetTmdbId(null!, out _));
    }
}
