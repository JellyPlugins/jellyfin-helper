using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
///     Integration tests for <see cref="CleanOrphanedSubtitlesTask"/>'s <c>ProcessLocation</c> flow.
/// </summary>
public sealed class CleanOrphanedSubtitlesTaskProcessLocationTests : CleanupTaskTestBase
{
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<ILogger<CleanOrphanedSubtitlesTask>> _loggerMock;
    private readonly CleanOrphanedSubtitlesTask _task;

    public CleanOrphanedSubtitlesTaskProcessLocationTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        _loggerMock = TestMockFactory.CreateLogger<CleanOrphanedSubtitlesTask>();

        _task = new CleanOrphanedSubtitlesTask(
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(),
            _loggerMock.Object,
            MockConfigHelper.Object,
            MockTrackingService.Object,
            MockTrashService.Object);
    }

    // ===== Chunk 1: Happy path - orphan detection & keep-if-matched =====

    [Fact]
    public async Task Execute_OrphanedSubtitleInDirWithVideo_IsDetectedInDryRun()
    {
        const string lib = "/media/movies";
        const string dir = "/media/movies/Old Movie (2018)";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, dir);
        SetupFilesInDir(lib);
        // Subtitle "MovieB" does NOT match video "MovieA" → orphan.
        SetupFilesInDir(dir, "MovieA.mkv", "MovieB.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "[Dry Run] Would delete orphaned subtitle", LogLevel.Information);
    }

    [Fact]
    public async Task Execute_SubtitleWithMatchingVideo_IsKept()
    {
        const string lib = "/media/movies";
        const string dir = "/media/movies/Good Movie (2020)";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, dir);
        SetupFilesInDir(lib);
        // "Movie.en.srt" strips "en" → base "Movie" matches "Movie.mkv".
        SetupFilesInDir(dir, "Movie.mkv", "Movie.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains(_loggerMock, "Would delete orphaned subtitle", LogLevel.Information);
        VerifyLogContains(_loggerMock, "Would have deleted 0 files", LogLevel.Information);
    }

    [Fact]
    public async Task Execute_DirWithOnlySubtitles_NoVideo_SkipsAll()
    {
        // No video in the directory → skip subtitle deletion (e.g. anime-only layouts).
        const string lib = "/media/tv";
        const string dir = "/media/tv/Subs Only (2020)/Season 01";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, dir);
        SetupFilesInDir(lib);
        SetupFilesInDir(dir, "S01E01.en.srt", "S01E02.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains(_loggerMock, "Would delete orphaned subtitle", LogLevel.Information);
        VerifyLogContains(_loggerMock, "Would have deleted 0 files", LogLevel.Information);
    }

    // ===== Chunk 2: guarded directories (trickplay, trash) =====

    [Fact]
    public async Task Execute_TrickplayDir_IsSkipped()
    {
        // ".trickplay" folders must not be scanned even when they contain look-alike files.
        const string lib = "/media/movies";
        const string trickplayDir = "/media/movies/Movie.trickplay";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, trickplayDir);
        SetupFilesInDir(lib);
        SetupFilesInDir(trickplayDir, "index.json", "movie.mkv", "orphan.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains(_loggerMock, "Would delete orphaned subtitle", LogLevel.Information);
    }

    [Fact]
    public async Task Execute_OrphanInsideTrashFolder_IsSkipped()
    {
        // Skip re-processing of files already inside the trash folder (root + nested).
        Config.OrphanedSubtitleTaskMode = TaskMode.Activate;
        Config.UseTrash = true;

        var lib = TestPath("media", "movies");
        var trashDir = Path.Join(lib, ".trash");
        var trashSubDir = Path.Join(trashDir, "2020-01-01");
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, trashDir, trashSubDir);
        SetupFilesInDir(lib);
        SetupFilesInDir(trashDir, "MovieA.mkv", "Orphan.en.srt");
        SetupFilesInDir(trashSubDir, "MovieA.mkv", "Orphan.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        MockTrashService.Verify(
            t => t.MoveFileToTrash(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
            Times.Never);
    }

    // ===== Chunk 3: activate mode + trash service integration =====

    [Fact]
    public async Task Execute_Activate_UseTrash_InvokesTrashServiceAndReportsBytes()
    {
        Config.OrphanedSubtitleTaskMode = TaskMode.Activate;
        Config.UseTrash = true;

        const string lib = "/media/movies";
        const string dir = "/media/movies/M";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, dir);
        SetupFilesInDir(lib);
        SetupFilesInDir(dir, "MovieA.mkv", "Orphan.en.srt");

        MockTrashService
            .Setup(t => t.MoveFileToTrash(
                It.Is<string>(s => s.EndsWith("Orphan.en.srt", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<ILogger>(),
                It.IsAny<DateTime?>()))
            .Returns(1234L);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        MockTrashService.Verify(
            t => t.MoveFileToTrash(
                It.Is<string>(s => s.EndsWith("Orphan.en.srt", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<ILogger>(),
                It.IsAny<DateTime?>()),
            Times.Once);
        VerifyLogContains(_loggerMock, "Moving orphaned subtitle to trash", LogLevel.Information);
        VerifyLogContains(_loggerMock, "Deleted 1 files, freed 1234 bytes", LogLevel.Information);
    }

    [Fact]
    public async Task Execute_Activate_UseTrash_MoveReturnsZero_SubtitleIsNotCounted()
    {
        // MoveFileToTrash returning 0 (cross-device fallback, permission denied, ...)
        // must not be counted as a deletion.
        Config.OrphanedSubtitleTaskMode = TaskMode.Activate;
        Config.UseTrash = true;

        const string lib = "/media/movies";
        const string dir = "/media/movies/M";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, dir);
        SetupFilesInDir(lib);
        SetupFilesInDir(dir, "MovieA.mkv", "Orphan.en.srt");

        MockTrashService
            .Setup(t => t.MoveFileToTrash(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ILogger>(), It.IsAny<DateTime?>()))
            .Returns(0L);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Verify the move was actually attempted for the orphan subtitle - otherwise a
        // "Deleted 0 files" summary could just mean the task never processed the file at all.
        MockTrashService.Verify(
            t => t.MoveFileToTrash(
                It.Is<string>(s => s.EndsWith("Orphan.en.srt", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<ILogger>(),
                It.IsAny<DateTime?>()),
            Times.Once);
        VerifyLogContains(_loggerMock, "Deleted 0 files, freed 0 bytes", LogLevel.Information);
    }

    // ===== Chunk 4: age gate & IOException resilience =====

    [Fact]
    public async Task Execute_OrphanTooNew_SkipsDelete_ProtectsRadarrRace()
    {
        // Age gate: guards against the Radarr/Sonarr download race where the .srt lands
        // slightly before its .mkv sibling.
        MockConfigHelper.Setup(c => c.IsFileOldEnoughForDeletion(It.IsAny<string>())).Returns(false);
        Config.OrphanMinAgeDays = 7;

        const string lib = "/media/movies";
        const string dir = "/media/movies/Newly-Added";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, dir);
        SetupFilesInDir(lib);
        SetupFilesInDir(dir, "MovieA.mkv", "Orphan.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains(_loggerMock, "Would delete orphaned subtitle", LogLevel.Information);
        VerifyLogNeverContains(_loggerMock, "Deleting orphaned subtitle", LogLevel.Information);
        VerifyLogNeverContains(_loggerMock, "Moving orphaned subtitle", LogLevel.Information);
    }

    [Fact]
    public async Task Execute_GetDirectoriesThrowsIOException_LogsWarningAndContinues()
    {
        // Unreadable subdirectory tree → warn, keep processing the root.
        const string lib = "/media/movies";
        _fileSystemMock.Setup(f => f.GetDirectories(lib))
            .Throws(new IOException("Broken NAS mount"));
        SetupLibrary(lib);
        SetupFilesInDir(lib, "MovieA.mkv", "Orphan.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "Could not enumerate subdirectories", LogLevel.Warning);
        // The root directory should still be processed → orphan detected.
        VerifyLogContains(_loggerMock, "Would delete orphaned subtitle", LogLevel.Information);
    }

    [Fact]
    public async Task Execute_GetFilesThrowsUnauthorized_LogsWarningAndSkipsDir()
    {
        // Single unreadable subdir → warn + skip that dir, keep others.
        const string lib = "/media/movies";
        const string blockedDir = "/media/movies/Locked";
        const string okDir = "/media/movies/Open";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, blockedDir, okDir);
        SetupFilesInDir(lib);
        _fileSystemMock.Setup(f => f.GetFiles(blockedDir))
            .Throws(new UnauthorizedAccessException("Access denied"));
        SetupFilesInDir(okDir, "MovieA.mkv", "Orphan.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "Could not list files in", LogLevel.Warning);
        VerifyLogContains(_loggerMock, "Would delete orphaned subtitle", LogLevel.Information);
    }

    // ===== Chunk 5: cancellation & progress =====

    [Fact]
    public async Task Execute_CancellationRequested_ThrowsAndStopsBetweenLocations()
    {
        // The base class checks the token BEFORE each ProcessLocation call.
        // A pre-cancelled token should therefore prevent any GetDirectories on the second library.
        const string lib1 = "/media/movies1";
        const string lib2 = "/media/movies2";

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([
                new VirtualFolderInfo { Locations = [lib1] },
                new VirtualFolderInfo { Locations = [lib2] }
            ]);
        SetupRecursiveDirs(lib1);
        SetupFilesInDir(lib1);
        SetupRecursiveDirs(lib2);
        SetupFilesInDir(lib2);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _task.ExecuteAsync(new Progress<double>(), cts.Token));

        _fileSystemMock.Verify(f => f.GetDirectories(lib2), Times.Never);
    }

    [Fact]
    public async Task Execute_ProgressIsReportedOncePerLibrary()
    {
        // The task must report progress at 50% and 100% for two libraries.
        const string lib1 = "/media/movies1";
        const string lib2 = "/media/movies2";

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([
                new VirtualFolderInfo { Locations = [lib1] },
                new VirtualFolderInfo { Locations = [lib2] }
            ]);
        SetupRecursiveDirs(lib1);
        SetupFilesInDir(lib1);
        SetupRecursiveDirs(lib2);
        SetupFilesInDir(lib2);

        var reported = new List<double>();
        var progress = new SynchronousProgress<double>(reported.Add);

        await _task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(2, reported.Count);
        Assert.Equal(50, reported[0]);
        Assert.Equal(100, reported[1]);
    }

    [Fact]
    public async Task Execute_NoLibraryFolders_ShortCircuits()
    {
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        var reported = new List<double>();
        var progress = new SynchronousProgress<double>(reported.Add);

        await _task.ExecuteAsync(progress, CancellationToken.None);

        VerifyLogContains(_loggerMock, "No library folders configured", LogLevel.Information);
        // Fast-path reports 100% exactly once.
        Assert.Single(reported);
        Assert.Equal(100, reported[0]);
    }

    [Fact]
    public async Task Execute_GetTrashPathThrowsUnexpected_LogsErrorAndDoesNotCrash()
    {
        // The trash-path setup is hoisted outside the per-directory loop. A non-IO failure there
        // (e.g. a bad configuration surfacing as InvalidOperationException) must be caught by the
        // outer catch-all so a single library does not abort the whole task.
        const string lib = "/media/movies";
        const string dir = "/media/movies/M";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, dir);
        SetupFilesInDir(lib);
        SetupFilesInDir(dir, "MovieA.mkv", "Orphan.en.srt");

        MockConfigHelper.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Throws(new InvalidOperationException("bad trash config"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "Error scanning directory", LogLevel.Error);
    }

    [Fact]
    public async Task Execute_DeeplyNestedDirs_AllLevelsScanned()
    {
        // The recursive descent uses an explicit stack. A grandchild directory is only reachable
        // when GetDirectories(child) is walked, so an orphan there proves the deep push works.
        const string lib = "/media/movies";
        const string child = "/media/movies/Show";
        const string grandchild = "/media/movies/Show/Season 01";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, child);
        _fileSystemMock.Setup(f => f.GetDirectories(child)).Returns(new[]
        {
            new FileSystemMetadata { FullName = grandchild, Name = "Season 01", IsDirectory = true }
        });
        _fileSystemMock.Setup(f => f.GetDirectories(grandchild)).Returns([]);
        SetupFilesInDir(lib);
        SetupFilesInDir(child);
        SetupFilesInDir(grandchild, "S01E01.mkv", "S01E02.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "Would delete orphaned subtitle", LogLevel.Information);
    }

    [Fact]
    public async Task Execute_NestedGetDirectoriesThrows_WarnsAndContinuesScan()
    {
        // A nested subdirectory that cannot be enumerated must warn (in-loop catch) but not stop
        // the scan: a readable sibling's orphan should still be detected.
        const string lib = "/media/movies";
        const string blockedDir = "/media/movies/Locked";
        const string okDir = "/media/movies/Open";
        SetupLibrary(lib);
        SetupRecursiveDirs(lib, blockedDir, okDir);
        _fileSystemMock.Setup(f => f.GetDirectories(blockedDir))
            .Throws(new UnauthorizedAccessException("Access denied"));
        _fileSystemMock.Setup(f => f.GetDirectories(okDir)).Returns([]);
        SetupFilesInDir(lib);
        SetupFilesInDir(blockedDir);
        SetupFilesInDir(okDir, "MovieA.mkv", "Orphan.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "Could not enumerate subdirectories", LogLevel.Warning);
        VerifyLogContains(_loggerMock, "Would delete orphaned subtitle", LogLevel.Information);
    }

    /// <summary>
    ///     When the seed-phase <c>GetDirectories(libraryPath)</c> call inside <c>TryGetSubdirectories</c>
    ///     throws, the method must log a warning and return an empty list.  The task still processes
    ///     <c>libraryPath</c> itself (it is prepended directly) and completes without crashing.
    /// </summary>
    [Fact]
    public async Task Execute_TryGetSubdirectoriesSeedThrows_LogsWarningAndContinues()
    {
        const string lib = "/media/movies";
        Config.OrphanedSubtitleTaskMode = TaskMode.Activate;
        SetupLibrary(lib);
        _fileSystemMock.Setup(f => f.GetDirectories(lib))
            .Throws(new IOException("Permission denied"));
        SetupFilesInDir(lib); // no files → nothing to clean

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "Could not enumerate subdirectories of", LogLevel.Warning);
        // Task must not crash; no deletions occurred.
        MockTrackingService.Verify(
            t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
            Times.Never);
    }

    /// <summary>
    ///     When <c>GetDirectories</c> throws for a nested directory during the loop phase of
    ///     <c>TryGetSubdirectories</c>, the method must log a warning and continue processing
    ///     the remaining directories (not abort the whole scan). Proven by a readable sibling
    ///     whose orphaned subtitle is still detected after the failing directory.
    /// </summary>
    [Fact]
    public async Task Execute_TryGetSubdirectoriesLoopThrows_LogsWarningAndContinues()
    {
        const string lib = "/media/movies";
        const string subDir = "/media/movies/ShowA";
        const string okDir = "/media/movies/ShowB";
        SetupLibrary(lib);
        // Seed order matters: TryGetSubdirectories uses a LIFO stack, so the FIRST-returned dir is
        // processed LAST. Return okDir first (→ bottom of stack) so the failing subDir is popped and
        // throws BEFORE okDir is reached — proving the loop continues past the failure, not around it.
        _fileSystemMock.Setup(f => f.GetDirectories(lib)).Returns([
            new FileSystemMetadata { FullName = okDir, Name = "ShowB", IsDirectory = true },
            new FileSystemMetadata { FullName = subDir, Name = "ShowA", IsDirectory = true }
        ]);
        // Loop-phase: GetDirectories(subDir) throws → logged, loop continues to okDir.
        _fileSystemMock.Setup(f => f.GetDirectories(subDir))
            .Throws(new IOException("Access denied"));
        _fileSystemMock.Setup(f => f.GetDirectories(okDir)).Returns([]);
        SetupFilesInDir(lib);   // no files in root
        SetupFilesInDir(subDir); // failing dir yields no files
        // Sibling has an orphaned subtitle (no matching video base name).
        SetupFilesInDir(okDir, "MovieA.mkv", "Orphan.en.srt");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "Could not enumerate subdirectories of", LogLevel.Warning);
        // Proof the loop continued past the failing directory: the sibling's orphan was detected.
        VerifyLogContains(_loggerMock, "Would delete orphaned subtitle", LogLevel.Information);
    }

    // ANCHOR: TESTS_END - do not remove, used by replace_in_file to append new tests.

    /// <summary>Populate a library with a single top-level path.</summary>
    private void SetupLibrary(string libraryPath)
    {
        var vf = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([vf]);
    }

    /// <summary>
    ///     Set up the recursive <c>GetDirectories(libraryPath, true)</c> call the task performs.
    /// </summary>
    private void SetupRecursiveDirs(string libraryPath, params string[] fullPaths)
    {
        var dirs = fullPaths.Select(p => new FileSystemMetadata
        {
            FullName = p,
            Name = Path.GetFileName(p),
            IsDirectory = true
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns(dirs);
    }

    /// <summary>Populate a directory with file leaf names.</summary>
    private void SetupFilesInDir(string dirPath, params string[] fileNames)
    {
        var files = fileNames.Select(name => new FileSystemMetadata
        {
            FullName = Path.Join(dirPath, name),
            Name = name,
            IsDirectory = false,
            Length = 100
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetFiles(dirPath)).Returns(files);
    }
}