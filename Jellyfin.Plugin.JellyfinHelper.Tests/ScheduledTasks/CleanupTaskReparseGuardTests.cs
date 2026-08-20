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

        [Fact]
        public async Task DryRun_OrphanIsReparsePoint_LogsInfoAndCountsWithoutDeleting()
        {
            Config.EmptyMediaFolderTaskMode = TaskMode.DryRun;

            const string libraryPath = "/media/movies";
            const string orphanDir = "/media/movies/Orphan (2019)";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(orphanDir, "Orphan (2019)")]);

            var task = new ReparseTask(
                _libraryManagerMock.Object, _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(), _loggerMock.Object,
                MockConfigHelper.Object, MockTrackingService.Object, MockTrashService.Object,
                orphanDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "[Dry Run] Would delete symlinked directory (link node only)", LogLevel.Information);
            Assert.False(task.LinkNodeDeleted);
            // RecordCleanup must never be called in dry-run mode.
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
        }

        [Fact]
        public async Task TooYoung_ReparsePoint_SkipsWithDebugLog()
        {
            Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
            MockConfigHelper.Setup(x => x.IsOldEnoughForDeletion(It.IsAny<string>())).Returns(false);

            const string libraryPath = "/media/movies";
            const string orphanDir = "/media/movies/Orphan (2019)";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(orphanDir, "Orphan (2019)")]);

            var task = new ReparseTask(
                _libraryManagerMock.Object, _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(), _loggerMock.Object,
                MockConfigHelper.Object, MockTrackingService.Object, MockTrashService.Object,
                orphanDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "too-new reparse-point directory", LogLevel.Debug);
            Assert.False(task.LinkNodeDeleted);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
        }

        /// <summary>
        ///     When <see cref="BaseLibraryCleanupTask.DeleteReparsePointLinkNode" /> (base implementation)
        ///     detects that the path is no longer a reparse point at deletion time it throws
        ///     <see cref="InvalidOperationException" />.  The caller must log a Warning and leave the
        ///     entry unchanged (no count increment, no RecordCleanup call).
        ///     This test also exercises the base <c>DeleteReparsePointLinkNode</c> throw path: the
        ///     unoverridden production implementation creates a <see cref="DirectoryInfo" /> for the
        ///     fake test path, finds it does not exist, and throws.
        /// </summary>
        [Fact]
        public async Task HardDelete_ReparsePoint_ConcurrentReplacement_WarnsAndSkips()
        {
            Config.EmptyMediaFolderTaskMode = TaskMode.Activate;

            const string libraryPath = "/media/movies";
            const string orphanDir = "/media/movies/Orphan (2019)";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(orphanDir, "Orphan (2019)")]);

            // Use the base DeleteReparsePointLinkNode (no override): the path does not exist on disk,
            // so info.Exists == false and the base impl throws InvalidOperationException (fail closed).
            var task = new BaseImplReparseTask(
                _libraryManagerMock.Object, _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(), _loggerMock.Object,
                MockConfigHelper.Object, MockTrackingService.Object, MockTrashService.Object,
                orphanDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Reparse-point node changed type before deletion, skipping", LogLevel.Warning);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
        }

        [Fact]
        public async Task AnalyzeRecursive_SubdirIsReparsePoint_ChildrenNeverEnumerated()
        {
            Config.EmptyMediaFolderTaskMode = TaskMode.Activate;

            const string libraryPath = "/media/movies";
            const string orphanDir = "/media/movies/Orphan (2019)";
            const string reparseSubDir = "/media/movies/Orphan (2019)/extras";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(orphanDir, "Orphan (2019)")]);
            // orphanDir is a real (non-reparse) directory that contains a subtitle and a reparse-point subdir.
            _fileSystemMock.Setup(f => f.GetFiles(orphanDir)).Returns([FileMeta(orphanDir + "/movie.srt")]);
            _fileSystemMock.Setup(f => f.GetDirectories(orphanDir)).Returns([DirMeta(reparseSubDir, "extras")]);
            // reparseSubDir is a reparse point — its children should never be queried.

            // reparseSubDir is the reparse path; orphanDir is a normal directory.
            var task = new ReparseTask(
                _libraryManagerMock.Object, _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(), _loggerMock.Object,
                MockConfigHelper.Object, MockTrackingService.Object, MockTrashService.Object,
                reparseSubDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            // The reparse-point subdir must never be descended into.
            _fileSystemMock.Verify(f => f.GetFiles(reparseSubDir), Times.Never);
            _fileSystemMock.Verify(f => f.GetDirectories(reparseSubDir), Times.Never);
        }

        /// <summary>
        ///     When <see cref="BaseLibraryCleanupTask.DeleteReparsePointLinkNode" /> throws an
        ///     <see cref="IOException" /> (e.g. permission denied), the caller must log an error and
        ///     leave the deletion count at 0 so <c>RecordCleanup</c> is never called.
        /// </summary>
        [Fact]
        public async Task HardDelete_ReparsePoint_IOExceptionDuringDeletion_LogsErrorAndNoCount()
        {
            Config.EmptyMediaFolderTaskMode = TaskMode.Activate;

            const string libraryPath = "/media/movies";
            const string orphanDir = "/media/movies/Orphan (2019)";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(orphanDir, "Orphan (2019)")]);

            var task = new IOExceptionReparseTask(
                _libraryManagerMock.Object, _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(), _loggerMock.Object,
                MockConfigHelper.Object, MockTrackingService.Object, MockTrashService.Object,
                orphanDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Failed to delete reparse point link node", LogLevel.Error);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
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

        /// <summary>Overrides <c>DeleteReparsePointLinkNode</c> to throw <see cref="IOException" />.</summary>
        private sealed class IOExceptionReparseTask : CleanEmptyMediaFoldersTask
        {
            private readonly string _reparsePath;

            public IOExceptionReparseTask(
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

            protected override bool IsReparsePoint(string path) =>
                string.Equals(path, _reparsePath, StringComparison.Ordinal);

            protected override void DeleteReparsePointLinkNode(string path) =>
                throw new IOException("Simulated permission denied");
        }

        /// <summary>
        ///     Overrides only <c>IsReparsePoint</c>; intentionally does NOT override
        ///     <c>DeleteReparsePointLinkNode</c> so the base-class implementation (with its
        ///     fail-closed re-check) runs against the fake test path.
        /// </summary>
        private sealed class BaseImplReparseTask : CleanEmptyMediaFoldersTask
        {
            private readonly string _reparsePath;

            public BaseImplReparseTask(
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

            protected override bool IsReparsePoint(string path) =>
                string.Equals(path, _reparsePath, StringComparison.Ordinal);
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

        /// <summary>
        ///     When the base <c>DeleteReparsePointLinkNode</c> detects a concurrent replacement it
        ///     throws <see cref="InvalidOperationException" />.  The trickplay task must catch it,
        ///     log a Warning, and not increment the deletion count.
        /// </summary>
        [Fact]
        public async Task HardDelete_TrickplayReparsePoint_ConcurrentReplacement_WarnsAndSkips()
        {
            Config.TrickplayTaskMode = TaskMode.Activate;

            const string libraryPath = "/media";
            const string trickplayDir = "/media/Movie.trickplay";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(trickplayDir, "Movie.trickplay")]);
            _fileSystemMock.Setup(f => f.GetDirectories(trickplayDir)).Returns([]);
            _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);

            var task = new BaseImplReparseTask(
                _libraryManagerMock.Object, _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(), _loggerMock.Object,
                MockConfigHelper.Object, MockTrackingService.Object, MockTrashService.Object,
                trickplayDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Reparse-point node changed type before deletion, skipping", LogLevel.Warning);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
        }

        [Fact]
        public async Task Traversal_ReparsePointDir_ChildrenNeverEnumerated()
        {
            Config.TrickplayTaskMode = TaskMode.Activate;

            const string libraryPath = "/media";
            const string reparseDir = "/media/SomeShow";  // reparse point — not a .trickplay dir

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(reparseDir, "SomeShow")]);
            // reparseDir is a reparse point: GetSubdirectoriesIterative must not call GetDirectories on it.

            var task = new ReparseTask(
                _libraryManagerMock.Object, _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(), _loggerMock.Object,
                MockConfigHelper.Object, MockTrackingService.Object, MockTrashService.Object,
                reparseDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            _fileSystemMock.Verify(f => f.GetDirectories(reparseDir), Times.Never);
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

        private sealed class BaseImplReparseTask : CleanTrickplayTask
        {
            private readonly string _reparsePath;

            public BaseImplReparseTask(
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

            protected override bool IsReparsePoint(string path) =>
                string.Equals(path, _reparsePath, StringComparison.Ordinal);
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
        public async Task TryGetSubdirectories_ReparsePointSubdir_ChildrenNeverEnumerated()
        {
            Config.OrphanedSubtitleTaskMode = TaskMode.Activate;

            const string libraryPath = "/media/tv";
            const string parentDir = "/media/tv/ShowDir";
            const string reparseSubDir = "/media/tv/ShowDir/Season1"; // reparse point

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            // TryGetSubdirectories seeds from GetDirectories(libraryPath) → parentDir.
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(parentDir, "ShowDir")]);
            // parentDir is real; its child reparseSubDir is a reparse point.
            _fileSystemMock.Setup(f => f.GetDirectories(parentDir)).Returns([DirMeta(reparseSubDir, "Season1")]);
            // No video or subtitle files in either real directory.
            _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);
            _fileSystemMock.Setup(f => f.GetFiles(parentDir)).Returns([]);

            var task = new ReparseTask(
                _libraryManagerMock.Object, _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(), _loggerMock.Object,
                MockConfigHelper.Object, MockTrackingService.Object, MockTrashService.Object,
                reparseSubDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            // reparseSubDir is a reparse point: TryGetSubdirectories must not enumerate its children,
            // and the per-directory guard must not list its files.
            _fileSystemMock.Verify(f => f.GetDirectories(reparseSubDir), Times.Never);
            _fileSystemMock.Verify(f => f.GetFiles(reparseSubDir), Times.Never);
        }

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

