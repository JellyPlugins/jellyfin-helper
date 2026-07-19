using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
///     Tests for <see cref="Plugin"/> — the plugin bootstrap that handles index.html script
///     injection (fallback path), on-disk data-file cleanup during uninstall, and recommendation
///     playlist directory purge.
///     <para>
///         All tests here operate on a real temp filesystem: the code under test writes
///         index.html, deletes <c>jellyfin-helper-*.json</c> data files, and shells out to
///         <c>Directory.GetDirectories</c> on the playlists path — pure filesystem operations
///         with no viable mock path. Each test uses a unique <see cref="Path.GetTempPath"/>
///         subdirectory that is cleaned up in <see cref="IDisposable.Dispose"/>.
///     </para>
///     <para>
///         Static-state hazard: <see cref="Plugin.Instance"/> is set in the constructor of every
///         Plugin. This class deliberately builds a fresh Plugin per test to exercise the
///         constructor code paths. The <c>Instance</c> pointer is overwritten, but since each
///         test only asserts against the local plugin reference it constructs, cross-test
///         pollution is contained.
///     </para>
/// </summary>
[Collection("ConfigOverride")]
public sealed class PluginTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _webPath;
    private readonly string _dataPath;

    public PluginTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "JellyfinHelperPluginTests_" + Guid.NewGuid().ToString("N"));
        _webPath = Path.Combine(_tempRoot, "web");
        _dataPath = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(_webPath);
        Directory.CreateDirectory(_dataPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private Plugin CreatePlugin()
    {
        // TestMockFactory sets DataPath, PluginConfigurationsPath, PluginsPath, LogDirectoryPath
        // and ConfigurationDirectoryPath. BasePlugin<T>..ctor calls Path.Combine on some of these
        // so we must ensure each is non-null. We then add WebPath on top, because Plugin
        // (not BasePlugin) reads it via IndexHtmlPath.
        var appPathsMock = TestMockFactory.CreateAppPaths(dataPath: _dataPath, configPath: _dataPath);
        appPathsMock.Setup(p => p.WebPath).Returns(_webPath);

        var xmlSerializerMock = new Mock<IXmlSerializer>();
        var loggerMock = new Mock<ILogger<Plugin>>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        return new Plugin(appPathsMock.Object, xmlSerializerMock.Object, loggerMock.Object);
    }

    [Fact]
    public void Ctor_SetsInstanceStatic()
    {
        var plugin = CreatePlugin();
        Assert.Same(plugin, Plugin.Instance);
    }

    [Fact]
    public void Ctor_ExposesNameAndDescription()
    {
        var plugin = CreatePlugin();
        Assert.Equal("Jellyfin Helper", plugin.Name);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Description));
        Assert.Equal(Guid.Parse("0c737645-5cbb-4bd8-80c7-d377b560aaa4"), plugin.Id);
    }

    [Fact]
    public void GetPages_ReturnsSingleMainMenuEntry()
    {
        // BUG GUARD: a single PluginPageInfo must be registered. Duplicates would surface as
        // a doubled menu entry in Jellyfin's sidebar; a wrong name would break the URL routing.
        var plugin = CreatePlugin();
        var pages = plugin.GetPages().ToList();
        Assert.Single(pages);
        var page = pages[0];
        Assert.Equal("Jellyfin Helper", page.Name);
        Assert.Equal("Jellyfin Helper", page.DisplayName);
        Assert.True(page.EnableInMainMenu);
        Assert.Equal("handyman", page.MenuIcon);
        Assert.EndsWith(".PluginPages.configPage.html", page.EmbeddedResourcePath, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateIndexHtml_Inject_InsertsScriptTagBeforeBody()
    {
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(indexPath, "<html><body><h1>Jellyfin</h1></body></html>");

        var plugin = CreatePlugin();
        plugin.UpdateIndexHtml(true);

        var content = File.ReadAllText(indexPath);
        Assert.Contains("plugin=\"Jellyfin Helper\"", content, StringComparison.Ordinal);
        Assert.Contains("../JellyfinHelper/Discovery/My/script", content, StringComparison.Ordinal);
        var scriptIdx = content.IndexOf("plugin=\"Jellyfin Helper\"", StringComparison.Ordinal);
        var bodyCloseIdx = content.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        Assert.True(scriptIdx < bodyCloseIdx, "script tag must be inserted BEFORE </body>");
    }

    [Fact]
    public void UpdateIndexHtml_Inject_IsIdempotent()
    {
        // BUG GUARD: repeated calls must not stack <script> tags.
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(indexPath, "<html><body></body></html>");

        var plugin = CreatePlugin();
        plugin.UpdateIndexHtml(true);
        plugin.UpdateIndexHtml(true);
        plugin.UpdateIndexHtml(true);

        var content = File.ReadAllText(indexPath);
        var occurrences = Regex.Matches(content, "plugin=\"Jellyfin Helper\"").Count;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void UpdateIndexHtml_Inject_ReplacesOldVersionOfScript()
    {
        // BUG GUARD: old version tag from a previous plugin build must be replaced, not appended.
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(
            indexPath,
            "<html><body>" +
            "<script plugin=\"Jellyfin Helper\" version=\"0.0.1\" src=\"../old-url\" defer></script>" +
            "</body></html>");

        var plugin = CreatePlugin();
        plugin.UpdateIndexHtml(true);

        var content = File.ReadAllText(indexPath);
        Assert.DoesNotContain("../old-url", content, StringComparison.Ordinal);
        Assert.Contains("../JellyfinHelper/Discovery/My/script", content, StringComparison.Ordinal);
        var occurrences = Regex.Matches(content, "plugin=\"Jellyfin Helper\"").Count;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void UpdateIndexHtml_Inject_WithoutBodyTag_LeavesFileUnchanged()
    {
        // BUG GUARD: minified index.html without </body> must not cause a crash or half-write.
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(indexPath, "<html><head></head></html>");

        var plugin = CreatePlugin();
        plugin.UpdateIndexHtml(true);

        var content = File.ReadAllText(indexPath);
        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateIndexHtml_Inject_MissingIndexFile_DoesNotThrow()
    {
        // BUG GUARD: on rolling-update deployments WebPath may temporarily lack index.html.
        var indexPath = Path.Combine(_webPath, "index.html");
        if (File.Exists(indexPath))
        {
            File.Delete(indexPath);
        }

        var plugin = CreatePlugin();
        var ex = Record.Exception(() => plugin.UpdateIndexHtml(true));
        Assert.Null(ex);
    }

    [Fact]
    public void UpdateIndexHtml_Remove_StripsExistingScript()
    {
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(
            indexPath,
            "<html><body>" +
            "<script plugin=\"Jellyfin Helper\" version=\"1.0.0\" src=\"../foo\" defer></script>" +
            "</body></html>");

        var plugin = CreatePlugin();
        plugin.UpdateIndexHtml(false);

        var content = File.ReadAllText(indexPath);
        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", content, StringComparison.Ordinal);
        Assert.Contains("<body>", content, StringComparison.Ordinal);
        Assert.Contains("</body>", content, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateIndexHtml_Remove_WhenNoScriptExists_IsNoOp()
    {
        // BUG GUARD: unconditional rewrite would churn AtomicFile temp files on every call.
        // The plugin constructor ALWAYS invokes UpdateIndexHtml (via InjectScript's fallback
        // path) as part of its normal bootstrap. Under test the FileTransformation plugin is
        // never present, so the ctor's UpdateIndexHtml(true) writes a fresh script tag on our
        // behalf. To exercise the "no matching script → skip write" fast path we must first
        // strip that tag with UpdateIndexHtml(false), THEN snapshot the mtime, THEN invoke
        // UpdateIndexHtml(false) once more — the second call is the real assertion target.
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(indexPath, "<html><body>clean</body></html>");

        var plugin = CreatePlugin();
        plugin.UpdateIndexHtml(false); // clear whatever the ctor injected + our sentinel

        var contentAfterFirstRemove = File.ReadAllText(indexPath);
        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", contentAfterFirstRemove, StringComparison.Ordinal);

        var mtimeBefore = File.GetLastWriteTimeUtc(indexPath);
        System.Threading.Thread.Sleep(50);

        plugin.UpdateIndexHtml(false); // second call — must be a strict no-op

        var content = File.ReadAllText(indexPath);
        Assert.Equal(contentAfterFirstRemove, content);
        var mtimeAfter = File.GetLastWriteTimeUtc(indexPath);
        Assert.Equal(mtimeBefore, mtimeAfter);
    }

    [Fact]
    public void UpdateIndexHtml_Remove_MultiplePluginScripts_StripsAll()
    {
        // BUG GUARD: RemovalRegex.Replace must strip ALL matches — a naive
        // IndexOf/Substring rewrite would miss the second tag.
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(
            indexPath,
            "<html><body>" +
            "<script plugin=\"Jellyfin Helper\" version=\"1.0\" src=\"a\" defer></script>" +
            "<div>content</div>" +
            "<script plugin=\"Jellyfin Helper\" version=\"1.1\" src=\"b\" defer></script>" +
            "</body></html>");

        var plugin = CreatePlugin();
        plugin.UpdateIndexHtml(false);

        var content = File.ReadAllText(indexPath);
        Assert.DoesNotContain("plugin=\"Jellyfin Helper\"", content, StringComparison.Ordinal);
        Assert.Contains("<div>content</div>", content, StringComparison.Ordinal);
    }

    [Fact]
    public void OnUninstalling_DeletesJellyfinHelperJsonFiles()
    {
        var dataFile1 = Path.Combine(_dataPath, "jellyfin-helper-statistics-latest.json");
        var dataFile2 = Path.Combine(_dataPath, "jellyfin-helper-recommendations-latest.json");
        var tmpLeftover = Path.Combine(_dataPath, "jellyfin-helper-growth-timeline.tmp");
        File.WriteAllText(dataFile1, "{}");
        File.WriteAllText(dataFile2, "{}");
        File.WriteAllText(tmpLeftover, string.Empty);

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.False(File.Exists(dataFile1));
        Assert.False(File.Exists(dataFile2));
        Assert.False(File.Exists(tmpLeftover));
    }

    [Fact]
    public void OnUninstalling_PreservesUnrelatedFilesInDataPath()
    {
        // BUG GUARD: a wildcard change to "jellyfin-*" or "*.json" would nuke unrelated files.
        var pluginFile = Path.Combine(_dataPath, "jellyfin-helper-statistics-latest.json");
        var unrelated1 = Path.Combine(_dataPath, "jellyfin.db");
        var unrelated2 = Path.Combine(_dataPath, "some-other-plugin-data.json");
        var unrelated3 = Path.Combine(_dataPath, "jellyfin-helper-something.log"); // wrong extension
        File.WriteAllText(pluginFile, "{}");
        File.WriteAllText(unrelated1, "db");
        File.WriteAllText(unrelated2, "{}");
        File.WriteAllText(unrelated3, "log");

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.False(File.Exists(pluginFile));
        Assert.True(File.Exists(unrelated1));
        Assert.True(File.Exists(unrelated2));
        // The .log file matches "jellyfin-helper-*" but must be preserved by the extension guard.
        Assert.True(File.Exists(unrelated3));
    }

    [Fact]
    public void OnUninstalling_MissingDataDirectory_DoesNotThrow()
    {
        Directory.Delete(_dataPath, recursive: true);

        var plugin = CreatePlugin();
        var ex = Record.Exception(() => plugin.OnUninstalling());
        Assert.Null(ex);
    }

    [Fact]
    public void OnUninstalling_DeletesRecommendationPlaylistFolders()
    {
        var playlistsRoot = Path.Combine(_dataPath, "playlists");
        Directory.CreateDirectory(playlistsRoot);
        var managed = Path.Combine(playlistsRoot, "🎬 Recommended for Alice");
        var userOwned = Path.Combine(playlistsRoot, "My Awesome Playlist");
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(userOwned);
        File.WriteAllText(Path.Combine(managed, "playlist.xml"), "<x/>");
        File.WriteAllText(Path.Combine(userOwned, "playlist.xml"), "<x/>");

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.False(Directory.Exists(managed));
        Assert.True(Directory.Exists(userOwned));
    }

    [Fact]
    public void OnUninstalling_PlaylistNamePrefixMismatch_DoesNotDelete()
    {
        // BUG GUARD: only exact "🎬 Recommended for " prefix must delete.
        var playlistsRoot = Path.Combine(_dataPath, "playlists");
        Directory.CreateDirectory(playlistsRoot);
        var closelyNamed = Path.Combine(playlistsRoot, "🎬 Recommended Movies of 2024");
        Directory.CreateDirectory(closelyNamed);

        var plugin = CreatePlugin();
        plugin.OnUninstalling();

        Assert.True(Directory.Exists(closelyNamed));
    }

    [Fact]
    public void OnUninstalling_MissingPlaylistsDirectory_DoesNotThrow()
    {
        // No playlists dir exists — must short-circuit without throwing.
        var plugin = CreatePlugin();
        var ex = Record.Exception(() => plugin.OnUninstalling());
        Assert.Null(ex);
    }
}
