using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
///     Tests for Shared.js - the central utility library used by every plugin page module.
/// </summary>
public class SharedHtmlTests : ConfigPageTestBase
{
    /// <summary>
    ///     Verifies the core utility functions are declared in the composed Shared.js.
    /// </summary>
    /// <param name="signature">The function signature marker expected in the HTML.</param>
    [Theory]
    [InlineData("function mi(name)")]
    [InlineData("function T(")]
    [InlineData("function loadTranslations")]
    [InlineData("function applyStaticTranslations")]
    [InlineData("function getCssVar")]
    [InlineData("function formatBytes")]
    [InlineData("function formatTimeAgo")]
    [InlineData("function escHtml")]
    [InlineData("function escAttr")]
    public void Html_ContainsUtilityFunction(string signature)
    {
        Assert.Contains(signature, HtmlContent);
    }

    /// <summary>
    ///     Verifies the UI helper functions are declared in the composed Shared.js.
    /// </summary>
    /// <param name="signature">The function signature marker expected in the HTML.</param>
    [Theory]
    [InlineData("function pluralize")]
    [InlineData("function showAutoSaveIndicatorOverlay")]
    [InlineData("function showButtonFeedback")]
    [InlineData("function createDialogOverlay")]
    [InlineData("function createDialogBtn")]
    [InlineData("function removeDialogById")]
    [InlineData("function attachTogglePanelHandlers")]
    [InlineData("function resolveArrInstances")]
    public void Html_ContainsUiFunction(string signature)
    {
        Assert.Contains(signature, HtmlContent);
    }

    [Fact]
    public void Html_MaterialIconRegistry_ContainsRequiredIcons()
    {
        var requiredIcons = new[]
        {
            "dashboard", "movie", "movie_filter", "settings", "link", "smart_toy",
            "assignment", "trending_up", "health_and_safety", "folder", "folder_open",
            "check_circle", "error", "warning", "schedule", "tv", "music_note",
            "expand_more", "expand_less", "delete", "download", "upload", "search",
            "cleaning_services"
        };
        foreach (var icon in requiredIcons)
        {
            Assert.Contains("\"" + icon + "\":", HtmlContent);
        }
    }

    [Fact]
    public void Html_MiHelper_ReturnsEmptyStringForUnknownIcons()
    {
        Assert.Matches(
            new Regex(@"function\s+mi\s*\([^)]*\)\s*\{[\s\S]*?if\s*\(\s*!d\s*\)\s*return\s*''"),
            HtmlContent);
    }

    [Fact]
    public void Html_ExposesSvgRegistry()
    {
        Assert.Contains("var SVG =", HtmlContent);
        Assert.Contains("REFRESH", HtmlContent);
        Assert.Contains("EYE", HtmlContent);
    }

    [Fact]
    public void Html_LoadTranslations_CallsTranslationsEndpoint()
    {
        Assert.Contains("JellyfinHelper/Translations", HtmlContent);
    }

    [Fact]
    public void Html_TranslationHelper_UsesHasOwnPropertyGuard()
    {
        Assert.Matches(
            new Regex(@"function\s+T\s*\([^)]*\)\s*\{[\s\S]*?(hasOwnProperty\.call|Object\.hasOwn)"),
            HtmlContent);
    }

    [Fact]
    public void Html_FormatBytes_HandlesZero()
    {
        Assert.Matches(new Regex(@"function\s+formatBytes[\s\S]*?bytes\s*===\s*0"), HtmlContent);
    }

    [Fact]
    public void Html_FormatBytes_HandlesNegative()
    {
        Assert.Matches(new Regex(@"function\s+formatBytes[\s\S]*?bytes\s*<\s*0"), HtmlContent);
    }

    [Fact]
    public void Html_FormatBytes_UsesStandardUnits()
    {
        Assert.Matches(
            new Regex(@"\[\s*'B'\s*,\s*'KB'\s*,\s*'MB'\s*,\s*'GB'\s*,\s*'TB'\s*\]"),
            HtmlContent);
    }

    [Theory]
    [InlineData("justNow")]
    [InlineData("minuteAgo")]
    [InlineData("minutesAgo")]
    [InlineData("hourAgo")]
    [InlineData("hoursAgo")]
    [InlineData("dayAgo")]
    [InlineData("daysAgo")]
    public void Html_FormatTimeAgo_UsesI18nKey(string key)
    {
        Assert.Contains("'" + key + "'", HtmlContent);
    }

    [Theory]
    [InlineData("&amp;")]
    [InlineData("&lt;")]
    [InlineData("&gt;")]
    [InlineData("&quot;")]
    [InlineData("&#39;")]
    public void Html_EscHtml_EscapesDangerousCharacter(string entity)
    {
        // escHtml must escape & < > " '
        Assert.Matches(
            new Regex(@"function\s+escHtml[\s\S]*?" + Regex.Escape(entity)),
            HtmlContent);
    }

    [Theory]
    [InlineData("function getPathSegments")]
    [InlineData("function buildPathTree")]
    [InlineData("function countTreeItems")]
    [InlineData("function renderTreeLevel")]
    [InlineData("function renderFileTree")]
    public void Html_ContainsPathTreeFunction(string signature)
    {
        Assert.Contains(signature, HtmlContent);
    }

    [Fact]
    public void Html_GetPathSegments_NormalizesBackslashes()
    {
        Assert.Matches(
            new Regex(@"function\s+getPathSegments[\s\S]*?replaceAll\('\\\\'"),
            HtmlContent);
    }

    [Fact]
    public void Html_GetPathSegments_PicksLongestRootMatch()
    {
        // Longest prefix wins - correctness for nested library roots.
        Assert.Matches(
            new Regex(@"function\s+getPathSegments[\s\S]*?root\.length\s*>\s*bestRoot\.length"),
            HtmlContent);
    }

    [Theory]
    [InlineData("hasMovies")]
    [InlineData("hasTvShows")]
    [InlineData("hasMusic")]
    [InlineData("hasOther")]
    public void Html_RenderFileTree_HasCategoryVariable(string varName)
    {
        Assert.Contains(varName, HtmlContent);
    }

    [Fact]
    public void Html_RenderFileTree_ShowsEmptyStateWhenNoFiles()
    {
        Assert.Contains("noFilesFound", HtmlContent);
        Assert.Contains("file-tree-empty", HtmlContent);
    }

    [Fact]
    public void Html_RenderFileTree_HasExpandAndCollapseAllButtons()
    {
        Assert.Contains("expandAll", HtmlContent);
        Assert.Contains("collapseAll", HtmlContent);
    }

    [Theory]
    [InlineData("badge-movies")]
    [InlineData("badge-tvshows")]
    [InlineData("badge-music")]
    [InlineData("badge-other")]
    public void Html_RenderFileTree_UsesBadgeClass(string cssClass)
    {
        Assert.Contains(cssClass, HtmlContent);
    }

    [Fact]
    public void Html_TreeToggle_HasKeyboardAccessibility()
    {
        // renderTreeLevel emits tabindex="0" and role="button" via JS-escaped attributes. The exact escape sequence in the compiled HTML uses \' (single-quoted attribute values), so match either form defensively.
        Assert.Contains("aria-expanded", HtmlContent);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(@"role\s*=\s*(?:\\?['""])button(?:\\?['""])"),
            HtmlContent);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(@"tabindex\s*=\s*(?:\\?['""])0(?:\\?['""])"),
            HtmlContent);
    }

    [Theory]
    [InlineData("function aggregateDict")]
    [InlineData("function collectFlatPaths")]
    [InlineData("function collectDictPaths")]
    public void Html_ContainsAggregateFunction(string signature)
    {
        Assert.Contains(signature, HtmlContent);
    }

    [Fact]
    public void Html_AggregateDict_UsesHasOwnPropertyGuard()
    {
        Assert.Matches(
            new Regex(@"function\s+aggregateDict[\s\S]*?(hasOwnProperty\.call|Object\.hasOwn)"),
            HtmlContent);
    }

    [Fact]
    public void Html_Pluralize_UsesCountOne()
    {
        Assert.Matches(new Regex(@"function\s+pluralize[\s\S]*?count\s*===\s*1"), HtmlContent);
    }

    [Fact]
    public void Html_AutoSaveIndicator_HandlesSelectElements()
    {
        Assert.Matches(
            new Regex(@"function\s+showAutoSaveIndicatorOverlay[\s\S]*?tagName\s*===\s*['""]SELECT['""]"),
            HtmlContent);
    }

    [Fact]
    public void Html_AutoSaveIndicator_UsesGuardCounterForRaceConditions()
    {
        // The race-condition guard is a monotonically-incremented counter stored on the element's dataset (dataset.saveGuard); accessed via the `.dataset` DOM property rather than get/setAttribute('data-save-guard').
        Assert.Contains("dataset.saveGuard", HtmlContent);
    }

    [Fact]
    public void Html_AutoSaveIndicator_UsesSuccessColorVar()
    {
        Assert.Contains("--color-success", HtmlContent);
    }

    [Fact]
    public void Html_AutoSaveIndicator_UsesDangerColorVar()
    {
        Assert.Contains("--color-danger", HtmlContent);
    }

    [Fact]
    public void Html_AutoSaveIndicator_HasSuccessAndErrorDelays()
    {
        // Success fades after 2s, error after 3s
        Assert.Matches(new Regex(@"ok\s*\?\s*2000\s*:\s*3000"), HtmlContent);
    }

    [Fact]
    public void Html_ShowButtonFeedback_TogglesSuccessErrorClass()
    {
        Assert.Matches(
            new Regex(@"function\s+showButtonFeedback[\s\S]*?classList\.remove\(\s*['""]success['""]\s*,\s*['""]error['""]"),
            HtmlContent);
    }

    [Theory]
    [InlineData("function apiGet(")]
    [InlineData("function apiPost(")]
    [InlineData("function apiPut(")]
    [InlineData("function apiDelete(")]
    [InlineData("function apiGetText(")]
    [InlineData("function apiPostRaw(")]
    [InlineData("function apiGetOptional(")]
    [InlineData("function apiFetchBlob(")]
    public void Html_ContainsApiWrapper(string signature)
    {
        Assert.Contains(signature, HtmlContent);
    }

    [Theory]
    [InlineData("apiGet", "GET")]
    [InlineData("apiPost", "POST")]
    [InlineData("apiPut", "PUT")]
    [InlineData("apiDelete", "DELETE")]
    public void Html_ApiWrapper_UsesCorrectHttpMethod(string fn, string method)
    {
        Assert.Matches(
            new Regex(@"function\s+" + fn + @"\s*\([^)]*\)\s*\{[\s\S]*?type\s*:\s*['""]" + method + @"['""]"),
            HtmlContent);
    }

    [Fact]
    public void Html_ApiGetOptional_Uses204NoContentHandling()
    {
        Assert.Matches(
            new Regex(@"function\s+apiGetOptional[\s\S]*?response\.status\s*===\s*204"),
            HtmlContent);
    }

    [Fact]
    public void Html_ApiFetchBlob_UsesAuthorizationHeader()
    {
        Assert.Matches(
            new Regex(@"function\s+apiFetchBlob[\s\S]*?Authorization[\s\S]*?accessToken\(\)"),
            HtmlContent);
    }

    [Fact]
    public void Html_ApiPost_JsonStringifiesObjectPayloads()
    {
        Assert.Matches(
            new Regex(@"function\s+apiPost[\s\S]*?JSON\.stringify"),
            HtmlContent);
    }

    [Fact]
    public void Html_ApiWrapper_HasDefaultErrorHandler()
    {
        Assert.Contains("function _apiDefaultError", HtmlContent);
    }

    [Fact]
    public void Html_ApiDefaultError_LogsToConsole()
    {
        Assert.Matches(
            new Regex(@"function\s+_apiDefaultError[\s\S]*?console\.error"),
            HtmlContent);
    }

    [Fact]
    public void Html_CreateDialogBtn_SupportsAllStyleVariants()
    {
        // cancel, danger, success, and primary/warning fallback
        Assert.Matches(new Regex(@"function\s+createDialogBtn[\s\S]*?['""]cancel['""]"), HtmlContent);
        Assert.Matches(new Regex(@"function\s+createDialogBtn[\s\S]*?['""]danger['""]"), HtmlContent);
        Assert.Matches(new Regex(@"function\s+createDialogBtn[\s\S]*?['""]success['""]"), HtmlContent);
    }

    [Fact]
    public void Html_AttachTogglePanel_GuardsAgainstDoubleBinding()
    {
        // toggleBound dataset flag prevents duplicate listeners
        Assert.Matches(
            new Regex(@"function\s+attachTogglePanelHandlers[\s\S]*?toggleBound"),
            HtmlContent);
    }

    [Fact]
    public void Html_AttachTogglePanel_IsKeyboardAccessible()
    {
        // Enter/Space triggers click
        Assert.Matches(
            new Regex(@"function\s+attachTogglePanelHandlers[\s\S]*?['""]Enter['""][\s\S]*?['""] ['""]"),
            HtmlContent);
    }

    [Fact]
    public void Html_AttachTogglePanel_ClosesOtherPanelsOnOpen()
    {
        Assert.Matches(
            new Regex(@"function\s+attachTogglePanelHandlers[\s\S]*?file-tree-panel-visible"),
            HtmlContent);
    }

    [Fact]
    public void Html_ResolveArrInstances_HandlesNullConfig()
    {
        Assert.Matches(new Regex(@"function\s+resolveArrInstances[\s\S]*?!cfg[\s\S]*?return\s*\[\]"), HtmlContent);
    }

    [Fact]
    public void Html_ExposesDonutColorPalette()
    {
        Assert.Contains("DONUT_COLORS", HtmlContent);
    }

    [Fact]
    public void Html_DonutPalette_HasAtLeastTenColors()
    {
        // Match array of hex color strings
        var match = Regex.Match(HtmlContent, @"var\s+DONUT_COLORS\s*=\s*\[([^\]]+)\]");
        Assert.True(match.Success, "DONUT_COLORS array not found.");
        var colorCount = Regex.Matches(match.Groups[1].Value, @"#[0-9a-fA-F]{6}").Count;
        Assert.True(colorCount >= 10, $"Expected at least 10 donut colors, found {colorCount}.");
    }
}
