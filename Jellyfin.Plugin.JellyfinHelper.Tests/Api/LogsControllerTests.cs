using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

[Collection("ConfigOverride")]
public class LogsControllerTests : IDisposable
{
    private readonly PluginLogService _log = TestMockFactory.CreatePluginLogService();
    private readonly LogsController _controller;

    public LogsControllerTests()
    {
        var loggerMock = new Mock<ILogger<LogsController>>();
        _controller = new LogsController(_log, loggerMock.Object);
        _log.TestMinLevelOverride = "INFO";
        _log.Clear();
    }

    public void Dispose()
    {
        _log.TestMinLevelOverride = null;
        _log.Clear();
    }

    [Fact]
    public void GetLogs_ReturnsLogs()
    {
        _log.LogInfo("Test", "Message");

        var result = _controller.GetLogs();

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic data = okResult.Value!;
        Assert.Equal(1, (int)data.Returned);
    }

    [Fact]
    public void DownloadLogs_ReturnsFile()
    {
        _log.LogInfo("Test", "Download Message");

        var result = _controller.DownloadLogs();

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/plain", fileResult.ContentType);
    }

    [Fact]
    public void ClearLogs_ClearsLogs()
    {
        _log.LogInfo("Test", "To be cleared");

        var result = _controller.ClearLogs();

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, _log.GetCount());
    }

    [Fact]
    public void GetLogs_InvalidMinLevel_Returns400()
    {
        var result = _controller.GetLogs(minLevel: "hack");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetLogs_SourceTooLong_Returns400()
    {
        var source = new string('x', 201);

        var result = _controller.GetLogs(source: source);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetLogs_ValidMinLevel_Returns200()
    {
        var result = _controller.GetLogs(minLevel: "warn");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void DownloadLogs_InvalidMinLevel_Returns400()
    {
        // An unrecognized filter must reject the request, not export a bogus-filtered file.
        var result = _controller.DownloadLogs(minLevel: "hack");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DownloadLogs_SourceTooLong_Returns400()
    {
        // The >200-char source guard applies to the download path too, before any file is produced.
        var source = new string('x', 201);

        var result = _controller.DownloadLogs(source: source);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("../../etc")]
    public void GetLogs_SourceWithControlCharsOrMarkup_Returns400(string source)
    {
        var result = _controller.GetLogs(source: source);

        // Control characters carry no legitimate filter value; markup/traversal must not
        // break the response. Either a 400 or a safe 200 with no reflection is acceptable,
        // but control characters must be rejected outright.
        if (source.Contains('\r') || source.Contains('\n') || source.Contains('\0') || source.Contains('\t'))
        {
            Assert.IsType<BadRequestObjectResult>(result);
        }
        else
        {
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);
        }
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    public void DownloadLogs_SourceWithCrlf_Returns400(string source)
    {
        var result = _controller.DownloadLogs(source: source);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DownloadLogs_Filename_IsServerGenerated()
    {
        var result = _controller.DownloadLogs();

        var file = Assert.IsType<FileContentResult>(result);
        Assert.StartsWith("jellyfin-helper-logs-", file.FileDownloadName, StringComparison.Ordinal);
        Assert.EndsWith(".txt", file.FileDownloadName, StringComparison.Ordinal);
        Assert.DoesNotContain("..", file.FileDownloadName, StringComparison.Ordinal);
    }
}