using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
/// Tests for the Codecs tab in the composed configPage.html.
/// </summary>
public class CodecsHtmlTests : ConfigPageTestBase
{
    [Fact]
    public void Html_ContainsCodecsTab()
    {
        Assert.Contains("id=\"tab-codecs\"", HtmlContent);
        Assert.Contains("id=\"codecsContent\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFillCodecsDataFunction()
    {
        Assert.Contains("function fillCodecsData", HtmlContent);
    }
}