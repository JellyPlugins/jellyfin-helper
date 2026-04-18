using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

public class TrendsHtmlTests : ConfigPageTestBase
{
    [Fact]
    public void Html_ContainsTrendsTab()
    {
        Assert.Contains("id=\"tab-trends\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsTrendChartContainer()
    {
        Assert.Contains("id=\"trendChartContainer\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsRenderTrendChartFunction()
    {
        Assert.Contains("function renderTrendChart", HtmlContent);
    }

    [Fact]
    public void Html_ContainsLoadTrendDataFunction()
    {
        Assert.Contains("function loadTrendData", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFormatGranularityLabelFunction()
    {
        Assert.Contains("function formatGranularityLabel", HtmlContent);
    }

    [Fact]
    public void Html_LoadTrendData_ReferencesCorrectApiEndpoint()
    {
        Assert.Contains("JellyfinHelper/GrowthTimeline", HtmlContent);
    }

    [Fact]
    public void Html_RenderTrendChart_ReferencesTimelineDataPoints()
    {
        Assert.Contains("timeline.dataPoints", HtmlContent);
    }

    [Fact]
    public void Html_RenderTrendChart_ReferencesTimelineGranularity()
    {
        Assert.Contains("timeline.granularity", HtmlContent);
    }

    [Fact]
    public void Html_RenderTrendChart_ReferencesTimelineTotalFilesScanned()
    {
        Assert.Contains("timeline.totalFilesScanned", HtmlContent);
    }

    [Fact]
    public void Html_RenderTrendChart_ReferencesTimelineEarliestFileDate()
    {
        Assert.Contains("timeline.earliestFileDate", HtmlContent);
    }

    [Fact]
    public void Html_TrendChart_AllReferencedDataPointProperties_ExistOnClass()
    {
        var pointProperties = typeof(GrowthTimelinePoint)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var start = HtmlContent.IndexOf("function renderTrendChart", StringComparison.Ordinal);
        Assert.True(start >= 0, "renderTrendChart function not found.");

        var bodyStart = HtmlContent.IndexOf('{', start);
        Assert.True(bodyStart >= 0, "renderTrendChart opening brace not found.");

        var depth = 0;
        var bodyEnd = -1;
        for (var i = bodyStart; i < HtmlContent.Length; i++)
        {
            if (HtmlContent[i] == '{')
            {
                depth++;
            }
            else if (HtmlContent[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    bodyEnd = i;
                    break;
                }
            }
        }

        Assert.True(bodyEnd > bodyStart, "renderTrendChart function body not found.");
        var functionBody = HtmlContent[(bodyStart + 1)..bodyEnd];

        var referenced = Regex.Matches(functionBody, @"dataPoints\[[^\]]+\]\.(\w+)")
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
        // The JS function should handle all 5 granularity levels
        Assert.Contains("'yearly'", HtmlContent);
        Assert.Contains("'quarterly'", HtmlContent);
        Assert.Contains("'monthly'", HtmlContent);
        Assert.Contains("'weekly'", HtmlContent);
        Assert.Contains("'daily'", HtmlContent);
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
}
