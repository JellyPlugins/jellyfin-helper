using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

[Collection("ConfigOverride")]
public class LogsControllerTests : IDisposable
{
    private readonly PluginLogService _log = new();
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

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, _log.GetCount());
    }
}