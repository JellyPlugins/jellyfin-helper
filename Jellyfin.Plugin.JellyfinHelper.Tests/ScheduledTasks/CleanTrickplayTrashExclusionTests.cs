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
///     Tests that <see cref="CleanTrickplayTask"/> correctly excludes the trash folder
///     from its recursive directory scan. Without this exclusion, previously trashed
///     .trickplay folders would be re-detected as orphans on every scheduled run,
///     accumulating timestamp prefixes until the path exceeds PATH_MAX.
/// </summary>
public class CleanTrickplayTrashExclusionTests : CleanupTaskTestBase
{
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<ILogger<CleanTrickplayTask>> _loggerMock;
    private readonly CleanTrickplayTask _task;

    public CleanTrickplayTrashExclusionTests()
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

        Config.TrickplayTaskMode = TaskMode.Activate;
        Config.UseTrash = true;
    }

    [Fact]
    public async Task ExecuteAsync_TrickplayInsideTrash_IsSkipped()
    {
        var libraryPath = TestPath("media", "series");
        // Use the same trash folder name the mock returns (Path.Join(lib, ".trash"))
        var trashPath = Path.Join(libraryPath, ".trash");
        var trashedTrickplay = Path.Join(trashPath, "20260510-010001_Movie.trickplay");

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        // The recursive scan returns a .trickplay folder that is INSIDE the trash
        var trashedDir = new FileSystemMetadata
        {
            FullName = trashedTrickplay,
            Name = "20260510-010001_Movie.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trashedDir]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Must NOT attempt to delete or trash anything
        VerifyLogNeverContains(_loggerMock, "Deleting orphaned trickplay folder", LogLevel.Information);
        VerifyLogNeverContains(_loggerMock, "Moving orphaned trickplay folder to trash", LogLevel.Information);
        VerifyLogNeverContains(_loggerMock, "[Dry Run] Would delete orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_TrickplayOutsideTrash_IsProcessed()
    {
        var libraryPath = TestPath("media", "series");
        var trickplayFullName = TestPath("media", "series", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
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

        // Should process the orphaned folder (trash mode is on)
        VerifyLogContains(_loggerMock, "Moving orphaned trickplay folder to trash", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_DeeplyNestedTrickplayInsideTrash_IsSkipped()
    {
        var libraryPath = TestPath("media", "anime");
        // Use the same trash folder name the mock returns (Path.Join(lib, ".trash"))
        var trashPath = Path.Join(libraryPath, ".trash");
        // Simulate the cascading timestamp bug scenario
        var deeplyNested = Path.Join(
            trashPath,
            "20260510-010001_20260503-010001_Arifureta.trickplay");

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trashedDir = new FileSystemMetadata
        {
            FullName = deeplyNested,
            Name = "20260510-010001_20260503-010001_Arifureta.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trashedDir]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains(_loggerMock, "Moving orphaned trickplay folder to trash", LogLevel.Information);
        VerifyLogNeverContains(_loggerMock, "Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_CustomTrashPath_TrickplayInsideCustomTrash_IsSkipped()
    {
        var libraryPath = TestPath("media", "movies");
        // Simulate a user-configured custom trash folder name
        var customTrashName = ".jellyfin-helper-trash";
        MockConfigHelper
            .Setup(x => x.GetTrashPath(libraryPath))
            .Returns(Path.Join(libraryPath, customTrashName));

        var trashPath = Path.Join(libraryPath, customTrashName);
        var trashedTrickplay = Path.Join(trashPath, "20260510-010001_Film.trickplay");

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var trashedDir = new FileSystemMetadata
        {
            FullName = trashedTrickplay,
            Name = "20260510-010001_Film.trickplay",
            IsDirectory = true
        };

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, true)).Returns([trashedDir]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains(_loggerMock, "Moving orphaned trickplay folder to trash", LogLevel.Information);
        VerifyLogNeverContains(_loggerMock, "Deleting orphaned trickplay folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteAsync_TrashDisabled_OrphanedFolderStillDeleted()
    {
        Config.UseTrash = false;

        var libraryPath = TestPath("media", "series");
        var trickplayFullName = TestPath("media", "series", "Movie.trickplay");
        var parentPath = Path.GetDirectoryName(trickplayFullName)!;

        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
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

        VerifyLogContains(_loggerMock, "Deleting orphaned trickplay folder", LogLevel.Information);
    }
}