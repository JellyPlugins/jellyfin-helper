using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
/// Tests for <see cref="CleanupTrackingService"/>.
/// Note: Since Plugin.Instance requires a full plugin setup, these tests cover
/// the null-instance fallback paths. The tracking logic itself is straightforward
/// (increment + save), so the null-safety is the critical path to verify.
/// </summary>
[Collection("ConfigOverride")]
public class CleanupTrackingServiceTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    [Fact]
    public void GetStatistics_WhenPluginInstanceNull_ReturnsDefaults()
    {
        // Plugin.Instance is null in test context
        var (totalBytesFreed, totalItemsDeleted, lastCleanupTimestamp) = CleanupTrackingService.GetStatistics();

        Assert.Equal(0, totalBytesFreed);
        Assert.Equal(0, totalItemsDeleted);
        Assert.Equal(DateTime.MinValue, lastCleanupTimestamp);
    }

    [Fact]
    public void RecordCleanup_WhenPluginInstanceNull_DoesNotThrow()
    {
        // Plugin.Instance is null in test context – should log warning but not throw
        var exception = Record.Exception(() =>
            CleanupTrackingService.RecordCleanup(1024, 5, _loggerMock.Object));

        Assert.Null(exception);
    }

    [Fact]
    public void RecordCleanup_WhenPluginInstanceNull_StatisticsRemainDefault()
    {
        CleanupTrackingService.RecordCleanup(2048, 10, _loggerMock.Object);

        var (totalBytesFreed, totalItemsDeleted, lastCleanupTimestamp) = CleanupTrackingService.GetStatistics();

        Assert.Equal(0, totalBytesFreed);
        Assert.Equal(0, totalItemsDeleted);
        Assert.Equal(DateTime.MinValue, lastCleanupTimestamp);
    }

    [Fact]
    public void RecordCleanup_WithZeroValues_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            CleanupTrackingService.RecordCleanup(0, 0, _loggerMock.Object));

        Assert.Null(exception);
    }

    [Fact]
    public void RecordCleanup_WithNegativeValues_DoesNotThrow()
    {
        // Edge case: negative values should not crash the service
        var exception = Record.Exception(() =>
            CleanupTrackingService.RecordCleanup(-100, -1, _loggerMock.Object));

        Assert.Null(exception);
    }
}
