using System;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Link;

/// <summary>
///     Unit tests for <see cref="StrmLinkHandler" />.
///     Tests the .strm-specific logic in isolation (CanHandle, ReadTarget, WriteTarget).
/// </summary>
public class StrmLinkHandlerTests
{
    private readonly MockFileSystem _fileSystem;
    private readonly StrmLinkHandler _handler;

    public StrmLinkHandlerTests()
    {
        _fileSystem = new MockFileSystem();
        _handler = new StrmLinkHandler(_fileSystem);
    }

    // ===== CanHandle =====

    [Theory]
    [InlineData("/media/movie.strm", true)]
    [InlineData("/media/movie.STRM", true)]
    [InlineData("/media/movie.Strm", true)]
    [InlineData("/media/movie.mkv", false)]
    [InlineData("/media/movie.mp4", false)]
    [InlineData("/media/movie.strm.bak", false)]
    [InlineData("/media/.strm", true)]
    [InlineData("/media/noext", false)]
    public void CanHandle_ChecksStrmExtension(string filePath, bool expected)
    {
        Assert.Equal(expected, _handler.CanHandle(filePath));
    }

    // ===== ReadTarget =====

    [Fact]
    public void ReadTarget_ReturnsFileContent()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("/movies/Movie1/movie.mkv"));

        var result = _handler.ReadTarget(linkFile);

        Assert.Equal("/movies/Movie1/movie.mkv", result);
    }

    [Fact]
    public void ReadTarget_TrimsWhitespace()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("  /movies/Movie1/movie.mkv  \n"));

        var result = _handler.ReadTarget(linkFile);

        Assert.Equal("/movies/Movie1/movie.mkv", result);
    }

    [Fact]
    public void ReadTarget_EmptyFile_ReturnsNull()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(""));

        Assert.Null(_handler.ReadTarget(linkFile));
    }

    [Fact]
    public void ReadTarget_WhitespaceOnly_ReturnsNull()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("   \n  "));

        Assert.Null(_handler.ReadTarget(linkFile));
    }

    [Fact]
    public void ReadTarget_NonExistentFile_ReturnsNull()
    {
        var result = _handler.ReadTarget(_fileSystem.Path.GetFullPath("/nonexistent.strm"));

        Assert.Null(result);
    }

    [Fact]
    public void ReadTarget_UrlContent_ReturnsTrimmedUrl()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/stream.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("https://example.com/video.mp4"));

        var result = _handler.ReadTarget(linkFile);

        Assert.Equal("https://example.com/video.mp4", result);
    }

    [Fact]
    public void ReadTarget_WhenReadThrowsIOException_ReturnsNull()
    {
        // A .strm that passes the Exists/size guard but faults mid-read is a broken pointer, not a
        // fatal error: the caller must see null, not a propagated exception. MockFileSystem can't
        // inject a read fault, so mock the file-info guard true and force ReadAllText to throw.
        const string linkFile = "/series/episode.strm";
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        var infoFactory = new Mock<IFileInfoFactory>();
        var info = new Mock<IFileInfo>();
        fs.SetupGet(f => f.File).Returns(file.Object);
        fs.SetupGet(f => f.FileInfo).Returns(infoFactory.Object);
        infoFactory.Setup(x => x.New(linkFile)).Returns(info.Object);
        info.SetupGet(i => i.Exists).Returns(true);
        info.SetupGet(i => i.Length).Returns(100);
        file.Setup(f => f.ReadAllText(linkFile)).Throws(new IOException("read fault"));

        var handler = new StrmLinkHandler(fs.Object);

        Assert.Null(handler.ReadTarget(linkFile));
    }

    [Fact]
    public void ReadTarget_WhenReadThrowsUnauthorizedAccess_ReturnsNull()
    {
        // Second arm of the exception filter: permission-denied on read must also map to null (a
        // broken pointer), never bubble up to the caller.
        const string linkFile = "/series/episode.strm";
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        var infoFactory = new Mock<IFileInfoFactory>();
        var info = new Mock<IFileInfo>();
        fs.SetupGet(f => f.File).Returns(file.Object);
        fs.SetupGet(f => f.FileInfo).Returns(infoFactory.Object);
        infoFactory.Setup(x => x.New(linkFile)).Returns(info.Object);
        info.SetupGet(i => i.Exists).Returns(true);
        info.SetupGet(i => i.Length).Returns(100);
        file.Setup(f => f.ReadAllText(linkFile)).Throws(new UnauthorizedAccessException("denied"));

        var handler = new StrmLinkHandler(fs.Object);

        Assert.Null(handler.ReadTarget(linkFile));
    }

    // ===== WriteTarget =====

    [Fact]
    public void WriteTarget_WritesContentToFile()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("old-target"));

        _handler.WriteTarget(linkFile, "/movies/Movie1/new-movie.mkv");

        Assert.Equal("/movies/Movie1/new-movie.mkv", _fileSystem.File.ReadAllText(linkFile));
    }

    [Fact]
    public void WriteTarget_OverwritesExistingContent()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("/old/path.mkv"));

        _handler.WriteTarget(linkFile, "/new/path.mkv");

        Assert.Equal("/new/path.mkv", _fileSystem.File.ReadAllText(linkFile));
    }

    [Fact]
    public void WriteTarget_LeavesNoTempFileAndDoesNotTruncateInPlace()
    {
        // Crash-safety guard: the write must stage to a sibling temp and atomically move it over the
        // target (never truncate-then-write in place, which loses the pointer on an interrupted write).
        // After a successful write the temp file must not linger, and the content must be exact.
        var linkFile = _fileSystem.Path.GetFullPath("/series/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("/old/path.mkv"));

        _handler.WriteTarget(linkFile, "/new/path.mkv");

        Assert.Equal("/new/path.mkv", _fileSystem.File.ReadAllText(linkFile));
        Assert.False(
            _fileSystem.File.Exists(linkFile + ".jfh-tmp"),
            "the staging temp file must not remain after a successful atomic write");
    }

    [Fact]
    public void WriteTarget_WhenMoveFails_DeletesTempAndRethrows_WithoutTruncatingOriginal()
    {
        // ERROR-PATH GUARD for the crash-safe write: if the atomic Move fails (IOException), the
        // handler must delete the staging temp file and rethrow, and must NEVER have touched the
        // original .strm in place (the whole point of temp+move). Uses a mocked IFileSystem to
        // deterministically force the Move failure that MockFileSystem cannot inject.
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        fs.SetupGet(f => f.File).Returns(file.Object);

        const string linkFile = "/series/episode.strm";
        const string tempFile = linkFile + ".jfh-tmp";

        file.Setup(f => f.WriteAllText(tempFile, It.IsAny<string>())); // temp write succeeds
        file.Setup(f => f.Move(tempFile, linkFile, true)).Throws(new IOException("disk full"));
        file.Setup(f => f.Exists(tempFile)).Returns(true); // temp exists -> must be cleaned up

        var handler = new StrmLinkHandler(fs.Object);

        Assert.Throws<IOException>(() => handler.WriteTarget(linkFile, "/new/path.mkv"));

        // Temp was cleaned up; the original .strm was only ever written via temp (never in place).
        file.Verify(f => f.Delete(tempFile), Times.Once);
        file.Verify(f => f.WriteAllText(linkFile, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void WriteTarget_WhenMoveFailsAndCleanupFails_StillRethrowsOriginal()
    {
        // Belt-and-suspenders: even if the best-effort temp cleanup itself throws, the ORIGINAL
        // write failure must still propagate (the inner cleanup catch must swallow only the
        // cleanup exception, never mask the real IOException).
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        fs.SetupGet(f => f.File).Returns(file.Object);

        const string linkFile = "/series/episode.strm";
        const string tempFile = linkFile + ".jfh-tmp";

        file.Setup(f => f.WriteAllText(tempFile, It.IsAny<string>()));
        file.Setup(f => f.Move(tempFile, linkFile, true)).Throws(new IOException("primary failure"));
        file.Setup(f => f.Exists(tempFile)).Returns(true);
        file.Setup(f => f.Delete(tempFile)).Throws(new IOException("cleanup failure"));

        var handler = new StrmLinkHandler(fs.Object);

        var ex = Assert.Throws<IOException>(() => handler.WriteTarget(linkFile, "/new/path.mkv"));
        Assert.Equal("primary failure", ex.Message);
    }
}