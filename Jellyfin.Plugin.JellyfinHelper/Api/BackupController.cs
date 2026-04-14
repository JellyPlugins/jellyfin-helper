using System;
using System.Globalization;
using System.IO;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for plugin data backups.
/// Handles exporting and importing configuration and historical data.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Backup")]
[Produces(MediaTypeNames.Application.Json)]
public class BackupController : ControllerBase
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<BackupController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupController"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The controller logger.</param>
    public BackupController(
        IApplicationPaths applicationPaths,
        ILogger<BackupController> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <summary>
    /// Exports the plugin configuration and historical data as a backup JSON file.
    /// Includes configuration preferences, Arr integration settings, growth timeline,
    /// and statistics history. Cleanup statistics are excluded (they reset on fresh installations).
    /// </summary>
    /// <returns>A JSON file download containing the backup data.</returns>
    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult ExportBackup()
    {
        var backupService = new BackupService(_applicationPaths, _logger);
        var backup = backupService.CreateBackup();
        var json = BackupService.SerializeBackup(backup);

        var bytes = Encoding.UTF8.GetBytes(json);
        switch (bytes.LongLength)
        {
            case > BackupService.MaxBackupSizeBytes:
                PluginLogService.LogWarning(
                    "API",
                    $"Backup export rejected: payload size {FormatBackupSize(bytes.LongLength)} exceeds limit {FormatBackupSize(BackupService.MaxBackupSizeBytes)} (timelinePoints={backup.GrowthTimeline?.DataPoints.Count ?? 0}, baselineDirs={backup.GrowthBaseline?.Directories.Count ?? 0}, historySnapshots={backup.StatisticsHistory.Count}).",
                    logger: _logger);

                return BadRequest(new
                {
                    message = $"Backup is too large to export ({FormatBackupSize(bytes.LongLength)}). Maximum size is {BackupService.MaxBackupSizeBytes / (1024 * 1024)} MB. Check the plugin logs for details.",
                });
            case >= BackupService.LargeBackupWarningThresholdBytes:
                PluginLogService.LogWarning(
                    "API",
                    $"Large backup export created: {FormatBackupSize(bytes.LongLength)} of {FormatBackupSize(BackupService.MaxBackupSizeBytes)} limit (timelinePoints={backup.GrowthTimeline?.DataPoints.Count ?? 0}, baselineDirs={backup.GrowthBaseline?.Directories.Count ?? 0}, historySnapshots={backup.StatisticsHistory.Count}).",
                    logger: _logger);
                break;
        }

        var timestamp = backup.CreatedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        PluginLogService.LogInfo("API", $"Backup exported ({FormatBackupSize(bytes.LongLength)}, timelinePoints={backup.GrowthTimeline?.DataPoints.Count ?? 0}, baselineDirs={backup.GrowthBaseline?.Directories.Count ?? 0}, historySnapshots={backup.StatisticsHistory.Count})", _logger);
        return File(bytes, "application/json", $"jellyfin-helper-backup-{timestamp}.json");
    }

    /// <summary>
    /// Imports a backup JSON payload to restore plugin configuration and historical data.
    /// Performs comprehensive validation to prevent malicious or corrupt data from being imported.
    /// Accepts the backup JSON directly in the request body (Content-Type: application/json).
    /// </summary>
    /// <returns>A result indicating success with validation details and restore summary.</returns>
    [HttpPost("Import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [Consumes("application/json")]
    public async Task<ActionResult> ImportBackupAsync()
    {
        try
        {
            // Early rejection based on Content-Length header (before reading entire body)
            var contentLength = Request.ContentLength ?? 0;
            switch (contentLength)
            {
                case > BackupService.MaxBackupSizeBytes:
                    PluginLogService.LogWarning("API", $"Backup import rejected: Content-Length too large ({FormatBackupSize(contentLength)}, max {FormatBackupSize(BackupService.MaxBackupSizeBytes)}).", logger: _logger);
                    return BadRequest(new { message = $"Backup too large ({FormatBackupSize(contentLength)}). Maximum size is {BackupService.MaxBackupSizeBytes / (1024 * 1024)} MB." });
                case >= BackupService.LargeBackupWarningThresholdBytes:
                    PluginLogService.LogWarning("API", $"Large backup import detected: {FormatBackupSize(contentLength)} of {FormatBackupSize(BackupService.MaxBackupSizeBytes)} limit.", logger: _logger);
                    break;
            }

            string json;
            try
            {
                using var reader = new StreamReader(
                    Request.Body,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: false);
                json = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PluginLogService.LogError("API", "Failed to read backup request body", ex, _logger);
                return BadRequest(new { message = "Failed to read the request body." });
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                PluginLogService.LogWarning("API", "Backup import attempted with empty body.", logger: _logger);
                return BadRequest(new { message = "No backup data provided." });
            }

            var jsonLength = Encoding.UTF8.GetByteCount(json);

            // Enforce size limit on actual body (Content-Length may be absent for chunked transfers)
            if (jsonLength > BackupService.MaxBackupSizeBytes)
            {
                PluginLogService.LogWarning("API", $"Backup import rejected: actual body too large ({FormatBackupSize(jsonLength)}, max {FormatBackupSize(BackupService.MaxBackupSizeBytes)}).", logger: _logger);
                return BadRequest(new { message = $"Backup too large ({FormatBackupSize(jsonLength)}). Maximum size is {BackupService.MaxBackupSizeBytes / (1024 * 1024)} MB." });
            }

            PluginLogService.LogInfo("API", $"Backup import started: size={jsonLength} bytes", _logger);

            if (jsonLength >= BackupService.LargeBackupWarningThresholdBytes)
            {
                PluginLogService.LogWarning("API", $"Large backup body detected: {FormatBackupSize(jsonLength)} of {FormatBackupSize(BackupService.MaxBackupSizeBytes)} limit.", logger: _logger);
            }

            // Deserialize
            var backup = BackupService.DeserializeBackup(json);
            if (backup == null)
            {
                PluginLogService.LogWarning("API", $"Backup import rejected: invalid JSON structure (length={json.Length}).", logger: _logger);
                return BadRequest(new { message = "Invalid backup file. Could not parse JSON structure." });
            }

            PluginLogService.LogInfo("API", $"Backup deserialized: version={backup.BackupVersion}, pluginVersion={backup.PluginVersion}, created={backup.CreatedAt:O}", _logger);

            var validation = BackupService.Validate(backup);

            foreach (var error in validation.Errors)
            {
                PluginLogService.LogError("Backup", $"Validation error: {error}", logger: _logger);
            }

            foreach (var warning in validation.Warnings)
            {
                PluginLogService.LogWarning("Backup", $"Validation warning: {warning}", logger: _logger);
            }

            if (!validation.IsValid)
            {
                PluginLogService.LogWarning("API", $"Backup import rejected: {validation.Errors.Count} validation error(s).", logger: _logger);
                return BadRequest(new
                {
                    message = $"Backup validation failed with {validation.Errors.Count} error(s). Check the plugin logs for details.",
                    errors = validation.Errors,
                    warnings = validation.Warnings,
                });
            }

            BackupService.Sanitize(backup);

            // Restore
            var backupService = new BackupService(_applicationPaths, _logger);
            var summary = backupService.RestoreBackup(backup);

            PluginLogService.LogInfo("API", $"Backup imported successfully. Config={summary.ConfigurationRestored}, Timeline={summary.TimelineRestored}, Baseline={summary.BaselineRestored}, History={summary.HistorySnapshotsRestored} snapshots", _logger);

            return Ok(new
            {
                message = "Backup imported successfully.",
                warnings = validation.Warnings,
                summary = new
                {
                    summary.ConfigurationRestored,
                    summary.TimelineRestored,
                    summary.BaselineRestored,
                    summary.HistorySnapshotsRestored,
                },
            });
        }
        catch (Exception ex)
        {
            PluginLogService.LogError("API", "Unexpected backup import failure", ex, _logger);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to import backup." });
        }
    }

    private static string FormatBackupSize(long bytes)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):0.00} MB");
    }
}
