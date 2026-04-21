using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Activity;

public class UserActivityInsightsServiceTests
{
    // === CalculateCompletion (internal static) ===

    [Fact]
    public void CalculateCompletion_Played_Returns100()
    {
        var result = UserActivityInsightsService.CalculateCompletion(0, 1000, played: true);
        Assert.Equal(100.0, result);
    }

    [Fact]
    public void CalculateCompletion_Played_IgnoresPosition()
    {
        var result = UserActivityInsightsService.CalculateCompletion(500, 1000, played: true);
        Assert.Equal(100.0, result);
    }

    [Fact]
    public void CalculateCompletion_ZeroRuntime_ReturnsZero()
    {
        var result = UserActivityInsightsService.CalculateCompletion(500, 0, played: false);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalculateCompletion_NegativeRuntime_ReturnsZero()
    {
        var result = UserActivityInsightsService.CalculateCompletion(500, -100, played: false);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalculateCompletion_ZeroPosition_ReturnsZero()
    {
        var result = UserActivityInsightsService.CalculateCompletion(0, 1000, played: false);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalculateCompletion_NegativePosition_ReturnsZero()
    {
        var result = UserActivityInsightsService.CalculateCompletion(-100, 1000, played: false);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalculateCompletion_HalfWatched_Returns50()
    {
        var result = UserActivityInsightsService.CalculateCompletion(500, 1000, played: false);
        Assert.Equal(50.0, result);
    }

    [Fact]
    public void CalculateCompletion_PartialWatch_RoundsToOneDecimal()
    {
        // 333 / 1000 = 33.3%
        var result = UserActivityInsightsService.CalculateCompletion(333, 1000, played: false);
        Assert.Equal(33.3, result);
    }

    [Fact]
    public void CalculateCompletion_ExceedsRuntime_CapsAt100()
    {
        // Position > runtime should not exceed 100%
        var result = UserActivityInsightsService.CalculateCompletion(1500, 1000, played: false);
        Assert.Equal(100.0, result);
    }

    [Fact]
    public void CalculateCompletion_AlmostComplete_RoundsCorrectly()
    {
        // 999 / 1000 = 99.9%
        var result = UserActivityInsightsService.CalculateCompletion(999, 1000, played: false);
        Assert.Equal(99.9, result);
    }

    [Fact]
    public void CalculateCompletion_BothZero_ReturnsZero()
    {
        var result = UserActivityInsightsService.CalculateCompletion(0, 0, played: false);
        Assert.Equal(0.0, result);
    }
}