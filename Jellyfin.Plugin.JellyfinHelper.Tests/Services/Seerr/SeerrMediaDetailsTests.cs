using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr;

/// <summary>
///     Unit tests for <see cref="SeerrMediaDetails" />.
/// </summary>
public class SeerrMediaDetailsTests
{
    // ===== DisplayTitle Logic =====

    [Fact]
    public void DisplayTitle_OnlyTitle_ReturnsTitle()
    {
        var details = new SeerrMediaDetails { Title = "The Matrix", Name = null };
        Assert.Equal("The Matrix", details.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_OnlyName_ReturnsName()
    {
        var details = new SeerrMediaDetails { Title = null, Name = "Breaking Bad" };
        Assert.Equal("Breaking Bad", details.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_BothTitleAndName_PrefersTitleOverName()
    {
        var details = new SeerrMediaDetails { Title = "Movie Title", Name = "TV Name" };
        Assert.Equal("Movie Title", details.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_BothNull_ReturnsUnknown()
    {
        var details = new SeerrMediaDetails { Title = null, Name = null };
        Assert.Equal("Unknown", details.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_EmptyTitle_FallsBackToName()
    {
        var details = new SeerrMediaDetails { Title = "", Name = "Fallback Show" };
        Assert.Equal("Fallback Show", details.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_WhitespaceTitle_FallsBackToName()
    {
        var details = new SeerrMediaDetails { Title = "   ", Name = "Real Show" };
        Assert.Equal("Real Show", details.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_EmptyName_FallsBackToUnknown()
    {
        var details = new SeerrMediaDetails { Title = null, Name = "" };
        Assert.Equal("Unknown", details.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_WhitespaceName_FallsBackToUnknown()
    {
        var details = new SeerrMediaDetails { Title = null, Name = "   " };
        Assert.Equal("Unknown", details.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_BothEmpty_ReturnsUnknown()
    {
        var details = new SeerrMediaDetails { Title = "", Name = "" };
        Assert.Equal("Unknown", details.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_BothWhitespace_ReturnsUnknown()
    {
        var details = new SeerrMediaDetails { Title = "  ", Name = "  " };
        Assert.Equal("Unknown", details.DisplayTitle);
    }

    // ===== Default Property Values =====

    [Fact]
    public void DefaultValues_TitleIsNull()
    {
        var details = new SeerrMediaDetails();
        Assert.Null(details.Title);
    }

    [Fact]
    public void DefaultValues_NameIsNull()
    {
        var details = new SeerrMediaDetails();
        Assert.Null(details.Name);
    }

    [Fact]
    public void DefaultValues_DisplayTitleIsUnknown()
    {
        var details = new SeerrMediaDetails();
        Assert.Equal("Unknown", details.DisplayTitle);
    }

    // ===== JSON Deserialization =====

    [Fact]
    public void Deserialize_MovieResponse_ParsesTitleCorrectly()
    {
        var json = """{"title":"Inception","name":null}""";
        var details = JsonSerializer.Deserialize<SeerrMediaDetails>(json);

        Assert.NotNull(details);
        Assert.Equal("Inception", details!.Title);
        Assert.Null(details.Name);
        Assert.Equal("Inception", details.DisplayTitle);
    }

    [Fact]
    public void Deserialize_TvResponse_ParsesNameCorrectly()
    {
        var json = """{"title":null,"name":"The Wire"}""";
        var details = JsonSerializer.Deserialize<SeerrMediaDetails>(json);

        Assert.NotNull(details);
        Assert.Null(details!.Title);
        Assert.Equal("The Wire", details.Name);
        Assert.Equal("The Wire", details.DisplayTitle);
    }

    [Fact]
    public void Deserialize_BothFields_ParsesCorrectly()
    {
        var json = """{"title":"Movie","name":"Show"}""";
        var details = JsonSerializer.Deserialize<SeerrMediaDetails>(json);

        Assert.NotNull(details);
        Assert.Equal("Movie", details!.Title);
        Assert.Equal("Show", details.Name);
        Assert.Equal("Movie", details.DisplayTitle);
    }

    [Fact]
    public void Deserialize_EmptyJson_DefaultsToNull()
    {
        var json = "{}";
        var details = JsonSerializer.Deserialize<SeerrMediaDetails>(json);

        Assert.NotNull(details);
        Assert.Null(details!.Title);
        Assert.Null(details.Name);
        Assert.Equal("Unknown", details.DisplayTitle);
    }

    [Fact]
    public void Deserialize_ExtraFields_IgnoredGracefully()
    {
        var json = """{"title":"Test","name":"Show","overview":"Some description","id":42}""";
        var details = JsonSerializer.Deserialize<SeerrMediaDetails>(json);

        Assert.NotNull(details);
        Assert.Equal("Test", details!.Title);
        Assert.Equal("Show", details.Name);
    }

    [Fact]
    public void Deserialize_UnicodeTitle_ParsesCorrectly()
    {
        var json = """{"title":"千と千尋の神隠し","name":null}""";
        var details = JsonSerializer.Deserialize<SeerrMediaDetails>(json);

        Assert.NotNull(details);
        Assert.Equal("千と千尋の神隠し", details!.DisplayTitle);
    }

    [Fact]
    public void Deserialize_SpecialCharactersInTitle_ParsesCorrectly()
    {
        var json = """{"title":"Spider-Man: No Way Home (2021)","name":null}""";
        var details = JsonSerializer.Deserialize<SeerrMediaDetails>(json);

        Assert.NotNull(details);
        Assert.Equal("Spider-Man: No Way Home (2021)", details!.DisplayTitle);
    }

    // ===== DisplayTitle is not serialized =====

    [Fact]
    public void Serialize_DisplayTitle_NotIncludedInJson()
    {
        var details = new SeerrMediaDetails { Title = "Test Movie", Name = "Test Show" };
        var json = JsonSerializer.Serialize(details);

        Assert.DoesNotContain("displayTitle", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DisplayTitle", json);
    }
}