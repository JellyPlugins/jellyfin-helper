using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    private List<ArrInstanceConfig> _radarrInstances = [];
    private List<ArrInstanceConfig> _sonarrInstances = [];

    /// <summary>
    /// Gets or sets the library names to include (whitelist). Empty means all libraries are included.
    /// Comma-separated list of library names.
    /// </summary>
    public string IncludedLibraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library names to exclude (blacklist).
    /// Comma-separated list of library names.
    /// </summary>
    public string ExcludedLibraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the minimum age in days an orphaned item must have before it is eligible for deletion.
    /// This protects against race conditions with active downloads. Default is 0 (immediate).
    /// </summary>
    public int OrphanMinAgeDays { get; set; }

    // ===== New TaskMode properties (replace old booleans) =====

    /// <summary>
    /// Gets or sets the execution mode for the Trickplay Folder Cleaner task.
    /// Default is <see cref="TaskMode.DryRun"/> (safe mode).
    /// </summary>
    public TaskMode TrickplayTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    /// Gets or sets the execution mode for the Empty Media Folder Cleaner task.
    /// Default is <see cref="TaskMode.DryRun"/> (safe mode).
    /// </summary>
    public TaskMode EmptyMediaFolderTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    /// Gets or sets the execution mode for the Orphaned Subtitle Cleaner task.
    /// Default is <see cref="TaskMode.DryRun"/> (safe mode).
    /// </summary>
    public TaskMode OrphanedSubtitleTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    /// Gets or sets the execution mode for the .strm File Repair task.
    /// Default is <see cref="TaskMode.DryRun"/> (safe mode).
    /// </summary>
    public TaskMode StrmRepairTaskMode { get; set; } = TaskMode.DryRun;

    // ===== Legacy boolean properties (kept for backward compatibility / config migration) =====

    /// <summary>
    /// Gets or sets a value indicating whether the Trickplay Folder Cleaner task runs in dry-run mode.
    /// Legacy property — use <see cref="TrickplayTaskMode"/> instead.
    /// </summary>
    public bool DryRunTrickplay { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Empty Media Folder Cleaner task runs in dry-run mode.
    /// Legacy property — use <see cref="EmptyMediaFolderTaskMode"/> instead.
    /// </summary>
    public bool DryRunEmptyMediaFolders { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Orphaned Subtitle Cleaner task runs in dry-run mode.
    /// Legacy property — use <see cref="OrphanedSubtitleTaskMode"/> instead.
    /// </summary>
    public bool DryRunOrphanedSubtitles { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the orphaned subtitle cleaner is enabled.
    /// Legacy property — use <see cref="OrphanedSubtitleTaskMode"/> instead.
    /// </summary>
    public bool EnableSubtitleCleaner { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the .strm file repair task is enabled.
    /// Legacy property — use <see cref="StrmRepairTaskMode"/> instead.
    /// </summary>
    public bool EnableStrmRepair { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the .strm repair task runs in dry-run mode.
    /// Legacy property — use <see cref="StrmRepairTaskMode"/> instead.
    /// </summary>
    public bool StrmRepairDryRun { get; set; } = true;

    // ===== Config version for migration =====

    /// <summary>
    /// Gets or sets the configuration version. Used to detect and apply one-time migrations
    /// from legacy boolean properties to the new <see cref="TaskMode"/> properties.
    /// 0 = pre-TaskMode (needs migration), 1 = TaskMode migrated.
    /// </summary>
    public int ConfigVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use a trash folder instead of permanently deleting files.
    /// </summary>
    public bool UseTrash { get; set; }

    /// <summary>
    /// Gets or sets the path to the trash folder. Defaults to ".jellyfin-trash" inside the library root.
    /// </summary>
    public string TrashFolderPath { get; set; } = ".jellyfin-trash";

    /// <summary>
    /// Gets or sets the number of days to keep items in the trash before permanent deletion.
    /// Default is 30 days.
    /// </summary>
    public int TrashRetentionDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the Radarr API URL (e.g., http://localhost:7878).
    /// Kept for backwards compatibility — migrated to <see cref="RadarrInstances"/> on first load.
    /// </summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Radarr API key.
    /// Kept for backwards compatibility — migrated to <see cref="RadarrInstances"/> on first load.
    /// </summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr API URL (e.g., http://localhost:8989).
    /// Kept for backwards compatibility — migrated to <see cref="SonarrInstances"/> on first load.
    /// </summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr API key.
    /// Kept for backwards compatibility — migrated to <see cref="SonarrInstances"/> on first load.
    /// </summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of Radarr instances (max 3).
    /// Using <see cref="List{T}"/> instead of <c>Collection&lt;T&gt;</c> because
    /// <c>System.Text.Json</c> cannot reliably round-trip <c>Collection&lt;T&gt;</c>
    /// (items are lost on deserialization when the property has a default initializer).
    /// The setter coalesces null to an empty list to prevent NullReferenceException
    /// when JSON deserialization provides an explicit null value.
    /// </summary>
    [SuppressMessage("Usage", "CA1002:DoNotExposeGenericLists", Justification = "Collection<T> breaks System.Text.Json round-trip deserialization")]
    public List<ArrInstanceConfig> RadarrInstances
    {
        get => _radarrInstances;
        set => _radarrInstances = value ?? [];
    }

    /// <summary>
    /// Gets or sets the list of Sonarr instances (max 3).
    /// Using <see cref="List{T}"/> instead of <c>Collection&lt;T&gt;</c> because
    /// <c>System.Text.Json</c> cannot reliably round-trip <c>Collection&lt;T&gt;</c>
    /// (items are lost on deserialization when the property has a default initializer).
    /// The setter coalesces null to an empty list to prevent NullReferenceException
    /// when JSON deserialization provides an explicit null value.
    /// </summary>
    [SuppressMessage("Usage", "CA1002:DoNotExposeGenericLists", Justification = "Collection<T> breaks System.Text.Json round-trip deserialization")]
    public List<ArrInstanceConfig> SonarrInstances
    {
        get => _sonarrInstances;
        set => _sonarrInstances = value ?? [];
    }

    /// <summary>
    /// Gets or sets the UI language code. Default is "en".
    /// Supported: en, de, fr, es, pt, zh, tr.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Gets or sets the minimum log level for the plugin's in-memory log buffer.
    /// Supported values: DEBUG, INFO, WARN, ERROR. Default is "INFO".
    /// </summary>
    public string PluginLogLevel { get; set; } = "INFO";

    /// <summary>
    /// Gets or sets the total bytes freed by all cleanup operations since the plugin was installed.
    /// This value is persisted and accumulated across runs.
    /// </summary>
    public long TotalBytesFreed { get; set; }

    /// <summary>
    /// Gets or sets the total number of items deleted by all cleanup operations since the plugin was installed.
    /// </summary>
    public int TotalItemsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last cleanup run.
    /// </summary>
    public DateTime LastCleanupTimestamp { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Migrates legacy boolean configuration properties to the new <see cref="TaskMode"/> properties.
    /// This is called once when <see cref="ConfigVersion"/> is 0 (pre-TaskMode config).
    /// After migration, <see cref="ConfigVersion"/> is set to 1 so migration does not run again.
    /// </summary>
    public void MigrateFromLegacyBooleans()
    {
        if (ConfigVersion >= 1)
        {
            return;
        }

        // Detect whether this is a genuine legacy config or a fresh install.
        // Fresh installs have all legacy booleans at their defaults:
        //   DryRunTrickplay=true, DryRunEmptyMediaFolders=true, DryRunOrphanedSubtitles=true,
        //   EnableSubtitleCleaner=true, EnableStrmRepair=false, StrmRepairDryRun=true
        // In that case, the TaskMode defaults (all DryRun) are already correct — just bump the version.
        bool isFreshConfig = DryRunTrickplay
                             && DryRunEmptyMediaFolders
                             && DryRunOrphanedSubtitles
                             && EnableSubtitleCleaner
                             && !EnableStrmRepair
                             && StrmRepairDryRun;

        if (isFreshConfig)
        {
            // All TaskMode defaults are already DryRun — nothing to migrate.
            ConfigVersion = 1;
            return;
        }

        // Trickplay: no enable toggle existed, only dry-run
        TrickplayTaskMode = DryRunTrickplay ? TaskMode.DryRun : TaskMode.Activate;

        // Empty media folders: no enable toggle existed, only dry-run
        EmptyMediaFolderTaskMode = DryRunEmptyMediaFolders ? TaskMode.DryRun : TaskMode.Activate;

        // Orphaned subtitles: had both enable toggle and dry-run
        if (!EnableSubtitleCleaner)
        {
            OrphanedSubtitleTaskMode = TaskMode.Deactivate;
        }
        else
        {
            OrphanedSubtitleTaskMode = DryRunOrphanedSubtitles ? TaskMode.DryRun : TaskMode.Activate;
        }

        // Strm repair: had both enable toggle and dry-run
        if (!EnableStrmRepair)
        {
            StrmRepairTaskMode = TaskMode.Deactivate;
        }
        else
        {
            StrmRepairTaskMode = StrmRepairDryRun ? TaskMode.DryRun : TaskMode.Activate;
        }

        ConfigVersion = 1;
    }

    /// <summary>
    /// Migrates legacy single-instance Radarr/Sonarr settings to the new multi-instance lists
    /// and returns the effective list of configured Radarr instances (max 3).
    /// </summary>
    /// <returns>A read-only list of configured Radarr instances.</returns>
    public IReadOnlyList<ArrInstanceConfig> GetEffectiveRadarrInstances()
    {
        // Migrate legacy single fields if the collection is empty
        if (RadarrInstances.Count == 0 &&
            !string.IsNullOrWhiteSpace(RadarrUrl) &&
            !string.IsNullOrWhiteSpace(RadarrApiKey))
        {
            RadarrInstances.Add(new ArrInstanceConfig
            {
                Name = "Radarr",
                Url = RadarrUrl,
                ApiKey = RadarrApiKey,
            });
        }

        return RadarrInstances.Count > 3
            ? RadarrInstances.Take(3).ToList().AsReadOnly()
            : RadarrInstances.ToList().AsReadOnly();
    }

    /// <summary>
    /// Migrates legacy single-instance Sonarr settings to the new multi-instance lists
    /// and returns the effective list of configured Sonarr instances (max 3).
    /// </summary>
    /// <returns>A read-only list of configured Sonarr instances.</returns>
    public IReadOnlyList<ArrInstanceConfig> GetEffectiveSonarrInstances()
    {
        // Migrate legacy single fields if the collection is empty
        if (SonarrInstances.Count == 0 &&
            !string.IsNullOrWhiteSpace(SonarrUrl) &&
            !string.IsNullOrWhiteSpace(SonarrApiKey))
        {
            SonarrInstances.Add(new ArrInstanceConfig
            {
                Name = "Sonarr",
                Url = SonarrUrl,
                ApiKey = SonarrApiKey,
            });
        }

        return SonarrInstances.Count > 3
            ? SonarrInstances.Take(3).ToList().AsReadOnly()
            : SonarrInstances.ToList().AsReadOnly();
    }
}
