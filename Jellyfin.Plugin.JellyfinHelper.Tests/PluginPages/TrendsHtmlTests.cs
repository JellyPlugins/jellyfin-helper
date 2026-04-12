using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Services;
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
        Assert.Contains("Statistics/GrowthTimeline", HtmlContent);
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

        var fnMatch = Regex.Match(
            HtmlContent,
            @"function\s+renderTrendChart\s*\([^)]*\)\s*\{(?<body>[\s\S]*?)\n\s{4}\}",
            RegexOptions.Multiline);
        Assert.True(fnMatch.Success, "renderTrendChart function not found.");

        var referenced = Regex.Matches(fnMatch.Groups["body"].Value, @"dataPoints\[[^\]]+\]\.(\w+)")
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