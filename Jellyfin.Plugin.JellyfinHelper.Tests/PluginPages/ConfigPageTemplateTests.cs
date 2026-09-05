using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
///     Tests for the composed configPage.html template shell (configPage.template.html with CSS_CONTENT and JS_CONTENT placeholders filled in by the build task).
/// </summary>
public class ConfigPageTemplateTests : ConfigPageTestBase
{
    /// <summary>
    ///     Verifies the template structure and emby-styling markers survive composition.
    /// </summary>
    /// <param name="marker">The structure or emby marker expected in the HTML.</param>
    [Theory]
    [InlineData("data-role=\"page\"")]
    [InlineData("emby-checkbox")]
    [InlineData("emby-select")]
    [InlineData("type-interior")]
    [InlineData("content-primary")]
    [InlineData("data-role=\"content\"")]
    [InlineData("<h2>Jellyfin Helper</h2>")]
    [InlineData("pluginConfigurationPage")]
    public void Html_ContainsStructureMarker(string marker)
    {
        Assert.Contains(marker, HtmlContent);
    }

    /// <summary>
    ///     Verifies the stats and element-id markers survive composition.
    /// </summary>
    /// <param name="marker">The stats or id marker expected in the HTML.</param>
    [Theory]
    [InlineData("stats-header")]
    [InlineData("stats-container")]
    [InlineData("id=\"statsContent\"")]
    [InlineData("id=\"statsPlaceholder\"")]
    [InlineData("id=\"statsResult\"")]
    [InlineData("id=\"loadingIndicator\"")]
    [InlineData("id=\"JellyfinHelperConfigPage\"")]
    public void Html_ContainsStatsMarker(string marker)
    {
        Assert.Contains(marker, HtmlContent);
    }

    /// <summary>
    ///     Verifies the raw build-task placeholders were replaced during composition.
    /// </summary>
    /// <param name="placeholder">The placeholder comment that must not survive.</param>
    [Theory]
    [InlineData("/* CSS_CONTENT */")]
    [InlineData("/* JS_CONTENT */")]
    public void Html_DoesNotContainRawPlaceholder(string placeholder)
    {
        Assert.DoesNotContain(placeholder, HtmlContent);
    }

    [Fact]
    public void Html_HasEmbyInputRequirement()
    {
        Assert.Contains("data-require=", HtmlContent);
        Assert.Contains("emby-input", HtmlContent);
    }

    [Fact]
    public void Html_ContainsInlineStyleTag()
    {
        // After composition the <style> block must still exist and hold real CSS.
        Assert.Contains("<style>", HtmlContent);
        Assert.Contains("</style>", HtmlContent);
    }

    [Fact]
    public void Html_ContainsInlineScriptTag()
    {
        Assert.Contains("<script type=\"text/javascript\">", HtmlContent);
        Assert.Contains("</script>", HtmlContent);
    }

    [Fact]
    public void Html_StyleBlockIsNonEmpty()
    {
        var match = Regex.Match(HtmlContent, @"<style>([\s\S]*?)</style>");
        Assert.True(match.Success, "No <style> block found.");
        Assert.True(match.Groups[1].Value.Trim().Length > 100,
            "Composed <style> block is unexpectedly small - CSS assets may be missing.");
    }

    [Fact]
    public void Html_ScriptBlockIsNonEmpty()
    {
        var match = Regex.Match(HtmlContent, @"<script type=""text/javascript"">([\s\S]*?)</script>");
        Assert.True(match.Success, "No inline <script> block found.");
        Assert.True(match.Groups[1].Value.Trim().Length > 5000,
            "Composed <script> block is unexpectedly small - JS assets may be missing.");
    }

    [Fact]
    public void Html_ContainsLastScanBadgeContainer()
    {
        Assert.Contains("last-scan-badge", HtmlContent);
        Assert.Contains("id=\"lastScanBadge\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsScanButton()
    {
        Assert.Contains("id=\"btnScanLibraries\"", HtmlContent);
        Assert.Contains("scan-libraries-btn", HtmlContent);
    }

    [Fact]
    public void Html_ScanButton_HasAccessibleLabel()
    {
        // The template lists aria-label before id, so match the containing <button ...> tag holistically.
        Assert.Matches(new Regex(@"<button\b[^>]*id=""btnScanLibraries""[^>]*>", RegexOptions.Singleline), HtmlContent);
        Assert.Matches(new Regex(@"<button\b[^>]*\baria-label=""[^""]+""[^>]*id=""btnScanLibraries""|<button\b[^>]*id=""btnScanLibraries""[^>]*\baria-label=""", RegexOptions.Singleline), HtmlContent);
    }

    [Fact]
    public void Html_ScanButton_HasTitle()
    {
        Assert.Matches(new Regex(@"<button\b[^>]*\btitle=""[^""]+""[^>]*id=""btnScanLibraries""|<button\b[^>]*id=""btnScanLibraries""[^>]*\btitle=""", RegexOptions.Singleline), HtmlContent);
    }

    [Fact]
    public void Html_StatsResult_HiddenByDefault()
    {
        Assert.Matches(new Regex(@"id=""statsResult""[^>]*style=""display:none;"""), HtmlContent);
    }

    [Fact]
    public void Html_LoadingIndicator_HiddenByDefault()
    {
        Assert.Matches(new Regex(@"id=""loadingIndicator""[^>]*style=""display:none;"""), HtmlContent);
    }

    [Fact]
    public void Html_LoadingIndicator_HasSpinner()
    {
        Assert.Matches(new Regex(@"id=""loadingIndicator""[\s\S]*?class=""spinner"""), HtmlContent);
    }
}