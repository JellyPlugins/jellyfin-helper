using Jellyfin.Plugin.JellyfinHelper.Services.WebInjection;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.WebInjection;

/// <summary>
///     Tests for the pure index.html transformation.
/// </summary>
public class IndexHtmlTransformerTests
{
    private const string Tag = "<script plugin=\"Jellyfin Helper\" version=\"1\" src=\"../JellyfinHelper/Discovery/My/script?v=1\" defer></script>";

    [Fact]
    public void TryInject_InsertsTheTagBeforeTheClosingBodyTag()
    {
        var html = "<html><body><h1>Jellyfin</h1></body></html>";

        Assert.True(IndexHtmlTransformer.TryInject(html, Tag, out var result));
        Assert.Equal("<html><body><h1>Jellyfin</h1>" + Tag + "\n</body></html>", result);
    }

    [Fact]
    public void TryInject_UsesTheLastClosingBodyTag()
    {
        // Only the final </body> ends the document; an earlier literal in the markup
        // must not turn into the anchor point.
        var html = "<body>text </body> more </body>";

        Assert.True(IndexHtmlTransformer.TryInject(html, Tag, out var result));
        Assert.EndsWith(" more " + Tag + "\n</body>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TryInject_IsCaseInsensitiveForTheClosingTag()
    {
        var html = "<html><body></BODY></html>";

        Assert.True(IndexHtmlTransformer.TryInject(html, Tag, out var result));
        Assert.Contains(Tag, result, StringComparison.Ordinal);
    }

    [Fact]
    public void TryInject_ReplacesAnOlderTagInsteadOfStacking()
    {
        var oldTag = "<script plugin=\"Jellyfin Helper\" version=\"0.9\" src=\"../JellyfinHelper/Discovery/My/script?v=0.9\" defer></script>";
        var html = "<html><body>" + oldTag + "\n</body></html>";

        Assert.True(IndexHtmlTransformer.TryInject(html, Tag, out var result));
        Assert.DoesNotContain("v=0.9", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "plugin=\"Jellyfin Helper\""));
        Assert.Contains(Tag, result, StringComparison.Ordinal);
    }

    [Fact]
    public void TryInject_WithoutClosingBodyTagReturnsFalseAndKeepsInput()
    {
        var html = "<html><body><h1>no close tag</h1></html>";

        Assert.False(IndexHtmlTransformer.TryInject(html, Tag, out var result));
        Assert.Equal(html, result);
    }

    [Fact]
    public void TryInject_EmptyInputReturnsFalse()
    {
        Assert.False(IndexHtmlTransformer.TryInject(string.Empty, Tag, out var result));
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Remove_StripsEveryTagOfOursAndKeepsForeignTags()
    {
        var html = "<body>"
            + "<script plugin=\"Other Plugin\" src=\"a\"></script>"
            + "<script plugin=\"Jellyfin Helper\" version=\"1\" src=\"a\"></script>"
            + "<script plugin=\"Jellyfin Helper\" version=\"2\" src=\"b\"></script>"
            + "</body>";

        var cleaned = IndexHtmlTransformer.Remove(html);

        Assert.DoesNotContain("Jellyfin Helper", cleaned, StringComparison.Ordinal);
        Assert.Contains("Other Plugin", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_NoTagIsANoOp()
    {
        var html = "<html><body></body></html>";

        Assert.Equal(html, IndexHtmlTransformer.Remove(html));
    }

    [Fact]
    public void Remove_NullOrEmptyReturnsEmpty()
    {
        Assert.Equal(string.Empty, IndexHtmlTransformer.Remove(null!));
        Assert.Equal(string.Empty, IndexHtmlTransformer.Remove(string.Empty));
    }

    [Theory]
    [InlineData("<script plugin=\"Jellyfin Helper\" src=\"a\"></script>")]
    [InlineData("<script src=\"../JellyfinHelper/Discovery/My/script\"></script>")]
    [InlineData("<script src=\"../JELLYFINHELPER/discovery/my/SCRIPT?v=1\"></script>")]
    public void ContainsScriptTag_RecognisesOurTagInAnyVersion(string html)
    {
        Assert.True(IndexHtmlTransformer.ContainsScriptTag(html));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body></body></html>")]
    [InlineData("<script src=\"../JellyfinHelper/Other/script\"></script>")]
    public void ContainsScriptTag_ReturnsFalseWithoutOurTag(string html)
    {
        Assert.False(IndexHtmlTransformer.ContainsScriptTag(html));
    }

    [Fact]
    public void ContainsScriptTag_NullReturnsFalse()
    {
        Assert.False(IndexHtmlTransformer.ContainsScriptTag(null!));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
