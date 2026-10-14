using System;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Verifies the virtual-item guard: Jellyfin virtual flags, missing physical locations, and paths inside
///     the host plugins directory count as virtual, while real files elsewhere (including under the data
///     directory but outside plugins, and a plugins-path sibling) count as owned. Path assertions branch on
///     the OS so they hold on Windows and Linux CI.
/// </summary>
public sealed class VirtualItemDetectorTests
{
    private static readonly string DataPath = OperatingSystem.IsWindows()
        ? @"C:\jellyfin\data"
        : "/var/lib/jellyfin/data";

    private static readonly string PluginsPath = DataPath + Path.DirectorySeparatorChar + "plugins";

    private static VirtualItemDetector Create()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.PluginsPath).Returns(PluginsPath);
        return new VirtualItemDetector(paths.Object, NullLogger<VirtualItemDetector>.Instance);
    }

    // Joins a relative segment under PluginsPath with an explicit separator. Path.Combine is avoided on purpose:
    // if the segment looked rooted it would silently discard PluginsPath, which would defeat a containment test.
    private static string UnderPlugins(string relative) =>
        PluginsPath + Path.DirectorySeparatorChar + relative.Replace('/', Path.DirectorySeparatorChar);

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
    public void IsVirtual_True_WhenPathInsidePluginsDirectory()
    {
        // Mirrors JellyNext's real stub location: <data>/plugins/Jellyfin.Plugin.JellyNext/jellynext-virtual/...
        var item = new Movie { Path = UnderPlugins("Jellyfin.Plugin.JellyNext/jellynext-virtual/abc/4875-B-.mp4") };

        Assert.True(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_True_WhenPathEqualsPluginsDirectory()
    {
        var item = new Movie { Path = PluginsPath };

        Assert.True(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_False_ForRealFileOutsidePluginsDirectory()
    {
        var item = new Movie { Path = OwnedPath() };

        Assert.False(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_False_ForRealLibraryUnderDataButOutsidePlugins()
    {
        // An administrator may store a real media library under the data directory. Such a file is NOT under the
        // plugins directory, so it must count as owned and still exclude its discovery candidate.
        var item = new Movie { Path = DataPath + Path.DirectorySeparatorChar + "media" + Path.DirectorySeparatorChar + "real.mkv" };

        Assert.False(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_False_ForPluginsPathSibling()
    {
        // A sibling that merely shares the plugins-path prefix must not be treated as inside it.
        var sibling = PluginsPath + "-cache";
        var item = new Movie { Path = sibling + Path.DirectorySeparatorChar + "real.mkv" };

        Assert.False(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_MatchesPluginsPath_CaseInsensitivelyOnWindowsOnly()
    {
        var item = new Movie { Path = UnderPlugins("Stub.mp4").ToUpperInvariant() };

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
        paths.Setup(p => p.PluginsPath).Returns(PluginsPath);

        Assert.Throws<ArgumentNullException>(() => new VirtualItemDetector(paths.Object, null!));
    }

    [Fact]
    public void IsVirtual_Throws_OnNullItem()
    {
        Assert.Throws<ArgumentNullException>(() => Create().IsVirtual(null!));
    }

    [Fact]
    public void IsVirtual_False_ForRealFile_WhenPluginsPathEmpty()
    {
        // A host that reports no plugins path cannot contain stub libraries, so containment never matches and a
        // real file stays owned.
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.PluginsPath).Returns(string.Empty);
        var detector = new VirtualItemDetector(paths.Object, NullLogger<VirtualItemDetector>.Instance);

        Assert.False(detector.IsVirtual(new Movie { Path = OwnedPath() }));
    }

    [Fact]
    public void IsVirtual_EmitsDebugLog_WhenDebugEnabled()
    {
        // The skip log is guarded by IsEnabled(Debug); with Debug on it must fire so the skip is diagnosable.
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.PluginsPath).Returns(PluginsPath);
        var logger = new Mock<ILogger<VirtualItemDetector>>();
        logger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);
        var detector = new VirtualItemDetector(paths.Object, logger.Object);

        Assert.True(detector.IsVirtual(new Movie { Path = UnderPlugins("stub.mp4") }));
        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void IsVirtual_False_ForUnnormalizablePath_FailsClosedAsOwned()
    {
        // A path with an invalid character makes Path.GetFullPath throw. Such a path cannot be a plugins-directory
        // stub, so the detector must fail closed (owned) rather than let the exception unwind into the caller's
        // library scan, which contains the whole scan and would discard every owned title's exclusion over one
        // bad path. A null character is rejected on both Windows and Linux.
        var item = new Movie { Path = "bad\0path.mkv" };

        Assert.False(Create().IsVirtual(item));
    }

    [Fact]
    public void IsVirtual_False_ForRealFile_WhenPluginsPathUnnormalizable()
    {
        // A host reporting a plugins path with invalid characters must not throw at construction or on every
        // scan; the root is simply unusable, so containment never matches and a real file stays owned.
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.PluginsPath).Returns("bad\0plugins");
        var detector = new VirtualItemDetector(paths.Object, NullLogger<VirtualItemDetector>.Instance);

        Assert.False(detector.IsVirtual(new Movie { Path = OwnedPath() }));
    }

    [Fact]
    public void IsVirtual_True_ForChildOfRootPluginsPath()
    {
        // A filesystem-root plugins path keeps its trailing separator, so NormalizeRoot must not append a second
        // one, or containment would never match and a stub under the root would wrongly count as owned.
        var root = OperatingSystem.IsWindows() ? @"C:\" : "/";
        var child = OperatingSystem.IsWindows() ? @"C:\stub.mp4" : "/stub.mp4";
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.PluginsPath).Returns(root);
        var detector = new VirtualItemDetector(paths.Object, NullLogger<VirtualItemDetector>.Instance);

        Assert.True(detector.IsVirtual(new Movie { Path = child }));
    }

    private static string OwnedPath() =>
        OperatingSystem.IsWindows() ? @"D:\media\movies\real.mkv" : "/media/movies/real.mkv";
}
