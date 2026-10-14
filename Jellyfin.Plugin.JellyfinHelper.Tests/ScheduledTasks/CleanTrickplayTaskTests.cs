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

public class CleanTrickplayTaskTests : CleanupTaskTestBase
{
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<ILogger<CleanTrickplayTask>> _loggerMock;
    private readonly CleanTrickplayTask _task;

    public CleanTrickplayTaskTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        _loggerMock = TestMockFactory.CreateLogger<CleanTrickplayTask>();
        _task = new CleanTrickplayTask(
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(),
            _loggerMock.Object,
            MockConfigHelper.Object,
            MockTrackingService.Object,
            MockTrashService.Object);

        // Default: DryRun OFF for most existing tests (non-dry-run behavior)
        Config.TrickplayTaskMode = TaskMode.Activate;
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
        Config.OrphanedSubtitleTaskMode = TaskMode.Activate;
    }

    private void VerifyLogContains(string messagePart, LogLevel level)
    {
        VerifyLogContains(_loggerMock, messagePart, level);
    }

    private void VerifyLogNeverContains(string messagePart, LogLevel level)
    {
        VerifyLogNeverContains(_loggerMock, messagePart, level);
    }

    [Fact]
    public async Task ExecuteInternalAsync_OrphanedFolder_DeletesFolder()
    {
        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MediaExists_DoesNotDelete()
    {
        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie.trickplay");
        var mediaFullName = TestPath("media", "Movie.mkv");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        var mediaFile = new FileSystemMetadata
        {
            FullName = mediaFullName,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns([mediaFile]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_LogsWouldDelete()
    {
        Config.TrickplayTaskMode = TaskMode.DryRun;

        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned trickplay folder", LogLevel.Information);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_NoLibraryFolders_CompletesWithoutError()
    {
        Config.TrickplayTaskMode = TaskMode.DryRun;

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("No library folders configured", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_NoTrickplayFolders_DeletesNothing()
    {
        Config.TrickplayTaskMode = TaskMode.DryRun;

        var libraryPath = TestPath("media");
        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var regularDir = new FileSystemMetadata
        {
            FullName = TestPath("media", "Subfolder"),
            Name = "Subfolder",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([regularDir]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Would have deleted 0 folders", LogLevel.Information);
        VerifyLogNeverContains("[Dry Run] Would delete orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_DirectoryScanError_LogsErrorAndContinues()
    {
        Config.TrickplayTaskMode = TaskMode.DryRun;

        var libraryPath1 = TestPath("media1");
        var libraryPath2 = TestPath("media2");
        var trickplayFullName = TestPath("media2", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1)).Throws(new IOException("Access denied"));

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath2)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Could not enumerate subdirectories of", LogLevel.Warning);
        VerifyLogContains("[Dry Run] Would delete orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NestedTrickplayFolder_IsSkipped()
    {
        var libraryPath = TestPath("media");

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        // A .trickplay folder nested inside another .trickplay folder
        var nestedDir = new FileSystemMetadata
        {
            FullName = TestPath("media", "Movie.trickplay", "sub.trickplay"),
            Name = "sub.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([nestedDir]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CaseInsensitiveTrickplayExtension_IsDetected()
    {
        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie.TRICKPLAY");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.TRICKPLAY",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Theory]
    [InlineData(".vob")]
    [InlineData(".wtv")]
    [InlineData(".dvr-ms")]
    [InlineData(".f4v")]
    [InlineData(".iso")]
    [InlineData(".mk3d")]
    [InlineData(".m2v")]
    [InlineData(".ogm")]
    [InlineData(".MKV")]
    [InlineData(".Mp4")]
    public async Task ExecuteInternalAsync_VariousMediaExtensions_MediaIsRecognized(string extension)
    {
        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        var mediaFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "Movie" + extension),
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns([mediaFile]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NonMediaExtension_IsNotRecognizedAsMedia()
    {
        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        // A .txt file should NOT count as a media file
        var textFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "Movie.txt"),
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns([textFile]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MultipleOrphanedFolders_DeletesAllAndReportsCount()
    {
        Config.TrickplayTaskMode = TaskMode.DryRun;

        var libraryPath = TestPath("media");
        var trickplayFullName1 = TestPath("media", "Movie1.trickplay");
        var trickplayFullName2 = TestPath("media", "Movie2.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName1)!;

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir1 = new FileSystemMetadata
        {
            FullName = trickplayFullName1,
            Name = "Movie1.trickplay",
            IsDirectory = true
        };

        var trickplayDir2 = new FileSystemMetadata
        {
            FullName = trickplayFullName2,
            Name = "Movie2.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir1, trickplayDir2]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Would have deleted 2 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NoLibraryFolders_CompletesWithoutError()
    {
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("No library folders configured", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NoTrickplayFolders_DeletesNothing()
    {
        var libraryPath = TestPath("media");

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var regularDir = new FileSystemMetadata
        {
            FullName = TestPath("media", "Subfolder"),
            Name = "Subfolder",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([regularDir]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleted 0 folders", LogLevel.Information);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CancellationRequested_StopsProcessing()
    {
        var libraryPath1 = TestPath("media1");
        var libraryPath2 = TestPath("media2");
        var trickplayFullName = TestPath("media1", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns(Array.Empty<FileSystemMetadata>());

        // Cancel immediately after first folder
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _task.ExecuteAsync(new Progress<double>(), cts.Token));

        // Second library folder should never be scanned
        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath2), Times.Never);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DirectoryScanError_LogsErrorAndContinues()
    {
        var libraryPath1 = TestPath("media1");
        var libraryPath2 = TestPath("media2");
        var trickplayFullName = TestPath("media2", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        // First folder throws an exception
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1)).Throws(new IOException("Access denied"));

        // Second folder is fine
        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath2)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Should log warning for first folder (IOException caught gracefully)
        VerifyLogContains("Could not enumerate subdirectories of", LogLevel.Warning);
        // Should still process second folder
        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ProgressIsReported()
    {
        var libraryPath1 = TestPath("media1");
        var libraryPath2 = TestPath("media2");

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath2)).Returns([]);

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(reportedValues.Add);

        await _task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(2, reportedValues.Count);
        Assert.Equal(50, reportedValues[0]);
        Assert.Equal(100, reportedValues[1]);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MediaNameMismatch_DeletesTrickplayFolder()
    {
        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie1.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie1.trickplay",
            IsDirectory = true
        };

        // Media file has a different name than the trickplay folder
        var mediaFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "Movie2.mkv"),
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns([mediaFile]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DuplicateLibraryPaths_ScansOnlyOnce()
    {
        var libraryPath = TestPath("media");

        // Same path appears in two virtual folders
        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // GetDirectories should only be called once due to Distinct()
        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath), Times.Once);
    }

    [Fact]
    public async Task ExecuteInternalAsync_SubdirectoryTrickplayFolder_ChecksCorrectParent()
    {
        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Shows", "Season1", "Episode01.trickplay");
        var expectedParentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Episode01.trickplay",
            IsDirectory = true
        };

        var mediaFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "Shows", "Season1", "Episode01.mkv"),
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(expectedParentPath)).Returns([mediaFile]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Should check files in the subdirectory (parent of the .trickplay folder), not the library root
        _fileSystemMock.Verify(f => f.GetFiles(expectedParentPath), Times.Once);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DirectoryScanError_CaughtAtInnerTryCatch_DoesNotPropagateToOuterHandler()
    {
        // Verifies that IOException during GetDirectories is caught by the inner try/catch
        // (materialized .ToList()) and triggers the warning/return path, not the broad outer error handler.
        var libraryPath = TestPath("media");

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Throws(new IOException("Access denied"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Inner catch logs a Warning and returns (0, 0) - outer catch would log an Error.
        VerifyLogContains("Could not enumerate subdirectories of", LogLevel.Warning);
        VerifyLogNeverContains("Error scanning directory", LogLevel.Error);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ChildDirectoryEnumerationError_IsIsolatedAndDeeperTreeStillScanned()
    {
        // A failure enumerating one subdirectory must not abort the whole walk: sibling
        // branches still have to be scanned so reachable orphans are not missed.
        var libraryPath = TestPath("media");
        var badBranch = TestPath("media", "BadBranch");
        var goodBranch = TestPath("media", "GoodBranch");
        var orphanFullName = TestPath("media", "GoodBranch", "Movie.trickplay");
        var orphanParent = Path.GetDirectoryName(orphanFullName)!;

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var badDir = new FileSystemMetadata { FullName = badBranch, Name = "BadBranch", IsDirectory = true };
        var goodDir = new FileSystemMetadata { FullName = goodBranch, Name = "GoodBranch", IsDirectory = true };
        var orphanDir = new FileSystemMetadata
        {
            FullName = orphanFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([badDir, goodDir]);
        // Enumerating children of one branch throws; the walk must log and keep going.
        _fileSystemMock.Setup(f => f.GetDirectories(badBranch)).Throws(new IOException("Access denied"));
        _fileSystemMock.Setup(f => f.GetDirectories(goodBranch)).Returns([orphanDir]);
        _fileSystemMock.Setup(f => f.GetDirectories(orphanFullName)).Returns([]);
        _fileSystemMock.Setup(f => f.GetFiles(orphanParent)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Could not enumerate subdirectories of", LogLevel.Warning);
        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_OrphanNestedTwoLevelsDeep_IsDiscoveredAndDeleted()
    {
        // The scan must recurse past the top level: an orphan two levels below the library
        // root should still be found and deleted.
        var libraryPath = TestPath("media");
        var level1 = TestPath("media", "Shows");
        var orphanFullName = TestPath("media", "Shows", "Episode.trickplay");
        var orphanParent = Path.GetDirectoryName(orphanFullName)!;

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var level1Dir = new FileSystemMetadata { FullName = level1, Name = "Shows", IsDirectory = true };
        var orphanDir = new FileSystemMetadata
        {
            FullName = orphanFullName,
            Name = "Episode.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([level1Dir]);
        _fileSystemMock.Setup(f => f.GetDirectories(level1)).Returns([orphanDir]);
        _fileSystemMock.Setup(f => f.GetDirectories(orphanFullName)).Returns([]);
        _fileSystemMock.Setup(f => f.GetFiles(orphanParent)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
        // The intermediate directory has to be descended into for the grandchild to be reached.
        _fileSystemMock.Verify(f => f.GetDirectories(level1), Times.Once);
    }

    [Fact]
    public async Task ExecuteInternalAsync_FileListingError_LogsWarningAndSkipsFolder()
    {
        // If the parent's files cannot be listed we cannot know whether media exists,
        // so the orphan must be skipped (not deleted) with a warning.
        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Throws(new IOException("Access denied"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Could not list files in", LogLevel.Warning);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_OrphanTooNew_IsSkippedWithDebugLog()
    {
        // The OrphanMinAgeDays gate must keep recently created orphans: a too-new folder
        // is skipped with a debug log and never deleted.
        MockConfigHelper.Setup(x => x.IsOldEnoughForDeletion(It.IsAny<string>())).Returns(false);

        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Skipping too-new orphan", LogLevel.Debug);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
        MockTrashService.Verify(
            t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteInternalAsync_TrashMoveReturnsPositiveSize_CountsAndBytesAccumulated()
    {
        // A successful trash move (size > 0) must accumulate both the deleted count and
        // the freed bytes reported in the finished summary.
        Config.UseTrash = true;
        MockTrashService
            .Setup(t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()))
            .Returns(4096);

        var libraryPath = TestPath("media");
        var trickplayFullName = TestPath("media", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("freed 4096 bytes", LogLevel.Information);
        VerifyLogContains("Deleted 1 folders", LogLevel.Information);
        MockTrashService.Verify(
            t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteInternalAsync_PermanentDeleteSucceeds_RemovesDirectoryAndCounts()
    {
        // The permanent-delete path uses real System.IO, so exercise it against a genuine
        // temp directory to prove the folder is actually removed and counted.
        Config.UseTrash = false;

        var libraryPath = Path.Combine(Path.GetTempPath(), "jf-trickplay-" + Guid.NewGuid().ToString("N"));
        var trickplayDirPath = Path.Combine(libraryPath, "Movie.trickplay");
        Directory.CreateDirectory(trickplayDirPath);

        try
        {
            var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
            _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

            var trickplayDir = new FileSystemMetadata
            {
                FullName = trickplayDirPath,
                Name = "Movie.trickplay",
                IsDirectory = true
            };

            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([trickplayDir]);
            _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns(Array.Empty<FileSystemMetadata>());

            await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.False(Directory.Exists(trickplayDirPath));
            VerifyLogContains("Deleted 1 folders", LogLevel.Information);
            VerifyLogNeverContains("Failed to delete directory", LogLevel.Error);
        }
        finally
        {
            if (Directory.Exists(libraryPath))
            {
                Directory.Delete(libraryPath, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteInternalAsync_UnexpectedErrorDuringScan_LoggedByOuterHandlerAndSwallowed()
    {
        // A non-IO error inside ProcessLocation must be caught by the broad outer handler,
        // logged, and swallowed so the overall task completes and still reports its summary.
        var libraryPath = TestPath("media");
        MockConfigHelper.Setup(x => x.GetTrashPath(libraryPath)).Throws(new InvalidOperationException("boom"));

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Error scanning directory", LogLevel.Error);
        VerifyLogContains("Deleted 0 folders", LogLevel.Information);
    }
}