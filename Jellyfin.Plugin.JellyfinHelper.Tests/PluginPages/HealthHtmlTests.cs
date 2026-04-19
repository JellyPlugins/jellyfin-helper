using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

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

    // === New tests: Health paths split by Movies/TvShows, no Music ===

    [Fact]
    public void Html_ContainsCollectHealthPathsFunction()
    {
        Assert.Contains("function collectHealthPaths", HtmlContent);
    }

    [Fact]
    public void Html_CollectHealthPaths_UsesMovies()
    {
        // collectHealthPaths should iterate over data.Movies
        Assert.Contains("data.Movies", HtmlContent);
    }

    [Fact]
    public void Html_CollectHealthPaths_UsesTvShows()
    {
        // collectHealthPaths should iterate over data.TvShows
        Assert.Contains("data.TvShows", HtmlContent);
    }

    [Fact]
    public void Html_CollectHealthPaths_UsesOther()
    {
        // collectHealthPaths should iterate over data.Other
        Assert.Contains("data.Other", HtmlContent);
    }

    [Fact]
    public void Html_CollectHealthPaths_ReturnsExpectedResultStructure()
    {
        // collectHealthPaths should return an object with movies, tvShows, other, music, AND rootPaths
        // music and rootPaths.music should always be empty
        Assert.Contains("return {", HtmlContent);
        Assert.Contains("music: []", HtmlContent);
        Assert.Contains("rootPaths: {", HtmlContent);
        Assert.Contains("movies: data.MovieRootPaths || []", HtmlContent);
        Assert.Contains("tvShows: data.TvShowRootPaths || []", HtmlContent);
        Assert.Contains("other: data.OtherRootPaths || []", HtmlContent);
        Assert.Contains("music: []", HtmlContent);
    }

    [Fact]
    public void Html_ContainsHealthPathMap()
    {
        Assert.Contains("HEALTH_PATH_MAP", HtmlContent);
        Assert.Contains("VideosWithoutSubtitlesPaths", HtmlContent);
        Assert.Contains("VideosWithoutImagesPaths", HtmlContent);
        Assert.Contains("VideosWithoutNfoPaths", HtmlContent);
        Assert.Contains("OrphanedMetadataDirectoriesPaths", HtmlContent);
    }

    [Fact]
    public void Html_HealthClickHandler_UsesCollectHealthPaths()
    {
        // The health click handler should use collectHealthPaths inside renderContent (within attachHealthClickHandlers)
        Assert.Matches(
            new Regex(
                @"function\s+attachHealthClickHandlers\s*\(\s*\)\s*\{[\s\S]*?renderContent\s*:\s*function[\s\S]*?collectHealthPaths\(\s*_lastScanResult\s*,\s*mapping\.prop\s*\)",
                RegexOptions.Multiline),
            HtmlContent);
    }

    [Fact]
    public void Html_HealthClickHandler_UsesSharedRenderFileTree()
    {
        // The health click handler should use the shared renderFileTree function
        Assert.Contains("renderFileTree(result, title)", HtmlContent);
    }

    [Fact]
    public void Html_ContainsAttachHealthClickHandlers()
    {
        Assert.Contains("function attachHealthClickHandlers", HtmlContent);
    }

    [Fact]
    public void Html_HealthClickHandlers_UseHealthDetailPanel()
    {
        Assert.Contains("healthDetailPanel", HtmlContent);
    }

    [Fact]
    public void Html_RenderHealthChecks_AddsClickableAttribute()
    {
        // Health items should have the data-health-type attribute for click handling
        Assert.Contains("data-health-type", HtmlContent);
        Assert.Contains("health-clickable", HtmlContent);
    }

    [Fact]
    public void Html_ContainsSharedRenderFileTreeFunction()
    {
        // renderFileTree should be in Shared.js (available to both Codecs + Health)
        Assert.Contains("function renderFileTree", HtmlContent);
    }

    [Fact]
    public void Html_FileTreeCssInShared()
    {
        // File tree CSS classes should be present (now in Shared.css)
        Assert.Contains("file-tree-header", HtmlContent);
        Assert.Contains("file-tree-columns", HtmlContent);
        Assert.Contains("tree-view", HtmlContent);
        Assert.Contains("tree-node", HtmlContent);
    }
}