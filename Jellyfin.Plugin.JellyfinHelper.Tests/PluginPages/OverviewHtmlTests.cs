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

    [Fact]
    public void Html_LibraryRows_LinkIntoCodecsExplorer()
    {
        // Each library name is a link that opens the Codecs tab
        // Library Explorer pre-scoped to that library.
        Assert.Contains("data-codec-explore-library", HtmlContent);
        Assert.Contains("codec-explore-link", HtmlContent);
        Assert.Contains("explorerOpenTooltip", HtmlContent);
    }

    [Fact]
    public void Html_MovieAndTvCards_LinkIntoCodecsExplorer()
    {
        // The Movies and TV stat cards act as links into the explorer,
        // pre-scoped to all libraries of that type.
        Assert.Contains("stat-card-link", HtmlContent);
        Assert.Contains("CODEC_EXPLORER_TYPE_MOVIES", HtmlContent);
        Assert.Contains("CODEC_EXPLORER_TYPE_TVSHOWS", HtmlContent);
    }

    [Fact]
    public void Html_MusicAndBookCards_LinkIntoCodecsExplorer()
    {
        // Music and Books cards link into the explorer with their type scope,
        // where their dedicated codec dimensions stay enabled.
        Assert.Contains("CODEC_EXPLORER_TYPE_MUSIC", HtmlContent);
        Assert.Contains("CODEC_EXPLORER_TYPE_BOOKS", HtmlContent);
    }

    [Fact]
    public void Html_TrickplayCard_ShowsInternalSize()
    {
        // Jellyfin managed images live outside every library, so the card
        // renders them on their own detail line with an (internal) marker whose
        // hover title explains the cleanup exclusion.
        Assert.Contains("trickplayInternalBadge", HtmlContent);
        Assert.Contains("trickplayInternalHint", HtmlContent);
        Assert.Contains("trickplay-internal-badge", HtmlContent);
        Assert.Contains("InternalTrickplaySize", HtmlContent);
    }
}
