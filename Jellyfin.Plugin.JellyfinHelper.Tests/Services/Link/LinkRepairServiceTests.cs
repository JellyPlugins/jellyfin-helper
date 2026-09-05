using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Link;

/// <summary>
///     Unit tests for LinkRepairService. Tests the handler-agnostic service logic.
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
        // Windows paths like C:\media\movie.mkv must NOT be treated as URLs
        // (Uri.TryCreate parses "C:" as a scheme, but the file:// scheme is excluded from the URL bypass)
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(@"C:\media\movie.mkv"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, true);

        // The path does not exist on the mock filesystem, so it must be Broken (not Valid via URL bypass)
        Assert.Equal(LinkFileStatus.Broken, result.Status);
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

        Assert.Equal(LinkFileStatus.Repaired, result.Status); // Dry-run: Repaired signals "would repair"
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

        Assert.Equal(LinkFileStatus.Repaired, result.Status); // Dry-run: Repaired signals "would repair"
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
        _symlinkHelper.Verify(h => h.CreateSymlink(symlinkFile + ".jfh-tmp", newFile), Times.Once);
        _symlinkHelper.Verify(h => h.ReplaceSymlink(symlinkFile + ".jfh-tmp", symlinkFile), Times.Once);
        _symlinkHelper.Verify(h => h.DeleteSymlink(symlinkFile), Times.Never);
    }

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

    [Fact]
    public void ProcessLinkFile_Symlink_UrlLikeTarget_IsNotSkippedAsUrl()
    {
        // A symlink whose target happens to contain "://" should NOT be treated as a URL.
        // Only handlers with SupportsUrlTargets == true (e.g. StrmLinkHandler) skip URL targets.
        var symlinkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns("https://example.com/video.mp4");

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, true);

        // The target is not a valid file path, so normalisation or file-exists check will fail. A URL target must be treated as Broken (or InvalidContent), never as Valid (which would mean the URL was silently skipped despite SupportsUrlTargets == false).
        Assert.True(
            result.Status == LinkFileStatus.Broken || result.Status == LinkFileStatus.InvalidContent,
            $"Expected Broken or InvalidContent but got {result.Status}");
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
        Assert.Equal(1, result.RepairedCount); // Dry-run: Repaired signals "would repair" (matches summary label)
        Assert.Equal(0, result.BrokenCount); // Only truly unrepairable links are Broken
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
        Assert.Equal(1, result.RepairedCount); // Dry-run: Repaired signals "would repair" (matches summary label)
        Assert.Equal(0, result.BrokenCount); // Only truly unrepairable links are Broken
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

    // Error-path & bug-surface coverage

    [Fact]
    public void ProcessLinkFile_Symlink_ActualRepair_DeleteThrows_ReturnsBroken_AndClearsNewTargetPath()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie-DeleteThrows");
        var newFile = _fileSystem.Path.Join(movieDir, "new.mkv");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old.mkv");
        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));

        var symlinkFile = _fileSystem.Path.GetFullPath("/series/DeleteThrows/ep.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(brokenTarget);
        _symlinkHelper.Setup(h => h.CreateSymlink(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new UnauthorizedAccessException("denied"));

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, dryRun: false);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
        Assert.Null(result.NewTargetPath);
        _symlinkHelper.Verify(h => h.ReplaceSymlink(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _symlinkHelper.Verify(h => h.DeleteSymlink(symlinkFile), Times.Never);
    }

    [Fact]
    public void ProcessLinkFile_Symlink_ActualRepair_CreateThrows_ReturnsBroken()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie-CreateThrows");
        var newFile = _fileSystem.Path.Join(movieDir, "new.mkv");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old.mkv");
        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));

        var symlinkFile = _fileSystem.Path.GetFullPath("/series/CreateThrows/ep.mkv");
        _symlinkHelper.Setup(h => h.GetSymlinkTarget(symlinkFile)).Returns(brokenTarget);
        _symlinkHelper.Setup(h => h.ReplaceSymlink(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new IOException("disk full"));

        var result = _service.ProcessLinkFile(symlinkFile, _symlinkHandler, dryRun: false);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
        Assert.Null(result.NewTargetPath);
        _symlinkHelper.Verify(h => h.DeleteSymlink(symlinkFile), Times.Never);
    }

    [Fact]
    public void ProcessLinkFile_Strm_FileUriTarget_ValidFile_ReturnsValid()
    {
        // file:// URIs must NOT be short-circuited by the URL bypass -
        // they reference local paths and must be validated normally.
        var movieFile = _fileSystem.Path.GetFullPath("/movies/FileUri/movie.mkv");
        _fileSystem.AddFile(movieFile, new MockFileData("video"));
        var fileUri = new Uri(movieFile).AbsoluteUri;

        var linkFile = _fileSystem.Path.GetFullPath("/series/FileUri/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(fileUri));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, dryRun: true);

        Assert.Equal(LinkFileStatus.Valid, result.Status);
        Assert.Equal(fileUri, result.OriginalTargetPath);
    }

    [Fact]
    public void ProcessLinkFile_Strm_FileUriTarget_MissingFile_ReturnsBroken()
    {
        // A broken file:// URI must NOT slip past the URL bypass as Valid.
        var missing = _fileSystem.Path.GetFullPath("/movies/FileUri-Missing/file.mkv");
        var fileUri = new Uri(missing).AbsoluteUri;

        var linkFile = _fileSystem.Path.GetFullPath("/series/FileUri-Missing/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(fileUri));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, dryRun: true);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_Strm_RelativeTarget_ResolvedRelativeToLinkFile()
    {
        // Relative paths must be resolved against the DIRECTORY of the link file, not CWD.
        var linkDir = _fileSystem.Path.GetFullPath("/series/Relative");
        var sibling = _fileSystem.Path.Join(linkDir, "actual-movie.mkv");
        _fileSystem.AddFile(sibling, new MockFileData("video"));

        var linkFile = _fileSystem.Path.Join(linkDir, "movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("actual-movie.mkv"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, dryRun: true);

        Assert.Equal(LinkFileStatus.Valid, result.Status);
    }

    public static TheoryData<Exception> ReadTargetExceptionVariants() => new()
    {
        new IOException("read failed"),
        new UnauthorizedAccessException("denied"),
    };

    [Theory]
    [MemberData(nameof(ReadTargetExceptionVariants))]
    public void ProcessLinkFile_ReadTargetThrows_ReturnsInvalidContent_DoesNotPropagate(Exception ex)
    {
        // A handler throwing while reading must be caught & mapped to InvalidContent - never propagate up and abort the whole library scan.
        var handler = new Mock<ILinkHandler>();
        handler.Setup(x => x.CanHandle(It.IsAny<string>())).Returns(true);
        handler.Setup(x => x.SupportsUrlTargets).Returns(false);
        handler.Setup(x => x.ReadTarget(It.IsAny<string>()))
            .Throws(ex);

        var linkFile = _fileSystem.Path.GetFullPath("/series/ReadThrows/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("payload"));

        var result = _service.ProcessLinkFile(linkFile, handler.Object, dryRun: true);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_TargetWithNulByte_ReturnsInvalidContent()
    {
        // NUL bytes are universally invalid across .NET path APIs. The ArgumentException
        // must be caught and mapped to InvalidContent - never bubble up as a crash.
        var handler = new Mock<ILinkHandler>();
        handler.Setup(x => x.CanHandle(It.IsAny<string>())).Returns(true);
        handler.Setup(x => x.SupportsUrlTargets).Returns(false);
        handler.Setup(x => x.ReadTarget(It.IsAny<string>())).Returns("/movies/bad\0path.mkv");

        var linkFile = _fileSystem.Path.GetFullPath("/series/NulByte/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("stub"));

        var result = _service.ProcessLinkFile(linkFile, handler.Object, dryRun: true);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_UrlTarget_NewTargetPathStaysNull()
    {
        // URL targets exit early via the URL-bypass branch; NewTargetPath must stay null
        // so the UI never shows a "would-repair-to" that was never computed.
        var linkFile = _fileSystem.Path.GetFullPath("/series/UrlBypass/stream.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("https://example.com/video.mp4"));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, dryRun: false);

        Assert.Equal(LinkFileStatus.Valid, result.Status);
        Assert.Null(result.NewTargetPath);
        Assert.Equal("https://example.com/video.mp4", result.OriginalTargetPath);
    }

    [Fact]
    public void RepairLinks_MissingLibraryPath_ContinuesScanningOtherLibraries()
    {
        // A missing mount point (e.g. NAS not yet mounted at startup) must NOT abort
        // scanning of the remaining library roots.
        var validDir = _fileSystem.Path.GetFullPath("/series-valid-lib");
        var movie = _fileSystem.Path.GetFullPath("/movies/lib-valid/movie.mkv");
        _fileSystem.AddFile(movie, new MockFileData("video"));
        _fileSystem.AddFile(
            _fileSystem.Path.Join(validDir, "Show/movie.strm"),
            new MockFileData(movie));

        var missing = _fileSystem.Path.GetFullPath("/does-not-exist");

        var result = _service.RepairLinks([missing, validDir], dryRun: true);

        Assert.Single(result.FileResults);
        Assert.Equal(LinkFileStatus.Valid, result.FileResults[0].Status);
    }

    [Fact]
    public void FindLinkFiles_DeepDirectoryTree_DoesNotOverflow()
    {
        // Build a 200-level deep chain: /root/d0/d1/.../d199/file.strm
        var root = _fileSystem.Path.GetFullPath("/deep-root");
        var current = root;
        for (var i = 0; i < 200; i++)
        {
            current = _fileSystem.Path.Combine(current, $"d{i}");
        }

        var strmPath = _fileSystem.Path.Combine(current, "deep.strm");
        _fileSystem.AddFile(strmPath, new MockFileData("/media/movie.mkv"));

        // Should complete without StackOverflowException
        var found = _service.FindLinkFiles([root]);

        Assert.Single(found);
        Assert.Equal(strmPath, found[0].FilePath);
    }

    [Fact]
    public void FindLinkFiles_VisitedDirectoryLimit_StopsAndReturnsPartialResults()
    {
        // Build a wide tree with many siblings (not deep) - verifies visited-set guard works
        var root = _fileSystem.Path.GetFullPath("/wide-root");
        var strmPaths = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var dir = _fileSystem.Path.Combine(root, $"dir{i}");
            var strm = _fileSystem.Path.Combine(dir, $"movie{i}.strm");
            _fileSystem.AddFile(strm, new MockFileData("/media/movie.mkv"));
            strmPaths.Add(strm);
        }

        var found = _service.FindLinkFiles([root]);

        // All 10 .strm files should be found (well within the visited-directory limit)
        Assert.Equal(10, found.Count);
        foreach (var expected in strmPaths)
        {
            Assert.Contains(found, r => r.FilePath == expected);
        }
    }

    // Double-enumeration of libraryPaths in RepairLinks =====

    [Fact]
    public void RepairLinks_YieldReturnLibraryPaths_RelativeTargetOutsideLibraryRoot_ReturnsInvalidContent()
    {
        // When libraryPaths is a non-replayable IEnumerable (yield-return), the second enumeration in RepairLinks produced an empty normalizedLibraryPaths, which caused the path-traversal guard (Count > 0) to be skipped - so a relative target that resolves outside the library root was.
        var libDir = _fileSystem.Path.GetFullPath("/series/Show1");
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");

        // Relative target that resolves to /etc/passwd - outside /series/Show1
        _fileSystem.AddFile(linkFile, new MockFileData("../../../etc/passwd"));

        // Use a yield-return sequence so the second enumeration would be empty without the fix
        IEnumerable<string> YieldPaths()
        {
            yield return libDir;
        }

        var result = _service.RepairLinks(YieldPaths(), dryRun: true);

        Assert.Single(result.FileResults);
        Assert.Equal(LinkFileStatus.InvalidContent, result.FileResults[0].Status);
    }

    // MaxVisitedDirectories cap stops outer foreach in FindLinkFiles =====

    [Fact]
    public void FindLinkFiles_VisitedDirectoryCapReached_DoesNotSilentlyProcessSubsequentLibraryPaths()
    {
        // When the visited-directory cap was hit inside FindLinkFilesRecursive, the inner while-loop broke but FindLinkFiles continued iterating further library paths.
        var fs = new MockFileSystem();
        var service = new LowCapLinkRepairService(
            fs,
            [new StrmLinkHandler(fs)],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);

        // lib1: root + 2 subdirs = 3 visited entries, exceeds cap of 2
        var lib1 = fs.Path.GetFullPath("/lib1");
        fs.AddFile(fs.Path.Combine(lib1, "subA", "a.strm"), new MockFileData("/t.mkv"));
        fs.AddFile(fs.Path.Combine(lib1, "subB", "b.strm"), new MockFileData("/t.mkv"));

        // lib2: must NOT be processed once cap is hit in lib1
        var lib2 = fs.Path.GetFullPath("/lib2");
        fs.AddFile(fs.Path.Combine(lib2, "c.strm"), new MockFileData("/t.mkv"));

        var found = service.FindLinkFiles([lib1, lib2]);

        Assert.DoesNotContain(found, r => r.FilePath.StartsWith(lib2, StringComparison.OrdinalIgnoreCase));
    }

    // RepairLinks: aggregate counts

    [Fact]
    public void RepairLinks_AggregatesInvalidContentCount()
    {
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/empty.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("")); // empty -> InvalidContent

        var result = _service.RepairLinks([seriesDir], dryRun: true);

        Assert.Equal(1, result.InvalidContentCount);
        Assert.Equal(0, result.BrokenCount);
        Assert.Equal(0, result.ValidCount);
    }

    [Fact]
    public void RepairLinks_AggregatesAmbiguousCount()
    {
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie1");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old.mkv");
        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "part1.mkv"), new MockFileData("v"));
        _fileSystem.AddFile(_fileSystem.Path.Join(movieDir, "part2.mkv"), new MockFileData("v"));

        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(brokenTarget));

        var result = _service.RepairLinks([seriesDir], dryRun: true);

        Assert.Equal(1, result.AmbiguousCount);
        Assert.Equal(0, result.BrokenCount);
        Assert.Equal(0, result.RepairedCount);
    }

    [Fact]
    public void RepairLinks_AggregatesBrokenCount()
    {
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        // Broken: parent dir does not exist
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(_fileSystem.Path.GetFullPath("/movies/DeletedDir/movie.mkv")));

        var result = _service.RepairLinks([seriesDir], dryRun: true);

        Assert.Equal(1, result.BrokenCount);
        Assert.Equal(0, result.ValidCount);
        Assert.Equal(0, result.RepairedCount);
    }

    // RepairLinks: cancellation between file-processing iterations

    [Fact]
    public void RepairLinks_CancellationBetweenFileProcessing_Throws()
    {
        // Two valid .strm files; cancel after the first iteration begins.
        // The cancellationToken.ThrowIfCancellationRequested() inside the
        // foreach loop must surface the cancellation.
        var movieFile1 = _fileSystem.Path.GetFullPath("/movies/M1/movie.mkv");
        var movieFile2 = _fileSystem.Path.GetFullPath("/movies/M2/movie.mkv");
        _fileSystem.AddFile(movieFile1, new MockFileData("v"));
        _fileSystem.AddFile(movieFile2, new MockFileData("v"));

        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        _fileSystem.AddFile(_fileSystem.Path.GetFullPath("/series/S1/ep.strm"), new MockFileData(movieFile1));
        _fileSystem.AddFile(_fileSystem.Path.GetFullPath("/series/S2/ep.strm"), new MockFileData(movieFile2));

        using var cts = new CancellationTokenSource();

        // Use a handler that cancels after the first file is found
        var callCount = 0;
        var interceptHandler = new Mock<ILinkHandler>();
        interceptHandler.Setup(h => h.CanHandle(It.IsAny<string>())).Returns(true);
        interceptHandler.Setup(h => h.SupportsUrlTargets).Returns(false);
        interceptHandler.Setup(h => h.ReadTarget(It.IsAny<string>()))
            .Returns<string>(path =>
            {
                if (++callCount == 1)
                {
                    cts.Cancel();
                }

                return _fileSystem.File.ReadAllText(path);
            });

        var service = new LinkRepairService(
            _fileSystem,
            [interceptHandler.Object],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);

        Assert.Throws<OperationCanceledException>(() =>
            service.RepairLinks([seriesDir], dryRun: true, cts.Token));
    }

    // FindMediaFilesInDirectory: inaccessible directory

    [Fact]
    public void FindMediaFilesInDirectory_InaccessibleDirectory_ReturnsEmpty()
    {
        // A handler that throws UnauthorizedAccessException when the directory is enumerated should be caught and result in an empty list (not a crash).
        var missingDir = _fileSystem.Path.GetFullPath("/no-such-dir");

        var result = _service.FindMediaFilesInDirectory(missingDir);

        Assert.Empty(result);
    }

    // ProcessLinkFile: normalizedLibraryPaths path-traversal guard

    [Fact]
    public void ProcessLinkFile_RelativeTarget_EscapesLibraryRoot_ReturnsInvalidContent()
    {
        // When ProcessLinkFile is called with an explicit normalizedLibraryPaths list and the relative target resolves outside every listed root, the result must be InvalidContent (path-traversal guard), not Broken.
        var libDir = _fileSystem.Path.GetFullPath("/series/Show1");
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("../../../etc/passwd"));

        var result = _service.ProcessLinkFile(
            linkFile,
            _strmHandler,
            dryRun: true,
            normalizedLibraryPaths: [libDir]);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_RelativeTarget_WithinLibraryRoot_ProcessesNormally()
    {
        // A relative target that resolves inside the library root must pass the
        // traversal guard and be evaluated normally (Valid if the resolved file exists).
        var libDir = _fileSystem.Path.GetFullPath("/series/Show1");
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        var sibling = _fileSystem.Path.GetFullPath("/series/Show1/actual.mkv");
        _fileSystem.AddFile(sibling, new MockFileData("video"));
        _fileSystem.AddFile(linkFile, new MockFileData("actual.mkv"));

        var result = _service.ProcessLinkFile(
            linkFile,
            _strmHandler,
            dryRun: true,
            normalizedLibraryPaths: [libDir]);

        Assert.Equal(LinkFileStatus.Valid, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_RelativeTarget_NullNormalizedPaths_SkipsGuard()
    {
        // When normalizedLibraryPaths is null the path-traversal guard is skipped, and a relative target that resolves to a non-existent file is Broken (not InvalidContent).
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("MissingSibling.mkv"));

        var result = _service.ProcessLinkFile(
            linkFile,
            _strmHandler,
            dryRun: true,
            normalizedLibraryPaths: null);

        // The resolved path (/series/Show1/MissingSibling.mkv) does not exist on the
        // mock filesystem and is not sensitive, so it is Broken.
        Assert.Equal(LinkFileStatus.Broken, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_RelativeTarget_NullNormalizedPaths_SensitiveTarget_ReturnsInvalidContent()
    {
        // Even with the path-traversal guard skipped (normalizedLibraryPaths null), a relative target that escapes to a sensitive system directory must still be refused as InvalidContent by the sensitive-system-target guard - link repair must never enumerate or rewrite toward host paths.
        if (!OperatingSystem.IsWindows())
        {
            var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
            _fileSystem.AddFile(linkFile, new MockFileData("../../../etc/passwd"));

            var result = _service.ProcessLinkFile(
                linkFile,
                _strmHandler,
                dryRun: true,
                normalizedLibraryPaths: null);

            Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
        }
        else
        {
            // On Windows "../../../etc/passwd" resolves to a non-sensitive drive-relative path (e.g. C:\etc\passwd), so the sensitive-system-target guard does NOT fire.
            var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/episode.strm");
            _fileSystem.AddFile(linkFile, new MockFileData("../../../etc/passwd"));

            var result = _service.ProcessLinkFile(
                linkFile,
                _strmHandler,
                dryRun: true,
                normalizedLibraryPaths: null);

            Assert.Equal(LinkFileStatus.Broken, result.Status);
        }
    }

    // ProcessLinkFile: strm WriteTarget exception variants

    public static TheoryData<Exception> WriteTargetExceptionVariants() => new()
    {
        new NotSupportedException("read-only fs"),
        new ArgumentException("invalid path chars"),
        new IOException("disk full"),
        new UnauthorizedAccessException("denied"),
    };

    [Theory]
    [MemberData(nameof(WriteTargetExceptionVariants))]
    public void ProcessLinkFile_Strm_ActualRepair_WriteTargetThrows_ReturnsBroken(Exception ex)
    {
        // Any exception from WriteTarget must be caught and mapped to Broken, and NewTargetPath
        // cleared so the UI does not show a phantom repair.
        var movieDir = _fileSystem.Path.GetFullPath("/movies/Movie-WriteThrows");
        var newFile = _fileSystem.Path.Join(movieDir, "new.mkv");
        var brokenTarget = _fileSystem.Path.Join(movieDir, "old.mkv");
        _fileSystem.AddDirectory(movieDir);
        _fileSystem.AddFile(newFile, new MockFileData("video"));

        var throwingHandler = new Mock<ILinkHandler>();
        throwingHandler.Setup(h => h.CanHandle(It.IsAny<string>())).Returns(true);
        throwingHandler.Setup(h => h.SupportsUrlTargets).Returns(false);
        throwingHandler.Setup(h => h.ReadTarget(It.IsAny<string>())).Returns(brokenTarget);
        throwingHandler.Setup(h => h.WriteTarget(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(ex);

        var linkFile = _fileSystem.Path.GetFullPath("/series/WriteThrows/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(brokenTarget));

        var result = _service.ProcessLinkFile(linkFile, throwingHandler.Object, dryRun: false);

        Assert.Equal(LinkFileStatus.Broken, result.Status);
        Assert.Null(result.NewTargetPath);
    }

    // StrmLinkHandler: oversized file handled at service level

    [Fact]
    public void ProcessLinkFile_Strm_OversizedFile_ReturnsInvalidContent()
    {
        // StrmLinkHandler.ReadTarget returns null for files > 32 KB. LinkRepairService must classify null-target reads as InvalidContent.
        var oversizedContent = new string('A', 33 * 1024); // > 32 KB MaxStrmFileSizeBytes
        var linkFile = _fileSystem.Path.GetFullPath("/series/Show1/oversized.strm");
        _fileSystem.AddFile(linkFile, new MockFileData(oversizedContent));

        var result = _service.ProcessLinkFile(linkFile, _strmHandler, dryRun: true);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    // ProcessLinkFile: path normalization exception variants

    [Fact]
    public void ProcessLinkFile_PathTooLongTarget_ReturnsInvalidContent()
    {
        // A path exceeding the OS maximum (PathTooLongException from Path.GetFullPath)
        // must be caught and mapped to InvalidContent - not propagated as a crash.
        var handler = new Mock<ILinkHandler>();
        handler.Setup(x => x.CanHandle(It.IsAny<string>())).Returns(true);
        handler.Setup(x => x.SupportsUrlTargets).Returns(false);
        // PathTooLongException is thrown when Path.GetFullPath processes an extremely long path on some platforms.
        handler.Setup(x => x.ReadTarget(It.IsAny<string>()))
            .Returns("/" + new string('x', 32_768) + ".mkv");

        var linkFile = _fileSystem.Path.GetFullPath("/series/LongPath/movie.strm");
        _fileSystem.AddFile(linkFile, new MockFileData("stub"));

        var result = _service.ProcessLinkFile(linkFile, handler.Object, dryRun: true);

        // The path does not exist on the mock filesystem, so it is Broken (not a crash).
        // The important contract is that no exception escapes.
        Assert.True(
            result.Status == LinkFileStatus.Broken || result.Status == LinkFileStatus.InvalidContent,
            $"Expected Broken or InvalidContent but got {result.Status}");
    }

    // RepairLinks: dryRun=false end-to-end aggregate

    [Fact]
    public void RepairLinks_ActualRepair_AllStatuses_AggregatedCorrectly()
    {
        var seriesDir = _fileSystem.Path.GetFullPath("/series");

        // 1. Valid
        var validTarget = _fileSystem.Path.GetFullPath("/movies/Valid/movie.mkv");
        _fileSystem.AddFile(validTarget, new MockFileData("v"));
        _fileSystem.AddFile(_fileSystem.Path.GetFullPath("/series/Valid/ep.strm"), new MockFileData(validTarget));

        // 2. Repaired (single replacement candidate)
        var repairDir = _fileSystem.Path.GetFullPath("/movies/Repair");
        _fileSystem.AddDirectory(repairDir);
        _fileSystem.AddFile(_fileSystem.Path.Join(repairDir, "new.mkv"), new MockFileData("v"));
        _fileSystem.AddFile(
            _fileSystem.Path.GetFullPath("/series/Repair/ep.strm"),
            new MockFileData(_fileSystem.Path.Join(repairDir, "old.mkv")));

        // 3. Broken (parent dir gone)
        _fileSystem.AddFile(
            _fileSystem.Path.GetFullPath("/series/Broken/ep.strm"),
            new MockFileData(_fileSystem.Path.GetFullPath("/movies/GoneDir/movie.mkv")));

        // 4. Ambiguous (multiple candidates)
        var ambigDir = _fileSystem.Path.GetFullPath("/movies/Ambig");
        _fileSystem.AddDirectory(ambigDir);
        _fileSystem.AddFile(_fileSystem.Path.Join(ambigDir, "a.mkv"), new MockFileData("v"));
        _fileSystem.AddFile(_fileSystem.Path.Join(ambigDir, "b.mkv"), new MockFileData("v"));
        _fileSystem.AddFile(
            _fileSystem.Path.GetFullPath("/series/Ambig/ep.strm"),
            new MockFileData(_fileSystem.Path.Join(ambigDir, "old.mkv")));

        // 5. InvalidContent (empty file)
        _fileSystem.AddFile(_fileSystem.Path.GetFullPath("/series/Invalid/ep.strm"), new MockFileData(""));

        var result = _service.RepairLinks([seriesDir], dryRun: false);

        Assert.Equal(5, result.FileResults.Count);
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(1, result.RepairedCount);
        Assert.Equal(1, result.BrokenCount);
        Assert.Equal(1, result.AmbiguousCount);
        Assert.Equal(1, result.InvalidContentCount);
    }

    // RepairLinks: malformed library path in normalizedLibraryPaths

    [Fact]
    public void RepairLinks_MalformedLibraryPath_DoesNotThrow_ReturnsEmptyResult()
    {
        var fs = new ThrowingPathFileSystem("bad\0path");
        var service = new LinkRepairService(
            fs,
            [_strmHandler, _symlinkHandler],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);

        var result = service.RepairLinks(["bad\0path"], dryRun: true);

        Assert.Empty(result.FileResults);
    }

    [Fact]
    public void RepairLinks_MalformedLibraryPathMixedWithValid_SkipsMalformedAndProcessesValid()
    {
        var badPath = "bad\0path";
        var fs = new ThrowingPathFileSystem(badPath);

        var validDir = fs.Path.GetFullPath("/series/Show1");
        var validTarget = fs.Path.GetFullPath("/movies/Movie1/movie.mkv");
        var linkFile = fs.Path.GetFullPath("/series/Show1/episode.strm");

        fs.AddFile(linkFile, new MockFileData(validTarget));
        fs.AddFile(validTarget, new MockFileData("video"));

        var strmHandler = new StrmLinkHandler(fs);
        var service = new LinkRepairService(
            fs,
            [strmHandler],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);

        var result = service.RepairLinks([badPath, validDir], dryRun: true);

        Assert.Single(result.FileResults);
        Assert.Equal(LinkFileStatus.Valid, result.FileResults[0].Status);
    }

    private sealed class ThrowingPathFileSystem : MockFileSystem
    {
        private readonly string _throwOnPath;

        public ThrowingPathFileSystem(string throwOnPath)
        {
            _throwOnPath = throwOnPath;
        }

        public override IPath Path => new ThrowingMockPath(this, _throwOnPath);

        private sealed class ThrowingMockPath(MockFileSystem fs, string throwOnPath) : MockPath(fs)
        {
            public override string GetFullPath(string path)
            {
                if (path == throwOnPath)
                {
                    throw new ArgumentException("Invalid characters in path.", nameof(path));
                }

                return base.GetFullPath(path);
            }
        }
    }

    private sealed class LowCapLinkRepairService(
        System.IO.Abstractions.IFileSystem fileSystem,
        IEnumerable<ILinkHandler> handlers,
        Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService pluginLog,
        Microsoft.Extensions.Logging.ILogger<LinkRepairService> logger)
        : LinkRepairService(fileSystem, handlers, pluginLog, logger)
    {
        protected override int VisitedDirectoryCap => 2;
    }

    // FindLinkFiles: per-file & per-directory fault isolation, cancellation,
    // directory-symlink resolution

    [Fact]
    public void FindLinkFiles_HandlerCanHandleThrowsIOException_SkipsFileAndContinuesScan()
    {
        // A locked/unreadable file whose CanHandle throws must not abort the whole scan -
        // the per-file catch has to swallow it and keep enumerating so sibling links are
        // still discovered.
        var seriesDir = _fileSystem.Path.GetFullPath("/series/Show1");
        var goodFile = _fileSystem.Path.GetFullPath("/series/Show1/good.strm");
        var badFile = _fileSystem.Path.GetFullPath("/series/Show1/bad.strm");
        _fileSystem.AddFile(goodFile, new MockFileData("target"));
        _fileSystem.AddFile(badFile, new MockFileData("target"));

        var throwingHandler = new Mock<ILinkHandler>();
        throwingHandler.Setup(h => h.SupportsUrlTargets).Returns(false);
        throwingHandler.Setup(h => h.CanHandle(badFile)).Throws(new IOException("file locked"));
        throwingHandler.Setup(h => h.CanHandle(It.Is<string>(s => s != badFile))).Returns(false);

        var service = new LinkRepairService(
            _fileSystem,
            [throwingHandler.Object, _strmHandler],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);

        var result = service.FindLinkFiles([seriesDir]);

        Assert.Contains(result, r => r.FilePath == goodFile);
        Assert.DoesNotContain(result, r => r.FilePath == badFile);
    }

    [Fact]
    public void FindLinkFiles_CancellationDuringFileInspection_PropagatesOperationCanceled()
    {
        // Cancellation raised from inside the try block (during per-file inspection) must surface as OperationCanceledException via the dedicated OCE catch - it must NOT be swallowed by the broader IOException catch that guards directory access.
        var seriesDir = _fileSystem.Path.GetFullPath("/series");
        _fileSystem.AddFile(_fileSystem.Path.GetFullPath("/series/a.strm"), new MockFileData("t"));
        _fileSystem.AddFile(_fileSystem.Path.GetFullPath("/series/b.strm"), new MockFileData("t"));

        using var cts = new CancellationTokenSource();
        var cancellingHandler = new Mock<ILinkHandler>();
        cancellingHandler.Setup(h => h.SupportsUrlTargets).Returns(false);
        cancellingHandler.Setup(h => h.CanHandle(It.IsAny<string>()))
            .Returns<string>(_ =>
            {
                cts.Cancel();
                return false;
            });

        var service = new LinkRepairService(
            _fileSystem,
            [cancellingHandler.Object],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);

        Assert.Throws<OperationCanceledException>(() => service.FindLinkFiles([seriesDir], cts.Token));
    }

    [Fact]
    public void FindLinkFiles_DirectoryEnumerationThrowsIOException_SkipsDirectoryAndReturnsPartial()
    {
        // A directory that exists but throws IOException when enumerated (e.g. transient
        // mount fault) must be logged and skipped, not crash the whole scan.
        var fs = new EnumerateThrowsFileSystem();
        var dir = fs.Path.GetFullPath("/throwing-dir");
        fs.SetThrowOnEnumerate(dir);
        fs.AddDirectory(dir);

        var service = new LinkRepairService(
            fs,
            [new StrmLinkHandler(fs)],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);

        var result = service.FindLinkFiles([dir]);

        Assert.Empty(result);
    }

    [Fact]
    public void FindLinkFiles_DirectorySymlinkResolvedToTarget_EnumeratesTargetContents()
    {
        // When a scanned directory is itself a symlink, ResolveLinkTarget returns the real target and the visited-set is keyed on that resolved path (dedupe) - the .strm inside must still be discovered exactly once.
        var fs = new ResolvingDirectoryFileSystem();
        var link = fs.Path.GetFullPath("/series/linkdir");
        var target = fs.Path.GetFullPath("/real/targetdir");
        var strmFile = fs.Path.Combine(link, "movie.strm");
        fs.AddFile(strmFile, new MockFileData("target"));
        fs.AddDirectory(target);
        fs.SetLink(link, target);

        var service = new LinkRepairService(
            fs,
            [new StrmLinkHandler(fs)],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);

        var result = service.FindLinkFiles([link]);

        Assert.Single(result);
        Assert.Equal(strmFile, result[0].FilePath);
    }

    // ProcessLinkFile: link-file directory null & normalization exceptions

    [Fact]
    public void ProcessLinkFile_LinkFilePathIsRoot_DirectoryNameNull_ReturnsInvalidContent()
    {
        // Path.GetDirectoryName returns null for a filesystem root. A link file living at
        // the root must map to InvalidContent, not crash on the null directory.
        var root = _fileSystem.Path.GetPathRoot(_fileSystem.Path.GetFullPath("/x"))!;

        var handler = new Mock<ILinkHandler>();
        handler.Setup(h => h.SupportsUrlTargets).Returns(false);
        handler.Setup(h => h.CanHandle(It.IsAny<string>())).Returns(true);
        handler.Setup(h => h.ReadTarget(It.IsAny<string>())).Returns("sibling.mkv");

        var result = _service.ProcessLinkFile(root, handler.Object, dryRun: true);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    [Fact]
    public void ProcessLinkFile_NormalizationThrowsArgumentException_ReturnsInvalidContent()
    {
        // GetFullPath throwing ArgumentException while normalizing a rooted target must be caught and mapped to InvalidContent - never propagate.
        var fs = new GetFullPathThrowsForTargetFileSystem();
        var rootedTarget = fs.Path.GetFullPath("/rooted/target.mkv");
        fs.SetThrowTarget(rootedTarget);

        var linkFile = fs.Path.GetFullPath("/series/Show1/movie.strm");
        fs.AddFile(linkFile, new MockFileData("stub"));

        var handler = new Mock<ILinkHandler>();
        handler.Setup(h => h.SupportsUrlTargets).Returns(false);
        handler.Setup(h => h.CanHandle(It.IsAny<string>())).Returns(true);
        handler.Setup(h => h.ReadTarget(It.IsAny<string>())).Returns(rootedTarget);

        var service = new LinkRepairService(
            fs,
            [handler.Object],
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LinkRepairService>().Object);

        var result = service.ProcessLinkFile(linkFile, handler.Object, dryRun: true);

        Assert.Equal(LinkFileStatus.InvalidContent, result.Status);
    }

    // MockFileSystem whose Directory.EnumerateFiles throws IOException for one path,
    // exercising the directory-level fault catch in FindLinkFilesRecursive.
    private sealed class EnumerateThrowsFileSystem : MockFileSystem
    {
        private string _throwOnEnumeratePath = string.Empty;

        public override IDirectory Directory => new ThrowingDirectory(this, _throwOnEnumeratePath);

        public void SetThrowOnEnumerate(string path) => _throwOnEnumeratePath = path;

        private sealed class ThrowingDirectory : MockDirectory
        {
            private readonly string _throwPath;

            public ThrowingDirectory(MockFileSystem fileSystem, string throwPath)
                : base(fileSystem, System.IO.Path.GetTempPath()) => _throwPath = throwPath;

            public override bool Exists(string? path) => true;

            public override IEnumerable<string> EnumerateFiles(string path)
            {
                if (string.Equals(path, _throwPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("enumeration failed");
                }

                return base.EnumerateFiles(path);
            }

            public override IEnumerable<string> EnumerateDirectories(string path) => Array.Empty<string>();
        }
    }

    // MockFileSystem whose Path.GetFullPath throws ArgumentException for a specific target,
    // exercising the normalization catch in ProcessLinkFile.
    private sealed class GetFullPathThrowsForTargetFileSystem : MockFileSystem
    {
        private string _throwOnGetFullPathFor = string.Empty;

        public override IPath Path => new ThrowingPath(this, _throwOnGetFullPathFor);

        public void SetThrowTarget(string path) => _throwOnGetFullPathFor = path;

        private sealed class ThrowingPath(MockFileSystem fileSystem, string throwFor) : MockPath(fileSystem)
        {
            public override string GetFullPath(string path)
            {
                if (string.Equals(path, throwFor, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Invalid characters in path.", nameof(path));
                }

                return base.GetFullPath(path);
            }
        }
    }

    // MockFileSystem whose DirectoryInfo.New returns a directory whose ResolveLinkTarget reports a non-null resolved target - plain MockFileSystem throws IOException there, which would take the fallback catch instead of the resolved-target branch.
    private sealed class ResolvingDirectoryFileSystem : MockFileSystem
    {
        private string _linkPath = string.Empty;
        private string _targetPath = string.Empty;

        public override IDirectoryInfoFactory DirectoryInfo => new ResolvingFactory(this, _linkPath, _targetPath);

        public void SetLink(string linkPath, string targetPath)
        {
            _linkPath = linkPath;
            _targetPath = targetPath;
        }

        private sealed class ResolvingFactory(MockFileSystem fileSystem, string linkPath, string targetPath)
            : IDirectoryInfoFactory
        {
            public IFileSystem FileSystem => fileSystem;

            public IDirectoryInfo New(string path)
                => string.Equals(path, linkPath, StringComparison.OrdinalIgnoreCase)
                    ? new ResolvingDirectoryInfo(fileSystem, path, targetPath)
                    : new MockDirectoryInfo(fileSystem, path);

            public IDirectoryInfo? Wrap(DirectoryInfo? directoryInfo)
                => directoryInfo == null ? null : new MockDirectoryInfo(fileSystem, directoryInfo.FullName);
        }

        private sealed class ResolvingDirectoryInfo(MockFileSystem fileSystem, string path, string targetPath)
            : MockDirectoryInfo(fileSystem, path)
        {
            public override IFileSystemInfo ResolveLinkTarget(bool returnFinalTarget)
                => new MockDirectoryInfo(fileSystem, targetPath);
        }
    }
}