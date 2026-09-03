using System;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Covers <see cref="PathComparison"/>: the exposed comparison/comparer match the current OS
///     convention (case-insensitive on Windows and macOS, ordinal elsewhere) and agree with each other.
/// </summary>
public sealed class PathComparisonTests
{
    private static bool ExpectsCaseInsensitive =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    [Fact]
    public void Comparison_MatchesPlatformConvention()
    {
        var expected = ExpectsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.Equal(expected, PathComparison.Comparison);
    }

    [Fact]
    public void Comparer_MatchesPlatformConvention()
    {
        var caseFoldingMatch = PathComparison.Comparer.Equals("/Media", "/media");
        Assert.Equal(ExpectsCaseInsensitive, caseFoldingMatch);
    }
}
