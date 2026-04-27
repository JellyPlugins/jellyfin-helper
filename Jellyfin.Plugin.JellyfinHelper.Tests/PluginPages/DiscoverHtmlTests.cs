using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
/// Tests that the composed configPage.html contains all expected Discover (Recommendations) tab elements,
/// API calls, functions, and i18n keys.
/// </summary>
public class DiscoverHtmlTests : ConfigPageTestBase
{
    // === Tab registration ===

    [Fact]
    public void Html_ContainsDiscoverTabButton()
    {
        Assert.Contains("data-tab=\"recommendations\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsDiscoverTabContent()
    {
        Assert.Contains("id=\"tab-recommendations\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsInitRecommendationsTabFunction()
    {
        Assert.Contains("function initRecommendationsTab()", HtmlContent);
    }

    [Fact]
    public void Html_ContainsLoadRecommendationsFunction()
    {
        Assert.Contains("function loadRecommendations()", HtmlContent);
    }

    [Fact]
    public void Html_ContainsRenderRecommendationsFunction()
    {
        Assert.Contains("function renderRecommendations(", HtmlContent);
    }

    [Fact]
    public void Html_ContainsRenderUserRecommendationsFunction()
    {
        Assert.Contains("function renderUserRecommendations(", HtmlContent);
    }

    [Fact]
    public void Html_ContainsOnUserChangedFunction()
    {
        Assert.Contains("function onUserChanged(", HtmlContent);
    }

    // === API calls ===

    [Fact]
    public void Html_ContainsRecommendationsApiCall()
    {
        Assert.Contains("JellyfinHelper/Recommendations", HtmlContent);
    }

    [Fact]
    public void Html_ContainsWatchProfileApiCall()
    {
        Assert.Contains("JellyfinHelper/Recommendations/WatchProfile/", HtmlContent);
    }

    [Fact]
    public void Html_ContainsUserActivityApiCall()
    {
        Assert.Contains("JellyfinHelper/UserActivity/User/", HtmlContent);
    }

    // === UI elements ===

    [Fact]
    public void Html_ContainsRecsContentContainer()
    {
        Assert.Contains("id=\"recsContent\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsRecsUserGridContainer()
    {
        Assert.Contains("id=\"recsUserGrid\"", HtmlContent);
    }

    // === i18n keys ===

    [Theory]
    [InlineData("tabRecommendations")]
    [InlineData("recsTitle")]
    [InlineData("loadingRecommendations")]
    [InlineData("recsError")]
    [InlineData("recsEmpty")]
    [InlineData("recsTotal")]
    [InlineData("recsItems")]
    [InlineData("recsNoItems")]
    public void Html_ContainsI18nKey(string key)
    {
        Assert.Contains($"'{key}'", HtmlContent);
    }

    // === CSS ===

    [Fact]
    public void Html_ContainsRecommendationsCss()
    {
        // Recommendations.css should be included via build-time composition
        Assert.Contains(".recs-empty", HtmlContent);
    }

    // === Settings integration ===

    [Fact]
    public void Html_ContainsRecommendationsTaskModeSelect()
    {
        Assert.Contains("cfgRecommendationsMode", HtmlContent);
    }

    [Fact]
    public void Html_ContainsUpdateRecsTabVisibilityFunction()
    {
        Assert.Contains("function updateRecsTabVisibility(", HtmlContent);
    }

    // === Tab icon ===

    [Fact]
    public void Html_ContainsDiscoverTabEmoji()
    {
        // The Discover tab uses the robot emoji 🤖
        Assert.Contains("🤖", HtmlContent);
    }
}