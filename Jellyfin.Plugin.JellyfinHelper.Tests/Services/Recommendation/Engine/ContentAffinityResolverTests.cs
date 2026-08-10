using System;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Behavioral tests for <see cref="ContentAffinityResolver.ResolveSeriesStatus"/>, the shared
///     resolver that feeds the SeriesCompletability feature. The status token must be extracted
///     identically on the live and training paths, so the exact string returned is a train/serve
///     parity contract - not merely "some non-null value".
/// </summary>
public sealed class ContentAffinityResolverTests
{
    [Theory]
    [InlineData(SeriesStatus.Continuing, "Continuing")]
    [InlineData(SeriesStatus.Ended, "Ended")]
    [InlineData(SeriesStatus.Unreleased, "Unreleased")]
    public void ResolveSeriesStatus_SeriesWithStatus_ReturnsStatusName(SeriesStatus status, string expected)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "S", Status = status };

        // Exact enum-name string, since the feature keys off this token verbatim across call sites.
        Assert.Equal(expected, ContentAffinityResolver.ResolveSeriesStatus(series));
    }

    [Fact]
    public void ResolveSeriesStatus_SeriesWithoutStatus_ReturnsNull()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "S", Status = null };

        // Missing metadata must neutralize to null - not an empty string that would emit a bogus token.
        Assert.Null(ContentAffinityResolver.ResolveSeriesStatus(series));
    }
}
