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
        public async Task TopLevelReparsePoint_IsSkippedNeverDeleted()
        {
            // Policy: a top-level symlink/junction is NEVER deleted — its target may hold live media
            // (Radarr/Sonarr place symlinked media folders under the library root) and the task has
            // no orphan evidence for an entry it did not analyze. It must be skipped with a warning,
            // never traversed, and never counted as a cleanup.
            Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
            Config.UseTrash = false;

            const string libraryPath = "/media/movies";
            const string linkedDir = "/media/movies/Linked (2019)";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(linkedDir, "Linked (2019)")]);

            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                linkedDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Skipping symlinked directory (reparse point)", LogLevel.Warning);
            // The link is never traversed and never trashed.
            _fileSystemMock.Verify(f => f.GetFiles(linkedDir), Times.Never);
            MockTrashService.Verify(
                t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
                Times.Never);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
        }

        [Fact]
        public async Task FolderWithReparsePointSubdir_IsNotDeleted_OrphanVerdictUnproven()
        {
            // Data-loss guard: a real folder whose only "orphan" signal is a stray non-video file,
            // but which ALSO contains a symlinked subdirectory, must NOT be deleted — video files
            // could live behind that link, so the orphan verdict is unproven.
            Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
            Config.UseTrash = false;

            const string libraryPath = "/media/movies";
            const string folder = "/media/movies/Show (2019)";
            const string reparseSubDir = "/media/movies/Show (2019)/season1";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(folder, "Show (2019)")]);
            // folder is a real dir with a non-metadata file (would look like an orphan on its own)
            // plus a reparse-point subdir whose contents are unknown.
            _fileSystemMock.Setup(f => f.GetFiles(folder)).Returns([FileMeta(folder + "/readme.txt")]);
            _fileSystemMock.Setup(f => f.GetDirectories(folder)).Returns([DirMeta(reparseSubDir, "season1")]);

            // Only the SUBdir is a reparse point; the top-level folder is a normal directory.
            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                reparseSubDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "unresolved symlinked/unreadable subdirectory", LogLevel.Warning);
            // The subtree behind the link was never enumerated, and nothing was deleted/trashed.
            _fileSystemMock.Verify(f => f.GetFiles(reparseSubDir), Times.Never);
            MockTrashService.Verify(
                t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
                Times.Never);
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

        [Fact]
        public async Task TopLevelStatFailure_IsSkippedNeverDeleted_FailClosed()
        {
            // Fail-closed guard: if the reparse-point stat on a top-level entry throws (I/O or an
            // access denial), the verdict is unknown, so the entry must be skipped with a warning —
            // never traversed, deleted, trashed, or counted.
            Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
            Config.UseTrash = false;

            const string libraryPath = "/media/movies";
            const string unreadableDir = "/media/movies/Unreadable (2019)";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(unreadableDir, "Unreadable (2019)")]);

            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                reparsePath: "/none",
                throwPath: unreadableDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Could not stat directory, skipping", LogLevel.Warning);
            // The entry is never traversed, trashed, or counted.
            _fileSystemMock.Verify(f => f.GetFiles(unreadableDir), Times.Never);
            MockTrashService.Verify(
                t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
                Times.Never);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
        }

        [Fact]
        public async Task SubdirStatFailure_EnclosingFolderSurvives_OrphanVerdictUnproven()
        {
            // Fail-closed guard: a real folder that looks like an orphan (a stray non-video file) but
            // whose subdirectory cannot be stat'd must NOT be deleted — a video could live in the
            // subtree we failed to inspect. The stat-failure branch must set the unresolved-link flag
            // so the enclosing folder is kept.
            Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
            Config.UseTrash = false;

            const string libraryPath = "/media/movies";
            const string folder = "/media/movies/Show (2019)";
            const string unreadableSubDir = "/media/movies/Show (2019)/season1";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(folder, "Show (2019)")]);
            _fileSystemMock.Setup(f => f.GetFiles(folder)).Returns([FileMeta(folder + "/readme.txt")]);
            _fileSystemMock.Setup(f => f.GetDirectories(folder)).Returns([DirMeta(unreadableSubDir, "season1")]);

            // The top-level folder stats fine (reparsePath "/none"); only the subdir stat throws.
            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                reparsePath: "/none",
                throwPath: unreadableSubDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Could not stat subdirectory, not traversing", LogLevel.Warning);
            VerifyLogContains(_loggerMock, "unresolved symlinked/unreadable subdirectory", LogLevel.Warning);
            // The subtree behind the failed stat was never enumerated, and nothing was deleted/trashed.
            _fileSystemMock.Verify(f => f.GetFiles(unreadableSubDir), Times.Never);
            MockTrashService.Verify(
                t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
                Times.Never);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
        }

        private sealed class ReparseTask : CleanEmptyMediaFoldersTask
        {
            private readonly string _reparsePath;
            private readonly string? _throwPath;

            public ReparseTask(
                ILibraryManager libraryManager,
                IFileSystem fileSystem,
                IPluginLogService pluginLog,
                ILogger<CleanEmptyMediaFoldersTask> logger,
                ICleanupConfigHelper configHelper,
                ICleanupTrackingService trackingService,
                ITrashService trashService,
                string reparsePath,
                string? throwPath = null)
                : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
            {
                _reparsePath = reparsePath;
                _throwPath = throwPath;
            }

            protected override bool IsReparsePoint(string path)
            {
                if (_throwPath != null && string.Equals(path, _throwPath, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("Access denied");
                }

                return string.Equals(path, _reparsePath, StringComparison.Ordinal);
            }
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
        public async Task TrickplayIsReparsePoint_IsSkippedNeverDeletedOrTrashed()
        {
            // Policy (matching CleanEmptyMediaFoldersTask): a reparse-point .trickplay dir is never
            // trashed and never recursively deleted — Directory.Delete/MoveToTrash could otherwise be
            // redirected into the link's real target. It is skipped with a warning, counting nothing.
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

            VerifyLogContains(_loggerMock, "Skipping symlinked trickplay directory (reparse point)", LogLevel.Warning);
            MockTrashService.Verify(
                t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
                Times.Never);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
        }

        [Fact]
        public async Task TrickplayIsReparsePoint_WithTrashEnabled_IsSkippedNotTrashed()
        {
            // Even with UseTrash=true, a reparse-point trickplay dir must be skipped, never moved to
            // trash (relocating a link node while its target stays behind is an ambiguous half-op).
            Config.TrickplayTaskMode = TaskMode.Activate;
            Config.UseTrash = true;

            const string libraryPath = "/media";
            const string trickplayDir = "/media/Movie.trickplay";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(trickplayDir, "Movie.trickplay")]);
            _fileSystemMock.Setup(f => f.GetDirectories(trickplayDir)).Returns([]);
            _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);

            var task = new ReparseTask(
                _libraryManagerMock.Object, _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(), _loggerMock.Object,
                MockConfigHelper.Object, MockTrackingService.Object, MockTrashService.Object,
                trickplayDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Skipping symlinked trickplay directory (reparse point)", LogLevel.Warning);
            MockTrashService.Verify(
                t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
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

        [Fact]
        public async Task TrickplayStatFailure_IsSkippedNeverDeletedOrTrashed_FailClosed()
        {
            // Fail-closed guard: if the reparse-point stat on an orphaned .trickplay dir throws, the
            // verdict is unknown, so the dir must be skipped with a warning — never trashed, deleted,
            // or counted. The hoisted guard covers this before any mode branch runs.
            Config.TrickplayTaskMode = TaskMode.Activate;
            Config.UseTrash = false;

            const string libraryPath = "/media";
            const string trickplayDir = "/media/Movie.trickplay";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(trickplayDir, "Movie.trickplay")]);
            _fileSystemMock.Setup(f => f.GetDirectories(trickplayDir)).Returns([]);
            // Parent has no matching video, so the .trickplay folder is orphaned and reaches the guard.
            _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);

            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                reparsePath: "/none",
                throwPath: trickplayDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Could not stat directory, skipping", LogLevel.Warning);
            MockTrashService.Verify(
                t => t.MoveToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
                Times.Never);
            MockTrackingService.Verify(
                t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
                Times.Never);
        }

        private sealed class ReparseTask : CleanTrickplayTask
        {
            private readonly string _reparsePath;
            private readonly string? _throwPath;

            public ReparseTask(
                ILibraryManager libraryManager,
                IFileSystem fileSystem,
                IPluginLogService pluginLog,
                ILogger<CleanTrickplayTask> logger,
                ICleanupConfigHelper configHelper,
                ICleanupTrackingService trackingService,
                ITrashService trashService,
                string reparsePath,
                string? throwPath = null)
                : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
            {
                _reparsePath = reparsePath;
                _throwPath = throwPath;
            }

            protected override bool IsReparsePoint(string path)
            {
                if (_throwPath != null && string.Equals(path, _throwPath, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("Access denied");
                }

                return string.Equals(path, _reparsePath, StringComparison.Ordinal);
            }
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

        [Fact]
        public async Task DirectoryStatFailure_IsSkippedBeforeListingFiles_FailClosed()
        {
            // Fail-closed guard (ProcessLocation): if the reparse-point stat on a directory throws
            // (I/O or an access denial), the verdict is unknown, so the directory must be skipped
            // with a warning before its files are ever listed — never deleted or trashed.
            Config.OrphanedSubtitleTaskMode = TaskMode.Activate;
            Config.UseTrash = false;

            const string libraryPath = "/media/tv";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            // No subdirectories, so libraryPath itself is the only directory processed. Its stat
            // (in ProcessLocation) throws; TryGetSubdirectories never stats the seed root.
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([]);

            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                reparsePath: "/none",
                throwPath: libraryPath);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Could not stat directory, skipping", LogLevel.Warning);
            // The guard short-circuits before any file listing happens.
            _fileSystemMock.Verify(f => f.GetFiles(libraryPath), Times.Never);
            MockTrashService.Verify(
                t => t.MoveFileToTrash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILogger>(), It.IsAny<DateTime?>()),
                Times.Never);
        }

        [Fact]
        public async Task SubdirectoryStatFailure_ChildrenNeverEnumerated_FailClosed()
        {
            // Fail-closed guard (TryGetSubdirectories): if the reparse-point stat on a subdirectory
            // throws, it must be treated as "do not traverse" so a symlinked/unreadable subtree is
            // never descended into. Its children must never be enumerated.
            Config.OrphanedSubtitleTaskMode = TaskMode.Activate;

            const string libraryPath = "/media/tv";
            const string parentDir = "/media/tv/ShowDir";
            const string unreadableSubDir = "/media/tv/ShowDir/Season1";

            _libraryManagerMock.Setup(m => m.GetVirtualFolders())
                .Returns([new VirtualFolderInfo { Locations = [libraryPath] }]);
            // TryGetSubdirectories seeds from GetDirectories(libraryPath) → parentDir → unreadableSubDir.
            _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([DirMeta(parentDir, "ShowDir")]);
            _fileSystemMock.Setup(f => f.GetDirectories(parentDir)).Returns([DirMeta(unreadableSubDir, "Season1")]);
            _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);
            _fileSystemMock.Setup(f => f.GetFiles(parentDir)).Returns([]);

            // Only the subdir stat throws; libraryPath and parentDir stat fine (reparsePath "/none").
            var task = new ReparseTask(
                _libraryManagerMock.Object,
                _fileSystemMock.Object,
                TestMockFactory.CreatePluginLogService(),
                _loggerMock.Object,
                MockConfigHelper.Object,
                MockTrackingService.Object,
                MockTrashService.Object,
                reparsePath: "/none",
                throwPath: unreadableSubDir);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains(_loggerMock, "Could not stat directory, not traversing", LogLevel.Warning);
            // The subtree behind the failed stat was never enumerated.
            _fileSystemMock.Verify(f => f.GetDirectories(unreadableSubDir), Times.Never);
            _fileSystemMock.Verify(f => f.GetFiles(unreadableSubDir), Times.Never);
        }

        private sealed class ReparseTask : CleanOrphanedSubtitlesTask
        {
            private readonly string _reparsePath;
            private readonly string? _throwPath;

            public ReparseTask(
                ILibraryManager libraryManager,
                IFileSystem fileSystem,
                IPluginLogService pluginLog,
                ILogger<CleanOrphanedSubtitlesTask> logger,
                ICleanupConfigHelper configHelper,
                ICleanupTrackingService trackingService,
                ITrashService trashService,
                string reparsePath,
                string? throwPath = null)
                : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
            {
                _reparsePath = reparsePath;
                _throwPath = throwPath;
            }

            protected override bool IsReparsePoint(string path)
            {
                if (_throwPath != null && string.Equals(path, _throwPath, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("Access denied");
                }

                return string.Equals(path, _reparsePath, StringComparison.Ordinal);
            }
        }
    }
}

