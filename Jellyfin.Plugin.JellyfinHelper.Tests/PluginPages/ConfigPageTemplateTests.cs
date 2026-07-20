using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
/// Tests for the composed configPage.html template shell (configPage.template.html
/// with CSS_CONTENT and JS_CONTENT placeholders filled in by the build task).
/// Covers: Jellyfin plugin-page metadata, header, scan button, loading indicator,
/// placeholder, and stats result container.
/// </summary>
public class ConfigPageTemplateTests : ConfigPageTestBase
{
    // === Page shell metadata (required by the Jellyfin plugin page loader) ===

    [Fact]
    public void Html_HasDataRolePage()
    {
        Assert.Contains("data-role=\"page\"", HtmlContent);
    }

    [Fact]
    public void Html_HasEmbyInputRequirement()
    {
        Assert.Contains("data-require=", HtmlContent);
        Assert.Contains("emby-input", HtmlContent);
    }

    [Fact]
    public void Html_HasEmbyCheckboxRequirement()
    {
        Assert.Contains("emby-checkbox", HtmlContent);
    }

    [Fact]
    public void Html_HasEmbySelectRequirement()
    {
        Assert.Contains("emby-select", HtmlContent);
    }

    [Fact]
    public void Html_HasTypeInteriorClass()
    {
        Assert.Contains("type-interior", HtmlContent);
    }

    [Fact]
    public void Html_HasContentPrimarySection()
    {
        Assert.Contains("content-primary", HtmlContent);
    }

    [Fact]
    public void Html_HasDataRoleContentWrapper()
    {
        Assert.Contains("data-role=\"content\"", HtmlContent);
    }

    // === Template placeholders must be replaced (not shipped as-is) ===

    [Fact]
    public void Html_DoesNotContainRawCssPlaceholder()
    {
        // If the placeholder still appears verbatim, the build task failed.
        Assert.DoesNotContain("/* CSS_CONTENT */", HtmlContent);
    }

    [Fact]
    public void Html_DoesNotContainRawJsPlaceholder()
    {
        Assert.DoesNotContain("/* JS_CONTENT */", HtmlContent);
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

    // === Header structure ===

    [Fact]
    public void Html_ContainsPluginHeader()
    {
        Assert.Contains("<h2>Jellyfin Helper</h2>", HtmlContent);
    }

    [Fact]
    public void Html_ContainsStatsHeader()
    {
        Assert.Contains("stats-header", HtmlContent);
    }

    [Fact]
    public void Html_ContainsLastScanBadgeContainer()
    {
        Assert.Contains("last-scan-badge", HtmlContent);
        Assert.Contains("id=\"lastScanBadge\"", HtmlContent);
    }

    // === Scan button ===

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

    // === Stats containers ===

    [Fact]
    public void Html_ContainsStatsContainer()
    {
        Assert.Contains("stats-container", HtmlContent);
    }

    [Fact]
    public void Html_ContainsStatsContent()
    {
        Assert.Contains("id=\"statsContent\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsStatsPlaceholder()
    {
        Assert.Contains("id=\"statsPlaceholder\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsStatsResult()
    {
        Assert.Contains("id=\"statsResult\"", HtmlContent);
    }

    [Fact]
    public void Html_StatsResult_HiddenByDefault()
    {
        Assert.Matches(new Regex(@"id=""statsResult""[^>]*style=""display:none;"""), HtmlContent);
    }

    // === Loading overlay ===

    [Fact]
    public void Html_ContainsLoadingIndicator()
    {
        Assert.Contains("id=\"loadingIndicator\"", HtmlContent);
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

    // === Page ID ===

    [Fact]
    public void Html_RootPageElementHasExpectedId()
    {
        Assert.Contains("id=\"JellyfinHelperConfigPage\"", HtmlContent);
    }

    [Fact]
    public void Html_RootPageHasPluginConfigurationPageClass()
    {
        // Required by Jellyfin to render inside the plugin admin area
        Assert.Contains("pluginConfigurationPage", HtmlContent);
    }
}