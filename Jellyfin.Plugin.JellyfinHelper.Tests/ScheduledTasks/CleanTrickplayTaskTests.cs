using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
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
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILogger<CleanTrickplayTask>> _loggerMock;
    private readonly CleanTrickplayTask _task;

    public CleanTrickplayTaskTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        _loggerMock = TestMockFactory.CreateLogger<CleanTrickplayTask>();
        _task = new CleanTrickplayTask(_libraryManagerMock.Object, _fileSystemMock.Object, _loggerMock.Object);

        // Default: DryRun OFF for most existing tests (non-dry-run behavior)
        Config.TrickplayTaskMode = TaskMode.Activate;
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
        Config.OrphanedSubtitleTaskMode = TaskMode.Activate;
        CleanupConfigHelper.ConfigOverride = Config;
    }

    private void VerifyLogContains(string messagePart, LogLevel level)
        => VerifyLogContains(_loggerMock, messagePart, level);

    private void VerifyLogNeverContains(string messagePart, LogLevel level)
        => VerifyLogNeverContains(_loggerMock, messagePart, level);

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns(Array.Empty<FileSystemMetadata>());

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns([mediaFile]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_LogsWouldDelete()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration { TrickplayTaskMode = TaskMode.DryRun };

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned trickplay folder", LogLevel.Information);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_NoLibraryFolders_CompletesWithoutError()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration { TrickplayTaskMode = TaskMode.DryRun };

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Would have deleted 0 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_NoTrickplayFolders_DeletesNothing()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration { TrickplayTaskMode = TaskMode.DryRun };

        var libraryPath = TestPath("media");
        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var regularDir = new FileSystemMetadata
        {
            FullName = TestPath("media", "Subfolder"),
            Name = "Subfolder",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([regularDir]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Would have deleted 0 folders", LogLevel.Information);
        VerifyLogNeverContains("[Dry Run] Would delete orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_DirectoryScanError_LogsErrorAndContinues()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration { TrickplayTaskMode = TaskMode.DryRun };

        var libraryPath1 = TestPath("media1");
        var libraryPath2 = TestPath("media2");
        var trickplayFullName = TestPath("media2", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1, true)).Throws(new IOException("Access denied"));

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath2, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Error scanning directory", LogLevel.Error);
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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([nestedDir]);

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns(Array.Empty<FileSystemMetadata>());

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns([mediaFile]);

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns([textFile]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MultipleOrphanedFolders_DeletesAllAndReportsCount()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration { TrickplayTaskMode = TaskMode.DryRun };

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir1, trickplayDir2]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Would have deleted 2 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NoLibraryFolders_CompletesWithoutError()
    {
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleted 0 folders", LogLevel.Information);
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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([regularDir]);

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns(Array.Empty<FileSystemMetadata>());

        // Cancel immediately after first folder
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await _task.ExecuteAsync(new Progress<double>(), cts.Token);

        // Second library folder should never be scanned
        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath2, true), Times.Never);
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
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1, true)).Throws(new IOException("Access denied"));

        // Second folder is fine
        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayFullName,
            Name = "Movie.trickplay",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath2, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Should log error for first folder
        VerifyLogContains("Error scanning directory", LogLevel.Error);
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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1, true)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath2, true)).Returns([]);

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(v => reportedValues.Add(v));

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(parentPath, false)).Returns([mediaFile]);

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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // GetDirectories should only be called once due to Distinct()
        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath, true), Times.Once);
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

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(expectedParentPath, false)).Returns([mediaFile]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Should check files in the subdirectory (parent of the .trickplay folder), not the library root
        _fileSystemMock.Verify(f => f.GetFiles(expectedParentPath, false), Times.Once);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }
}
