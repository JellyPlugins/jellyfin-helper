using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
/// Tests for the Overview tab in the composed configPage.html.
/// </summary>
public class OverviewHtmlTests : ConfigPageTestBase
{
    [Fact]
    public void Html_ContainsOverviewTabContent()
    {
        Assert.Contains("id=\"tab-overview\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsOverviewContentDiv()
    {
        Assert.Contains("id=\"overviewContent\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFillOverviewDataFunction()
    {
        Assert.Contains("function fillOverviewData", HtmlContent);
    }

    [Fact]
    public void Html_ContainsScanPlaceholder()
    {
        Assert.Contains("scanPlaceholder", HtmlContent);
    }
}