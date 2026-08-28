using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Exercises the filesystem-error catch paths in TrashService by holding a real exclusive Windows lock (None) on an entry so the delete/move throws IOException.
/// </summary>
public sealed class TrashServiceFailureTests : IDisposable
{
    private readonly Mock<IPluginLogService> _mockPluginLog = new();
    private readonly ILogger _logger = TestMockFactory.CreateLogger().Object;
    private readonly TrashService _service;
    private readonly string _testRoot = Path.Join(Path.GetTempPath(), $"TrashFailure-{Guid.NewGuid():N}");

    public TrashServiceFailureTests()
    {
        _service = new TrashService(_mockPluginLog.Object);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(_testRoot))
                {
                    return;
                }

                Directory.Delete(_testRoot, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A lock may briefly outlive the FileStream disposal; retry then give up.
                _ = ex;
                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public void PurgeExpiredTrash_ExpiredDirectoryLocked_LogsErrorAndContinuesPurgingOthers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_testRoot);

        // Two expired directories; the first holds an open file so Directory.Delete(...,true) throws.
        var lockedDir = Path.Join(_testRoot, "20200101-000000_Locked");
        var freeDir = Path.Join(_testRoot, "20200102-000000_Free");
        Directory.CreateDirectory(lockedDir);
        Directory.CreateDirectory(freeDir);
        var lockedFile = Path.Join(lockedDir, "held.bin");
        File.WriteAllText(lockedFile, "x");
        File.WriteAllText(Path.Join(freeDir, "ok.bin"), "x");

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (_, itemsPurged) = _service.PurgeExpiredTrash(_testRoot, 1, _logger);

            // The unlocked directory is purged; the locked one survives and its failure is logged.
            Assert.Equal(1, itemsPurged);
            Assert.False(Directory.Exists(freeDir));
            Assert.True(Directory.Exists(lockedDir));
            _mockPluginLog.Verify(
                l => l.LogError(
                    "Trash",
                    It.Is<string>(m => m.Contains("Failed to purge trash directory")),
                    It.IsAny<Exception>(),
                    It.IsAny<ILogger>()),
                Times.Once);
        }
    }

    [Fact]
    public void PurgeExpiredTrash_ExpiredFileLocked_LogsErrorAndContinuesPurgingOthers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_testRoot);

        var lockedFile = Path.Join(_testRoot, "20200101-000000_locked.srt");
        var freeFile = Path.Join(_testRoot, "20200102-000000_free.srt");
        File.WriteAllText(lockedFile, "locked");
        File.WriteAllText(freeFile, "free");

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (_, itemsPurged) = _service.PurgeExpiredTrash(_testRoot, 1, _logger);

            Assert.Equal(1, itemsPurged);
            Assert.False(File.Exists(freeFile));
            Assert.True(File.Exists(lockedFile));
            _mockPluginLog.Verify(
                l => l.LogError(
                    "Trash",
                    It.Is<string>(m => m.Contains("Failed to purge trash file")),
                    It.IsAny<Exception>(),
                    It.IsAny<ILogger>()),
                Times.Once);
        }
    }

    [Fact]
    public void RelocateTrashContents_DirectoryMoveLocked_CountsFailedAndMovesOthers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var oldTrash = Path.Join(_testRoot, "old");
        var newTrash = Path.Join(_testRoot, "new");
        Directory.CreateDirectory(oldTrash);

        var lockedDir = Path.Join(oldTrash, "20260101-120000_Locked");
        var freeDir = Path.Join(oldTrash, "20260102-120000_Free");
        Directory.CreateDirectory(lockedDir);
        Directory.CreateDirectory(freeDir);
        var lockedFile = Path.Join(lockedDir, "held.mkv");
        File.WriteAllText(lockedFile, "x");
        File.WriteAllText(Path.Join(freeDir, "ok.mkv"), "x");

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (moved, failed) = _service.RelocateTrashContents(oldTrash, newTrash, _logger);

            Assert.Equal(1, moved);
            Assert.Equal(1, failed);
            // Locked dir stays under old trash; free dir moved to new trash.
            Assert.True(Directory.Exists(lockedDir));
            Assert.True(Directory.Exists(Path.Join(newTrash, "20260102-120000_Free")));
            _mockPluginLog.Verify(
                l => l.LogError(
                    "Trash",
                    It.Is<string>(m => m.Contains("Failed to relocate directory")),
                    It.IsAny<Exception>(),
                    It.IsAny<ILogger>()),
                Times.Once);
        }
    }

    [Fact]
    public void RelocateTrashContents_FileMoveLocked_CountsFailedAndMovesOthers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var oldTrash = Path.Join(_testRoot, "old");
        var newTrash = Path.Join(_testRoot, "new");
        Directory.CreateDirectory(oldTrash);

        var lockedFile = Path.Join(oldTrash, "20260101-120000_locked.srt");
        var freeFile = Path.Join(oldTrash, "20260102-120000_free.srt");
        File.WriteAllText(lockedFile, "locked");
        File.WriteAllText(freeFile, "free");

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (moved, failed) = _service.RelocateTrashContents(oldTrash, newTrash, _logger);

            Assert.Equal(1, moved);
            Assert.Equal(1, failed);
            Assert.True(File.Exists(lockedFile));
            Assert.True(File.Exists(Path.Join(newTrash, "20260102-120000_free.srt")));
            _mockPluginLog.Verify(
                l => l.LogError(
                    "Trash",
                    It.Is<string>(m => m.Contains("Failed to relocate file")),
                    It.IsAny<Exception>(),
                    It.IsAny<ILogger>()),
                Times.Once);
        }
    }

    [Fact]
    public void MoveFileToTrash_SourceFileLocked_ReturnsZeroAndLogsError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var library = Path.Join(_testRoot, "library");
        var trashBase = Path.Join(library, ".jellyfin-trash");
        Directory.CreateDirectory(library);
        var sourceFile = Path.Join(library, "orphan.srt");
        File.WriteAllText(sourceFile, "sub");

        using (new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = _service.MoveFileToTrash(sourceFile, trashBase, _logger);

            // The exclusive lock makes File.Move throw; nothing is trashed and the source stays put.
            Assert.Equal(0, result);
            Assert.True(File.Exists(sourceFile));
            _mockPluginLog.Verify(
                l => l.LogError(
                    "Trash",
                    It.Is<string>(m => m.Contains("Failed to move file to trash")),
                    It.IsAny<Exception>(),
                    It.IsAny<ILogger>()),
                Times.Once);
        }
    }
}
