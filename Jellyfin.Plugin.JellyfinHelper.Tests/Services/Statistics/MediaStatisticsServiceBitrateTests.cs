using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

/// <summary>
/// Tests for the new bitrate tier thresholds and legacy mapping.
/// </summary>
public class MediaStatisticsServiceBitrateTests
{
    [Theory]
    [InlineData(1_000_000, "< 2 Mbps")]
    [InlineData(1_999_999, "< 2 Mbps")]
    public void ClassifyBitrateTier_Below2_ReturnsLt2Mbps(int bitrate, string expected)
        => Assert.Equal(expected, MediaStatisticsService.ClassifyBitrateTier(bitrate, 999_999_999L, 1L));

    [Theory]
    [InlineData(2_000_000, "2–4 Mbps")]
    [InlineData(3_000_000, "2–4 Mbps")]
    [InlineData(3_999_999, "2–4 Mbps")]
    public void ClassifyBitrateTier_2To4_Returns2To4Mbps(int bitrate, string expected)
        => Assert.Equal(expected, MediaStatisticsService.ClassifyBitrateTier(bitrate, 999_999_999L, 1L));

    [Theory]
    [InlineData(4_000_000, "4–8 Mbps")]
    [InlineData(6_000_000, "4–8 Mbps")]
    [InlineData(7_999_999, "4–8 Mbps")]
    public void ClassifyBitrateTier_4To8_Returns4To8Mbps(int bitrate, string expected)
        => Assert.Equal(expected, MediaStatisticsService.ClassifyBitrateTier(bitrate, 999_999_999L, 1L));

    [Theory]
    [InlineData(8_000_000, "8–16 Mbps")]
    [InlineData(12_000_000, "8–16 Mbps")]
    [InlineData(15_999_999, "8–16 Mbps")]
    public void ClassifyBitrateTier_8To16_Returns8To16Mbps(int bitrate, string expected)
        => Assert.Equal(expected, MediaStatisticsService.ClassifyBitrateTier(bitrate, 999_999_999L, 1L));

    [Theory]
    [InlineData(16_000_000, "16–32 Mbps")]
    [InlineData(25_000_000, "16–32 Mbps")]
    [InlineData(31_999_999, "16–32 Mbps")]
    public void ClassifyBitrateTier_16To32_Returns16To32Mbps(int bitrate, string expected)
        => Assert.Equal(expected, MediaStatisticsService.ClassifyBitrateTier(bitrate, 999_999_999L, 1L));

    [Theory]
    [InlineData(32_000_000, "32–60 Mbps")]
    [InlineData(50_000_000, "32–60 Mbps")]
    [InlineData(59_999_999, "32–60 Mbps")]
    public void ClassifyBitrateTier_32To60_Returns32To60Mbps(int bitrate, string expected)
        => Assert.Equal(expected, MediaStatisticsService.ClassifyBitrateTier(bitrate, 999_999_999L, 1L));

    [Theory]
    [InlineData(60_000_000, "> 60 Mbps")]
    [InlineData(100_000_000, "> 60 Mbps")]
    public void ClassifyBitrateTier_Above60_ReturnsGt60Mbps(int bitrate, string expected)
        => Assert.Equal(expected, MediaStatisticsService.ClassifyBitrateTier(bitrate, 999_999_999L, 1L));

    [Fact]
    public void ClassifyBitrateTier_NullBitrateWithDuration_ComputesFromSizeAndDuration()
    {
        // 5 MB over 8s = 5 Mbps -> 4–8 tier
        Assert.Equal("4–8 Mbps", MediaStatisticsService.ClassifyBitrateTier(null, 5_000_000L, 80_000_000L));
    }

    [Theory]
    [InlineData(null, 0L, null)]
    [InlineData(0, 1_000_000L, 0L)]
    public void ClassifyBitrateTier_InvalidInput_ReturnsUnknown(int? bitrate, long size, long? ticks)
        => Assert.Equal("Unknown", MediaStatisticsService.ClassifyBitrateTier(bitrate, size, ticks));

    [Theory]
    [InlineData("2-5 Mbps", "2–4 Mbps")]
    [InlineData("5-10 Mbps", "4–8 Mbps")]
    [InlineData("10-20 Mbps", "8–16 Mbps")]
    [InlineData("20-40 Mbps", "16–32 Mbps")]
    [InlineData("> 40 Mbps", "32–60 Mbps")]
    public void MapLegacyBitrateTier_OldLabels_MapToNewLabels(string old, string expected)
        => Assert.Equal(expected, MediaStatisticsService.MapLegacyBitrateTier(old));

    [Theory]
    [InlineData("< 2 Mbps")]
    [InlineData("2–4 Mbps")]
    [InlineData("> 60 Mbps")]
    public void MapLegacyBitrateTier_NewLabel_PassThrough(string label)
        => Assert.Equal(label, MediaStatisticsService.MapLegacyBitrateTier(label));

    [Fact]
    public void MapLegacyBitrateTier_UnknownLabel_PassThrough()
        => Assert.Equal("Unknown", MediaStatisticsService.MapLegacyBitrateTier("Unknown"));

    [Fact]
    public void MapLegacyBitrateTier_CustomLabel_PassThrough()
        => Assert.Equal("CustomTier", MediaStatisticsService.MapLegacyBitrateTier("CustomTier"));
}
