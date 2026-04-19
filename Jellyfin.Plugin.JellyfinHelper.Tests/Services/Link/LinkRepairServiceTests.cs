using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Link;

/// <summary>
///     Unit tests for <see cref="LinkRepairService" />.
///     Tests the handler-agnostic service logic. Uses real StrmLinkHandler for .strm tests
///     and a mocked ISymlinkHelper-backed SymlinkHandler for symlink tests.
/// </summary>
public class LinkRepairServiceTests
{
    private readonly MockFileSystem _fileSystem;
    private readonly LinkRepairService _service;
    private readonly StrmLinkHandler _strmHandler;
    private readonly SymlinkHandler _symlinkHandler;
    private readonly Mock<ISymlinkHelper> _symlinkHelper;

    public LinkRepairServiceTests()
    {
        _fileSystem = new MockFileSystem();
        _strmHandler = new StrmLinkHandler(_fileSystem);
        _symlinkHelper = new Mock<ISymlinkHelper>();
        _symlinkHandler = new SymlinkHandler(_symlinkHelper.Object);
        _service = new LinkRepairService(
            _fileSystem,
            [_strmHandler, _symlinkHandler],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);
    }

    // ===== FindLinkFiles: .strm =====

    [Fact]
    public void FindLinkFiles_FindsStrmFilesRecursively()
    {
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        var linkFile1 = _fileSystem.Path.GetFullPath("/series/Show1/Specials/movie.strm");
        var linkFile2 = _fileSystem.Path.GetFullPath("/series/Show2/Specials/special.strm");
        var videoFile = _fileSystem.Path.GetFullPath("/series/Show1/S01E01.mkv");

        _fileSystem.AddFile(linkFile1, new MockFileData("target1"));
        _fileSystem.AddFile(videoFile, new MockFileData("video"));
        _fileSystem.AddFile(linkFile2, new MockFileData("target2"));

        var result = _service.FindLinkFiles([seriesDir]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.FilePath == linkFile1);
        Assert.Contains(result, r => r.FilePath == linkFile2);
    }

    [Fact]
    public void FindLinkFiles_AssociatesStrmHandler()
    {
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("target1"));

        var result = _service.FindLinkFiles([seriesDir]);

        Assert.Single(result);
        Assert.Same(_strmHandler, result[0].Handler);
    }

    // ===== FindLinkFiles: Symlinks =====

    [Fact]
    public void FindLinkFiles_FindsSymlinkFiles()
    {
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/S01E01.mkv");
        _fileSystem.AddFile(symlinkFile, new MockFileData("video"));
        _symlinkHelper.Setup(h => h.IsSymlink(symlinkFile)).Returns(true);

        var result = _service.FindLinkFiles([seriesDir]);

        Assert.Single(result);
        Assert.Same(_symlinkHandler, result[0].Handler);
        _symlinkHelper.Verify(h => h.IsSymlink(symlinkFile), Times.Once);
    }

    [Fact]
    public void FindLinkFiles_StrmTakesPriorityOverSymlinkCheck()
    {
        // A .strm file should be handled by StrmLinkHandler, not SymlinkHandler
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData("target"));
        _symlinkHelper.Setup(h => h.IsSymlink(strmFile)).Returns(true); // even if also symlink

        var result = _service.FindLinkFiles([seriesDir]);

        Assert.Single(result);
        Assert.Same(_strmHandler, result[0].Handler); // strm handler wins (registered first)
    }

    [Fact]
    public void FindLinkFiles_MixedStrmAndSymlinks()
    {
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show2/episode.mkv");
        var regularFile = _fileSystem.Path.GetFullPath("/series/Show3/video.mkv");

        _fileSystem.AddFile(strmFile, new MockFileData("target"));
        _fileSystem.AddFile(symlinkFile, new MockFileData("video"));
        _fileSystem.AddFile(regularFile, new MockFileData("video"));

        _symlinkHelper.Setup(h => h.IsSymlink(symlinkFile)).Returns(true);
        _symlinkHelper.Setup(h => h.IsSymlink(regularFile)).Returns(false);

        var result = _service.FindLinkFiles([seriesDir]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.FilePath == strmFile && r.Handler == _strmHandler);
        Assert.Contains(result, r => r.FilePath == symlinkFile && r.Handler == _symlinkHandler);
    }

    // ===== FindLinkFiles: Edge Cases =====

    [Fact]
    public void FindLinkFiles_SkipsNonExistentLibraryPaths()
    {
        var result = _service.FindLinkFiles([_fileSystem.Path.GetFullPath("/nonexistent")]);
        Assert.Empty(result);
    }

    [Fact]
    public void FindLinkFiles_EmptyLibraryList_ReturnsEmpty()
    {
        var result = _service.FindLinkFiles([]);
        Assert.Empty(result);
    }

    // ===== ProcessLinkFile: .strm scenarios =====

    [Fact]
    public void ProcessLinkFile_Strm_ValidTarget_ReturnsValid()
    {
        var movieFile = _fileSystem.Path.GetFullPath("/movies/Movie1/movie.mkv");
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(movieFile));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Valid, result.Status);
        Assert.Equal(movieFile, result.OriginalTargetPath);
    }

    [Fact]
    public void ProcessLinkFile_Strm_EmptyFile_ReturnsInvalidContent()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(""));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_Strm_UrlBased_ReturnsValid()
    {
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/stream.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("https://example.com/video.mp4"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Valid, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_Strm_WindowsStylePath_NotTreatedAsUrl()
    {
        // Regression: Windows paths like C:\media\movie.mkv must NOT be treated as URLs
        // (Uri.TryCreate parses "C:" as a scheme, but the file:// scheme is excluded from the URL bypass)
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(@"C:\media\movie.mkv"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        // The path does not exist on the mock filesystem, so it must be Broken (not Valid via URL bypass)
        Assert.NotEqual(LinkFileStatus.Valid, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_Strm_BrokenTarget_SingleMediaFile_DryRun()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var newFile = _fileSystem.Path.Join(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));

        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(brokenTarget));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Repaired, result.Status);
        Assert.Equal(brokenTarget, result.OriginalTargetPath);
        Assert.Equal(newFile, result.NewTargetPath);
        // Dry run: file should NOT be modified
        Assert.Equal(brokenTarget, _fileSystem.File.ReadAllText(linkFile));
    }

    [Fact]
    public void ProcessLinkFile_Strm_BrokenTarget_SingleMediaFile_ActualRepair()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var newFile = _fileSystem.Path.Join(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));

        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(brokenTarget));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, false);

        Assert.Equal(LinkFileStatus.Repaired, result.Status);
        Assert.Equal(newFile, _fileSystem.File.ReadAllText(linkFile));
    }

    // ===== ProcessLinkFile: Symlink scenarios =====

    [Fact]
    public void ProcessLinkFile_Symlink_ValidTarget_ReturnsValid()
    {
        var targetFile = _fileSystem.Path.GetFullPath("/movies/Movie1/movie.mkv");
        _fileSystem.AddFile(targetFile, new MockFileData("video"));

        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(targetFile);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Valid, result.Status);
        Assert.Equal(targetFile, result.OriginalTargetPath);
    }

    [Fact]
    public void ProcessLinkFile_Symlink_NullTarget_ReturnsInvalidContent()
    {
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns((string?)null);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_Symlink_BrokenTarget_SingleMediaFile_DryRun()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var newFile = _fileSystem.Path.Join(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));

        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(brokenTarget);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Repaired, result.Status);
        Assert.Equal(brokenTarget, result.OriginalTargetPath);
        Assert.Equal(newFile, result.NewTargetPath);
        // Dry run: WriteTarget should NOT be called
        _symlinkHelper.Verify(h => h.DeleteSymlink(It.IsAny<string>()), Times.Never);
        _symlinkHelper.Verify(h => h.CreateSymlink(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ProcessLinkFile_Symlink_BrokenTarget_SingleMediaFile_ActualRepair()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var newFile = _fileSystem.Path.Join(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));

        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(brokenTarget);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, false);

        Assert.Equal(LinkFileStatus.Repaired, result.Status);
        _symlinkHelper.Verify(h => h.DeleteSymlink(symlinkFile), Times.Once);
        _symlinkHelper.Verify(h => h.CreateSymlink(symlinkFile, newFile), Times.Once);
    }

    // ===== ProcessLinkFile: Shared scenarios (handler-agnostic) =====

    [Fact]
    public void ProcessLinkFile_BrokenTarget_ParentDirDoesNotExist_ReturnsBroken()
    {
        var brokenTarget = _fileSystem.Path.GetFullPath("/movies/DeletedMovie/movie.mkv");
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(brokenTarget));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_Symlink_BrokenTarget_ParentDirDoesNotExist_ReturnsBroken()
    {
        var brokenTarget = _fileSystem.Path.GetFullPath("/movies/DeletedMovie/movie.mkv");
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(brokenTarget);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_BrokenTarget_NoMediaFiles_ReturnsBroken()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "readme.txt"), new MockFileData("info"));

        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(brokenTarget));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_Symlink_BrokenTarget_NoMediaFiles_ReturnsBroken()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "readme.txt"), new MockFileData("info"));

        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(brokenTarget);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_BrokenTarget_MultipleMediaFiles_ReturnsAmbiguous()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "part1.mkv"), new MockFileData("video"));
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "part2.mkv"), new MockFileData("video"));

        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(brokenTarget));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Ambiguous, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_Symlink_BrokenTarget_MultipleMediaFiles_ReturnsAmbiguous()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "part1.mkv"), new MockFileData("video"));
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "part2.mkv"), new MockFileData("video"));

        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(brokenTarget);

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        Assert.Equal(LinkFileStatus.Ambiguous, result.Status);
    }

    // ===== URL bypass: only for handlers that support URLs =====

    [Fact]
    public void ProcessLinkFile_Symlink_UrlLikeTarget_IsNotSkippedAsUrl()
    {
        // A symlink whose target happens to contain "://" should NOT be treated as a URL.
        // Only handlers with SupportsUrlTargets == true (e.g. StrmLinkHandler) skip URL targets.
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns("https://example.com/video.mp4");

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        // The target is not a valid file path, so normalisation or file-exists check will fail.
        // The key assertion: it must NOT return Valid (which would mean the URL was silently skipped).
        Assert.NotEqual(LinkFileStatus.Valid, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_TrimsWhitespaceFromStrmTarget()
    {
        var movieFile = _fileSystem.Path.GetFullPath("/movies/Movie1/movie.mkv");
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("  " + movieFile + "  \n"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        Assert.Equal(LinkFileStatus.Valid, result.Status);
    }

    // ===== FindMediaFilesInDirectory =====

    [Fact]
    public void FindMediaFilesInDirectory_FindsOnlyVideoFiles()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "movie.mkv"), new MockFileData("video"));
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "subtitle.srt"), new MockFileData("sub"));
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "poster.jpg"), new MockFileData("img"));

        var result = _service.FindMediaFilesInDirectory(movieDir);

        Assert.Single(result);
        Assert.EndsWith(".mkv", result[0]);
    }

    [Fact]
    public void FindMediaFilesInDirectory_FindsMultipleVideoExtensions()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "movie.mkv"), new MockFileData("v"));
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "movie.mp4"), new MockFileData("v"));
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "movie.avi"), new MockFileData("v"));

        var result = _service.FindMediaFilesInDirectory(movieDir);

        Assert.Equal(3, result.Count);
    }

    // ===== RepairLinks: Full workflow =====

    [Fact]
    public void RepairLinks_Strm_FullWorkflow_DryRun()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var movieFile = _fileSystem.Path.Join(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");
        var seriesDir = _fileSystem.Path.GetFullPath("/series");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var linkFile1 = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile1, new MockFileData(brokenTarget));

        var validMovie = _fileSystem.Path.GetFullPath("/movies/Movie2/movie.mkv");
        _fileSystem.AddFile(validMovie, new MockFileData("video"));

        var linkFile2 = _fileSystem.Path.GetFullPath("/series/Show2/movie.strm");
        _fileSystem.AddFile(linkFile2, new MockFileData(validMovie));

        var result = _service.RepairLinks([seriesDir], true);

        Assert.Equal(1, result.ValidCount);
        Assert.Equal(1, result.RepairedCount);
        Assert.Equal(brokenTarget, _fileSystem.File.ReadAllText(linkFile1));
    }

    [Fact]
    public void RepairLinks_Strm_FullWorkflow_ActualRepair()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var movieFile = _fileSystem.Path.Join(movieDir, "new-name.mkv");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(brokenTarget));

        var result = _service.RepairLinks([_fileSystem.Path.GetFullPath("/series")], false);

        Assert.Equal(1, result.RepairedCount);
        Assert.Equal(movieFile, _fileSystem.File.ReadAllText(linkFile));
    }

    [Fact]
    public void RepairLinks_MultipleLibraryPaths()
    {
        var seriesDir1 = _fileSystem.Path.GetFullPath("/series1");
        var seriesDir2 = _fileSystem.Path.GetFullPath("/series2");
        var movieFile = _fileSystem.Path.GetFullPath("/movies/Movie1/movie.mkv");
        _fileSystem.AddFile(movieFile, new MockFileData("video"));

        _fileSystem.AddFile(_fileSystem.Path.GetFullPath("/series1/Show1/movie.strm"), new MockFileData(movieFile));
        _fileSystem.AddFile(_fileSystem.Path.GetFullPath("/series2/Show2/movie.strm"), new MockFileData(movieFile));

        var result = _service.RepairLinks([seriesDir1, seriesDir2], true);

        Assert.Equal(2, result.ValidCount);
        Assert.Equal(2, result.FileResults.Count);
    }

    [Fact]
    public void RepairLinks_MixedStrmAndSymlink_AggregatesBothHandlers()
    {
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var validTarget = _fileSystem.Path.Join(movieDir, "movie.mkv");
        var brokenStrmTarget = _fileSystem.Path.Join(movieDir, "old-name.mkv");

        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(validTarget, new MockFileData("video"));

        // Broken .strm file that can be repaired
        var strmFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(strmFile, new MockFileData(brokenStrmTarget));

        // Valid symlink file
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show2/episode.mkv");
        _fileSystem.AddFile(symlinkFile, new MockFileData("placeholder"));
        _symlinkHelper.Setup(h => h.IsSymlink(symlinkFile)).Returns(true);
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(validTarget);

        var result = _service.RepairLinks([seriesDir], true);

        Assert.Equal(2, result.FileResults.Count);
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(1, result.RepairedCount);
    }

    [Fact]
    public void RepairLinks_FindLinkFiles_HonorsCancellation()
    {
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        for (var i = 0; i < 100; i++)
        {
            _fileSystem.AddFile(_fileSystem.Path.Combine(seriesDir, $"file_{i}.strm"), new MockFileData("target"));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _service.RepairLinks([seriesDir], true, cts.Token));
    }
}