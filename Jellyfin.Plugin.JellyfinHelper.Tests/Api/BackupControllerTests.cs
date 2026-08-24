using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class BackupControllerTests
{
    private readonly PluginLogService _log = TestMockFactory.CreatePluginLogService();

    [Fact]
    public void ExportBackup_WhenPayloadIsLargeButWithinLimit_ReturnsFileAndLogsWarning()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            WriteBaselineJsonAtLeast(
                Path.Join(tempDir, "jellyfin-helper-growth-baseline.json"),
                (int)BackupService.LargeBackupWarningThresholdBytes + (256 * 1024));

            var controller = CreateController(tempDir);

            var result = controller.ExportBackup();

            var fileResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("application/json", fileResult.ContentType);

            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(logs,
                entry => entry.Level == "WARN" &&
                         entry.Message.Contains("Large backup export created", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExportBackup_WhenPayloadExceedsLimit_ReturnsBadRequestAndLogsWarning()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            WriteBaselineJsonAtLeast(
                Path.Join(tempDir, "jellyfin-helper-growth-baseline.json"),
                (int)BackupService.MaxBackupSizeBytes + (256 * 1024));

            var controller = CreateController(tempDir);

            var result = controller.ExportBackup();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("Maximum size is 10 MB", payloadJson, StringComparison.Ordinal);

            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(logs,
                entry => entry.Level == "WARN" &&
                         entry.Message.Contains("Backup export rejected", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ImportBackup_WhenContentLengthExceedsLimit_ReturnsBadRequest()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var controller =
                CreateControllerWithJsonBody(tempDir, "{}", contentLength: BackupService.MaxBackupSizeBytes + 1);

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("Maximum size is 10 MB", payloadJson, StringComparison.Ordinal);
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ImportBackup_WhenContentLengthIsLargeButWithinLimit_LogsWarning()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            // Use a minimal valid backup JSON but declare a large Content-Length
            var controller = CreateControllerWithJsonBody(tempDir, "{}",
                contentLength: BackupService.LargeBackupWarningThresholdBytes);

            await controller.ImportBackupAsync();

            // The body is only "{}" so deserialization will produce a default BackupData
            // which passes validation - we just want to verify the warning was logged
            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(logs,
                entry => entry.Level == "WARN" &&
                         entry.Message.Contains("Large backup import detected", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ImportBackup_WhenBodyIsEmpty_ReturnsBadRequest()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var controller = CreateControllerWithJsonBody(tempDir, string.Empty);

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("No backup data provided", payloadJson, StringComparison.Ordinal);
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExportBackup_ThenImportBackup_RoundTripsSuccessfully()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(tempDir, "jellyfin-helper-growth-timeline.json"),
                "{\"granularity\":\"monthly\",\"dataPoints\":[{\"date\":\"2024-06-01T00:00:00Z\",\"cumulativeSize\":1000,\"cumulativeFileCount\":1}]}",
                Encoding.UTF8);

            await File.WriteAllTextAsync(
                Path.Join(tempDir, "jellyfin-helper-growth-baseline.json"),
                "{\"firstScanTimestamp\":\"2024-04-01T00:00:00Z\",\"directories\":{\"/media/movie-1\":{\"createdUtc\":\"2024-04-01T00:00:00Z\",\"size\":2000}}}",
                Encoding.UTF8);

            var exportController = CreateController(tempDir);
            var exportResult = exportController.ExportBackup();
            var exportFile = Assert.IsType<FileContentResult>(exportResult);
            var exportedJson = Encoding.UTF8.GetString(exportFile.FileContents);

            // Create a new controller with the exported JSON as the request body
            var importController = CreateControllerWithJsonBody(tempDir, exportedJson);

            var importResult = await importController.ImportBackupAsync();

            var okResult = Assert.IsType<OkObjectResult>(importResult);
            var payloadJson = JsonSerializer.Serialize(okResult.Value);
            Assert.Contains("Backup imported successfully.", payloadJson, StringComparison.Ordinal);
            Assert.Contains("baselineRestored", payloadJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExportBackup_WhenSerializedPayloadExceedsLimit_ReturnsBadRequestAndLogsPayloadSize()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            // Two source files each comfortably under the 10 MB cap so the AnySourceFileOversized
            // pre-check stays false, but their combined re-serialized backup crosses the cap. This
            // is the post-serialization gate (case > MaxBackupSizeBytes), distinct from the single
            // oversized-source-file pre-check exercised elsewhere.
            const int perFileBytes = 6 * 1024 * 1024;
            WriteBaselineJsonAtLeast(
                Path.Join(tempDir, "jellyfin-helper-growth-baseline.json"),
                perFileBytes);
            WriteTimelineJsonAtLeast(
                Path.Join(tempDir, "jellyfin-helper-growth-timeline.json"),
                perFileBytes);

            var controller = CreateController(tempDir);

            var result = controller.ExportBackup();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("too large to export", payloadJson, StringComparison.Ordinal);
            // The formatted MB size is part of the message on this branch (unlike the pre-check).
            Assert.Contains("MB", payloadJson, StringComparison.Ordinal);

            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(logs,
                entry => entry.Level == "WARN" &&
                         entry.Message.Contains("Backup export rejected: payload size", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ImportBackup_WhenActualBodyIsLargeButWithinLimit_LogsLargeBodyWarning()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            // A real body whose byte length is >= the warning threshold but < the hard cap, so the
            // jsonLength-based "Large backup body detected" warning fires. Content-Length matches the
            // body length so the earlier Content-Length warning path is not what we are measuring.
            // The padding is insignificant JSON whitespace between tokens: it inflates the byte count
            // the controller measures without tripping any per-field length validator, so the backup
            // still deserializes and validates, letting control reach the success path.
            var whitespace = new string(' ', (int)BackupService.LargeBackupWarningThresholdBytes + (64 * 1024));
            var backupJson = "{" + whitespace + "\"backupVersion\":1,\"useTrash\":false}";

            var controller = CreateControllerWithJsonBody(tempDir, backupJson);

            var result = await controller.ImportBackupAsync();

            Assert.IsType<OkObjectResult>(result);

            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(logs,
                entry => entry.Level == "WARN" &&
                         entry.Message.Contains("Large backup body detected", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExportBackup_WithSecrets_LogsPlaintextSecretsAudit()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var controller = ControllerTestFactory.CreateBackupController(
                dataPath: tempDir,
                pluginLog: _log,
                configuration: new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { SeerrApiKey = "super-secret-key" });

            var result = controller.ExportBackup(includeSecrets: true);

            Assert.IsType<FileContentResult>(result);
            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(logs,
                entry => entry.Level == "WARN" &&
                         entry.Message.Contains("plaintext secrets", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExportBackup_WithoutSecrets_DoesNotLogSecretsAudit()
    {
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var controller = ControllerTestFactory.CreateBackupController(
                dataPath: tempDir,
                pluginLog: _log,
                configuration: new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { SeerrApiKey = "super-secret-key" });

            // includeSecrets defaults to false -> keys are redacted -> no audit warning.
            var result = controller.ExportBackup();

            Assert.IsType<FileContentResult>(result);
            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.DoesNotContain(logs,
                entry => entry.Message.Contains("plaintext secrets", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    private BackupController CreateController(string dataPath)
        => ControllerTestFactory.CreateBackupController(dataPath: dataPath, pluginLog: _log);

    private BackupController CreateControllerWithJsonBody(string dataPath, string jsonBody,
        long? contentLength = null)
        => (BackupController)ControllerTestFactory.AddJsonBodyToController(CreateController(dataPath), jsonBody,
            contentLength);

    private static string CreateTempDir()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "jh-backup-api-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void WriteBaselineJsonAtLeast(string filePath, int targetBytes)
    {
        var sb = new StringBuilder();
        sb.Append("{\"firstScanTimestamp\":\"2024-01-01T00:00:00Z\",\"directories\":{");

        var first = true;
        var suffix = new string('x', 860);
        var index = 0;

        while (sb.Length < targetBytes)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append('"')
                .Append("/media/")
                .Append(index.ToString("D6", CultureInfo.InvariantCulture))
                .Append('-')
                .Append(suffix)
                .Append("\":{\"createdUtc\":\"2024-01-01T00:00:00Z\",\"size\":1}");
            index++;
        }

        sb.Append("}}");
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteTimelineJsonAtLeast(string filePath, int targetBytes)
    {
        var sb = new StringBuilder();
        sb.Append("{\"granularity\":\"monthly\",\"dataPoints\":[");

        var first = true;
        var index = 0;

        while (sb.Length < targetBytes)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            // Distinct dates keep the points from collapsing on deserialize; the extra digits
            // in cumulativeSize pad each point so we reach the target byte count in fewer entries.
            sb.Append("{\"date\":\"2024-")
                .Append(((index % 12) + 1).ToString("D2", CultureInfo.InvariantCulture))
                .Append("-01T00:00:00Z\",\"cumulativeSize\":")
                .Append((1_000_000_000L + index).ToString(CultureInfo.InvariantCulture))
                .Append(",\"cumulativeFileCount\":")
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .Append('}');
            index++;
        }

        sb.Append("]}");
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    [Fact]
    public async Task ImportBackup_WithDifferentApiKey_ResponseIncludesCredentialsChanged()
    {
        var tempDir = CreateTempDir();
        try
        {
            // Build a minimal backup JSON that has a different Seerr API key than the default config.
            var backupJson = JsonSerializer.Serialize(new
            {
                seerrApiKey = "new-different-api-key-abc123",
                seerrBaseUrl = "http://seerr.local:5055",
                useTrash = false
            });

            var controller = CreateControllerWithJsonBody(tempDir, backupJson);
            var result = await controller.ImportBackupAsync();

            var okResult = Assert.IsType<OkObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(okResult.Value);

            Assert.Contains("credentialsChanged", payloadJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ImportBackup_WithSameApiKey_CredentialsChangedIsFalse()
    {
        var tempDir = CreateTempDir();
        try
        {
            // Empty or missing API key in backup means "leave existing" -> no credential change.
            var backupJson = JsonSerializer.Serialize(new
            {
                seerrApiKey = string.Empty,
                useTrash = false
            });

            var controller = CreateControllerWithJsonBody(tempDir, backupJson);
            var result = await controller.ImportBackupAsync();

            var okResult = Assert.IsType<OkObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(okResult.Value);

            // Field must be present and false (no credential change occurred).
            Assert.Contains("credentialsChanged", payloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"credentialsChanged\":false", payloadJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
