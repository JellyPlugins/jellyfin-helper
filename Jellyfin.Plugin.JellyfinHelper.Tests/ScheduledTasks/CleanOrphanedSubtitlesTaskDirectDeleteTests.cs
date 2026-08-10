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
///     Tests for <see cref="CleanOrphanedSubtitlesTask"/>'s direct on-disk delete path
///     (Activate mode, <c>UseTrash = false</c>), exercising the real <c>File.Delete</c> branch
///     against actual temp files rather than mocks.
/// </summary>
public sealed class CleanOrphanedSubtitlesTaskDirectDeleteTests : CleanupTaskTestBase, IDisposable
{
    private readonly string _root;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<ILogger<CleanOrphanedSubtitlesTask>> _loggerMock;
    private readonly CleanOrphanedSubtitlesTask _task;

    public CleanOrphanedSubtitlesTaskDirectDeleteTests()
    {
        _root = Path.Join(Path.GetTempPath(), "JfhOrphanSubs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        Config.OrphanedSubtitleTaskMode = TaskMode.Activate;
        Config.UseTrash = false;

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

    [Fact]
    public async Task Execute_Activate_NoTrash_DeletesOrphanFromDiskAndCountsBytes()
    {
        // Orphan .srt has no matching video base name -> the direct File.Delete branch runs,
        // must remove the real file and report its byte size as freed.
        var dir = Path.Join(_root, "Movie (2020)");
        Directory.CreateDirectory(dir);
        var videoPath = Path.Join(dir, "MovieA.mkv");
        var orphanPath = Path.Join(dir, "Orphan.en.srt");
        File.WriteAllText(videoPath, "video");
        File.WriteAllText(orphanPath, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        var orphanSize = new FileInfo(orphanPath).Length;

        SetupLibrary(_root);
        SetupRecursiveDirs(_root, dir);
        SetupFilesInDir(_root);
        SetupFilesOnDisk(dir, (videoPath, "MovieA.mkv"), (orphanPath, "Orphan.en.srt"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "Deleting orphaned subtitle", LogLevel.Information);
        Assert.False(File.Exists(orphanPath));
        VerifyLogContains(_loggerMock, $"Deleted 1 files, freed {orphanSize} bytes", LogLevel.Information);
    }

    [Fact]
    public async Task Execute_Activate_NoTrash_DeleteFails_LogsErrorAndSkips()
    {
        // The orphan "path" is actually a directory on disk, so File.Delete throws
        // (UnauthorizedAccessException) and is caught by the IO/Unauthorized filter:
        // the run must log the failure and count zero successful deletions.
        var dir = Path.Join(_root, "Movie (2021)");
        Directory.CreateDirectory(dir);
        var videoPath = Path.Join(dir, "MovieA.mkv");
        var orphanPath = Path.Join(dir, "Orphan.en.srt");
        File.WriteAllText(videoPath, "video");
        Directory.CreateDirectory(orphanPath);

        SetupLibrary(_root);
        SetupRecursiveDirs(_root, dir);
        SetupFilesInDir(_root);
        SetupFilesOnDisk(dir, (videoPath, "MovieA.mkv"), (orphanPath, "Orphan.en.srt"));

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains(_loggerMock, "Failed to delete", LogLevel.Error);
        VerifyLogContains(_loggerMock, "Deleted 0 files, freed 0 bytes", LogLevel.Information);
    }

    public override void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp tree.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp tree.
        }

        base.Dispose();
    }

    private void SetupLibrary(string libraryPath)
    {
        var vf = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([vf]);
    }

    private void SetupRecursiveDirs(string libraryPath, params string[] fullPaths)
    {
        var dirs = fullPaths.Select(p => new FileSystemMetadata
        {
            FullName = p,
            Name = Path.GetFileName(p),
            IsDirectory = true
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns(dirs);
        foreach (var p in fullPaths)
        {
            _fileSystemMock.Setup(f => f.GetDirectories(p)).Returns([]);
        }
    }

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

    /// <summary>
    ///     Wires GetFiles to return metadata whose FullName points at real on-disk paths,
    ///     with Length reflecting the actual file size so the freed-bytes accounting is genuine.
    /// </summary>
    private void SetupFilesOnDisk(string dirPath, params (string FullName, string Name)[] entries)
    {
        var files = entries.Select(e => new FileSystemMetadata
        {
            FullName = e.FullName,
            Name = e.Name,
            IsDirectory = false,
            Length = File.Exists(e.FullName) ? new FileInfo(e.FullName).Length : 0
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetFiles(dirPath)).Returns(files);
    }
}
