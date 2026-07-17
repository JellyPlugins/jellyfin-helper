using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FileTransformation;

/// <summary>
///     Tests for <see cref="DiscoveryScriptTag"/> — the single source of truth for the
///     Discovery sidebar script injection contract.
///     Verifies:
///     <list type="bullet">
///         <item>Build() produces a well-formed HTML script tag with a URL-escaped version parameter.</item>
///         <item>RemovalRegex actually matches every tag Build() emits (round-trip integrity).</item>
///         <item>Edge cases: null / empty / whitespace / special-character versions.</item>
///         <item>The regex is anchored to <c>plugin="Jellyfin Helper"</c> and does not eat unrelated
///               script tags (regression against over-broad regexes).</item>
///     </list>
/// </summary>
public class DiscoveryScriptTagTests
{
    // The DiscoveryScriptTag class is internal, so we reach it via reflection to keep
    // tests robust even if the internal accessibility ever changes.
    private static readonly Type ScriptTagType =
        typeof(Plugin).Assembly.GetType("Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation.DiscoveryScriptTag", throwOnError: true)!;

    private static string InvokeBuild(string version)
    {
        var method = ScriptTagType.GetMethod("Build", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, [version]);
        return (string)result!;
    }

    private static Regex GetRemovalRegex()
    {
        var field = ScriptTagType.GetField("RemovalRegex", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        return (Regex)field!.GetValue(null)!;
    }

    // -----------------------------------------------------------------------
    // Build()
    // -----------------------------------------------------------------------

    [Fact]
    public void Build_TypicalVersion_ProducesWellFormedScriptTag()
    {
        var tag = InvokeBuild("1.2.3.4");
        Assert.Contains("<script", tag, StringComparison.Ordinal);
        Assert.Contains("plugin=\"Jellyfin Helper\"", tag, StringComparison.Ordinal);
        Assert.Contains("version=\"1.2.3.4\"", tag, StringComparison.Ordinal);
        Assert.Contains("src=\"../JellyfinHelper/Discovery/My/script?v=1.2.3.4\"", tag, StringComparison.Ordinal);
        Assert.Contains("defer", tag, StringComparison.Ordinal);
        Assert.EndsWith("</script>", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_VersionWithPlusSign_UrlEscapesInSrcQueryButKeepsRawInAttribute()
    {
        // Bug guard: a "+" in a query string is interpreted as a space. Uri.EscapeDataString
        // must convert "+" to "%2B" so the client fetches the exact same versioned URL.
        var tag = InvokeBuild("1.0.0+beta");
        Assert.Contains("v=1.0.0%2Bbeta", tag, StringComparison.Ordinal);
        // The version attribute itself is intentionally NOT escaped (it's just a display value).
        Assert.Contains("version=\"1.0.0+beta\"", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_VersionWithSpaces_UrlEscapesInSrc()
    {
        var tag = InvokeBuild("v 1 0");
        Assert.Contains("v=v%201%200", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_VersionWithSpecialChars_UrlEscapesQueryValue()
    {
        // Include chars that MUST be escaped in a query string: & = ? #
        var tag = InvokeBuild("a&b=c?d#e");
        Assert.Contains("v=a%26b%3Dc%3Fd%23e", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EmptyVersion_ProducesValidTagWithEmptyQueryValue()
    {
        var tag = InvokeBuild(string.Empty);
        Assert.Contains("version=\"\"", tag, StringComparison.Ordinal);
        Assert.Contains("?v=", tag, StringComparison.Ordinal);
        Assert.Contains("</script>", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NullVersion_TreatedAsEmptyString_DoesNotThrow()
    {
        // Bug guard: `version ?? string.Empty` must protect Uri.EscapeDataString.
        var tag = InvokeBuild(null!);
        Assert.Contains("</script>", tag, StringComparison.Ordinal);
        // The unescaped attribute becomes 'version=""' when the null is passed straight through
        // to string interpolation.
        Assert.Contains("version=\"\"", tag, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // RemovalRegex round-trip: whatever Build produces, the regex must remove it.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.2.3.4")]
    [InlineData("2024.10.29-alpha")]
    [InlineData("0.0.0-dev+build.42")]
    [InlineData("")]
    public void RemovalRegex_MatchesEveryTagBuildProduces(string version)
    {
        var tag = InvokeBuild(version);
        var html = $"<html><body>content{tag}\nmore</body></html>";

        var stripped = GetRemovalRegex().Replace(html, string.Empty);

        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", stripped, StringComparison.Ordinal);
        Assert.Contains("content", stripped, StringComparison.Ordinal);
        Assert.Contains("more", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovalRegex_DoesNotMatchOtherPluginScriptTags()
    {
        // Bug guard: the regex must only target tags with plugin="Jellyfin Helper".
        var html = "<script plugin=\"OtherPlugin\" version=\"1\" src=\"x\"></script>" +
                   "<script plugin=\"Jellyfin Helper\" version=\"1\" src=\"y\"></script>";

        var stripped = GetRemovalRegex().Replace(html, string.Empty);

        Assert.Contains("plugin=\"OtherPlugin\"", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovalRegex_RemovesMultipleOccurrencesInSameDocument()
    {
        // A previous buggy write-back could leave several copies. The regex must clean them all.
        var tag = InvokeBuild("1.0");
        var html = $"<body>{tag}\n{tag}\nAAA{tag}\n</body>";

        var stripped = GetRemovalRegex().Replace(html, string.Empty);

        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", stripped, StringComparison.Ordinal);
        Assert.Contains("AAA", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovalRegex_AllowsSingleQuotedPluginAttribute()
    {
        // The regex should tolerate either quote style — the plugin attribute may have been
        // rewritten by a proxy or by a differently-quoted HTML template.
        var html = "<script plugin='Jellyfin Helper' version='1.0' src='x'></script>after";

        var stripped = GetRemovalRegex().Replace(html, string.Empty);

        Assert.DoesNotContain("Jellyfin Helper", stripped, StringComparison.Ordinal);
        Assert.Contains("after", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovalRegex_HandlesTrailingCrLfAfterClosingTag()
    {
        // The regex was declared with an optional \r?\n after </script>. This must eat
        // the newline so re-injection doesn't accumulate blank lines each time.
        var tag = InvokeBuild("1.0");
        var htmlUnix = $"<body>{tag}\n<div>keep</div></body>";
        var htmlWin = $"<body>{tag}\r\n<div>keep</div></body>";

        var strippedUnix = GetRemovalRegex().Replace(htmlUnix, string.Empty);
        var strippedWin = GetRemovalRegex().Replace(htmlWin, string.Empty);

        // No stray leading newline before "<div>keep</div>".
        Assert.Equal("<body><div>keep</div></body>", strippedUnix);
        Assert.Equal("<body><div>keep</div></body>", strippedWin);
    }

    [Fact]
    public void RemovalRegex_DoesNotRemoveScriptsMentioningPluginNameInAnotherAttribute()
    {
        // Regression: the regex must look for plugin="Jellyfin Helper", not just any string
        // that happens to contain "Jellyfin Helper".
        var html = "<script src=\"foo.js\" title=\"Jellyfin Helper Snippet\"></script>keep";

        var stripped = GetRemovalRegex().Replace(html, string.Empty);

        // The script tag has NO plugin= attribute, so it must survive.
        Assert.Contains("Jellyfin Helper Snippet", stripped, StringComparison.Ordinal);
        Assert.Contains("keep", stripped, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Constants (defensive against accidental rename/typo)
    // -----------------------------------------------------------------------

    [Fact]
    public void PluginName_IsExactlyJellyfinHelper()
    {
        var field = ScriptTagType.GetField("PluginName", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal("Jellyfin Helper", (string)field!.GetValue(null)!);
    }

    [Fact]
    public void ScriptBaseUrl_IsRelativeAndPointsToDiscoveryScriptEndpoint()
    {
        var field = ScriptTagType.GetField("ScriptBaseUrl", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        var url = (string)field!.GetValue(null)!;
        Assert.Equal("../JellyfinHelper/Discovery/My/script", url);
    }
}
