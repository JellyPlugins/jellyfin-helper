using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

public class TrendsHtmlTests : ConfigPageTestBase
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

    [Fact]
    public void Html_TrendChart_AllReferencedDataPointProperties_ExistOnClass()
    {
        var pointProperties = typeof(GrowthTimelinePoint)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The renderer accesses daily points as fullDaily[i].prop / dailyPoints[i].prop /
        // projected[i].prop. Any JS property name read off a point array must map to a real
        // C# property so a rename cannot silently desync training/serve field names.
        var referenced = Regex.Matches(
                HtmlContent,
                @"(?:fullDaily|dailyPoints|projected|dataPoints)\[[^\]]+\]\.(\w+)")
            .Select(m => m.Groups[1].Value)
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
        var timelineRefs = Regex.Matches(HtmlContent, @"timeline\.(\w+)")
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
