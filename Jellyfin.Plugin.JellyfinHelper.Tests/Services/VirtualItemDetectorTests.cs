using System;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Verifies the virtual-item guard: Jellyfin virtual flags, missing physical locations, and paths inside
///     the host data directory count as virtual, while real files outside it (including a data-path sibling)
///     count as owned. Path assertions branch on the OS so they hold on Windows and Linux CI.
/// </summary>
public sealed class VirtualItemDetectorTests
{
    private static readonly string DataPath = OperatingSystem.IsWindows()
        ? @"C:\jellyfin\data"
        : "/var/lib/jellyfin/data";

    private static VirtualItemDetector Create()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(DataPath);
        return new VirtualItemDetector(paths.Object, NullLogger<VirtualItemDetector>.Instance);
    }

    private static string Under(string relative) =>
        Path.Combine(DataPath, relative);

    [Fact]
    public void IsVirtual_True_WhenFlaggedVirtualItem()
    {
        var item = new Movie { Path = OwnedPath(), IsVirtualItem = true };

        Assert.True(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_True_WhenPathEmpty()
    {
        // An empty path is how Jellyfin represents a purely virtual entry.
        var item = new Movie { Path = null };

        Assert.True(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_True_WhenPathInsideDataDirectory()
    {
        var item = new Movie { Path = Under("stubs/4875-B-.mp4") };

        Assert.True(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_True_WhenPathEqualsDataDirectory()
    {
        var item = new Movie { Path = DataPath };

        Assert.True(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_False_ForRealFileOutsideDataDirectory()
    {
        var item = new Movie { Path = OwnedPath() };

        Assert.False(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_False_ForDataPathSibling()
    {
        // A sibling that merely shares the data-path prefix must not be treated as inside it.
        var sibling = Path.TrimEndingDirectorySeparator(DataPath) + "-cache";
        var item = new Movie { Path = Path.Combine(sibling, "real.mkv") };

        Assert.False(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_MatchesDataPath_CaseInsensitivelyOnWindowsOnly()
    {
        var item = new Movie { Path = Under("Stub.mp4").ToUpperInvariant() };

        Assert.Equal(OperatingSystem.IsWindows(), Create().IsVirtual(item));
    }

    [Fact]
    public void Ctor_Throws_OnNullApplicationPaths()
    {
        Assert.Throws<ArgumentNullException>(
            () => new VirtualItemDetector(null!, NullLogger<VirtualItemDetector>.Instance));
    }

    [Fact]
    public void Ctor_Throws_OnNullLogger()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(DataPath);

        Assert.Throws<ArgumentNullException>(() => new VirtualItemDetector(paths.Object, null!));
    }

    [Fact]
    public void IsVirtual_Throws_OnNullItem()
    {
        Assert.Throws<ArgumentNullException>(() => Create().IsVirtual(null!));
    }

    [Fact]
    public void IsVirtual_False_ForRealFile_WhenDataPathEmpty()
    {
        // A host that reports no data path cannot contain stub libraries, so containment never matches and a
        // real file outside it stays owned.
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(string.Empty);
        var detector = new VirtualItemDetector(paths.Object, NullLogger<VirtualItemDetector>.Instance);

        Assert.False(detector.IsVirtual(new Movie { Path = OwnedPath() }));
    }

    [Fact]
    public void IsVirtual_EmitsDebugLog_WhenDebugEnabled()
    {
        // The skip log is guarded by IsEnabled(Debug); with Debug on it must fire so the skip is diagnosable.
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(DataPath);
        var logger = new Mock<ILogger<VirtualItemDetector>>();
        logger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);
        var detector = new VirtualItemDetector(paths.Object, logger.Object);

        Assert.True(detector.IsVirtual(new Movie { Path = Under("stub.mp4") }));
        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private static string OwnedPath() =>
        OperatingSystem.IsWindows() ? @"D:\media\movies\real.mkv" : "/media/movies/real.mkv";
}
