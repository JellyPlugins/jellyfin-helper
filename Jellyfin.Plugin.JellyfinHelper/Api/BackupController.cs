using System;
using System.Globalization;
using System.IO;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
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
    private readonly BackupService _backupService;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<BackupController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupController"/> class.
    /// </summary>
    /// <param name="backupService">The backup service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    public BackupController(
        BackupService backupService,
        IPluginLogService pluginLog,
        ILogger<BackupController> logger)
    {
        _backupService = backupService;
        _pluginLog = pluginLog;
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
        var backup = _backupService.CreateBackup();
        var json = BackupService.SerializeBackup(backup);

        var bytes = Encoding.UTF8.GetBytes(json);
        switch (bytes.LongLength)
        {
            case > BackupService.MaxBackupSizeBytes:
                _pluginLog.LogWarning(
                    "API",
                    $"Backup export rejected: payload size {FormatBackupSize(bytes.LongLength)} exceeds limit {FormatBackupSize(BackupService.MaxBackupSizeBytes)} (timelinePoints={backup.GrowthTimeline?.DataPoints.Count ?? 0}, baselineDirs={backup.GrowthBaseline?.Directories.Count ?? 0}).",
                    logger: _logger);

                return BadRequest(new
                {
                    message = $"Backup is too large to export ({FormatBackupSize(bytes.LongLength)}). Maximum size is {BackupService.MaxBackupSizeBytes / (1024 * 1024)} MB. Check the plugin logs for details.",
                });
            case >= BackupService.LargeBackupWarningThresholdBytes:
                _pluginLog.LogWarning(
                    "API",
                    $"Large backup export created: {FormatBackupSize(bytes.LongLength)} of {FormatBackupSize(BackupService.MaxBackupSizeBytes)} limit (timelinePoints={backup.GrowthTimeline?.DataPoints.Count ?? 0}, baselineDirs={backup.GrowthBaseline?.Directories.Count ?? 0}).",
                    logger: _logger);
                break;
        }

        var timestamp = backup.CreatedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _pluginLog.LogInfo("API", $"Backup exported ({FormatBackupSize(bytes.LongLength)}, timelinePoints={backup.GrowthTimeline?.DataPoints.Count ?? 0}, baselineDirs={backup.GrowthBaseline?.Directories.Count ?? 0})", _logger);
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
                    _pluginLog.LogWarning("API", $"Backup import rejected: Content-Length too large ({FormatBackupSize(contentLength)}, max {FormatBackupSize(BackupService.MaxBackupSizeBytes)}).", logger: _logger);
                    return BadRequest(new { message = $"Backup too large ({FormatBackupSize(contentLength)}). Maximum size is {BackupService.MaxBackupSizeBytes / (1024 * 1024)} MB." });
                case >= BackupService.LargeBackupWarningThresholdBytes:
                    _pluginLog.LogWarning("API", $"Large backup import detected: {FormatBackupSize(contentLength)} of {FormatBackupSize(BackupService.MaxBackupSizeBytes)} limit.", logger: _logger);
                    break;
            }

            string json;
            try
            {
                // Stream with inline size enforcement to prevent memory exhaustion from chunked uploads
                var buffer = new MemoryStream();
                try
                {
                    var chunk = new byte[16 * 1024];
                    long totalBytes = 0;
                    int read;

                    while ((read = await Request.Body.ReadAsync(chunk, HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
                    {
                        totalBytes += read;
                        if (totalBytes > BackupService.MaxBackupSizeBytes)
                        {
                            _pluginLog.LogWarning(
                                "API",
                                $"Backup import rejected: actual body too large (>{FormatBackupSize(totalBytes)}, max {FormatBackupSize(BackupService.MaxBackupSizeBytes)}).",
                                logger: _logger);
                            return BadRequest(new { message = $"Backup too large. Maximum size is {BackupService.MaxBackupSizeBytes / (1024 * 1024)} MB." });
                        }

                        await buffer.WriteAsync(chunk.AsMemory(0, read), HttpContext.RequestAborted).ConfigureAwait(false);
                    }

                    buffer.Position = 0;
                    using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                    json = await reader.ReadToEndAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                }
                finally
                {
                    await buffer.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or DecoderFallbackException)
            {
                _pluginLog.LogError("API", "Failed to read backup request body", ex, _logger);
                return BadRequest(new { message = "Failed to read the request body." });
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                _pluginLog.LogWarning("API", "Backup import attempted with empty body.", logger: _logger);
                return BadRequest(new { message = "No backup data provided." });
            }

            var jsonLength = Encoding.UTF8.GetByteCount(json);

            _pluginLog.LogInfo("API", $"Backup import started: size={jsonLength} bytes", _logger);

            if (jsonLength >= BackupService.LargeBackupWarningThresholdBytes)
            {
                _pluginLog.LogWarning("API", $"Large backup body detected: {FormatBackupSize(jsonLength)} of {FormatBackupSize(BackupService.MaxBackupSizeBytes)} limit.", logger: _logger);
            }

            // Deserialize
            var backup = BackupService.DeserializeBackup(json);
            if (backup == null)
            {
                _pluginLog.LogWarning("API", $"Backup import rejected: invalid JSON structure (length={json.Length}).", logger: _logger);
                return BadRequest(new { message = "Invalid backup file. Could not parse JSON structure." });
            }

            _pluginLog.LogInfo("API", $"Backup deserialized: version={backup.BackupVersion}, pluginVersion={backup.PluginVersion}, created={backup.CreatedAt:O}", _logger);

            var validation = BackupService.Validate(backup);

            foreach (var error in validation.Errors)
            {
                _pluginLog.LogError("Backup", $"Validation error: {error}", logger: _logger);
            }

            foreach (var warning in validation.Warnings)
            {
                _pluginLog.LogWarning("Backup", $"Validation warning: {warning}", logger: _logger);
            }

            if (!validation.IsValid)
            {
                _pluginLog.LogWarning("API", $"Backup import rejected: {validation.Errors.Count} validation error(s).", logger: _logger);
                return BadRequest(new
                {
                    message = $"Backup validation failed with {validation.Errors.Count} error(s). Check the plugin logs for details.",
                    errors = validation.Errors,
                    warnings = validation.Warnings,
                });
            }

            BackupService.Sanitize(backup);

            // Restore
            var summary = _backupService.RestoreBackup(backup);

            _pluginLog.LogInfo("API", $"Backup imported successfully. Config={summary.ConfigurationRestored}, Timeline={summary.TimelineRestored}, Baseline={summary.BaselineRestored}", _logger);

            return Ok(new
            {
                message = "Backup imported successfully.",
                warnings = validation.Warnings,
                summary = new
                {
                    summary.ConfigurationRestored,
                    summary.TimelineRestored,
                    summary.BaselineRestored,
                },
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or FormatException or InvalidDataException or JsonException)
        {
            _pluginLog.LogError("API", "Unexpected backup import failure", ex, _logger);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to import backup." });
        }
    }

    private static string FormatBackupSize(long bytes)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):0.00} MB");
    }
}
