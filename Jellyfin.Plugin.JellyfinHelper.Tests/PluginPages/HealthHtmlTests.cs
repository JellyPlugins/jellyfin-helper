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
    public void Html_CollectHealthPaths_ReturnsMusicEmpty()
    {
        // collectHealthPaths should always return music: [] because health checks don't apply to music
        Assert.Contains("return { movies: moviePaths, tvShows: tvPaths, other: otherPaths, music: [] }", HtmlContent);
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
        // The health click handler should use collectHealthPaths to gather paths
        Assert.Contains("collectHealthPaths(_lastScanData, mapping.prop)", HtmlContent);
    }

    [Fact]
    public void Html_HealthClickHandler_UsesSharedRenderFileList()
    {
        // The health click handler should use the shared renderFileList function
        Assert.Contains("renderFileList(result, title)", HtmlContent);
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
    public void Html_ContainsSharedGetFileNameFunction()
    {
        // getFileName should be in shared.js (available to both Codecs + Health)
        Assert.Contains("function getFileName", HtmlContent);
    }

    [Fact]
    public void Html_ContainsSharedGetParentFolderFunction()
    {
        // getParentFolder should be in shared.js (available to both Codecs + Health)
        Assert.Contains("function getParentFolder", HtmlContent);
    }

    [Fact]
    public void Html_ContainsSharedRenderFileListFunction()
    {
        // renderFileList should be in shared.js (available to both Codecs + Health)
        Assert.Contains("function renderFileList", HtmlContent);
    }

    [Fact]
    public void Html_FileListCssInShared()
    {
        // File list CSS classes should be present (now in shared.css)
        Assert.Contains("codec-files-header", HtmlContent);
        Assert.Contains("codec-files-columns", HtmlContent);
        Assert.Contains("codec-file-item", HtmlContent);
        Assert.Contains("codec-file-name", HtmlContent);
    }
}