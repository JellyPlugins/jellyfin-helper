using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.WebInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.WebInjection;

/// <summary>
///     Tests for the best-effort cleanup of index.html changes older plugin versions wrote to disk.
/// </summary>
public class LegacyIndexHtmlCleanupTests : IDisposable
{
    private readonly string _webPath;

    public LegacyIndexHtmlCleanupTests()
    {
        _webPath = Path.Combine(Path.GetTempPath(), "JellyfinHelperLegacyCleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_webPath))
            {
                Directory.Delete(_webPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void TryRemoveScriptTag_StripsOurTagAndKeepsForeignMarkup()
    {
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(
            indexPath,
            "<html><body><script plugin=\"Other Plugin\" src=\"a\"></script>"
            + "<script plugin=\"Jellyfin Helper\" version=\"1\" src=\"../JellyfinHelper/Discovery/My/script?v=1\"></script>\n"
            + "</body></html>");

        Assert.True(LegacyIndexHtmlCleanup.TryRemoveScriptTag(Mock.Of<IPluginLogService>(), NullLogger.Instance, _webPath));

        var content = File.ReadAllText(indexPath);
        Assert.DoesNotContain("Jellyfin Helper", content, StringComparison.Ordinal);
        Assert.Contains("Other Plugin", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRemoveScriptTag_LogsTheSuccessfulRemoval()
    {
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(indexPath, "<html><body><script plugin=\"Jellyfin Helper\" src=\"a\"></script></body></html>");
        var pluginLog = new Mock<IPluginLogService>();

        Assert.True(LegacyIndexHtmlCleanup.TryRemoveScriptTag(pluginLog.Object, NullLogger.Instance, _webPath));

        pluginLog.Verify(
            l => l.LogInfo(
                "Discovery",
                It.Is<string>(message => message.Contains("Removed a legacy on-disk script tag", StringComparison.Ordinal)),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public void TryDeleteStaleTempFiles_LogsTheRemovedCount()
    {
        var orphan = Path.Combine(_webPath, "index.html." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(orphan, "leftover");
        var pluginLog = new Mock<IPluginLogService>();

        LegacyIndexHtmlCleanup.TryDeleteStaleTempFiles(pluginLog.Object, NullLogger.Instance, _webPath);

        pluginLog.Verify(
            l => l.LogInfo(
                "Discovery",
                It.Is<string>(message => message.Contains("Removed 1 stale index.html temp file", StringComparison.Ordinal)),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public void TryRemoveScriptTag_WithoutOurTagDoesNotRewrite()
    {
        var indexPath = Path.Combine(_webPath, "index.html");
        var original = "<html><body></body></html>";
        File.WriteAllText(indexPath, original);

        Assert.False(LegacyIndexHtmlCleanup.TryRemoveScriptTag(Mock.Of<IPluginLogService>(), NullLogger.Instance, _webPath));
        Assert.Equal(original, File.ReadAllText(indexPath));
    }

    [Fact]
    public void TryRemoveScriptTag_SecondCallIsANoOp()
    {
        var indexPath = Path.Combine(_webPath, "index.html");
        File.WriteAllText(indexPath, "<html><body><script plugin=\"Jellyfin Helper\" src=\"a\"></script></body></html>");

        Assert.True(LegacyIndexHtmlCleanup.TryRemoveScriptTag(Mock.Of<IPluginLogService>(), NullLogger.Instance, _webPath));
        Assert.False(LegacyIndexHtmlCleanup.TryRemoveScriptTag(Mock.Of<IPluginLogService>(), NullLogger.Instance, _webPath));
    }

    [Fact]
    public void TryRemoveScriptTag_MissingFileReturnsFalse()
    {
        Assert.False(LegacyIndexHtmlCleanup.TryRemoveScriptTag(Mock.Of<IPluginLogService>(), NullLogger.Instance, _webPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRemoveScriptTag_MissingWebPathReturnsFalse(string? webPath)
    {
        Assert.False(LegacyIndexHtmlCleanup.TryRemoveScriptTag(Mock.Of<IPluginLogService>(), NullLogger.Instance, webPath));
    }

    [Fact]
    public void TryDeleteStaleTempFiles_RemovesOnlyOurAtomicTempFiles()
    {
        var orphan = Path.Combine(_webPath, "index.html." + Guid.NewGuid().ToString("N") + ".tmp");
        var unrelated = Path.Combine(_webPath, "main.chunk.js");
        File.WriteAllText(orphan, "leftover");
        File.WriteAllText(unrelated, "app");

        LegacyIndexHtmlCleanup.TryDeleteStaleTempFiles(Mock.Of<IPluginLogService>(), NullLogger.Instance, _webPath);

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void TryDeleteStaleTempFiles_MissingDirectoryDoesNotThrow()
    {
        var missing = Path.Combine(_webPath, "does-not-exist");

        var ex = Record.Exception(() => LegacyIndexHtmlCleanup.TryDeleteStaleTempFiles(Mock.Of<IPluginLogService>(), NullLogger.Instance, missing));

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryDeleteStaleTempFiles_MissingWebPathDoesNotThrow(string? webPath)
    {
        var ex = Record.Exception(() => LegacyIndexHtmlCleanup.TryDeleteStaleTempFiles(Mock.Of<IPluginLogService>(), NullLogger.Instance, webPath));

        Assert.Null(ex);
    }
}
