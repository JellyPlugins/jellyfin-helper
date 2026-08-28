using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
/// Tests that the composed configPage.html contains all expected Discover (Recommendations) tab elements,
/// API calls, functions, and i18n keys.
/// </summary>
public class DiscoverHtmlTests : ConfigPageTestBase
{
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

    [Fact]
    public void Html_ContainsRecommendationsCss()
    {
        // Recommendations.css should be included via build-time composition
        Assert.Contains(".recs-empty", HtmlContent);
    }

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

    [Fact]
    public void Html_ContainsDiscoverTabSmartToyIcon()
    {
        // The Discover tab uses the smart_toy Material Icon via mi("smart_toy")
        Assert.Contains("\"smart_toy\"", HtmlContent);
    }
}