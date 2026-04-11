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

    [Fact]
    public void Html_ContainsDonutSvgFunction()
    {
        Assert.Contains("function renderDonutSvg", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecBreakdownFunction()
    {
        Assert.Contains("function renderCodecBreakdown", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecClickHandlers()
    {
        Assert.Contains("function attachCodecClickHandlers", HtmlContent);
    }

    [Fact]
    public void Html_ContainsSharedFileListRenderer()
    {
        // renderCodecFileList was replaced by the shared renderFileList in shared.js
        Assert.Contains("function renderFileList", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecPathCollector()
    {
        Assert.Contains("function collectCodecPaths", HtmlContent);
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
    }

    [Fact]
    public void Html_ContainsCodecCategoryMap()
    {
        Assert.Contains("CODEC_CATEGORY_MAP", HtmlContent);
    }

    [Fact]
    public void Html_CodecCategoryMap_VideoCodecsExcludesMusic()
    {
        // videoCodecs should have music: false
        Assert.Contains("'videoCodecs': { movies: true, tvShows: true, music: false }", HtmlContent);
    }

    [Fact]
    public void Html_CodecCategoryMap_VideoAudioCodecsExcludesMusic()
    {
        Assert.Contains("'videoAudioCodecs': { movies: true, tvShows: true, music: false }", HtmlContent);
    }

    [Fact]
    public void Html_CodecCategoryMap_MusicAudioCodecsExcludesVideo()
    {
        // musicAudioCodecs should only include music
        Assert.Contains("'musicAudioCodecs': { movies: false, tvShows: false, music: true }", HtmlContent);
    }

    [Fact]
    public void Html_CodecCategoryMap_ContainersIncludesAll()
    {
        // containers should include all library types
        Assert.Contains("'containers': { movies: true, tvShows: true, music: true }", HtmlContent);
    }

    [Fact]
    public void Html_CodecCategoryMap_ResolutionsExcludesMusic()
    {
        Assert.Contains("'resolutions': { movies: true, tvShows: true, music: false }", HtmlContent);
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
        Assert.Contains("collectCodecPaths(_lastCodecData, pathsProp, codecName, categories)", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecBreakdownCssClasses()
    {
        Assert.Contains("codec-breakdown", HtmlContent);
        Assert.Contains("codec-clickable", HtmlContent);
        Assert.Contains("codec-detail-panel", HtmlContent);
        Assert.Contains("codec-row-active", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecFileListCssClasses()
    {
        Assert.Contains("codec-files-header", HtmlContent);
        Assert.Contains("codec-files-columns", HtmlContent);
        Assert.Contains("codec-files-multi", HtmlContent);
        Assert.Contains("codec-files-section", HtmlContent);
        Assert.Contains("codec-file-item", HtmlContent);
        Assert.Contains("codec-file-name", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecDetailAnimationCss()
    {
        Assert.Contains("codec-detail-visible", HtmlContent);
        Assert.Contains("max-height", HtmlContent);
    }

    [Fact]
    public void Html_ContainsHelperFunctions()
    {
        Assert.Contains("function getFileName", HtmlContent);
        Assert.Contains("function getParentFolder", HtmlContent);
    }
}