using Jellyfin.Plugin.FiletreeCleaner.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FiletreeCleaner.Tests;

public class CleanTrickplayTaskTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILogger<CleanTrickplayTask>> _loggerMock;
    private readonly CleanTrickplayTask _task;

    public CleanTrickplayTaskTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _loggerMock = new Mock<ILogger<CleanTrickplayTask>>();
        _task = new CleanTrickplayTask(_libraryManagerMock.Object, _fileSystemMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteInternalAsync_OrphanedFolder_DeletesFolder()
    {
        const string libraryPath = "C:\\Media";
        const string trickplayPath = "C:\\Media\\Movie.trickplay";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayPath,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MediaExists_DoesNotDelete()
    {
        const string libraryPath = "C:\\Media";
        const string trickplayPath = "C:\\Media\\Movie.trickplay";
        const string mediaPath = "C:\\Media\\Movie.mkv";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayPath,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        var mediaFile = new FileSystemMetadata
        {
            FullName = mediaPath,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([mediaFile]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_LogsWouldDelete()
    {
        const string libraryPath = "C:\\Media";
        const string trickplayPath = "C:\\Media\\Movie.trickplay";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayPath,
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([]);

        await _task.ExecuteInternalAsync(true, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned trickplay folder", LogLevel.Information);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NestedTrickplayFolder_IsSkipped()
    {
        const string libraryPath = "C:\\Media";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        // A .trickplay folder nested inside another .trickplay folder
        var nestedDir = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie.trickplay\\sub.trickplay",
            Name = "sub.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([nestedDir]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CaseInsensitiveTrickplayExtension_IsDetected()
    {
        const string libraryPath = "C:\\Media";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        // Uppercase .TRICKPLAY extension
        var trickplayDir = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie.TRICKPLAY",
            Name = "Movie.TRICKPLAY",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

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
        const string libraryPath = "C:\\Media";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie.trickplay",
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        var mediaFile = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie" + extension,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([mediaFile]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NonMediaExtension_IsNotRecognizedAsMedia()
    {
        const string libraryPath = "C:\\Media";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie.trickplay",
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        // A .txt file should NOT count as a media file
        var textFile = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie.txt",
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([textFile]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MultipleOrphanedFolders_DeletesAllAndReportsCount()
    {
        const string libraryPath = "C:\\Media";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir1 = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie1.trickplay",
            Name = "Movie1.trickplay",
            IsDirectory = true
        };

        var trickplayDir2 = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie2.trickplay",
            Name = "Movie2.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir1, trickplayDir2]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(Array.Empty<FileSystemMetadata>());

        // Use dry run to avoid Directory.Delete on non-existent paths
        await _task.ExecuteInternalAsync(true, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Would have deleted 2 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NoLibraryFolders_CompletesWithoutError()
    {
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleted 0 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NoTrickplayFolders_DeletesNothing()
    {
        const string libraryPath = "C:\\Media";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var regularDir = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Subfolder",
            Name = "Subfolder",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([regularDir]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleted 0 folders", LogLevel.Information);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CancellationRequested_StopsProcessing()
    {
        const string libraryPath1 = "C:\\Media1";
        const string libraryPath2 = "C:\\Media2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = "C:\\Media1\\Movie.trickplay",
            Name = "Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath1, false)).Returns(Array.Empty<FileSystemMetadata>());

        // Cancel immediately after first folder
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await _task.ExecuteInternalAsync(false, new Progress<double>(), cts.Token);

        // Second library folder should never be scanned
        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath2, true), Times.Never);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DirectoryScanError_LogsErrorAndContinues()
    {
        const string libraryPath1 = "C:\\Media1";
        const string libraryPath2 = "C:\\Media2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        // First folder throws an exception
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1, true)).Throws(new IOException("Access denied"));

        // Second folder is fine
        var trickplayDir = new FileSystemMetadata
        {
            FullName = "C:\\Media2\\Movie.trickplay",
            Name = "Movie.trickplay",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath2, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath2, false)).Returns(Array.Empty<FileSystemMetadata>());

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        // Should log error for first folder
        VerifyLogContains("Error scanning directory", LogLevel.Error);
        // Should still process second folder
        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ProgressIsReported()
    {
        const string libraryPath1 = "C:\\Media1";
        const string libraryPath2 = "C:\\Media2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1, true)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath2, true)).Returns([]);

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(v => reportedValues.Add(v));

        await _task.ExecuteInternalAsync(false, progress, CancellationToken.None);

        Assert.Equal(2, reportedValues.Count);
        Assert.Equal(50, reportedValues[0]);
        Assert.Equal(100, reportedValues[1]);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MediaNameMismatch_DeletesTrickplayFolder()
    {
        const string libraryPath = "C:\\Media";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie1.trickplay",
            Name = "Movie1.trickplay",
            IsDirectory = true
        };

        // Media file has a different name than the trickplay folder
        var mediaFile = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Movie2.mkv",
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([mediaFile]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DuplicateLibraryPaths_ScansOnlyOnce()
    {
        const string libraryPath = "C:\\Media";

        // Same path appears in two virtual folders
        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        // GetDirectories should only be called once due to Distinct()
        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath, true), Times.Once);
    }

    [Fact]
    public async Task ExecuteInternalAsync_SubdirectoryTrickplayFolder_ChecksCorrectParent()
    {
        const string libraryPath = "C:\\Media";
        const string subDir = "C:\\Media\\Shows\\Season1";

        var virtualFolder = new VirtualFolderInfo
        {
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Shows\\Season1\\Episode01.trickplay",
            Name = "Episode01.trickplay",
            IsDirectory = true
        };

        var mediaFile = new FileSystemMetadata
        {
            FullName = "C:\\Media\\Shows\\Season1\\Episode01.mkv",
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trickplayDir]);
        _fileSystemMock.Setup(f => f.GetFiles(subDir, false)).Returns([mediaFile]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        // Should check files in the subdirectory (parent of the .trickplay folder), not the library root
        _fileSystemMock.Verify(f => f.GetFiles(subDir, false), Times.Once);
        VerifyLogNeverContains("Deleting orphaned trickplay folder", LogLevel.Information);
    }

    /// <summary>
    /// A synchronous implementation of IProgress that invokes the callback immediately.
    /// Unlike Progress&lt;T&gt;, this does not post to a SynchronizationContext.
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }

    private void VerifyLogContains(string messagePart, LogLevel level)
    {
        _loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    private void VerifyLogNeverContains(string messagePart, LogLevel level)
    {
        _loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
}