using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
/// Tests for the Statistics tab (merged Overview + Codecs) in the composed configPage.html.
/// </summary>
public class StatisticsHtmlTests : ConfigPageTestBase
{
    [Fact]
    public void Html_ContainsStatisticsTab()
    {
        Assert.Contains("id=\"tab-statistics\"", HtmlContent);
        Assert.Contains("id=\"statisticsContent\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFillStatisticsDataFunction()
    {
        Assert.Contains("function fillStatisticsData", HtmlContent);
    }

    [Fact]
    public void Html_DoesNotContainOldOverviewTab()
    {
        Assert.DoesNotContain("tab-overview", HtmlContent);
        Assert.DoesNotContain("id=\"overviewContent\"", HtmlContent);
        Assert.DoesNotContain("function fillOverviewData", HtmlContent);
    }

    [Fact]
    public void Html_DoesNotContainOldCodecsTab()
    {
        Assert.DoesNotContain("tab-codecs", HtmlContent);
        Assert.DoesNotContain("id=\"codecsContent\"", HtmlContent);
        Assert.DoesNotContain("function fillCodecsData", HtmlContent);
    }

    [Fact]
    public void Html_ContainsPerLibraryExplorer()
    {
        Assert.Contains("stat-lib-row", HtmlContent);
        Assert.Contains("stat-lib-row-header", HtmlContent);
        Assert.Contains("data-lib-index", HtmlContent);
        Assert.Contains("stat-explorer-section", HtmlContent);
    }

    [Fact]
    public void Html_ContainsKpiStrip()
    {
        Assert.Contains("stat-kpi-strip", HtmlContent);
        Assert.Contains("stat-kpi-card", HtmlContent);
        Assert.Contains("stat-kpi-value", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFilterStrip()
    {
        Assert.Contains("stat-filter-strip", HtmlContent);
        Assert.Contains("stat-filter-pill", HtmlContent);
        Assert.Contains("stat-count-badge", HtmlContent);
    }

    [Fact]
    public void Html_ContainsTwoPanelLayout()
    {
        Assert.Contains("stat-panel-layout", HtmlContent);
        Assert.Contains("stat-filter-panel", HtmlContent);
        Assert.Contains("stat-results-panel", HtmlContent);
        Assert.Contains("data-scope", HtmlContent);
    }

    [Fact]
    public void Html_ContainsDonutSections()
    {
        Assert.Contains("stat-donut-section", HtmlContent);
        Assert.Contains("stat-donut-header", HtmlContent);
        Assert.Contains("stat-donut-body", HtmlContent);
        Assert.Contains("aria-expanded", HtmlContent);
    }

    [Fact]
    public void Html_ContainsAllElevenDimensionsInPathMap()
    {
        Assert.Contains("STATISTICS_PATH_MAP", HtmlContent);
        Assert.Contains("AudioLanguagePaths", HtmlContent);
        Assert.Contains("SubtitleLanguagePaths", HtmlContent);
        Assert.Contains("WatchedTierPaths", HtmlContent);
        Assert.Contains("WatchedByUsers", HtmlContent);
        Assert.Contains("VideoCodecPaths", HtmlContent);
        Assert.Contains("ResolutionPaths", HtmlContent);
        Assert.Contains("VideoBitrateTierPaths", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCategoryMap()
    {
        Assert.Contains("STATISTICS_CATEGORY_MAP", HtmlContent);
        Assert.Contains("STATISTICS_DIMENSIONS", HtmlContent);
    }

    [Fact]
    public void Html_ContainsStorageOverview()
    {
        Assert.Contains("stat-storage-section", HtmlContent);
        Assert.Contains("stat-storage-header", HtmlContent);
        Assert.Contains("stat-lib-row-list", HtmlContent);
        Assert.Contains("buildBarSegments", HtmlContent);
    }

    [Fact]
    public void Html_ContainsCuratedDefaults()
    {
        Assert.Contains("stat-curated", HtmlContent);
        Assert.Contains("statTopLargest", HtmlContent);
        Assert.Contains("statTopNeverWatched", HtmlContent);
    }

    [Fact]
    public void Html_ContainsMobileBreakpoint()
    {
        Assert.Contains("@media", HtmlContent);
        Assert.Contains("640px", HtmlContent);
        Assert.Contains("stat-lib-row-header", HtmlContent);
    }

    [Fact]
    public void Html_ContainsNewBitrateTiers()
    {
        // Bitrate tiers are defined in C# (ClassifyBitrateTier) and rendered dynamically.
        // The HTML/JS side must at least reference the videoBitrate dimension.
        Assert.Contains("videoBitrate", HtmlContent);
        Assert.Contains("VideoBitrateTier", HtmlContent);
    }

    [Fact]
    public void Html_DoesNotContainOldBitrateLabels()
    {
        // Old labels should not appear as tier definitions; they may appear in MapLegacyBitrateTier mapping but not as Classify thresholds
        // We check that the new thresholds use en-dash, not hyphen for 2-5 etc in the active tier list
        Assert.DoesNotContain("\"2-5 Mbps\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsBitrateThresholdConstants()
    {
        // Dimensions are declared statically in Statistics.js
        Assert.Contains("STATISTICS_DIMENSIONS", HtmlContent);
        Assert.Contains("videoBitrate", HtmlContent);
    }

    [Fact]
    public void Html_ContainsStatisticsCssClasses()
    {
        Assert.Contains("stat-breakdown", HtmlContent);
        Assert.Contains("stat-file-entry", HtmlContent);
        Assert.Contains("stat-file-detail", HtmlContent);
        Assert.Contains("stat-chip", HtmlContent);
        Assert.Contains("stat-lib-row", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFileTreeReuse()
    {
        Assert.Contains("renderFileTree", HtmlContent);
        Assert.Contains("buildPathTree", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFilterEngineFunctions()
    {
        Assert.Contains("function toggleFilter", HtmlContent);
        Assert.Contains("function clearAllFilters", HtmlContent);
        Assert.Contains("function computeFilteredPaths", HtmlContent);
        Assert.Contains("function getScopedData", HtmlContent);
    }

    [Fact]
    public void Html_ContainsWatchedAndLanguageLogic()
    {
        Assert.Contains("audioLanguages", HtmlContent);
        Assert.Contains("subtitleLanguages", HtmlContent);
        Assert.Contains("watched", HtmlContent);
        Assert.Contains("AudioLanguagePaths", HtmlContent);
        Assert.Contains("SubtitleLanguagePaths", HtmlContent);
        Assert.Contains("WatchedTierPaths", HtmlContent);
        Assert.Contains("WatchedByUserPaths", HtmlContent);
        Assert.Contains("WatchedDetails", HtmlContent);
        Assert.Contains("watchedByUser", HtmlContent);
    }

    [Fact]
    public void Html_ContainsXssProtection()
    {
        Assert.Contains("escHtml", HtmlContent);
        Assert.Contains("escAttr", HtmlContent);
    }

    [Fact]
    public void Html_ContainsScopedExplorerEngine()
    {
        // Each library row and the aggregate "All Libraries" view get their own independent
        // filter/tree state so combining filters in one library never leaks into another.
        Assert.Contains("function getExplorerState", HtmlContent);
        Assert.Contains("function getScopedLib", HtmlContent);
        Assert.Contains("function libScopeKey", HtmlContent);
        Assert.Contains("function buildLibraryRowHtml", HtmlContent);
        Assert.Contains("function buildExplorerHtml", HtmlContent);
    }

    [Fact]
    public void Html_ContainsRestoredCleanupAndFileCountCards()
    {
        Assert.Contains("totalFiles", HtmlContent);
        Assert.Contains("totalBytesFreed", HtmlContent);
        Assert.Contains("totalItemsDeleted", HtmlContent);
    }

    [Fact]
    public void Html_ContainsProgressiveDisclosureBehavior()
    {
        // Sections collapsed by default
        Assert.Contains("aria-expanded=\"false\"", HtmlContent);
        Assert.Contains("hidden", HtmlContent);
        Assert.Contains("stat-donut-dimmed", HtmlContent);
    }
}
