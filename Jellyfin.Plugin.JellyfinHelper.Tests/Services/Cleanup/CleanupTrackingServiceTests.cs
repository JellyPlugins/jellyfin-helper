using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
/// Tests for <see cref="CleanupTrackingService"/>.
/// Uses a mock <see cref="ICleanupConfigHelper"/> that returns a shared <see cref="PluginConfiguration"/>
/// instance so that mutations in <see cref="CleanupTrackingService.RecordCleanup"/> are visible
/// in subsequent <see cref="CleanupTrackingService.GetStatistics"/> calls (Plugin.Instance is null in tests).
/// </summary>
public class CleanupTrackingServiceTests
{
    private readonly Mock<ILogger> _loggerMock = new();
    private readonly PluginConfiguration _config = new();
    private readonly CleanupTrackingService _trackingService;

    public CleanupTrackingServiceTests()
    {
        var configHelperMock = new Mock<ICleanupConfigHelper>();
        configHelperMock.Setup(c => c.GetConfig()).Returns(_config);

        var configServiceMock = TestMockFactory.CreateConfigurationService();
        var pluginLog = new PluginLogService(configServiceMock.Object);
        _trackingService = new CleanupTrackingService(configHelperMock.Object, configServiceMock.Object, pluginLog);
    }

    [Fact]
    public void GetStatistics_WhenPluginInstanceNull_ReturnsDefaults()
    {
        var (totalBytesFreed, totalItemsDeleted, lastCleanupTimestamp) = _trackingService.GetStatistics();

        Assert.Equal(0, totalBytesFreed);
        Assert.Equal(0, totalItemsDeleted);
        Assert.Equal(DateTime.MinValue, lastCleanupTimestamp);
    }

    [Fact]
    public void RecordCleanup_WhenPluginInstanceNull_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            _trackingService.RecordCleanup(1024, 5, _loggerMock.Object));

        Assert.Null(exception);
    }

    [Fact]
    public void RecordCleanup_WhenPluginInstanceNull_StatisticsAreUpdated()
    {
        _trackingService.RecordCleanup(2048, 10, _loggerMock.Object);

        var (totalBytesFreed, totalItemsDeleted, lastCleanupTimestamp) = _trackingService.GetStatistics();

        Assert.Equal(2048, totalBytesFreed);
        Assert.Equal(10, totalItemsDeleted);
        Assert.NotEqual(DateTime.MinValue, lastCleanupTimestamp);
    }

    [Fact]
    public void RecordCleanup_WithZeroValues_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            _trackingService.RecordCleanup(0, 0, _loggerMock.Object));

        Assert.Null(exception);
    }

    [Fact]
    public void RecordCleanup_AccumulatesMultipleCalls()
    {
        _trackingService.RecordCleanup(100, 1, _loggerMock.Object);
        _trackingService.RecordCleanup(200, 2, _loggerMock.Object);

        var (totalBytesFreed, totalItemsDeleted, _) = _trackingService.GetStatistics();

        Assert.Equal(300, totalBytesFreed);
        Assert.Equal(3, totalItemsDeleted);
    }
}