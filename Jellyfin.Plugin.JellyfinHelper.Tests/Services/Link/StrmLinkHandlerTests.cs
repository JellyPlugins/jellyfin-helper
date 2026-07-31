using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
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
}