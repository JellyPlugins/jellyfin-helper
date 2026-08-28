using System;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for the TmdbDiscoverItem DTO - the parsing surface between Seerr/TMDb JSON payloads and the recommendation engine.
/// </summary>
public sealed class TmdbDiscoverItemTests
{
    // GenreIds setter null-coalesces to empty list - TMDb sometimes emits null here.
    [Fact]
    public void GenreIds_SetToNull_ReturnsEmptyList()
    {
        var item = new TmdbDiscoverItem { GenreIds = null! };
        Assert.NotNull(item.GenreIds);
        Assert.Empty(item.GenreIds);
    }

    [Fact]
    public void GenreIds_SetToPopulatedList_RoundTrips()
    {
        var item = new TmdbDiscoverItem { GenreIds = [28, 12, 16] };
        Assert.Equal(3, item.GenreIds.Count);
        Assert.Contains(28, item.GenreIds);
    }

    [Fact]
    public void GenreIds_DefaultInitializedToEmpty()
    {
        var item = new TmdbDiscoverItem();
        Assert.NotNull(item.GenreIds);
        Assert.Empty(item.GenreIds);
    }

    // DisplayTitle - prefers Title over Name over "Unknown".
    [Fact]
    public void DisplayTitle_TitlePresent_ReturnsTitle()
    {
        var item = new TmdbDiscoverItem { Title = "Inception", Name = "TVShowName" };
        Assert.Equal("Inception", item.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_TitleNull_ReturnsName()
    {
        // BUG GUARD: TV shows have Name but no Title.
        var item = new TmdbDiscoverItem { Title = null, Name = "Breaking Bad" };
        Assert.Equal("Breaking Bad", item.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_BothNull_ReturnsUnknown()
    {
        // BUG GUARD: rare but real - TMDb occasionally returns entries with neither
        // Title nor Name. The "Unknown" fallback prevents null-render exceptions.
        var item = new TmdbDiscoverItem { Title = null, Name = null };
        Assert.Equal("Unknown", item.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_TitleEmptyString_ReturnsEmptyStringNotName()
    {
        // The fallback only kicks in on NULL, not on empty string - a future change
        // to `?? "" ?? Name` would silently rename TV shows.
        var item = new TmdbDiscoverItem { Title = string.Empty, Name = "Fallback Name" };
        Assert.Equal(string.Empty, item.DisplayTitle);
    }

    // EffectiveReleaseDate - prefers ReleaseDate over FirstAirDate.
    [Fact]
    public void EffectiveReleaseDate_ReleaseDatePresent_ReturnsReleaseDate()
    {
        var releaseDate = new DateTime(2010, 7, 16, 0, 0, 0, DateTimeKind.Utc);
        var firstAirDate = new DateTime(2008, 1, 20, 0, 0, 0, DateTimeKind.Utc);
        var item = new TmdbDiscoverItem { ReleaseDate = releaseDate, FirstAirDate = firstAirDate };
        Assert.Equal(releaseDate, item.EffectiveReleaseDate);
    }

    [Fact]
    public void EffectiveReleaseDate_ReleaseDateNull_ReturnsFirstAirDate()
    {
        // BUG GUARD: TV series only have FirstAirDate - recency scoring would break
        // for every TV candidate if this fallback disappeared.
        var firstAirDate = new DateTime(2008, 1, 20, 0, 0, 0, DateTimeKind.Utc);
        var item = new TmdbDiscoverItem { ReleaseDate = null, FirstAirDate = firstAirDate };
        Assert.Equal(firstAirDate, item.EffectiveReleaseDate);
    }

    [Fact]
    public void EffectiveReleaseDate_BothNull_ReturnsNull()
    {
        // Consumers must handle null EffectiveReleaseDate gracefully - silently synthesising
        // e.g. DateTime.MinValue would corrupt the recency-score ranking.
        var item = new TmdbDiscoverItem { ReleaseDate = null, FirstAirDate = null };
        Assert.Null(item.EffectiveReleaseDate);
    }

    // Defaults - sanity guards.
    [Fact]
    public void Defaults_MediaTypeIsMovie()
    {
        var item = new TmdbDiscoverItem();
        Assert.Equal("movie", item.MediaType);
    }

    [Fact]
    public void Defaults_KnownPeopleIsNull()
    {
        // KnownPeople is not populated from /discover - only from /search or /credits.
        // Default of null lets consumer code distinguish "not enriched yet" vs "enriched but empty".
        var item = new TmdbDiscoverItem();
        Assert.Null(item.KnownPeople);
    }

    [Fact]
    public void Defaults_AdultIsFalse()
    {
        // BUG GUARD: default false is the safe posture - TMDb must EXPLICITLY opt in.
        var item = new TmdbDiscoverItem();
        Assert.False(item.Adult);
    }

    // JSON round-trip - the real integration surface.
    [Fact]
    public void JsonDeserialize_EmptyReleaseDateString_HandledGracefully()
    {
        // BUG GUARD: TMDb sends releaseDate="" for unreleased items. Without
        // NullableDateTimeConverter, System.Text.Json throws and the entire batch dies.
        var json = "{\"id\":12345,\"mediaType\":\"movie\",\"title\":\"Unreleased\",\"releaseDate\":\"\"}";
        var result = JsonSerializer.Deserialize<TmdbDiscoverItem>(json);
        Assert.NotNull(result);
        Assert.Equal(12345, result!.Id);
        Assert.Null(result.ReleaseDate);
    }

    [Fact]
    public void JsonDeserialize_MissingGenreIds_YieldsEmptyList()
    {
        var json = "{\"id\":42,\"mediaType\":\"movie\",\"title\":\"No Genres\"}";
        var result = JsonSerializer.Deserialize<TmdbDiscoverItem>(json);
        Assert.NotNull(result);
        Assert.NotNull(result!.GenreIds);
        Assert.Empty(result.GenreIds);
    }

    [Fact]
    public void JsonDeserialize_PopulatedGenreIds_RoundTrips()
    {
        var json = "{\"id\":550,\"mediaType\":\"movie\",\"title\":\"Fight Club\",\"genreIds\":[18,53]}";
        var result = JsonSerializer.Deserialize<TmdbDiscoverItem>(json);
        Assert.NotNull(result);
        Assert.Equal(2, result!.GenreIds.Count);
        Assert.Contains(18, result.GenreIds);
        Assert.Contains(53, result.GenreIds);
    }

    [Fact]
    public void JsonDeserialize_TvItem_UsesFirstAirDate()
    {
        // TV items use firstAirDate not releaseDate. EffectiveReleaseDate must resolve
        // to firstAirDate for these so recency scoring produces a real signal.
        var json = "{\"id\":1396,\"mediaType\":\"tv\",\"name\":\"Breaking Bad\",\"firstAirDate\":\"2008-01-20\"}";
        var result = JsonSerializer.Deserialize<TmdbDiscoverItem>(json);
        Assert.NotNull(result);
        Assert.Equal("Breaking Bad", result!.DisplayTitle);
        Assert.NotNull(result.EffectiveReleaseDate);
        Assert.Equal(2008, result.EffectiveReleaseDate!.Value.Year);
    }

    [Fact]
    public void JsonDeserialize_PosterPath_RoundTrips()
    {
        // The poster path is a CDN-relative segment the UI concatenates onto the image
        // base URL; any mangling would break every artwork render downstream.
        var json = "{\"id\":603,\"mediaType\":\"movie\",\"title\":\"The Matrix\",\"posterPath\":\"/abc123.jpg\"}";
        var result = JsonSerializer.Deserialize<TmdbDiscoverItem>(json);
        Assert.NotNull(result);
        Assert.Equal("/abc123.jpg", result!.PosterPath);
    }

    [Fact]
    public void JsonDeserialize_Overview_RoundTrips()
    {
        var json = "{\"id\":603,\"mediaType\":\"movie\",\"title\":\"The Matrix\",\"overview\":\"A hacker learns the truth about his reality.\"}";
        var result = JsonSerializer.Deserialize<TmdbDiscoverItem>(json);
        Assert.NotNull(result);
        Assert.Equal("A hacker learns the truth about his reality.", result!.Overview);
    }

    [Fact]
    public void JsonDeserialize_MissingPosterAndOverview_YieldNull()
    {
        // Both fields are optional on TMDb; they must default to null (not empty string)
        // so consumers can distinguish "absent" from "present but blank".
        var json = "{\"id\":603,\"mediaType\":\"movie\",\"title\":\"The Matrix\"}";
        var result = JsonSerializer.Deserialize<TmdbDiscoverItem>(json);
        Assert.NotNull(result);
        Assert.Null(result!.PosterPath);
        Assert.Null(result.Overview);
    }
}