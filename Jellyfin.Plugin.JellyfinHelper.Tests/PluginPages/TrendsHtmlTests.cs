using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

public class TrendsHtmlTests : ConfigPageTestBase
{
    [Fact]
    public void Html_ContainsTrendsTab()
    {
        Assert.Contains("id=\"tab-trends\"", HtmlContent);
    }

    [Fact]
    public void Html_TrendChart_AllReferencedSnapshotProperties_ExistOnClass()
    {
        var snapshotProperties = typeof(StatisticsSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var fnMatch = Regex.Match(
            HtmlContent,
            @"function\s+renderTrendChart\s*\([^)]*\)\s*\{(?<body>[\s\S]*?)\n\s{4}\}",
            RegexOptions.Multiline);
        Assert.True(fnMatch.Success, "renderTrendChart function not found.");

        var referenced = Regex.Matches(fnMatch.Groups["body"].Value, @"snapshots\[[^\]]+\]\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(referenced);

        foreach (var prop in referenced)
        {
            Assert.Contains(prop, snapshotProperties);
        }
    }
}