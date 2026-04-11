using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

public class StrmRepairServiceTests
{
    private readonly MockFileSystem _fileSystem;
    private readonly Mock<ILogger<StrmRepairService>> _loggerMock;
    private readonly StrmRepairService _service;

    public StrmRepairServiceTests()
    {
        _fileSystem = new MockFileSystem();
        _loggerMock = new Mock<ILogger<StrmRepairService>>();
        _service = new StrmRepairService(_fileSystem, _loggerMock.Object);
    }

    [Fact]
    public void FindStrmFiles_FindsStrmFilesRecursively()
    {
        // Arrange
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        var strmFile1 = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        var strmFile2 = _fileSystem.Path.GetFullPath("/series/Show2/Specials/special.strm");
        var videoFile = _fileSystem.Path.GetFullPath("/series/Show1/S01E01.mkv");

        _fileSystem.AddFile(strmFile1, new MockFileData("target1"));
        _fileSystem.AddFile(videoFile, new MockFileData("video"));
        _fileSystem.AddFile(strmFile2, new MockFileData("target2"));

        // Act
        var result = _service.FindStrmFiles(new[] { seriesDir });

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(strmFile1, result);
        Assert.Contains(strmFile2, result);
    }

    [Fact]
    public void FindStrmFiles_SkipsNonExistentLibraryPaths()
    {
        // Act
        var result = _service.FindStrmFiles(new[] { _fileSystem.Path.GetFullPath("/nonexistent") });

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ProcessStrmFile_ValidTarget_ReturnsValid()
    {
        // Arrange
        var movieFile = _fileSystem.Path.GetFullPath("/movies/Movie1/movie.mkv");
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(movieFile));

        // Act
        var result = _service.ProcessStrmFile(strmFile, true);

        // Assert
        Assert.Equal(StrmFileStatus.Valid, result.Status);
        Assert.Equal(movieFile, result.OriginalTargetPath);
        Assert.Null(result.NewTargetPath);
    }

    [Fact]
    public void ProcessStrmFile_EmptyFile_ReturnsInvalidContent()
    {
        // Arrange
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(""));

        // Act
        var result = _service.ProcessStrmFile(strmFile, true);

        // Assert
        Assert.Equal(StrmFileStatus.InvalidContent, result.Status);
    }

    [Fact]
    public void ProcessStrmFile_WhitespaceOnly_ReturnsInvalidContent()
    {
        // Arrange
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData("   \n  "));

        // Act
        var result = _service.ProcessStrmFile(strmFile, true);

        // Assert
        Assert.Equal(StrmFileStatus.InvalidContent, result.Status);
    }

    [Fact]
    public void ProcessStrmFile_UrlBasedStrm_ReturnsValid()
    {
        // Arrange
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/stream.strm");
        _fileSystem.AddFile(strmFile, new MockFileData("https://example.com/video.mp4"));

        // Act
        var result = _service.ProcessStrmFile(strmFile, true);

        // Assert
        Assert.Equal(StrmFileStatus.Valid, result.Status);
    }

    [Fact]
    public void ProcessStrmFile_BrokenTarget_ParentDirExists_SingleMediaFile_DryRun()
    {
        // Arrange - the .strm points to old-name.mkv but file was renamed to new-name.mkv
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var newFile = _fileSystem.Path.Combine(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Combine(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));

        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(brokenTarget));

        // Act (dry run)
        var result = _service.ProcessStrmFile(strmFile, true);

        // Assert
        Assert.Equal(StrmFileStatus.Repaired, result.Status);
        Assert.Equal(brokenTarget, result.OriginalTargetPath);
        Assert.Equal(newFile, result.NewTargetPath);
        // In dry run, the file should NOT be modified
        Assert.Equal(brokenTarget, _fileSystem.File.ReadAllText(strmFile));
    }

    [Fact]
    public void ProcessStrmFile_BrokenTarget_ParentDirExists_SingleMediaFile_RepairsActual()
    {
        // Arrange
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var newFile = _fileSystem.Path.Combine(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Combine(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));

        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(brokenTarget));

        // Act (NOT dry run)
        var result = _service.ProcessStrmFile(strmFile, false);

        // Assert
        Assert.Equal(StrmFileStatus.Repaired, result.Status);
        Assert.Equal(newFile, result.NewTargetPath);
        // The file SHOULD be modified
        Assert.Equal(newFile, _fileSystem.File.ReadAllText(strmFile));
    }

    [Fact]
    public void ProcessStrmFile_BrokenTarget_ParentDirExists_NoMediaFiles_ReturnsBroken()
    {
        // Arrange - parent directory exists but has no media files
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var brokenTarget = _fileSystem.Path.Combine(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "readme.txt"), new MockFileData("info"));

        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(brokenTarget));

        // Act
        var result = _service.ProcessStrmFile(strmFile, true);

        // Assert
        Assert.Equal(StrmFileStatus.Broken, result.Status);
    }

    [Fact]
    public void ProcessStrmFile_BrokenTarget_ParentDirExists_MultipleMediaFiles_ReturnsAmbiguous()
    {
        // Arrange - parent directory has multiple media files
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var brokenTarget = _fileSystem.Path.Combine(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "movie-part1.mkv"), new MockFileData("video"));
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "movie-part2.mkv"), new MockFileData("video"));

        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(brokenTarget));

        // Act
        var result = _service.ProcessStrmFile(strmFile, true);

        // Assert
        Assert.Equal(StrmFileStatus.Ambiguous, result.Status);
    }

    [Fact]
    public void ProcessStrmFile_BrokenTarget_ParentDirDoesNotExist_ReturnsBroken()
    {
        // Arrange - parent directory was also removed/renamed
        var brokenTarget = _fileSystem.Path.GetFullPath("/movies/DeletedMovie/movie.mkv");
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(brokenTarget));

        // Act
        var result = _service.ProcessStrmFile(strmFile, true);

        // Assert
        Assert.Equal(StrmFileStatus.Broken, result.Status);
    }

    [Fact]
    public void ProcessStrmFile_TrimsWhitespaceFromTargetPath()
    {
        // Arrange
        var movieFile = _fileSystem.Path.GetFullPath("/movies/Movie1/movie.mkv");
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData("  " + movieFile + "  \n"));

        // Act
        var result = _service.ProcessStrmFile(strmFile, true);

        // Assert
        Assert.Equal(StrmFileStatus.Valid, result.Status);
        Assert.Equal(movieFile, result.OriginalTargetPath);
    }

    [Fact]
    public void FindMediaFilesInDirectory_FindsOnlyVideoFiles()
    {
        // Arrange
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "movie.mkv"), new MockFileData("video"));
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "subtitle.srt"), new MockFileData("subtitle"));
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "poster.jpg"), new MockFileData("image"));
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "info.nfo"), new MockFileData("info"));

        // Act
        var result = _service.FindMediaFilesInDirectory(movieDir);

        // Assert
        Assert.Single(result);
        Assert.EndsWith(".mkv", result[0]);
    }

    [Fact]
    public void FindMediaFilesInDirectory_FindsMultipleVideoExtensions()
    {
        // Arrange
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "movie.mkv"), new MockFileData("video"));
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "movie.mp4"), new MockFileData("video"));
        _fileSystem.AddFile(_fileSystem.Path.Combine(movieDir, "movie.avi"), new MockFileData("video"));

        // Act
        var result = _service.FindMediaFilesInDirectory(movieDir);

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void RepairStrmFiles_FullWorkflow_DryRun()
    {
        // Arrange
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var movieFile = _fileSystem.Path.Combine(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Combine(movieDir, "old-name.mkv");
        var seriesDir = _fileSystem.Path.GetFullPath("/series");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var strmFile1 = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile1, new MockFileData(brokenTarget));

        // Also add a valid .strm
        var validMovieFile = _fileSystem.Path.GetFullPath("/movies/Movie2/movie.mkv");
        _fileSystem.AddFile(validMovieFile, new MockFileData("video"));

        var strmFile2 = _fileSystem.Path.GetFullPath("/series/Show2/Specials/movie.strm");
        _fileSystem.AddFile(strmFile2, new MockFileData(validMovieFile));

        // Act
        var result = _service.RepairStrmFiles(new[] { seriesDir }, true);

        // Assert
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(1, result.RepairedCount);
        Assert.Equal(0, result.BrokenCount);

        // Verify dry run did NOT modify the file
        Assert.Equal(brokenTarget, _fileSystem.File.ReadAllText(strmFile1));
    }

    [Fact]
    public void RepairStrmFiles_FullWorkflow_ActualRepair()
    {
        // Arrange
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var movieFile = _fileSystem.Path.Combine(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Combine(movieDir, "old-name.mkv");
        var seriesDir = _fileSystem.Path.GetFullPath("/series");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(brokenTarget));

        // Act
        var result = _service.RepairStrmFiles(new[] { seriesDir }, false);

        // Assert
        Assert.Equal(0, result.ValidCount);
        Assert.Equal(1, result.RepairedCount);

        // Verify the file WAS modified
        Assert.Equal(movieFile, _fileSystem.File.ReadAllText(strmFile));
    }

    [Fact]
    public void RepairStrmFiles_MultipleLibraryPaths()
    {
        // Arrange
        var seriesDir1 = _fileSystem.Path.GetFullPath("/series1");
        var seriesDir2 = _fileSystem.Path.GetFullPath("/series2");
        var movieFile = _fileSystem.Path.GetFullPath("/movies/Movie1/movie.mkv");

        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var strmFile1 = _fileSystem.Path.GetFullPath("/series1/Show1/Specials/movie.strm");
        _fileSystem.AddFile(strmFile1, new MockFileData(movieFile));

        var strmFile2 = _fileSystem.Path.GetFullPath("/series2/Show2/Specials/movie.strm");
        _fileSystem.AddFile(strmFile2, new MockFileData(movieFile));

        // Act
        var result = _service.RepairStrmFiles(new[] { seriesDir1, seriesDir2 }, true);

        // Assert
        Assert.Equal(2, result.ValidCount);
        Assert.Equal(2, result.FileResults.Count);
    }
}