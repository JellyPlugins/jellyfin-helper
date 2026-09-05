using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

public partial class TrendsHtmlTests : ConfigPageTestBase
{
    /// <summary>
    ///     Verifies the trend tab elements, functions, endpoint, and timeline references are present.
    /// </summary>
    /// <param name="marker">The trend-related marker expected in the HTML.</param>
    [Theory]
    [InlineData("id=\"tab-trends\"")]
    [InlineData("id=\"trendChartContainer\"")]
    [InlineData("function renderTrendChart")]
    [InlineData("function loadTrendData")]
    [InlineData("function formatGranularityLabel")]
    [InlineData("function bucketStartDate")]
    [InlineData("function projectToGranularity")]
    [InlineData("function pickLevelForSpan")]
    [InlineData("function drawTrendWindow")]
    [InlineData("JellyfinHelper/GrowthTimeline")]
    [InlineData("timeline.dataPoints")]
    [InlineData("timeline.granularity")]
    [InlineData("timeline.totalDirectoriesScanned")]
    [InlineData("timeline.earliestFileDate")]
    public void Html_ContainsTrendMarker(string marker)
    {
        Assert.Contains(marker, HtmlContent);
    }

    /// <summary>
    ///     Verifies the insights tab elements, functions, and endpoint are present.
    /// </summary>
    /// <param name="marker">The insights-related marker expected in the HTML.</param>
    [Theory]
    [InlineData("id=\"insightsContainer\"")]
    [InlineData("function loadInsightsData")]
    [InlineData("function renderInsightCards")]
    [InlineData("function toggleInsightPanel")]
    [InlineData("JellyfinHelper/LibraryInsights")]
    [InlineData("insight-card")]
    public void Html_ContainsInsightsMarker(string marker)
    {
        Assert.Contains(marker, HtmlContent);
    }

    [GeneratedRegex(@"(?:fullDaily|dailyPoints|projected|dataPoints)\[[^\]]+\]\.(\w+)")]
    private static partial Regex DataPointPropertyRegex();

    [GeneratedRegex(@"\b(?:p|pt|point)\.(cumulativeSize|cumulativeFileCount|date)\b")]
    private static partial Regex AliasPointPropertyRegex();

    [GeneratedRegex(@"timeline\.(\w+)")]
    private static partial Regex TimelinePropertyRegex();

    [Fact]
    public void Html_TrendChart_AllReferencedDataPointProperties_ExistOnClass()
    {
        var pointProperties = typeof(GrowthTimelinePoint)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The renderer accesses daily points as fullDaily[i].prop / dailyPoints[i].prop /
        // projected[i].prop and via aliases like p.cumulativeSize in projectToGranularity.
        // Any JS property name read off a point array must map to a real C# property so a
        // rename cannot silently desync training/serve field names.
        var indexed = DataPointPropertyRegex().Matches(HtmlContent)
            .Select(m => m.Groups[1].Value);
        var aliased = AliasPointPropertyRegex().Matches(HtmlContent)
            .Select(m => m.Groups[1].Value);
        var referenced = indexed.Concat(aliased)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(referenced);

        foreach (var prop in referenced)
        {
            Assert.Contains(prop, pointProperties);
        }
    }

    [Fact]
    public void Html_TrendChart_AllReferencedResultProperties_ExistOnClass()
    {
        var resultProperties = typeof(GrowthTimelineResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Check timeline.xxx references (excluding dataPoints access via [])
        var timelineRefs = TimelinePropertyRegex().Matches(HtmlContent)
            .Select(m => m.Groups[1].Value)
            .Where(p => !string.Equals(p, "dataPoints", StringComparison.OrdinalIgnoreCase)
                     || resultProperties.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(timelineRefs);

        foreach (var prop in timelineRefs)
        {
            Assert.Contains(prop, resultProperties);
        }
    }

    [Fact]
    public void Html_FormatGranularityLabel_HandlesAllGranularities()
    {
        // The JS function handles the four zoom granularity levels (quarterly was removed).
        Assert.Contains("'yearly'", HtmlContent);
        Assert.Contains("'monthly'", HtmlContent);
        Assert.Contains("'weekly'", HtmlContent);
        Assert.Contains("'daily'", HtmlContent);
        Assert.DoesNotContain("'quarterly'", HtmlContent);
    }

    [Fact]
    public void Html_TrendChart_UsesI18nKeys()
    {
        // The trend chart should use the i18n translation function for dynamic text
        Assert.Contains("T('trendEmpty'", HtmlContent);
        Assert.Contains("T('trendGranularity'", HtmlContent);
        Assert.Contains("T('trendFiles'", HtmlContent);
        Assert.Contains("T('trendEarliest'", HtmlContent);
        Assert.Contains("T('trendError'", HtmlContent);
        Assert.Contains("T('trendNow'", HtmlContent);
    }

    [Fact]
    public void Html_TrendChart_ContainsDiffPanel()
    {
        // The trend chart should include a diff panel for hover comparison
        Assert.Contains("trend-diff-panel", HtmlContent);
        Assert.Contains("trend-diff-content", HtmlContent);
        Assert.Contains("trend-diff-dates", HtmlContent);
        Assert.Contains("trend-diff-size", HtmlContent);
        Assert.Contains("trend-diff-files", HtmlContent);
    }

    [Fact]
    public void Html_TrendChart_ContainsDiffPanelInteraction()
    {
        // The interaction handler should update and hide the diff panel
        Assert.Contains("function updateDiffPanel", HtmlContent);
        Assert.Contains("function hideDiffPanel", HtmlContent);
    }

    [Fact]
    public void Html_TrendChart_ContainsZoomPanGestures()
    {
        // Desktop wheel zoom + drag pan and the window-mutation helpers.
        Assert.Contains("function zoomAbout", HtmlContent);
        Assert.Contains("function panByPixels", HtmlContent);
        Assert.Contains("'wheel'", HtmlContent);
        // Mobile pinch uses finger distance.
        Assert.Contains("Math.hypot", HtmlContent);
    }

    [Fact]
    public void Css_TrendChart_DisablesNativeTouchGestures()
    {
        // Horizontal pan/pinch is handled in JS; vertical swipe is left to the browser for page scroll.
        Assert.Contains("touch-action: pan-y", HtmlContent);
    }

    [Fact]
    public void Html_TrendChart_GeneratesSvg()
    {
        // The renderTrendChart function should generate SVG elements
        Assert.Contains("<svg", HtmlContent);
        Assert.Contains("<polyline", HtmlContent);
        Assert.Contains("<polygon", HtmlContent);
        Assert.Contains("<circle", HtmlContent);
    }

    // -- Insights section -------------------------------------------

    [Fact]
    public void Html_Insights_UsesI18nKeys()
    {
        Assert.Contains("T('insightLargest'", HtmlContent);
        Assert.Contains("T('insightRecent'", HtmlContent);
        Assert.Contains("T('insightNoData'", HtmlContent);
        Assert.Contains("T('insightsError'", HtmlContent);
        Assert.Contains("T('loadingInsights'", HtmlContent);
    }

    [Fact]
    public void Html_Insights_ContainsHelperFunctions()
    {
        Assert.Contains("function getInsightTypeBadge", HtmlContent);
        Assert.Contains("function formatInsightDate", HtmlContent);
    }
}
