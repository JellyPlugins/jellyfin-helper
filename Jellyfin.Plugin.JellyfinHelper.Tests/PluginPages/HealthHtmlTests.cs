using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

public class HealthHtmlTests : ConfigPageTestBase
{
    [Fact]
    public void Html_ContainsHealthTab()
    {
        Assert.Contains("id=\"tab-health\"", HtmlContent);
        Assert.Contains("id=\"healthContent\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFillHealthDataFunction()
    {
        Assert.Contains("function fillHealthData", HtmlContent);
    }

    [Fact]
    public void Html_ContainsLoadTrashHealthSectionFunction()
    {
        Assert.Contains("function loadTrashHealthSection", HtmlContent);
    }

    [Fact]
    public void Html_LoadTrashHealthSection_ChecksConfig()
    {
        Assert.Contains("cfg.UseTrash", HtmlContent);
    }

    [Fact]
    public void Html_LoadTrashHealthSection_CallsEndpoint()
    {
        Assert.Contains("JellyfinHelper/Trash/Contents", HtmlContent);
    }

    [Fact]
    public void Html_TrashHealthUsesI18nKeys()
    {
        Assert.Contains("trashContents", HtmlContent);
        Assert.Contains("trashItems", HtmlContent);
        Assert.Contains("trashTotalSize", HtmlContent);
        Assert.Contains("trashRetentionDays", HtmlContent);
        Assert.Contains("trashEmpty", HtmlContent);
    }

    [Fact]
    public void Html_FillScanDataCallsTrashHealth()
    {
        Assert.Contains("loadTrashHealthSection()", HtmlContent);
    }
}
