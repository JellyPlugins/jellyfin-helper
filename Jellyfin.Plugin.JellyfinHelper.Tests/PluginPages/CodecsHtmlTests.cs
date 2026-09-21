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
        Assert.Contains("VideoBitrateTierPaths", HtmlContent);
        Assert.Contains("AudioLanguagePaths", HtmlContent);
        Assert.Contains("SubtitleLanguagePaths", HtmlContent);
        Assert.Contains("WatchedTierPaths", HtmlContent);
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
    [InlineData(@"'containers':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*true,\s*books:\s*true,\s*other:\s*true\s*\}")]
    [InlineData(@"'resolutions':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
    [InlineData(@"'dynamicRanges':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
    [InlineData(@"'videoBitrate':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
    [InlineData(@"'audioLanguages':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
    [InlineData(@"'subtitleLanguages':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
    [InlineData(@"'watched':\s*\{\s*movies:\s*true,\s*tvShows:\s*true,\s*music:\s*false,\s*other:\s*true\s*\}")]
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
    public void Html_FillCodecsData_AggregatesNewVideoDimensions()
    {
        Assert.Contains("aggregateDict(videoLibraries, 'VideoBitrateTiers')", HtmlContent);
        Assert.Contains("aggregateDict(videoLibraries, 'AudioLanguages')", HtmlContent);
        Assert.Contains("aggregateDict(videoLibraries, 'SubtitleLanguages')", HtmlContent);
        Assert.Contains("aggregateDict(videoLibraries, 'WatchedTiers')", HtmlContent);
    }

    [Fact]
    public void Html_FillCodecsData_RendersNewDonutCharts()
    {
        Assert.Contains("renderDonutChart(videoBitrate,", HtmlContent);
        Assert.Contains("renderDonutChart(audioLanguages,", HtmlContent);
        Assert.Contains("renderDonutChart(subtitleLanguages,", HtmlContent);
        Assert.Contains("renderDonutChart(watched,", HtmlContent);
    }

    [Fact]
    public void Html_FillCodecsData_RendersLibraryExplorer()
    {
        Assert.Contains("renderCodecsExplorer(codecsContainer)", HtmlContent);
        Assert.Contains("function buildCodecsExplorerHtml", HtmlContent);
        Assert.Contains("function computeCodecsExplorerPaths", HtmlContent);
        Assert.Contains("function computePathsExcluding", HtmlContent);
        Assert.Contains("function countExplorerOptions", HtmlContent);
        Assert.Contains("function buildCodecsExplorerMulti", HtmlContent);
        Assert.Contains("function groupExplorerResults", HtmlContent);
        Assert.Contains("function openCodecsExplorer", HtmlContent);
        Assert.Contains("CODEC_EXPLORER_TYPE_MOVIES", HtmlContent);
        Assert.Contains("CODEC_EXPLORER_TYPE_TVSHOWS", HtmlContent);
        Assert.Contains("CODEC_EXPLORER_TYPE_MUSIC", HtmlContent);
        Assert.Contains("CODEC_EXPLORER_TYPE_BOOKS", HtmlContent);
        Assert.Contains("codec-explorer-toggle", HtmlContent);
        Assert.Contains("codec-multi-toggle", HtmlContent);
        Assert.Contains("data-codec-explore-library", HtmlContent);
    }

    [Fact]
    public void Html_LibraryExplorer_RendersAboveDonutGrid()
    {
        // The explorer is prepended so the search sits above the charts, not below them.
        Assert.Contains("container.insertBefore(tmp.firstChild, container.firstChild)", HtmlContent);
    }

    [Fact]
    public void Html_FillCodecsData_WatchedChartShowsPerUserSlices()
    {
        // The Watched donut breaks down by username plus Never watched.
        Assert.Contains("function countWatchedUsers", HtmlContent);
        Assert.Contains("WatchedByUserPaths", HtmlContent);
        Assert.Contains("WatchedByUserSizes", HtmlContent);
    }

    [Fact]
    public void Html_LibraryExplorer_LibraryScopeIsMultiDropdown()
    {
        // The library scope is a multi-dropdown like the dimension ones: real
        // library names only, no synthetic type entries.
        Assert.Contains("function buildCodecsExplorerLibraryMulti", HtmlContent);
        Assert.Contains("data-library-toggle", HtmlContent);
        Assert.Contains("data-library-panel", HtmlContent);
        Assert.Contains("data-library-option", HtmlContent);
        Assert.DoesNotContain("<optgroup", HtmlContent);
    }

    [Fact]
    public void Html_LibraryExplorer_DisablesEmptyDimensions()
    {
        // Dimensions with no options in the current scope render greyed out.
        Assert.Contains("codec-explorer-field--disabled", HtmlContent);
        Assert.Contains("function visibleExplorerOptions", HtmlContent);
        Assert.Contains("function pruneCodecsExplorerState", HtmlContent);
    }

    [Fact]
    public void Html_LibraryExplorer_ResultRowsExpandToFileDetails()
    {
        // Clicking a result file opens an inline detail card with path, size,
        // codec/language values and a Watched expander. The count rides along
        // as a data attribute so tests stay locale-independent.
        Assert.Contains("function buildExplorerFileDetail", HtmlContent);
        Assert.Contains("function toggleExplorerFileDetail", HtmlContent);
        Assert.Contains("function bindExplorerFileDetails", HtmlContent);
        Assert.Contains("codec-file-detail", HtmlContent);
        Assert.Contains("data-explorer-count", HtmlContent);
        Assert.Contains("function collectScopePaths", HtmlContent);
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

    [Fact]
    public void Html_CollectCodecPaths_IncludesBookRootPaths()
    {
        // The book-format drill-down needs BookRootPaths so renderFileTree can trim a
        // common prefix, matching movies/tvShows/music. Guards against the field being
        // dropped from collectCodecPaths.rootPaths.
        Assert.Contains("books: data.BookRootPaths || []", HtmlContent);
    }

    [Fact]
    public void Html_ResolutionDrilldown_ShowsRealPixelDimensions()
    {
        // The resolution drill-down must feed renderFileTree a per-file dimensions map so
        // each file shows its true pixel size (e.g. 1920x800) behind the tier label.
        Assert.Contains("function collectResolutionDimensions", HtmlContent);
        Assert.Contains("ResolutionDimensions", HtmlContent);
        Assert.Contains("function collectDrilldownMeta", HtmlContent);
        Assert.Contains("function collectTrackLabelMeta", HtmlContent);
        Assert.Contains("renderFileTree(result, codecName, collectDrilldownMeta(chartId))", HtmlContent);
    }

    [Fact]
    public void Html_LibraryExplorer_RendersFilterBarWithAddFilter()
    {
        // The explorer opens clean: library scope plus a single Add filter entry
        // instead of a full dropdown grid. Dimensions open on demand in the popover.
        Assert.Contains("function buildCodecsExplorerFilterAdd", HtmlContent);
        Assert.Contains("function buildCodecsExplorerPills", HtmlContent);
        Assert.Contains("function buildCodecsExplorerDimList", HtmlContent);
        Assert.Contains("function buildCodecsExplorerDimEditor", HtmlContent);
        Assert.Contains("function hasVisibleExplorerOptions", HtmlContent);
        Assert.Contains("function getCachedExplorerMaps", HtmlContent);
        Assert.Contains("function getDimPathIndex", HtmlContent);
        Assert.Contains("function closeFilterPop", HtmlContent);
        Assert.Contains("role=\"radio\"", HtmlContent);
        Assert.Contains("codec-filter-bar", HtmlContent);
        Assert.Contains("codec-filter-pop", HtmlContent);
        Assert.Contains("codec-pill", HtmlContent);
        Assert.Contains("codec-multi-chevron", HtmlContent);
        Assert.Contains("data-filter-dim", HtmlContent);
    }

    [Fact]
    public void Html_LibraryExplorer_SingleListScopeNullAndAriaState()
    {
        // Single dims render as inline radio rows (no OS popup on phones).
        Assert.Contains("function buildCodecsExplorerSingleList", HtmlContent);
        Assert.Contains("data-single-option", HtmlContent);
        // File detail toggles report their expansion state.
        Assert.Contains("setAttribute('aria-expanded', 'false')", HtmlContent);
        // A pruned scope without survivors stays an explicit empty scope with
        // its own label instead of pretending to be all libraries.
        Assert.Contains("explorerNoMatchingLibraries", HtmlContent);
        // Preview and commit share one normalization so they never diverge.
        Assert.Contains("function normalizeBitrateRange", HtmlContent);
        // Rapid checkbox picks share one debounced rebuild.
        Assert.Contains("function refreshCodecsExplorerControlsDebounced", HtmlContent);
    }

    [Fact]
    public void Html_LibraryExplorer_BitrateFiltersByAbsoluteRange()
    {
        // Bitrate filtering uses measured per file values with a slider editor,
        // independent of the donut tier buckets.
        Assert.Contains("function buildBitrateEditor", HtmlContent);
        Assert.Contains("function getBitrateMap", HtmlContent);
        Assert.Contains("function getBitrateBounds", HtmlContent);
        Assert.Contains("function getBitrateHistogram", HtmlContent);
        Assert.Contains("function formatBitrateRange", HtmlContent);
        Assert.Contains("function applyBitrateRange", HtmlContent);
        Assert.Contains("VideoBitrates", HtmlContent);
        Assert.Contains("codec-bitrate-hist", HtmlContent);
    }
}
