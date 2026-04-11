using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
/// General page structure and README quality tests.
/// Module-specific tests live in OverviewHtmlTests, CodecsHtmlTests,
/// HealthHtmlTests, TrendsHtmlTests, SettingsHtmlTests, ArrIntegrationHtmlTests.
/// </summary>
public class ConfigPageHtmlTests : ConfigPageTestBase
{
    // ── General page structure ─────────────────────────────────────────

    [Fact]
    public void Html_ContainsPluginConfigPageClass()
    {
        Assert.Contains("pluginConfigurationPage", HtmlContent);
    }

    [Fact]
    public void Html_ContainsJellyfinHelperPageId()
    {
        Assert.Contains("JellyfinHelperConfigPage", HtmlContent);
    }

    // ── README.md quality checks ─────────────────────────────────────────

    [Fact]
    public void Readme_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ReadmeContent), "README.md could not be loaded or is empty.");
    }

    [Fact]
    public void Readme_DoesNotContainObsoleteAlignAttribute()
    {
        var pattern = new Regex(@"<\w+[^>]*\balign\s*=", RegexOptions.IgnoreCase);
        Assert.DoesNotMatch(pattern, ReadmeContent);
    }

    [Theory]
    [InlineData("bgcolor")]
    [InlineData("valign")]
    [InlineData("cellpadding")]
    [InlineData("cellspacing")]
    [InlineData("border")]
    public void Readme_DoesNotContainObsoleteHtmlAttribute(string attribute)
    {
        var pattern = new Regex($@"<\w+[^>]*\b{attribute}\s*=", RegexOptions.IgnoreCase);
        Assert.DoesNotMatch(pattern, ReadmeContent);
    }

    [Fact]
    public void Readme_DoesNotContainDeprecatedHtmlTags()
    {
        var pattern = new Regex(@"<\s*/?\s*(center|font|marquee|blink)\b", RegexOptions.IgnoreCase);
        Assert.DoesNotMatch(pattern, ReadmeContent);
    }
}