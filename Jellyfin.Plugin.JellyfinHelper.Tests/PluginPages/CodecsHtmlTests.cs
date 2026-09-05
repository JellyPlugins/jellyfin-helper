using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
///     Tests for the Codecs tab in the composed configPage.html.
/// </summary>
public class CodecsHtmlTests : ConfigPageTestBase
{
    [Fact]
    public void Html_ContainsCodecsTab()
    {
        Assert.Contains("id=\"tab-codecs\"", HtmlContent);
        Assert.Contains("id=\"codecsContent\"", HtmlContent);
    }

    /// <summary>
    ///     Verifies each Codecs tab function is declared in the composed HTML.
    /// </summary>
    /// <param name="signature">The function signature marker expected in the HTML.</param>
    [Theory]
    [InlineData("function fillCodecsData")]
    [InlineData("function renderDonutSvg")]
    [InlineData("function renderCodecBreakdown")]
    [InlineData("function attachCodecClickHandlers")]
    [InlineData("function renderFileTree")]
    [InlineData("function collectCodecPaths")]
    [InlineData("function renderDonutChart")]
    [InlineData("function triggerCodecRowForSegment")]
    public void Html_ContainsCodecsFunction(string signature)
    {
        Assert.Contains(signature, HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecPathMap()
    {
        Assert.Contains("CODEC_PATH_MAP", HtmlContent);
        Assert.Contains("VideoCodecPaths", HtmlContent);
        Assert.Contains("VideoAudioCodecPaths", HtmlContent);
        Assert.Contains("MusicAudioCodecPaths", HtmlContent);
        Assert.Contains("ContainerFormatPaths", HtmlContent);
        Assert.Contains("ResolutionPaths", HtmlContent);
        Assert.Contains("DynamicRangePaths", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecCategoryMap()
    {
        Assert.Contains("CODEC_CATEGORY_MAP", HtmlContent);
    }

    /// <summary>
    ///     Verifies each codec category map entry has its expected library-type flags.
    /// </summary>
    /// <param name="pattern">The whitespace-tolerant regex describing one category entry.</param>
    [Theory]
    [InlineData(@"'videoCodecs':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
    [InlineData(@"'videoAudioCodecs':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
    [InlineData(@"'musicAudioCodecs':\s*\{\s*movies:\s*false,\s*tvShows:\s*false,\s*music:\s*true,\s*other:\s*false\s*\}")]
    [InlineData(@"'containers':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*true,\s*other:\s*true\s*\}")]
    [InlineData(@"'resolutions':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
    [InlineData(@"'dynamicRanges':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
    public void Html_CodecCategoryMap_HasExpectedLibraryFlags(string pattern)
    {
        Assert.Matches(pattern, HtmlContent);
    }

    [Fact]
    public void Html_FillCodecsData_UsesVideoLibrariesForVideoCodecs()
    {
        // fillCodecsData should aggregate video codecs from videoLibraries, not data.Libraries
        Assert.Contains("var videoLibraries = (data.Movies || []).concat(data.TvShows || [])", HtmlContent);
        Assert.Contains("aggregateDict(videoLibraries, 'VideoCodecs')", HtmlContent);
    }

    [Fact]
    public void Html_FillCodecsData_UsesMusicLibrariesForMusicAudioCodecs()
    {
        // fillCodecsData should aggregate music audio codecs from musicLibraries only
        Assert.Contains("var musicLibraries = data.Music || []", HtmlContent);
        Assert.Contains("aggregateDict(musicLibraries, 'MusicAudioCodecs')", HtmlContent);
    }

    [Fact]
    public void Html_FillCodecsData_UsesAllLibrariesForContainerFormats()
    {
        // Container formats should use all libraries
        Assert.Contains("aggregateDict(data.Libraries, 'ContainerFormats')", HtmlContent);
    }

    [Fact]
    public void Html_FillCodecsData_UsesVideoLibrariesForResolutions()
    {
        Assert.Contains("aggregateDict(videoLibraries, 'Resolutions')", HtmlContent);
    }

    [Fact]
    public void Html_FillCodecsData_UsesVideoLibrariesForDynamicRanges()
    {
        Assert.Contains("aggregateDict(videoLibraries, 'DynamicRanges')", HtmlContent);
    }

    [Fact]
    public void Html_CollectCodecPaths_AcceptsCategoriesParameter()
    {
        // collectCodecPaths should accept a categories parameter
        Assert.Contains("function collectCodecPaths(data, pathsProp, codecName, categories)", HtmlContent);
    }

    [Fact]
    public void Html_ClickHandler_PassesCategoryMapToCollectPaths()
    {
        // The click handler should pass CODEC_CATEGORY_MAP to collectCodecPaths
        Assert.Contains("var categories = CODEC_CATEGORY_MAP[chartId]", HtmlContent);
        Assert.Matches(
            @"collectCodecPaths\(_lastCodecData,\s*pathsProp,\s*codecName,\s*categories\)",
            HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecBreakdownCssClasses()
    {
        Assert.Contains("codec-breakdown", HtmlContent);
        Assert.Contains("codec-clickable", HtmlContent);
        Assert.Contains("file-tree-panel", HtmlContent);
        Assert.Contains("codec-row-active", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFileTreeCssClasses()
    {
        Assert.Contains("file-tree-header", HtmlContent);
        Assert.Contains("file-tree-columns", HtmlContent);
        Assert.Contains("file-tree-multi", HtmlContent);
        Assert.Contains("file-tree-section", HtmlContent);
        Assert.Contains("tree-view", HtmlContent);
        Assert.Contains("tree-node", HtmlContent);
        Assert.Contains("tree-folder", HtmlContent);
        Assert.Contains("tree-leaf", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFileTreePanelVisibilityCss()
    {
        Assert.Contains("file-tree-panel-visible", HtmlContent);
        Assert.Contains("max-height", HtmlContent);
    }

    [Fact]
    public void Html_ContainsDonutTooltipFunctions()
    {
        Assert.Contains("function showDonutTooltip", HtmlContent);
        Assert.Contains("function hideDonutTooltip", HtmlContent);
        Assert.Contains("function attachDonutHoverTooltips", HtmlContent);
    }

    [Fact]
    public void Html_ContainsDonutTooltipStateVariables()
    {
        Assert.Contains("_donutTooltipData", HtmlContent);
        Assert.Contains("_activeTooltipSegmentId", HtmlContent);
    }

    [Fact]
    public void Html_ContainsDonutTooltipCssClass()
    {
        Assert.Contains("donut-tooltip", HtmlContent);
    }
}
