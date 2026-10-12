using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
///     Tests for the Codecs tab in the composed configPage.html.
/// </summary>
public partial class CodecsHtmlTests : ConfigPageTestBase
{
    [Fact]
    public void Html_ContainsCodecsTab()
    {
        Assert.Contains("id=\"tab-codecs\"", HtmlContent);
        Assert.Contains("id=\"codecsContent\"", HtmlContent);
    }

    [Fact]
    public void Html_FilterPopover_OpensLeftwardsFromButton()
    {
        // The add button sits at the bar right, so a left-anchored card would spill past the viewport edge.
        var css = WhitespaceRegex().Replace(HtmlContent, " ");
        Assert.Matches(FilterPopoverAnchorRegex(), css);
    }

    [Fact]
    public void Html_PhoneSheet_IgnoresButtonWrapper()
    {
        // The phone sheet anchors to the bar, so the positioned button wrapper must go static: the nearer ancestor would win otherwise and shrink the sheet to button width.
        var css = WhitespaceRegex().Replace(HtmlContent, " ");
        Assert.Matches(ButtonWrapperStaticRegex(), css);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\.codec-filter-pop\s*\{[^}]*left:\s*auto;[^}]*right:\s*0;")]
    private static partial Regex FilterPopoverAnchorRegex();

    [GeneratedRegex(@"\.codec-filter-add\s*\{[^}]*position:\s*static;")]
    private static partial Regex ButtonWrapperStaticRegex();

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
    public void Html_FillCodecsData_MusicChartRendersBeforeBooks()
    {
        // Order: Bitrate, Watched, Audio, Subtitle, Music, Books. Video charts
        // stay together on top, music/books close the grid as separate types.
        var bitrateAt = HtmlContent.IndexOf("renderDonutChart(videoBitrate,", StringComparison.Ordinal);
        var watchedAt = HtmlContent.IndexOf("renderDonutChart(watched,", StringComparison.Ordinal);
        var audioAt = HtmlContent.IndexOf("renderDonutChart(audioLanguages,", StringComparison.Ordinal);
        var subsAt = HtmlContent.IndexOf("renderDonutChart(subtitleLanguages,", StringComparison.Ordinal);
        var musicAt = HtmlContent.IndexOf("renderDonutChart(musicAudioCodecs,", StringComparison.Ordinal);
        var booksAt = HtmlContent.IndexOf("renderDonutChart(bookFormats,", StringComparison.Ordinal);
        Assert.True(bitrateAt >= 0 && watchedAt >= 0 && audioAt >= 0 && subsAt >= 0 && musicAt >= 0 && booksAt >= 0);
        Assert.True(bitrateAt < watchedAt);
        Assert.True(watchedAt < audioAt);
        Assert.True(audioAt < subsAt);
        Assert.True(subsAt < musicAt);
        Assert.True(musicAt < booksAt);
    }

    [Fact]
    public void Html_CodecBreakdown_UnknownSortsLast()
    {
        // Unknown is a bucket, not a language; it always sorts after real facets.
        Assert.Contains("toLowerCase() === 'unknown'", HtmlContent);
    }

    [Fact]
    public void Html_CodecBreakdown_LongListsCollapse()
    {
        // Long breakdowns collapse with a Show all toggle so language facets do
        // not stretch the card. The file-tree panel stays outside the list.
        Assert.Contains("codec-show-more", HtmlContent);
        Assert.Contains("function toggleCodecBreakdown", HtmlContent);
        Assert.Contains("function syncCollapsedBreakdownRows", HtmlContent);
        Assert.Contains("data-breakdown-toggle", HtmlContent);
        Assert.Contains("_codecRowSyncBound", HtmlContent);
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
    public void Html_LibraryExplorer_DimensionEditorHasExcludeToggle()
    {
        // Each categorical dimension offers Includes/Excludes so filters negate
        // without a second value state. Bitrate stays positive only by design.
        Assert.Contains("data-exclude-toggle", HtmlContent);
        Assert.Contains("function buildExcludeToggle", HtmlContent);
        Assert.Contains("function onExplorerExcludeChanged", HtmlContent);
        Assert.Contains("function isCodecsExplorerExcluded", HtmlContent);
        Assert.Contains("function collectUniversePaths", HtmlContent);
        Assert.Contains("explorerExcludes", HtmlContent);
    }

    [Fact]
    public void Html_LibraryExplorer_RendersLazyTreeWithoutSectionCaps()
    {
        // Results render as a lazy tree: only top-level shells reach the DOM and
        // children materialize on expand, so huge libraries stay interactive
        // with truthful totals and without continuation buttons.
        Assert.Contains("FILE_TREE_EXPAND_BUDGET", HtmlContent);
        Assert.Contains("data-tree-key", HtmlContent);
        Assert.Contains("file-tree-section-actions", HtmlContent);
        Assert.Contains("function ensureTreeChildren", HtmlContent);
        Assert.Contains("function toggleTreeNode", HtmlContent);
        Assert.Contains("function runTreeAction", HtmlContent);
        Assert.Contains("function setTreeCappedNote", HtmlContent);
        Assert.Contains("treeExpandCapped", HtmlContent);
        Assert.Contains("jfTreeChildren", HtmlContent);
        Assert.DoesNotContain("CODEC_EXPLORER_SECTION_FILES", HtmlContent);
        Assert.DoesNotContain("appendSectionMoreButtons", HtmlContent);
        Assert.DoesNotContain("explorerSectionMore", HtmlContent);
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
    public void Html_ExplorerResultSectionsScrollIndependently()
    {
        // Each explorer result column (Movies, Shows, Books) scrolls on its own
        // instead of sharing one panel scroll with the other columns.
        Assert.Contains("#codecExplorerResults .file-tree-section", HtmlContent);
        Assert.Contains("#codecExplorerResults .file-tree-section .tree-view", HtmlContent);
        Assert.Contains("#codecExplorerResults .file-tree-section .tree-view::-webkit-scrollbar", HtmlContent);
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
        Assert.Contains("function previewBitrateRangeCoalesced", HtmlContent);
        Assert.Contains("function getBitrateMap", HtmlContent);
        Assert.Contains("function getBitrateBounds", HtmlContent);
        Assert.Contains("function getBitrateHistogram", HtmlContent);
        Assert.Contains("function formatBitrateRange", HtmlContent);
        Assert.Contains("function applyBitrateRange", HtmlContent);
        Assert.Contains("VideoBitrates", HtmlContent);
        Assert.Contains("codec-bitrate-hist", HtmlContent);
    }

    [Fact]
    public void Html_CodecCards_CarryChartIdAndHandle()
    {
        // Each card needs a stable id on the wrapper (the drag engine reads data-chart-id) and a
        // keyboard-focusable handle in the header. The old markup had neither.
        Assert.Contains("data-chart-id=\"' + escAttr(id) + '\"", HtmlContent);
        Assert.Contains("codec-card-handle", HtmlContent);
        Assert.Contains("role=\"button\" tabindex=\"0\" aria-grabbed=\"false\"", HtmlContent);
    }

    [Fact]
    public void Html_CodecCards_RenderFromDescriptorOrder()
    {
        // The if-chain was replaced by a descriptor map ordered via orderedCodecCardIds, so the
        // persisted order can drive the layout.
        Assert.Contains("var cardDescriptors", HtmlContent);
        Assert.Contains("orderedCodecCardIds(cardDescriptors)", HtmlContent);
        Assert.Contains("CANONICAL_CODEC_ORDER", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecDragEngineFunctions()
    {
        Assert.Contains("function attachCodecCardDragHandlers", HtmlContent);
        Assert.Contains("function onCodecHandlePointerDown", HtmlContent);
        Assert.Contains("function onCodecHandleKeyDown", HtmlContent);
        Assert.Contains("function moveCodecPlaceholderTo", HtmlContent);
        Assert.Contains("function updateCodecAutoScroll", HtmlContent);
        Assert.Contains("function commitCodecCardOrderFromDom", HtmlContent);
        Assert.Contains("function saveCodecCardOrder", HtmlContent);
        Assert.Contains("function loadCodecCardOrderOnce", HtmlContent);
    }

    [Fact]
    public void Html_CodecOrder_SavesViaDedicatedEndpoint()
    {
        // Reorders must hit the dedicated PUT, never the full-config PUT, to avoid racing the
        // Settings dirty state.
        Assert.Contains("apiPut('JellyfinHelper/Configuration/CodecCardOrder'", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCodecDragCssClasses()
    {
        Assert.Contains("codec-dragging", HtmlContent);
        Assert.Contains("codec-drop-placeholder", HtmlContent);
        Assert.Contains(".codec-card-handle", HtmlContent);
    }

    [Fact]
    public void Html_CodecHandle_LabelNamesTheCard()
    {
        // Every grip must carry the card's own title so a screen reader can tell the 11 apart,
        // rather than announcing the same "Reorder card" for each.
        Assert.Contains("var handleLabel = T('codecCardReorder', 'Reorder card') + ': ' + cardTitle", HtmlContent);
    }

    [Fact]
    public void Html_CodecDrag_LiftsCardToFollowPointer()
    {
        // The dragged card must track the pointer (not sit static in its slot), reset cleanly on drop.
        Assert.Contains("function moveCodecCardToPointer", HtmlContent);
        Assert.Contains("state.lifted", HtmlContent);
        Assert.Contains("state.card.style.transform = ''", HtmlContent);
    }

    [Fact]
    public void Html_CodecDrag_MouseButtonGuardIsMouseOnly()
    {
        // Touch/pen can report button -1 for a normal press, so the non-primary-button abort is
        // scoped to mouse; a blanket button!==0 guard would swallow legitimate touch drags.
        Assert.Contains("e.pointerType === 'mouse' && e.button !== 0", HtmlContent);
    }

    [Fact]
    public void Html_CodecHandle_SuppressesContextMenu()
    {
        // Long-press on Android/iOS must arm a drag, not pop the browser context menu / callout.
        Assert.Contains("handle.addEventListener('contextmenu'", HtmlContent);
    }

    [Fact]
    public void Html_CodecHandle_CssHardensAgainstSelectionAndCallout()
    {
        Assert.Contains("-webkit-touch-callout: none", HtmlContent);
        Assert.Matches(@"\.codec-card-handle\s*\{[^}]*user-select:\s*none", HtmlContent);
    }

    [Fact]
    public void Html_CodecKeyboard_EscapeRestoresInPlaceAndRefocuses()
    {
        // Escape (and the error rollback) must not full-re-render: that drops focus to <body>.
        // Instead the card is restored to its origin index in place and the handle refocused.
        Assert.Contains("function restoreCardToIndex", HtmlContent);
        Assert.Contains("handle.dataset.originIndex", HtmlContent);
        Assert.Contains("card.querySelector('.codec-card-handle').focus()", HtmlContent);
    }

    [Fact]
    public void Html_CodecSave_AnnouncesOutcomeViaLiveRegion()
    {
        // The save-ok / save-failed i18n keys drive the aria-live announcement, so they are wired
        // through T() rather than sitting dead in the locale files.
        Assert.Contains("T('codecCardOrderSaved'", HtmlContent);
        Assert.Contains("T('codecCardOrderSaveFailed'", HtmlContent);
    }

    [Fact]
    public void Html_CodecSave_RollsBackToPreviousOrderOnFailure()
    {
        // A failed PUT must restore the previously persisted order, never re-render the rejected one.
        Assert.Contains("function flushQueuedCodecOrderSave", HtmlContent);
        Assert.Contains("prevOrder", HtmlContent);
    }

    [Fact]
    public void Html_CodecCommit_IgnoresDetachedCardsFromRescan()
    {
        // A background rescan can detach dragged nodes mid-interaction; only cards still in the live
        // document are persisted, so a stale snapshot never reaches the server.
        Assert.Contains("document.contains(box)", HtmlContent);
    }

    [Fact]
    public void Html_DragHandleIcon_IsRegisteredInIconMap()
    {
        // The grip calls mi('drag_indicator'); if the icon map lacks that key, mi() returns '' and
        // the handle renders 0px wide - invisible on desktop and untappable on touch. Assert the
        // path is actually present in the composed map, not just that the call site exists.
        Assert.Contains("\"drag_indicator\":\"M11 18c0 1.1", HtmlContent);
        Assert.Contains("mi('drag_indicator')", HtmlContent);
    }

    [Fact]
    public void Html_CodecTouchDrop_StampsTouchEndToSuppressCompatClick()
    {
        // A touch drag ends over a donut/row; the browser then fires a compatibility click there.
        // Stamping _lastTouchEndTime at drag-end makes the existing click guards swallow it, so a
        // drop never opens an unintended drill-down.
        Assert.Contains("_lastTouchEndTime = Date.now()", HtmlContent);
        Assert.Contains("wasActive && state.isTouch", HtmlContent);
    }

    [Fact]
    public void Html_CodecCommit_AbortsWhenChartsRowDetached()
    {
        // If the whole row is detached mid-drag, reading the DOM yields [] and the server would
        // normalize that to the full canonical order, wiping the user's layout. The commit must
        // bail on a detached row instead of persisting empty.
        Assert.Contains("!document.contains(chartsRow)", HtmlContent);
    }

    [Fact]
    public void Html_CodecSaveIndicator_AnchorsToCardNotHandle()
    {
        // The save overlay must span the card, not the ~1em handle (where it overlaps the grip).
        // Both drop paths pass the card element as the feedback target.
        Assert.Contains("commitCodecCardOrderFromDom(state.chartsRow, state.card)", HtmlContent);
        Assert.Contains("commitCodecCardOrderFromDom(chartsRow, card)", HtmlContent);
    }

    [Fact]
    public void Html_CodecCommit_RebasesPartialDomOntoFullOrder()
    {
        // Cards hidden this scan are absent from the DOM; committing only visible ids would append
        // them canonically at the end next save. The commit rebases onto the last full order so a
        // temporarily data-less card keeps its custom slot.
        Assert.Contains("function reorderFullOrder", HtmlContent);
        Assert.Contains("reorderFullOrder(prevOrder, domIds)", HtmlContent);
    }

    [Fact]
    public void Html_CodecLoad_GuardsStaleGetWithGenerationCounter()
    {
        // A config GET issued before a drop must not overwrite the newer local order when it
        // returns. The load snapshots the generation and bails if a commit bumped it meanwhile.
        Assert.Contains("_codecOrderGeneration", HtmlContent);
        Assert.Contains("_codecOrderGeneration !== genAtRequest", HtmlContent);
    }

    [Fact]
    public void Html_CodecPointerDrag_ReflectsGrabStateToAssistiveTech()
    {
        // aria-grabbed was only toggled on the keyboard path; pointer/touch drags left it stale.
        // beginCodecDrag now sets it true and teardown resets it, matching the keyboard behaviour.
        Assert.Contains("state.handle.setAttribute('aria-grabbed', 'true')", HtmlContent);
        Assert.Contains("state.handle.setAttribute('aria-grabbed', 'false')", HtmlContent);
    }

    [Fact]
    public void Html_CodecDrag_DropsDeadCardWidthField()
    {
        // state.cardWidth was written but never read; removing it keeps the drag state honest.
        Assert.DoesNotContain("state.cardWidth", HtmlContent);
    }

    [Fact]
    public void Html_CodecDrop_SettlesCardIntoFlowBeforeInsert()
    {
        // The card must leave its fixed-position lift before being moved to the new slot, otherwise
        // it shows for a frame as a fixed element at the new DOM position (reads as the card
        // vanishing). The settle call precedes the DOM move in the drop path.
        Assert.Contains("function settleCardIntoFlow", HtmlContent);
        // In onCodecDragPointerUp the settle happens before the card is placed at the placeholder.
        var dropSettleAt = HtmlContent.IndexOf("settleCardIntoFlow(state);", StringComparison.Ordinal);
        var insertAt = HtmlContent.IndexOf("state.placeholder.before(state.card)", StringComparison.Ordinal);
        Assert.True(dropSettleAt >= 0 && insertAt >= 0 && dropSettleAt < insertAt);
    }

    [Fact]
    public void Html_CodecSave_ShowsCornerBadgeNotHandleOverlay()
    {
        // Save feedback is a dedicated top-right corner badge on the card, not the shared overlay
        // anchored to the ~1em handle (where it overlapped the grip).
        Assert.Contains("function showCodecSaveBadge", HtmlContent);
        Assert.Contains("showCodecSaveBadge(feedbackEl, true)", HtmlContent);
        Assert.Contains("showCodecSaveBadge(feedbackEl, false)", HtmlContent);
        Assert.DoesNotContain("showAutoSaveIndicatorOverlay(feedbackEl", HtmlContent);
        // The badge anchors to the enclosing card and renders in its corner.
        Assert.Contains("feedbackEl.closest('.chart-box')", HtmlContent);
        Assert.Contains("codec-save-badge", HtmlContent);
    }

    [Fact]
    public void Html_CodecSaveBadge_AnchorsToCardCorner()
    {
        // The card is the positioning context and the badge sits in its top-right corner.
        Assert.Matches(@"\.chart-box\s*\{[^}]*position:\s*relative", HtmlContent);
        Assert.Matches(@"\.codec-save-badge\s*\{[^}]*position:\s*absolute", HtmlContent);
    }

    [Fact]
    public void Html_CodecPlaceholder_UsesNearestCardHitTest()
    {
        // The placeholder follows the card nearest the pointer (euclidean), not a row-major
        // first-match that snaps between columns at every center line.
        Assert.Contains("nearestDist", HtmlContent);
        Assert.Contains("(dx * dx) + (dy * dy)", HtmlContent);
    }
}
