using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr;

/// <summary>
///     Unit tests for <see cref="SeerrMediaDetails" />.
/// </summary>
public class SeerrMediaDetailsTests
{
    // DisplayTitle prefers a non-blank Title, then falls back to a non-blank Name.
    [Theory]
    [InlineData("The Matrix", null, "The Matrix")]
    [InlineData(null, "Breaking Bad", "Breaking Bad")]
    [InlineData("Movie Title", "TV Name", "Movie Title")]
    [InlineData("", "Fallback Show", "Fallback Show")]
    [InlineData("   ", "Real Show", "Real Show")]
    public void DisplayTitle_PrefersTitleThenName(string? title, string? name, string expected)
    {
        var details = new SeerrMediaDetails { Title = title, Name = name };
        Assert.Equal(expected, details.DisplayTitle);
    }

    // When neither field carries a non-blank value, DisplayTitle is null.
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData(null, "   ")]
    [InlineData("", "")]
    [InlineData("  ", "  ")]
    public void DisplayTitle_BlankInputs_ReturnsNull(string? title, string? name)
    {
        var details = new SeerrMediaDetails { Title = title, Name = name };
        Assert.Null(details.DisplayTitle);
    }

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
    public void DefaultValues_DisplayTitleIsNull()
    {
        var details = new SeerrMediaDetails();
        Assert.Null(details.DisplayTitle);
    }

    [Theory]
    [InlineData("""{"title":"Inception","name":null}""", "Inception", null, "Inception")]
    [InlineData("""{"title":null,"name":"The Wire"}""", null, "The Wire", "The Wire")]
    [InlineData("""{"title":"Movie","name":"Show"}""", "Movie", "Show", "Movie")]
    [InlineData("{}", null, null, null)]
    public void Deserialize_PopulatesTitleNameAndDisplayTitle(string json, string? title, string? name, string? displayTitle)
    {
        var details = JsonSerializer.Deserialize<SeerrMediaDetails>(json);

        Assert.NotNull(details);
        Assert.Equal(title, details!.Title);
        Assert.Equal(name, details.Name);
        Assert.Equal(displayTitle, details.DisplayTitle);
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

    [Fact]
    public void Serialize_DisplayTitle_NotIncludedInJson()
    {
        var details = new SeerrMediaDetails { Title = "Test Movie", Name = "Test Show" };
        var json = JsonSerializer.Serialize(details);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.DoesNotContain(
            doc.RootElement.EnumerateObject(),
            p => string.Equals(p.Name, "displayTitle", StringComparison.OrdinalIgnoreCase));
    }
}