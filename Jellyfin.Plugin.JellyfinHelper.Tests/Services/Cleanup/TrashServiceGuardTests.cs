using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Tests the defense-in-depth guard in MoveToTrash that prevents re-trashing items already located inside the trash folder.
/// </summary>
public sealed class TrashServiceGuardTests
{
    private readonly Mock<IPluginLogService> _mockPluginLog;
    private readonly Mock<ILogger> _mockLogger;
    private readonly TrashService _service;

    public TrashServiceGuardTests()
    {
        _mockPluginLog = new Mock<IPluginLogService>();
        _mockLogger = TestMockFactory.CreateLogger();
        _service = new TrashService(_mockPluginLog.Object);
    }

    [Fact]
    public void MoveToTrash_SourceInsideTrashFolder_ReturnsZeroAndLogsWarning()
    {
        var tempBase = Path.Join(Path.GetTempPath(), $"trash-guard-inside-{Guid.NewGuid():N}");
        var trashBasePath = Path.Join(tempBase, ".jellyfin-trash");
        var sourcePath = Path.Join(trashBasePath, "20260510-010001_Movie.trickplay");

        Directory.CreateDirectory(sourcePath);
        try
        {
            var result = _service.MoveToTrash(sourcePath, trashBasePath, _mockLogger.Object);

            Assert.Equal(0, result);
            _mockPluginLog.Verify(
                l => l.LogWarning(
                    "Trash",
                    It.Is<string>(msg => msg.Contains("already inside trash folder")),
                    It.IsAny<Exception>(),
                    It.IsAny<ILogger>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempBase))
            {
                Directory.Delete(tempBase, true);
            }
        }
    }

    [Fact]
    public void MoveToTrash_SourceEqualsTrashFolder_ReturnsZero()
    {
        var tempBase = Path.Join(Path.GetTempPath(), $"trash-guard-eq-{Guid.NewGuid():N}");
        var trashBasePath = Path.Join(tempBase, ".jellyfin-trash");

        Directory.CreateDirectory(trashBasePath);
        try
        {
            var result = _service.MoveToTrash(trashBasePath, trashBasePath, _mockLogger.Object);

            Assert.Equal(0, result);
        }
        finally
        {
            if (Directory.Exists(tempBase))
            {
                Directory.Delete(tempBase, true);
            }
        }
    }

    [Fact]
    public void MoveToTrash_SourceOutsideTrash_Succeeds()
    {
        var tempBase = Path.Join(Path.GetTempPath(), $"trash-guard-test-{Guid.NewGuid():N}");
        var libraryPath = Path.Join(tempBase, "library");
        var trashBasePath = Path.Join(libraryPath, ".jellyfin-trash");
        var sourcePath = Path.Join(libraryPath, "Orphan.trickplay");

        Directory.CreateDirectory(sourcePath);
        // Put a file inside so size > 0
        File.WriteAllText(Path.Join(sourcePath, "tile.bif"), "test-content");

        try
        {
            var result = _service.MoveToTrash(sourcePath, trashBasePath, _mockLogger.Object);

            Assert.True(result > 0);
            Assert.False(Directory.Exists(sourcePath)); // Original should be gone
            Assert.True(Directory.Exists(trashBasePath)); // Trash folder should exist now

            // Verify the trashed item exists with timestamp prefix
            var trashedItems = Directory.GetDirectories(trashBasePath);
            Assert.Single(trashedItems);
            Assert.Contains("Orphan.trickplay", Path.GetFileName(trashedItems[0]));
        }
        finally
        {
            if (Directory.Exists(tempBase))
            {
                Directory.Delete(tempBase, true);
            }
        }
    }

    [Fact]
    public void MoveToTrash_SourceDoesNotExist_ReturnsZero()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var trashBasePath = Path.Join(Path.GetTempPath(), $"nonexistent-lib-{uniqueId}", ".jellyfin-trash");
        var sourcePath = Path.Join(Path.GetTempPath(), $"missing-source-{uniqueId}");

        var result = _service.MoveToTrash(sourcePath, trashBasePath, _mockLogger.Object);

        Assert.Equal(0, result);
    }

    [Fact]
    public void MoveFileToTrash_SourceFileInsideTrashFolder_ReturnsZeroAndLogsWarning()
    {
        // A file that already lives under the trash prefix must not be re-trashed: repeated timestamp prefixing would grow the path past PATH_MAX.
        var tempBase = Path.Join(Path.GetTempPath(), $"trash-guard-file-inside-{Guid.NewGuid():N}");
        var trashBasePath = Path.Join(tempBase, ".jellyfin-trash");
        var sourceFile = Path.Join(trashBasePath, "20260510-010001_x.srt");

        Directory.CreateDirectory(trashBasePath);
        File.WriteAllText(sourceFile, "sub");
        try
        {
            var result = _service.MoveFileToTrash(sourceFile, trashBasePath, _mockLogger.Object);

            Assert.Equal(0, result);
            Assert.True(File.Exists(sourceFile), "File inside trash must be left in place");
            _mockPluginLog.Verify(
                l => l.LogWarning(
                    "Trash",
                    It.Is<string>(msg => msg.Contains("already inside trash folder")),
                    It.IsAny<Exception>(),
                    It.IsAny<ILogger>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempBase))
            {
                Directory.Delete(tempBase, true);
            }
        }
    }
}