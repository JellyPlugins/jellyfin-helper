using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Link;

/// <summary>
///     Unit tests for <see cref="SymlinkHandler" />.
///     Tests the symlink-specific logic in isolation using a mocked <see cref="ISymlinkHelper" />.
/// </summary>
public class SymlinkHandlerTests
{
    private readonly SymlinkHandler _handler;
    private readonly Mock<ISymlinkHelper> _symlinkHelper;

    public SymlinkHandlerTests()
    {
        _symlinkHelper = new Mock<ISymlinkHelper>();
        _handler = new SymlinkHandler(_symlinkHelper.Object);
    }

    [Fact]
    public void CanHandle_SymlinkFile_ReturnsTrue()
    {
        _symlinkHelper.Setup(h => h.IsSymlink("/media/movie.mkv")).Returns(true);

        Assert.True(_handler.CanHandle("/media/movie.mkv"));
    }

    [Fact]
    public void CanHandle_RegularFile_ReturnsFalse()
    {
        _symlinkHelper.Setup(h => h.IsSymlink("/media/movie.mkv")).Returns(false);

        Assert.False(_handler.CanHandle("/media/movie.mkv"));
    }

    [Fact]
    public void CanHandle_DelegatesToSymlinkHelper()
    {
        _handler.CanHandle("/some/path");

        _symlinkHelper.Verify(h => h.IsSymlink("/some/path"), Times.Once);
    }

    [Fact]
    public void ReadTarget_ReturnsSymlinkTarget()
    {
        _symlinkHelper.Setup(h => h.GetSymlinkTarget("/series/episode.mkv"))
            .Returns("/movies/Movie1/movie.mkv");

        var result = _handler.ReadTarget("/series/episode.mkv");

        Assert.Equal("/movies/Movie1/movie.mkv", result);
    }

    [Fact]
    public void ReadTarget_BrokenSymlink_ReturnsNull()
    {
        _symlinkHelper.Setup(h => h.GetSymlinkTarget("/series/episode.mkv"))
            .Returns((string?)null);

        Assert.Null(_handler.ReadTarget("/series/episode.mkv"));
    }

    [Fact]
    public void ReadTarget_DelegatesToSymlinkHelper()
    {
        _handler.ReadTarget("/some/path");

        _symlinkHelper.Verify(h => h.GetSymlinkTarget("/some/path"), Times.Once);
    }

    [Fact]
    public void WriteTarget_CreatesAtTempThenReplaces()
    {
        var tempPath = "/link.jfh-tmp";
        var createOrder = new List<string>();

        _symlinkHelper.Setup(h => h.CreateSymlink(tempPath, "/new-target"))
            .Callback(() => createOrder.Add("create-temp"));
        _symlinkHelper.Setup(h => h.ReplaceSymlink(tempPath, "/link"))
            .Callback(() => createOrder.Add("replace"));

        _handler.WriteTarget("/link", "/new-target");

        Assert.Equal(new[] { "create-temp", "replace" }, createOrder);
    }

    [Fact]
    public void WriteTarget_NeverTouchesOriginalBeforeReplace()
    {
        _handler.WriteTarget("/link", "/target");

        _symlinkHelper.Verify(h => h.DeleteSymlink("/link"), Times.Never);
    }

    [Fact]
    public void WriteTarget_WhenCreateTempFails_OriginalUntouched_ExceptionRethrown()
    {
        _symlinkHelper.Setup(h => h.CreateSymlink("/link.jfh-tmp", "/new-target"))
            .Throws(new IOException("no space"));

        Assert.Throws<IOException>(() => _handler.WriteTarget("/link", "/new-target"));

        _symlinkHelper.Verify(h => h.ReplaceSymlink(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _symlinkHelper.Verify(h => h.DeleteSymlink("/link"), Times.Never);
    }

    [Fact]
    public void WriteTarget_WhenReplaceFails_DeletesTempAndRethrows()
    {
        var tempPath = "/link.jfh-tmp";
        _symlinkHelper.Setup(h => h.IsSymlink(tempPath)).Returns(true);
        _symlinkHelper.Setup(h => h.ReplaceSymlink(tempPath, "/link"))
            .Throws(new IOException("cross-device"));

        Assert.Throws<IOException>(() => _handler.WriteTarget("/link", "/new-target"));

        _symlinkHelper.Verify(h => h.DeleteSymlink(tempPath), Times.Once);
        _symlinkHelper.Verify(h => h.DeleteSymlink("/link"), Times.Never);
    }

    [Fact]
    public void WriteTarget_WhenReplaceFails_AndTempCleanupFails_OriginalUntouched_ExceptionRethrown()
    {
        var tempPath = "/link.jfh-tmp";
        _symlinkHelper.Setup(h => h.IsSymlink(tempPath)).Returns(true);
        _symlinkHelper.Setup(h => h.ReplaceSymlink(tempPath, "/link"))
            .Throws(new IOException("cross-device"));
        _symlinkHelper.Setup(h => h.DeleteSymlink(tempPath))
            .Throws(new IOException("cleanup also failed"));

        Assert.Throws<IOException>(() => _handler.WriteTarget("/link", "/new-target"));

        _symlinkHelper.Verify(h => h.DeleteSymlink("/link"), Times.Never);
    }
}
