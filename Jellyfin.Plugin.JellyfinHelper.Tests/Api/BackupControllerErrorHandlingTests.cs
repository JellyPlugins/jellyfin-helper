using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Covers the two outer catch blocks of <see cref="BackupController.ImportBackupAsync"/> that only
///     fire when the restore itself throws AFTER validation has already passed. These paths are
///     unreachable through the real <see cref="BackupService"/> (it swallows I/O and parse faults
///     internally), so a mocked <see cref="IBackupService"/> is required to make
///     <see cref="IBackupService.RestoreBackup"/> throw the specific exception families the controller
///     is contracted to translate into a 400 (client-caused, malformed data) versus a 500 (server-side
///     filesystem failure).
/// </summary>
public sealed class BackupControllerErrorHandlingTests
{
    private readonly PluginLogService _log = TestMockFactory.CreatePluginLogService();

    [Theory]
    [InlineData(typeof(JsonException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(InvalidDataException))]
    public async Task ImportBackup_WhenRestoreThrowsMalformedDataException_ReturnsBadRequest(Type exceptionType)
    {
        // A corrupt data file may only reveal itself at restore time. Surfacing that as a 400 lets the
        // client understand the file is bad, whereas a 500 would wrongly implicate the server.
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var toThrow = (Exception)Activator.CreateInstance(exceptionType, "restore-time corruption")!;
            var controller = CreateControllerWithFailingRestore(tempDir, toThrow);

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("The JSON content could not be parsed.", payloadJson, StringComparison.Ordinal);

            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(logs,
                entry => entry.Level == "WARN" &&
                         entry.Message.Contains("Backup import rejected: malformed data", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task ImportBackup_WhenRestoreThrowsIOException_ReturnsInternalServerError(Type exceptionType)
    {
        // A genuine filesystem failure during restore is a server-side problem: it must be a 500 with a
        // generic message, never a 400 that would mislead the client into re-uploading a fine file.
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var toThrow = (Exception)Activator.CreateInstance(exceptionType, "disk failure during restore")!;
            var controller = CreateControllerWithFailingRestore(tempDir, toThrow);

            var result = await controller.ImportBackupAsync();

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
            var payloadJson = JsonSerializer.Serialize(objectResult.Value);
            Assert.Contains("Failed to import backup.", payloadJson, StringComparison.Ordinal);

            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(logs,
                entry => entry.Level == "ERROR" &&
                         entry.Message.Contains("Unexpected backup import failure", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    ///     Builds a controller over a mocked backup service whose <see cref="IBackupService.RestoreBackup"/>
    ///     throws <paramref name="restoreException"/>. A minimal valid backup body is attached so
    ///     deserialization and validation both pass and control reaches the restore call.
    /// </summary>
    private BackupController CreateControllerWithFailingRestore(string dataPath, Exception restoreException)
    {
        var backupServiceMock = new Mock<IBackupService>();
        backupServiceMock.Setup(s => s.RestoreBackup(It.IsAny<BackupData>())).Throws(restoreException);

        var controller = new BackupController(
            backupServiceMock.Object,
            _log,
            new Mock<ILogger<BackupController>>().Object);

        // A minimal, structurally valid backup that clears validation so the restore call is reached.
        return (BackupController)ControllerTestFactory.AddJsonBodyToController(controller, "{\"backupVersion\":1}");
    }

    private static string CreateTempDir()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "jh-backup-api-err-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }
}
