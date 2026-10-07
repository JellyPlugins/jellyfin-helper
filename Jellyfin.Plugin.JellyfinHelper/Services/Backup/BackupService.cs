using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
///     Service for creating and restoring plugin backups. Handles export of configuration, historical data, and Arr settings.
/// </summary>
public sealed class BackupService : IBackupService
{
    private const string LogSource = "Backup";

    /// <summary>
    ///     Maximum allowed size of a backup JSON payload in bytes (10 MB).
    ///     Per-directory baselines can be larger for media servers with many items.
    /// </summary>
    internal const long MaxBackupSizeBytes = 10 * 1024 * 1024;

    /// <summary>
    ///     Threshold at which backup payload size should be logged as unusually large.
    /// </summary>
    internal const long LargeBackupWarningThresholdBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;

    private readonly IPluginConfigurationService _configService;

    private readonly string _dataPath;
    private readonly ILogger<BackupService> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly ISecretProtector _secretProtector;

    // Optional gate onto the timeline service's read-compute-write lock. When present, backup
    // export and restore serialize against scheduled scans so neither clobbers the other's write.
    private readonly IGrowthTimelineService? _growthTimeline;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BackupService" /> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="configService">The plugin configuration service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="growthTimeline">The growth timeline service, used to coordinate file access with scans.</param>
    /// <param name="secretProtector">Decrypts secrets for export and re-encrypts them on restore.</param>
    public BackupService(
        IApplicationPaths applicationPaths,
        IPluginConfigurationService configService,
        IPluginLogService pluginLog,
        ILogger<BackupService> logger,
        IGrowthTimelineService growthTimeline,
        ISecretProtector secretProtector)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _dataPath = applicationPaths.DataPath;
        _configService = configService;
        _pluginLog = pluginLog;
        _logger = logger;
        _growthTimeline = growthTimeline;
        _secretProtector = secretProtector;
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="BackupService" /> class for testing.
    /// </summary>
    /// <param name="dataPath">The data path.</param>
    /// <param name="configService">The plugin configuration service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="growthTimeline">The optional growth timeline service used to coordinate file access.</param>
    /// <param name="secretProtector">Optional secret protector; defaults to an in-memory keyring for tests.</param>
    internal BackupService(
        string dataPath,
        IPluginConfigurationService configService,
        IPluginLogService pluginLog,
        ILogger<BackupService> logger,
        IGrowthTimelineService? growthTimeline = null,
        ISecretProtector? secretProtector = null)
    {
        _dataPath = dataPath;
        _configService = configService;
        _pluginLog = pluginLog;
        _logger = logger;
        _growthTimeline = growthTimeline;

        // Tests that do not care about encryption get a working in-memory protector. Plaintext config
        // values pass through Unprotect unchanged, so existing plaintext-seeded tests keep their meaning.
        _secretProtector = secretProtector
            ?? new SecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance);
    }

    /// <summary>
    ///     Acquires the timeline service's exclusive gate so this backup operation cannot race a
    ///     scheduled scan write. Returns a no-op scope when no timeline service is wired (unit tests).
    /// </summary>
    private IDisposable AcquireTimelineGate()
    {
        if (_growthTimeline is null)
        {
            return NoopScope.Instance;
        }

        // Restore/export are rare, admin-triggered, off the hot path; a blocking wait here is fine
        // and keeps the synchronous IBackupService contract intact.
        return _growthTimeline.AcquireExclusiveAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Returns true if any source data file exceeds MaxBackupSizeBytes. Call before CreateBackup to reject oversized exports early.
    /// </summary>
    /// <returns><c>true</c> when at least one source file exceeds the size limit.</returns>
    public bool AnySourceFileOversized()
    {
        var paths = new[]
        {
            Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json"),
            Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json"),
        };
        return paths.Any(p => File.Exists(p) && new FileInfo(p).Length > MaxBackupSizeBytes);
    }

    /// <summary>
    ///     Creates a backup of all exportable plugin data.
    /// </summary>
    /// <param name="includeSecrets">
    ///     When <c>true</c>, API key values are included in the backup.
    ///     When <c>false</c> (the default), all API key fields are replaced with an empty
    ///     string so that the exported file does not contain plaintext credentials.
    /// </param>
    /// <returns>The backup data object ready for serialization.</returns>
    public BackupData CreateBackup(bool includeSecrets = false)
    {
        _pluginLog.LogInfo(LogSource, "Creating plugin backup...", _logger);

        var config = _configService.GetConfiguration();
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = DateTime.UtcNow,
            PluginVersion = _configService.PluginVersion,

            // Configuration preferences
            Language = config.Language,
            ExcludedLibraries = config.ExcludedLibraries,
            OrphanMinAgeDays = config.OrphanMinAgeDays,
            PluginLogLevel = config.PluginLogLevel,

            // Task modes
            TrickplayTaskMode = config.TrickplayTaskMode.ToString(),
            EmptyMediaFolderTaskMode = config.EmptyMediaFolderTaskMode.ToString(),
            OrphanedSubtitleTaskMode = config.OrphanedSubtitleTaskMode.ToString(),
            LinkRepairTaskMode = config.LinkRepairTaskMode.ToString(),
            SeerrCleanupTaskMode = config.SeerrCleanupTaskMode.ToString(),

            // Seerr settings. Keys are stored encrypted; the backup holds plaintext so it stays portable
            // across hosts without the keyring. Redaction below still applies when includeSecrets is false.
            SeerrUrl = config.SeerrUrl,
            SeerrApiKey = _secretProtector.Unprotect(config.SeerrApiKey),
            SeerrSkipCertificateValidation = config.SeerrSkipCertificateValidation,
            SeerrCleanupAgeDays = config.SeerrCleanupAgeDays,

            // Trakt settings. Client id + enabled flag are plain config; the secret is decrypted here like the
            // Seerr key so the backup holds plaintext, and is stripped below when secrets are excluded.
            TraktEnabled = config.TraktEnabled,
            TraktClientId = config.TraktClientId,
            TraktClientSecret = _secretProtector.Unprotect(config.TraktClientSecret),

            // Trash settings
            UseTrash = config.UseTrash,
            TrashFolderPath = config.TrashFolderPath,
            TrashRetentionDays = config.TrashRetentionDays,

            // Smart Recommendations (only task mode - count and strategy use sensible defaults)
            RecommendationsTaskMode = config.RecommendationsTaskMode.ToString(),
            SyncRecommendationsToPlaylist = config.SyncRecommendationsToPlaylist,

            // Discovery user access
            DiscoveryUserAccessEnabled = config.DiscoveryUserAccessEnabled
        };

        // Arr instances - API keys are included so that credentials survive a full
        // backup/restore cycle. ContainsSecrets is set below when any key is non-empty.
        foreach (var instance in config.RadarrInstances)
        {
            backup.RadarrInstances.Add(
                new BackupArrInstance
                {
                    Name = instance.Name,
                    Url = instance.Url,
                    ApiKey = _secretProtector.Unprotect(instance.ApiKey),
                    Libraries = instance.Libraries,
                    SkipCertificateValidation = instance.SkipCertificateValidation
                });
        }

        foreach (var instance in config.SonarrInstances)
        {
            backup.SonarrInstances.Add(
                new BackupArrInstance
                {
                    Name = instance.Name,
                    Url = instance.Url,
                    ApiKey = _secretProtector.Unprotect(instance.ApiKey),
                    Libraries = instance.Libraries,
                    SkipCertificateValidation = instance.SkipCertificateValidation
                });
        }

        // Growth timeline + baseline. Read under the timeline gate so we cannot capture a file
        // mid-write from a concurrent scan. AtomicFile already prevents torn reads; the gate keeps
        // the two files consistent with each other.
        using (AcquireTimelineGate())
        {
            backup.GrowthTimeline = LoadJsonFile<GrowthTimelineResult>(
                Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json"),
                out _);

            backup.GrowthBaseline = LoadJsonFile<GrowthTimelineBaseline>(
                Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json"),
                out _);
        }

        // When the caller opts out of secrets, redact all API key values so the exported file cannot be used to harvest plaintext credentials.
        if (!includeSecrets)
        {
            backup.SeerrApiKey = string.Empty;
            backup.TraktClientSecret = string.Empty;
            foreach (var instance in backup.RadarrInstances)
            {
                instance.ApiKey = string.Empty;
            }

            foreach (var instance in backup.SonarrInstances)
            {
                instance.ApiKey = string.Empty;
            }
        }

        // Flag the backup when it contains plaintext credentials so the UI/caller can
        // warn the user to store the exported file securely.
        backup.ContainsSecrets =
            !string.IsNullOrEmpty(backup.SeerrApiKey)
            || !string.IsNullOrEmpty(backup.TraktClientSecret)
            || backup.RadarrInstances.Any(i => !string.IsNullOrEmpty(i.ApiKey))
            || backup.SonarrInstances.Any(i => !string.IsNullOrEmpty(i.ApiKey));

        _pluginLog.LogInfo(
            LogSource,
            $"Backup created: timeline={backup.GrowthTimeline != null}, baseline={backup.GrowthBaseline != null}",
            _logger);
        return backup;
    }

    /// <summary>
    ///     Restores backup data into the plugin configuration and data files. Must be called only after Validate returns a valid result.
    /// </summary>
    /// <param name="backup">The validated backup data.</param>
    /// <returns>A summary of what was restored.</returns>
    public BackupRestoreSummary RestoreBackup(BackupData backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var summary = new BackupRestoreSummary();

        _pluginLog.LogInfo(LogSource, "Starting backup restore...", _logger);

        // Write data files FIRST so that if a file-write fails,
        // the live configuration has not yet been replaced.  Only after all
        // I/O completes do we commit the new configuration to disk.

        // Snapshot the paths of files that will be overwritten so that, if the restore only partially succeeds, an operator can identify which files may be in an inconsistent state.
        var timelinePath = Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json");
        var baselinePath = Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json");

        var timelineWriteOk = false;
        var baselineWriteOk = false;

        // Hold the timeline gate across both writes so a concurrent scan cannot clobber the
        // just-restored files, and so the merge below reads a stable current series.
        using var gate = AcquireTimelineGate();
        try
        {
            // Restore growth timeline. The incoming timeline is day-based (coarse ones were dropped
            // during sanitize). Merge it into the current on-disk series so restoring an older backup
            // fills history in retroactively instead of discarding newer days; higher cumulative wins
            // on any overlapping day.
            if (backup.GrowthTimeline != null)
            {
                var merged = MergeWithCurrentTimeline(timelinePath, backup.GrowthTimeline);
                if (SaveJsonFile(timelinePath, merged))
                {
                    timelineWriteOk = true;
                    summary.TimelineRestored = true;
                    _pluginLog.LogInfo(
                        LogSource,
                        $"Restored growth timeline ({merged.DataPoints.Count} data points after merge)",
                        _logger);
                }
                else
                {
                    // Stay failsafe: the backup carried timeline data but the write failed. Do not abort the
                    // restore. The next scheduled task run regenerates it. Log distinctly so the skipped
                    // write is not mistaken for "no timeline in the backup" (TimelineRestored stays false).
                    _pluginLog.LogWarning(
                        LogSource,
                        $"Backup restore: growth timeline was present but could not be written to [{timelinePath}]. It will be regenerated on the next scheduled run.",
                        logger: _logger);
                }
            }

            // Restore growth baseline
            if (backup.GrowthBaseline != null)
            {
                if (SaveJsonFile(baselinePath, backup.GrowthBaseline))
                {
                    baselineWriteOk = true;
                    summary.BaselineRestored = true;
                    _pluginLog.LogInfo(
                        LogSource,
                        $"Restored growth baseline ({backup.GrowthBaseline.Directories.Count} directories)",
                        _logger);
                }
                else
                {
                    _pluginLog.LogWarning(
                        LogSource,
                        $"Backup restore: growth baseline was present but could not be written to [{baselinePath}]. It will be regenerated on the next scheduled run.",
                        logger: _logger);
                }
            }

            // Restore configuration last - uses ReadAndMutate so the entire
            // read-mutate-save sequence is atomic with respect to concurrent callers.
            RestoreConfiguration(backup, summary);
        }
        catch (Exception ex)
        {
            var anyWriteSucceeded = timelineWriteOk || baselineWriteOk;
            if (anyWriteSucceeded)
            {
                _pluginLog.LogWarning(
                    LogSource,
                    $"Restore partially applied. Manual recovery may be required. Check [{timelinePath}] and [{baselinePath}] files.",
                    ex,
                    _logger);
            }

            throw;
        }

        _pluginLog.LogInfo(
            LogSource,
            $"Backup restore complete. Config={summary.ConfigurationRestored}, Timeline={summary.TimelineRestored}, Baseline={summary.BaselineRestored}",
            _logger);
        return summary;
    }

    /// <summary>
    ///     Serializes backup data to a JSON string.
    /// </summary>
    /// <param name="backup">The backup data.</param>
    /// <returns>The JSON string.</returns>
    public static string SerializeBackup(BackupData backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return JsonSerializer.Serialize(backup, JsonOptions);
    }

    /// <summary>
    ///     Deserializes a JSON string to backup data.
    ///     Returns null if the JSON is invalid.
    /// </summary>
    /// <param name="json">The JSON string.</param>
    /// <param name="logger">Optional logger for diagnostic output on parse failure.</param>
    /// <returns>The backup data, or null if deserialization fails.</returns>
    public static BackupData? DeserializeBackup(string json, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupData>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Failed to deserialize backup JSON.");
            return null;
        }
    }

    private void RestoreConfiguration(BackupData backup, BackupRestoreSummary summary)
    {
        if (!_configService.IsInitialized)
        {
            _pluginLog.LogWarning(
                LogSource,
                "Plugin instance not available, skipping configuration restore.",
                logger: _logger);
            return;
        }

        // Use ReadAndMutate so the entire read-mutate-save sequence runs under a lock, preventing a concurrent UpdateConfigurationAsync or UpdateLogLevel call from interleaving mutations on the same config object.
        _configService.ReadAndMutate(config =>
        {
            // Restore preferences
            config.Language = BackupValidator.ValidLanguages.Contains(backup.Language) ? backup.Language : "en";
            config.ExcludedLibraries = backup.ExcludedLibraries;
            config.OrphanMinAgeDays = Math.Clamp(backup.OrphanMinAgeDays, 0, BackupValidator.MaxRetentionDays);
            config.PluginLogLevel = BackupValidator.ValidLogLevels.Contains(backup.PluginLogLevel)
                ? backup.PluginLogLevel
                : "INFO";

            // Task modes
            config.TrickplayTaskMode = ParseTaskMode(backup.TrickplayTaskMode, nameof(config.TrickplayTaskMode));
            config.EmptyMediaFolderTaskMode = ParseTaskMode(backup.EmptyMediaFolderTaskMode, nameof(config.EmptyMediaFolderTaskMode));
            config.OrphanedSubtitleTaskMode = ParseTaskMode(backup.OrphanedSubtitleTaskMode, nameof(config.OrphanedSubtitleTaskMode));
            config.LinkRepairTaskMode = ParseTaskMode(backup.LinkRepairTaskMode, nameof(config.LinkRepairTaskMode));
            config.SeerrCleanupTaskMode = ParseTaskMode(backup.SeerrCleanupTaskMode, nameof(config.SeerrCleanupTaskMode), TaskMode.Deactivate);

            // Seerr settings
            RestoreSeerrSettings(config, backup, summary);

            // Trakt settings
            RestoreTraktSettings(config, backup, summary);

            // Trash settings
            RestoreTrashSettings(config, backup);

            // Smart Recommendations (only task mode - count and strategy use sensible defaults).
            // Default to DryRun so importing an older backup enables the Discover UI in read-only mode.
            config.RecommendationsTaskMode = ParseTaskMode(backup.RecommendationsTaskMode, nameof(config.RecommendationsTaskMode));

            // Playlist sync toggle - defaults to false for older backups without this field
            config.SyncRecommendationsToPlaylist = backup.SyncRecommendationsToPlaylist;

            // Discovery user access - defaults to false for older backups without this field
            config.DiscoveryUserAccessEnabled = backup.DiscoveryUserAccessEnabled;

            // Arr instances - preserve live keys when the backup omitted them, and flag any
            // credential replacements via CredentialsChanged on the summary.
            RestoreArrInstances(backup.RadarrInstances, config.RadarrInstances, "Radarr", summary);
            RestoreArrInstances(backup.SonarrInstances, config.SonarrInstances, "Sonarr", summary);

            // Set only after the entire mutation has been applied; if ReadAndMutate throws,
            // this flag stays false so the caller does not falsely report success.
            summary.ConfigurationRestored = true;
        });

        _pluginLog.LogInfo(LogSource, "Configuration restored from backup.", _logger);
    }

    /// <summary>
    ///     Restores the Seerr URL and API key from the backup into the live config.
    /// </summary>
    /// <param name="config">The live configuration being mutated.</param>
    /// <param name="backup">The backup data being restored.</param>
    /// <param name="summary">The restore summary to flag credential changes on.</param>
    private void RestoreSeerrSettings(PluginConfiguration config, BackupData backup, BackupRestoreSummary summary)
    {
        // An empty backup URL means "leave the existing URL in place", mirroring the API key guard below - a backup created without Seerr must not silently wipe a working URL.
        if (!string.IsNullOrEmpty(backup.SeerrUrl))
        {
            var truncatedUrl = BackupSanitizer.TruncateString(backup.SeerrUrl, BackupValidator.MaxUrlLength);
            if (Uri.TryCreate(truncatedUrl, UriKind.Absolute, out var parsedUrl)
                && (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps))
            {
                config.SeerrUrl = truncatedUrl;
            }
            else
            {
                _pluginLog.LogWarning(
                    LogSource,
                    $"Backup SeerrUrl '{truncatedUrl}' is not a valid http/https URL - skipping to avoid persisting an unsafe scheme.",
                    logger: _logger);
            }
        }

        // API keys: an empty backup value means "leave the existing key in place"; a non-empty value is applied after the same length-truncation as other fields.
        if (!string.IsNullOrEmpty(backup.SeerrApiKey))
        {
            var truncatedSeerrKey = BackupSanitizer.TruncateString(backup.SeerrApiKey, BackupValidator.MaxApiKeyLength);

            // The backup holds plaintext; compare against the decrypted stored key so an unchanged key is
            // not misreported as a credential change just because the stored form is ciphertext.
            var storedPlainKey = _secretProtector.Unprotect(config.SeerrApiKey);
            var truncatedStoredKey = BackupSanitizer.TruncateString(storedPlainKey, BackupValidator.MaxApiKeyLength);
            if (truncatedSeerrKey != truncatedStoredKey)
            {
                _pluginLog.LogWarning(
                    LogSource,
                    "Backup restore is replacing credentials: Seerr API key changed.",
                    logger: _logger);
                summary.CredentialsChanged = true;
            }

            // Re-encrypt before persisting so the restored key matches the at-rest format.
            config.SeerrApiKey = _secretProtector.Protect(truncatedSeerrKey);
        }

        // Null means "absent in backup" (older plugin version or field omitted), so leave the live value unchanged.
        if (backup.SeerrCleanupAgeDays.HasValue)
        {
            config.SeerrCleanupAgeDays = Math.Clamp(
                backup.SeerrCleanupAgeDays.Value,
                0,
                BackupValidator.MaxRetentionDays);
        }

        // Same absent-guard as above: an old backup without the field must not silently
        // re-enable certificate validation on a working private-CA setup.
        if (backup.SeerrSkipCertificateValidation.HasValue)
        {
            if (backup.SeerrSkipCertificateValidation.Value && !config.SeerrSkipCertificateValidation)
            {
                _pluginLog.LogWarning(
                    LogSource,
                    "Backup restore is enabling TLS certificate validation bypass for Seerr. Verify this is intended.",
                    logger: _logger);
            }

            config.SeerrSkipCertificateValidation = backup.SeerrSkipCertificateValidation.Value;
        }
    }

    /// <summary>
    ///     Restores Trakt flag, client id, and client secret. Empty backup values preserve the live ones so older
    ///     backups cannot wipe a working setup; a changed secret is re-encrypted and flagged. The flag also
    ///     requires stored credentials, and an explicit disable is honored - it never re-enables by itself.
    /// </summary>
    /// <param name="config">The live configuration being mutated.</param>
    /// <param name="backup">The backup data being restored.</param>
    /// <param name="summary">The restore summary, flagged when the secret changes.</param>
    private void RestoreTraktSettings(PluginConfiguration config, BackupData backup, BackupRestoreSummary summary)
    {
        if (!string.IsNullOrEmpty(backup.TraktClientId))
        {
            config.TraktClientId = BackupSanitizer.TruncateString(backup.TraktClientId, BackupValidator.MaxTraktClientIdLength);
        }

        if (!string.IsNullOrEmpty(backup.TraktClientSecret))
        {
            var truncatedSecret = BackupSanitizer.TruncateString(backup.TraktClientSecret, BackupValidator.MaxStringLength);
            var storedPlain = _secretProtector.Unprotect(config.TraktClientSecret);
            var truncatedStored = BackupSanitizer.TruncateString(storedPlain, BackupValidator.MaxStringLength);
            if (truncatedSecret != truncatedStored)
            {
                _pluginLog.LogWarning(LogSource, "Backup restore is replacing credentials: Trakt client secret changed.", logger: _logger);
                summary.CredentialsChanged = true;
            }

            config.TraktClientSecret = _secretProtector.Protect(truncatedSecret);
        }

        config.TraktEnabled = backup.TraktEnabled
            && !string.IsNullOrWhiteSpace(config.TraktClientId)
            && !string.IsNullOrWhiteSpace(config.TraktClientSecret);
    }

    /// <summary>
    ///     Restores the trash toggle, folder path, and retention days from the backup into the live config, defanging an unsafe trash path (traversal or sensitive system path) to the default.
    /// </summary>
    /// <param name="config">The live configuration being mutated.</param>
    /// <param name="backup">The backup data being restored.</param>
    private static void RestoreTrashSettings(PluginConfiguration config, BackupData backup)
    {
        config.UseTrash = backup.UseTrash;
        // Defang unsafe trash path to default instead of failing restore. Reuses the
        // settings-save guard so control characters and traversal are handled identically.
        var rawTrashPath = backup.TrashFolderPath;
        var strictError = Api.ConfigurationRequestValidator.ValidateTrashPathStrict(rawTrashPath, backup.UseTrash);
        var isSensitive = !string.IsNullOrWhiteSpace(rawTrashPath) &&
            PathValidator.IsSensitiveSystemPath(rawTrashPath);
        config.TrashFolderPath = string.IsNullOrWhiteSpace(rawTrashPath) || strictError != null || isSensitive
            ? ".jellyfin-trash"
            : rawTrashPath;
        config.TrashRetentionDays = Math.Clamp(backup.TrashRetentionDays, 0, BackupValidator.MaxRetentionDays);
    }

    /// <summary>
    ///     Restores a single Arr instance list (Radarr or Sonarr) from backup into the live config list.
    /// </summary>
    private void RestoreArrInstances(
        IReadOnlyList<BackupArrInstance> backupInstances,
        List<ArrInstanceConfig> liveInstances,
        string label,
        BackupRestoreSummary summary)
    {
        // Snapshot existing keys DECRYPTED (truncated to MaxApiKeyLength for apples-to-apples comparison with
        // the plaintext backup). Case-INSENSITIVE name lookup: the "empty backup key means preserve the live
        // key" rule keys off the instance Name, so a case-only rename between export and import (e.g.
        var previousKeys = liveInstances
            .ToLookup(
                i => i.Name,
                i => BackupSanitizer.TruncateString(_secretProtector.Unprotect(i.ApiKey), BackupValidator.MaxApiKeyLength),
                StringComparer.OrdinalIgnoreCase);

        var newList = new List<ArrInstanceConfig>();
        var keysChanged = 0;
        var silentWipes = 0;

        // Name (case-insensitive, mirroring the key-preserve rule above) + Url (exact) -> live skip-cert
        // value, so a backup that omits the field (older format) falls back to the live setting instead of
        // forcing validation back on. The composite key is lower-cased on the name only.
        var liveSkipCert = liveInstances.ToLookup(
            i => LiveInstanceKey(i.Name, i.Url),
            i => i.SkipCertificateValidation,
            StringComparer.Ordinal);

        foreach (var instance in backupInstances.Take(BackupValidator.MaxArrInstances))
        {
            // An empty backup key means "preserve the live key" - fall back to the previously
            // stored key for this instance name, consistent with the SeerrApiKey guard above.
            var backupKeyEmpty = string.IsNullOrEmpty(instance.ApiKey);
            var apiKey = backupKeyEmpty
                ? (previousKeys[instance.Name].FirstOrDefault() ?? string.Empty)
                : BackupSanitizer.TruncateString(instance.ApiKey, BackupValidator.MaxApiKeyLength);

            // "Preserve the live key" that matched nothing -> the instance ends up with an empty key. Surface it instead of letting the wipe pass silently (the keysChanged audit below is gated on a NON-empty key, so it never reports this case).
            if (backupKeyEmpty && string.IsNullOrEmpty(apiKey))
            {
                silentWipes++;
            }

            // Detect credential change: non-empty incoming key not found in any prior entry for this name. Both sides are truncated to the same length so a key that was stored full-length but backed up at MaxApiKeyLength is not a false positive.
            if (!string.IsNullOrEmpty(apiKey) && !previousKeys[instance.Name].Any(k => k == apiKey))
            {
                keysChanged++;
            }

            newList.Add(
                new ArrInstanceConfig
                {
                    Name = BackupSanitizer.TruncateString(instance.Name, BackupValidator.MaxInstanceNameLength),
                    Url = BackupSanitizer.TruncateString(instance.Url, BackupValidator.MaxUrlLength),

                    // Re-encrypt before persisting so the restored key matches the at-rest format. Empty stays empty.
                    ApiKey = _secretProtector.Protect(apiKey),
                    Libraries = BackupSanitizer.TruncateString(instance.Libraries, BackupValidator.MaxArrLibrariesLength),

                    // Absent in the backup -> keep the matching live instance's setting; an explicit value wins.
                    SkipCertificateValidation = instance.SkipCertificateValidation
                        ?? liveSkipCert[LiveInstanceKey(instance.Name, instance.Url)].FirstOrDefault()
                });
        }

        liveInstances.Clear();
        liveInstances.AddRange(newList);

        // Only count instances the restore newly flips ON: live value was false or absent, now bypassed.
        // An unchanged restore of an already-bypassed instance must not log a false "now enabling" warning.
        var newlyEnabled = newList.Count(i =>
            i.SkipCertificateValidation
            && !liveSkipCert[LiveInstanceKey(i.Name, i.Url)].FirstOrDefault());
        if (newlyEnabled > 0)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Backup restore is enabling TLS certificate validation bypass for {newlyEnabled} {label} instance(s). Verify this is intended.",
                logger: _logger);
        }

        if (silentWipes > 0)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"{label}: {silentWipes} instance(s) had an empty backup API key with no matching live key to preserve. "
                + "Their API key is now empty and must be re-entered.",
                logger: _logger);
        }

        if (keysChanged > 0)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Backup restore is replacing credentials: {keysChanged} {label} instance API key(s) changed.",
                logger: _logger);
            summary.CredentialsChanged = true;
        }
    }

    // Composite lookup key matching an Arr instance by case-insensitive Name and exact Url. The name is
    // lower-cased with the invariant culture and joined with a NUL so distinct name/url splits cannot collide.
    private static string LiveInstanceKey(string name, string url) =>
        (name ?? string.Empty).ToLowerInvariant() + "\0" + (url ?? string.Empty);

    private TaskMode ParseTaskMode(string? value, string fieldName, TaskMode fallback = TaskMode.DryRun)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (Enum.TryParse<TaskMode>(value, true, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        // A non-empty value that fails to parse is malformed backup data, not a legitimate default;
        // surface it so the admin knows the restored mode differs from what the file claimed.
        _pluginLog.LogWarning(
            LogSource,
            $"Backup restore: {fieldName} value '{value}' is not a valid task mode. Falling back to {fallback}.",
            logger: _logger);
        return fallback;
    }

    /// <summary>
    ///     Merges an incoming day-based timeline with the current on-disk series so a restore fills
    ///     history in retroactively. The current file is read directly (the caller already holds the
    ///     timeline gate). A missing or non-day-based current file is ignored, leaving the incoming
    ///     series as the result. On an overlapping day the whole point with the higher cumulative
    ///     size wins (TimelineAggregator.MergeDailySeries), the single merge rule shared with scans.
    /// </summary>
    /// <param name="timelinePath">The on-disk timeline path.</param>
    /// <param name="incoming">The sanitized day-based timeline from the backup.</param>
    /// <returns>The merged timeline to persist.</returns>
    private GrowthTimelineResult MergeWithCurrentTimeline(string timelinePath, GrowthTimelineResult incoming)
    {
        var current = LoadJsonFile<GrowthTimelineResult>(timelinePath, out _);
        if (current is null || !TimelineAggregator.IsDayBased(current))
        {
            return incoming;
        }

        var mergedPoints = TimelineAggregator.MergeDailySeries(current.DataPoints, incoming.DataPoints);

        // The sanitizer caps the incoming series, but merging it with the current on-disk series can
        // still exceed the cap when the two barely overlap. Re-apply the same retention (earliest plus
        // newest cap-1) so the persisted file and the chart never carry more than the client can render.
        mergedPoints = TimelineAggregator.TrimToCap(mergedPoints, BackupValidator.MaxTimelineDataPoints);

        var result = new GrowthTimelineResult
        {
            Granularity = "daily",
            ComputedAt = DateTime.UtcNow,
            EarliestFileDate = mergedPoints.Count > 0 ? mergedPoints[0].Date : incoming.EarliestFileDate,
            // The current on-disk scan reflects the newer directory count; prefer it over the backup's.
            TotalDirectoriesScanned = current.TotalDirectoriesScanned > 0 ? current.TotalDirectoriesScanned : incoming.TotalDirectoriesScanned,
            FirstScanTimestamp = EarliestFirstScan(current.FirstScanTimestamp, incoming.FirstScanTimestamp)
        };
        foreach (var point in mergedPoints)
        {
            result.DataPoints.Add(point);
        }

        return result;
    }

    /// <summary>
    ///     Returns the earlier of two optional first-scan timestamps, preferring whichever is set.
    /// </summary>
    private static DateTime? EarliestFirstScan(DateTime? a, DateTime? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return a.Value <= b.Value ? a : b;
    }

    private T? LoadJsonFile<T>(string filePath, out bool oversized)
        where T : class
    {
        oversized = false;
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxBackupSizeBytes)
            {
                oversized = true;
                _pluginLog.LogWarning(
                    LogSource,
                    $"Skipping {filePath} for backup: file size {fileInfo.Length} bytes exceeds {MaxBackupSizeBytes} byte limit.",
                    logger: _logger);
                return null;
            }

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(LogSource, $"Could not load {filePath} for backup", ex, _logger);
            return null;
        }
    }

    private bool SaveJsonFile<T>(string filePath, T data)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, JsonOptions);

            // Use AtomicFile so a transient sharing violation on the final File.Move (typical when an AV scanner or the Search indexer briefly holds the file handle) gets a bounded retry with backoff.
            AtomicFile.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _pluginLog.LogError(LogSource, $"Could not save {filePath} during restore", ex, _logger);
            return false;
        }
    }

    /// <summary>
    ///     A do-nothing disposable used when no timeline service is available to gate access.
    /// </summary>
    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
