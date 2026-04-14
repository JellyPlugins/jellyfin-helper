using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

public class ArrIntegrationHtmlTests : ConfigPageTestBase
{
    [Fact]
    public void Html_ContainsArrTab()
    {
        Assert.Contains("id=\"tab-arr\"", HtmlContent);
        Assert.Contains("id=\"arrContent\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsInitArrButtonsFunction()
    {
        Assert.Contains("function initArrButtons", HtmlContent);
    }
}
