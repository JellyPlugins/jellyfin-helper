using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

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

    [Fact]
    public void Html_BooksCard_RendersOnlyWhenBookLibraryExists()
    {
        // The Books stat card is gated on a Book library actually contributing files.
        Assert.Contains("data.TotalBookFileCount > 0", HtmlContent);
    }

    [Fact]
    public void Html_TotalFilesCard_SpansFullWidthWhenBooksPresent()
    {
        // Odd card count (Book library adds a 7th card) => Total Files spans both columns
        // via the stat-card-full class, so the last grid row has no gap.
        Assert.Contains("stat-card-full", HtmlContent);
    }
}
