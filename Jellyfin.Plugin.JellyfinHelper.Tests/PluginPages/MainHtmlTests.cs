using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
///     Tests for Main.js (page bootstrap / tab lifecycle) as embedded in the composed configPage.html.
/// </summary>
public class MainHtmlTests : ConfigPageTestBase
{
    [Theory]
    [InlineData("function initTabs")]
    [InlineData("function doTabSwitch")]
    [InlineData("function updateLastScanBadge")]
    [InlineData("function loadLatestStatistics")]
    [InlineData("function renderShell")]
    [InlineData("function fillScanData")]
    [InlineData("function loadStatistics")]
    [InlineData("function initPage")]
    [InlineData("function bindPageLifecycle")]
    public void Html_ContainsMainFunction(string signature)
    {
        Assert.Contains(signature, HtmlContent);
    }

    [Fact]
    public void Html_InitTabs_QueriesTabButtons()
    {
        Assert.Contains(".tab-btn", HtmlContent);
        Assert.Contains("data-tab", HtmlContent);
    }

    [Fact]
    public void Html_InitTabs_ChecksUnsavedChangesWhenLeavingSettings()
    {
        Assert.Contains("checkUnsavedAndProceed", HtmlContent);
        Assert.Contains("currentTab === 'settings'", HtmlContent);
    }

    /// <summary>
    ///     Verifies the page lifecycle and tab-init markers are present in the composed Main.js.
    /// </summary>
    /// <param name="marker">The lifecycle marker expected in the HTML.</param>
    [Theory]
    [InlineData("destroyLogsTab")]
    [InlineData("initRecommendationsTab")]
    [InlineData("initLogsTab")]
    [InlineData("_pageInitialized")]
    [InlineData("_handlersBound")]
    [InlineData("_pageLifecycleBound")]
    [InlineData("DOMContentLoaded")]
    public void Html_ContainsLifecycleMarker(string marker)
    {
        Assert.Contains(marker, HtmlContent);
    }

    /// <summary>
    ///     Verifies the shell, scan, and stats markers are present in the composed Main.js.
    /// </summary>
    /// <param name="marker">The shell or scan marker expected in the HTML.</param>
    [Theory]
    [InlineData("renderShell()")]
    [InlineData("#JellyfinHelperConfigPage")]
    [InlineData("lastScanBadge")]
    [InlineData("initializingScan")]
    [InlineData("JellyfinHelper/MediaStatistics/ScanLibraries")]
    [InlineData("statsLoadError")]
    public void Html_ContainsShellMarker(string marker)
    {
        Assert.Contains(marker, HtmlContent);
    }

    [Fact]
    public void Html_UpdateLastScanBadge_UsesFormatTimeAgo()
    {
        Assert.Matches(
            new Regex(@"function\s+updateLastScanBadge[\s\S]*?formatTimeAgo\s*\("),
            HtmlContent);
    }

    [Fact]
    public void Html_LoadLatestStatistics_UsesApiGetOptional()
    {
        Assert.Contains("apiGetOptional", HtmlContent);
        Assert.Contains("JellyfinHelper/MediaStatistics/Latest", HtmlContent);
    }

    [Fact]
    public void Html_LoadLatestStatistics_TriggersScanOnNoContent()
    {
        Assert.Matches(
            new Regex(@"function\s+loadLatestStatistics[\s\S]*?loadStatistics\s*\("),
            HtmlContent);
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("codecs")]
    [InlineData("health")]
    [InlineData("trends")]
    [InlineData("settings")]
    [InlineData("arr")]
    [InlineData("recommendations")]
    [InlineData("logs")]
    public void Html_RenderShell_CreatesTabButton(string tabId)
    {
        Assert.Contains("data-tab=\"" + tabId + "\"", HtmlContent);
    }

    [Fact]
    public void Html_RenderShell_RecommendationsTabHiddenByDefault()
    {
        Assert.Matches(
            new Regex(@"data-tab=""recommendations""[^>]*style=""display:none;"""),
            HtmlContent);
    }

    [Theory]
    [InlineData("id=\"tab-overview\"")]
    [InlineData("id=\"tab-codecs\"")]
    [InlineData("id=\"tab-health\"")]
    [InlineData("id=\"tab-trends\"")]
    [InlineData("id=\"tab-settings\"")]
    [InlineData("id=\"tab-arr\"")]
    [InlineData("id=\"tab-recommendations\"")]
    [InlineData("id=\"tab-logs\"")]
    public void Html_RenderShell_CreatesTabContent(string idAttr)
    {
        Assert.Contains(idAttr, HtmlContent);
    }

    [Theory]
    [InlineData("tabOverview")]
    [InlineData("tabCodecs")]
    [InlineData("tabHealth")]
    [InlineData("tabTrends")]
    [InlineData("tabSettings")]
    [InlineData("tabArr")]
    [InlineData("tabRecommendations")]
    [InlineData("tabLogs")]
    public void Html_RenderShell_UsesI18nKeyForTabLabel(string key)
    {
        Assert.Contains("'" + key + "'", HtmlContent);
    }

    [Theory]
    [InlineData("fillOverviewData")]
    [InlineData("fillCodecsData")]
    [InlineData("fillHealthData")]
    [InlineData("loadCleanupStats")]
    public void Html_FillScanData_Calls(string callee)
    {
        Assert.Matches(
            new Regex(@"function\s+fillScanData[\s\S]*?" + Regex.Escape(callee) + @"\s*\("),
            HtmlContent);
    }

    [Fact]
    public void Html_LoadStatistics_LoadsTrendsAfterScan()
    {
        Assert.Matches(
            new Regex(@"function\s+loadStatistics[\s\S]*?loadTrendData\s*\(\s*true\s*\)"),
            HtmlContent);
    }

    [Fact]
    public void Html_LoadStatistics_LoadsInsightsAfterScan()
    {
        Assert.Matches(
            new Regex(@"function\s+loadStatistics[\s\S]*?loadInsightsData\s*\("),
            HtmlContent);
    }

    [Fact]
    public void Html_InitPage_HasRetryCounter()
    {
        Assert.Contains("_initRetries", HtmlContent);
        Assert.Contains("_maxInitRetries", HtmlContent);
    }

    [Fact]
    public void Html_InitPage_RetriesUpTo20Times()
    {
        Assert.Matches(new Regex(@"_maxInitRetries\s*=\s*20"), HtmlContent);
    }

    [Fact]
    public void Html_InitPage_LoadsTranslationsBeforeRendering()
    {
        Assert.Matches(
            new Regex(@"loadTranslations\s*\(\s*function\s*\(\s*\)\s*\{[\s\S]*?applyStaticTranslations"),
            HtmlContent);
    }

    [Theory]
    [InlineData("pageshow")]
    [InlineData("viewshow")]
    [InlineData("pagehide")]
    [InlineData("viewhide")]
    public void Html_BindPageLifecycle_RegistersEvent(string eventName)
    {
        Assert.Contains("'" + eventName + "'", HtmlContent);
    }

    [Fact]
    public void Html_BindPageLifecycle_ResetsInitStateOnShow()
    {
        // When the page becomes visible again, init state must be reset for SPA navigation
        Assert.Matches(
            new Regex(@"pageshow[\s\S]*?_pageInitialized\s*=\s*false"),
            HtmlContent);
    }

    [Fact]
    public void Html_BindPageLifecycle_TeardownOnHide()
    {
        Assert.Matches(
            new Regex(@"pagehide[\s\S]*?destroyLogsTab"),
            HtmlContent);
    }

    [Fact]
    public void Html_ScanButton_HasSpinningClassSupport()
    {
        // The scan button must have both 'spinning' add and remove for proper feedback
        Assert.Matches(new Regex(@"classList\.add\(\s*['""]spinning['""]"), HtmlContent);
        Assert.Matches(new Regex(@"classList\.remove\(\s*['""]spinning['""]"), HtmlContent);
    }

    [Fact]
    public void Html_ScanButton_SetsRefreshSvgOnInit()
    {
        // btnScanLibraries gets its icon from SVG.REFRESH
        Assert.Contains("SVG.REFRESH", HtmlContent);
    }
}