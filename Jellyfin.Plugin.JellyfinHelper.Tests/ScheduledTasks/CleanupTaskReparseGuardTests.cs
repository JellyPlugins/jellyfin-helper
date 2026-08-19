using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
///     Covers the reparse-point (symlink/junction) guards in the concrete cleanup tasks. These guards
///     read real reparse-point attributes, which the mocked <see cref="IFileSystem"/> model can never
///     trigger, and creating real symlinks needs elevated privileges (unavailable in CI). Each test
///     subclasses its task and overrides the shared <c>IsReparsePoint</c> seam so the guard branch
///     runs deterministically.
/// </summary>
public sealed class CleanupTaskReparseGuardTests
{
    private static FileSystemMetadata DirMeta(string fullName, string name) =>
        new() { FullName = fullName, Name = name, IsDirectory = true };

    private static FileSystemMetadata FileMeta(string fullName) =>
        new() { FullName = fullName, Name = Path.GetFileName(fullName), IsDirectory = false, Length = 10 };

    // ── CleanEmptyMediaFoldersTask ────────────────────────────────────────────

    public sealed class EmptyMediaFolders : CleanupTaskTestBase
    {
        private readonly Mock<ILibraryManager> _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        private readonly Mock<IFileSystem> _fileSystemMock = TestMockFactory.CreateFileSystem();
        private readonly Mock<ILogger<CleanEmptyMediaFoldersTask>> _loggerMock =
            TestMockFactory.CreateLogger<CleanEmptyMediaFoldersTask>();

        [Fact]
        public async Task HardDelete_OrphanIsReparsePoint_RemovesLinkNodeOnlyAndWarns()
        {
            Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
            Config.UseTrash = false;

            const string libraryPath = "/media/movies";
            const string orphanDir = "/media/movies/Orphan (2019)";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(orphanDir, "Orphan (2019)")]);
            _fileSystemMock.Setup(f => f.GetDirectories(orphanDir)).Returns([]);
            // A subtitle is a non-metadata file with no accompanying video -> the folder is an orphan.
            _fileSystemMock.Setup(f => f.GetFiles(orphanDir)).Returns([FileMeta(orphanDir + "/movie.srt")]);

            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                orphanDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Skipping deletion of symlinked directory", LogLevel.Warning);
            Assert.True(task.LinkNodeDeleted);
            // Counted as one deletion, but a link-node removal frees no bytes.
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), 1, It.IsAny<ILogger>()),
                Times.Once);
        }

        private sealed class ReparseTask : CleanEmptyMediaFoldersTask
        {
            private readonly string _reparsePath;

            public ReparseTask(
                ILibraryManager libraryManager,
                IFileSystem fileSystem,
                IPluginLogService pluginLog,
                ILogger<CleanEmptyMediaFoldersTask> logger,
                ICleanupConfigHelper configHelper,
                ICleanupTrackingService trackingService,
                ITrashService trashService,
                string reparsePath)
                : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
                => _reparsePath = reparsePath;

            public bool LinkNodeDeleted { get; private set; }

            protected override bool IsReparsePoint(string path) =>
                string.Equals(path, _reparsePath, StringComparison.Ordinal);

            protected override void DeleteReparsePointLinkNode(string path) => LinkNodeDeleted = true;
        }
    }

    // ── CleanTrickplayTask ────────────────────────────────────────────────────

    public sealed class Trickplay : CleanupTaskTestBase
    {
        private readonly Mock<ILibraryManager> _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        private readonly Mock<IFileSystem> _fileSystemMock = TestMockFactory.CreateFileSystem();
        private readonly Mock<ILogger<CleanTrickplayTask>> _loggerMock =
            TestMockFactory.CreateLogger<CleanTrickplayTask>();

        [Fact]
        public async Task HardDelete_TrickplayIsReparsePoint_RemovesLinkNodeOnlyAndWarns()
        {
            Config.TrickplayTaskMode = TaskMode.Activate;
            Config.UseTrash = false;

            const string libraryPath = "/media";
            const string trickplayDir = "/media/Movie.trickplay";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(trickplayDir, "Movie.trickplay")]);
            _fileSystemMock.Setup(f => f.GetDirectories(trickplayDir)).Returns([]);
            // Parent has no matching video, so the .trickplay folder is orphaned.
            _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);

            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                trickplayDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Skipping deletion of symlinked trickplay directory", LogLevel.Warning);
            Assert.True(task.LinkNodeDeleted);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), 1, It.IsAny<ILogger>()),
                Times.Once);
        }

        private sealed class ReparseTask : CleanTrickplayTask
        {
            private readonly string _reparsePath;

            public ReparseTask(
                ILibraryManager libraryManager,
                IFileSystem fileSystem,
                IPluginLogService pluginLog,
                ILogger<CleanTrickplayTask> logger,
                ICleanupConfigHelper configHelper,
                ICleanupTrackingService trackingService,
                ITrashService trashService,
                string reparsePath)
                : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
                => _reparsePath = reparsePath;

            public bool LinkNodeDeleted { get; private set; }

            protected override bool IsReparsePoint(string path) =>
                string.Equals(path, _reparsePath, StringComparison.Ordinal);

            protected override void DeleteReparsePointLinkNode(string path) => LinkNodeDeleted = true;
        }
    }

    // ── CleanOrphanedSubtitlesTask ────────────────────────────────────────────

    public sealed class OrphanedSubtitles : CleanupTaskTestBase
    {
        private readonly Mock<ILibraryManager> _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        private readonly Mock<IFileSystem> _fileSystemMock = TestMockFactory.CreateFileSystem();
        private readonly Mock<ILogger<CleanOrphanedSubtitlesTask>> _loggerMock =
            TestMockFactory.CreateLogger<CleanOrphanedSubtitlesTask>();

        [Fact]
        public async Task DirectoryIsReparsePoint_SkipsDirectoryBeforeListingFiles()
        {
            Config.OrphanedSubtitleTaskMode = TaskMode.Activate;
            Config.UseTrash = false;

            const string libraryPath = "/media/tv";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([]);

            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                libraryPath);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Skipping symlinked directory (reparse point)", LogLevel.Warning);
            // The guard short-circuits before any file listing happens.
            _fileSystemMock.Verify(f => f.GetFiles(libraryPath), Times.Never);
        }

        private sealed class ReparseTask : CleanOrphanedSubtitlesTask
        {
            private readonly string _reparsePath;

            public ReparseTask(
                ILibraryManager libraryManager,
                IFileSystem fileSystem,
                IPluginLogService pluginLog,
                ILogger<CleanOrphanedSubtitlesTask> logger,
                ICleanupConfigHelper configHelper,
                ICleanupTrackingService trackingService,
                ITrashService trashService,
                string reparsePath)
                : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
                => _reparsePath = reparsePath;

            protected override bool IsReparsePoint(string path) =>
                string.Equals(path, _reparsePath, StringComparison.Ordinal);
        }
    }
}

