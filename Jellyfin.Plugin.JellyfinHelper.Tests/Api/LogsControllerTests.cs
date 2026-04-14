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
    private readonly LogsController _controller;

    public LogsControllerTests()
    {
        var loggerMock = new Mock<ILogger<LogsController>>();
        _controller = new LogsController(loggerMock.Object);
        PluginLogService.TestMinLevelOverride = "INFO";
        PluginLogService.Clear();
    }

    public void Dispose()
    {
        PluginLogService.TestMinLevelOverride = null;
        PluginLogService.Clear();
    }

    [Fact]
    public void GetLogs_ReturnsLogs()
    {
        PluginLogService.LogInfo("Test", "Message");

        var result = _controller.GetLogs();

        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic data = okResult.Value!;
        Assert.Equal(1, (int)data.Returned);
    }

    [Fact]
    public void DownloadLogs_ReturnsFile()
    {
        PluginLogService.LogInfo("Test", "Download Message");

        var result = _controller.DownloadLogs();

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/plain", fileResult.ContentType);
    }

    [Fact]
    public void ClearLogs_ClearsLogs()
    {
        PluginLogService.LogInfo("Test", "To be cleared");

        var result = _controller.ClearLogs();

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, PluginLogService.GetCount());
    }
}
