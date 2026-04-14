using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
/// Represents the complete backup payload for export and import.
/// Contains configuration preferences, historical trend data, and Arr integration settings.
/// Cleanup statistics are intentionally excluded (reset on fresh install).
/// </summary>
public class BackupData
{
    /// <summary>
    /// Gets or sets the backup format version. Used for future compatibility checks.
    /// </summary>
    [JsonPropertyName("backupVersion")]
    public int BackupVersion { get; set; } = 1;

    /// <summary>
    /// Gets or sets the UTC timestamp when this backup was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the plugin version that created this backup.
    /// </summary>
    [JsonPropertyName("pluginVersion")]
    public string PluginVersion { get; set; } = string.Empty;

    // === Configuration Preferences ===

    /// <summary>
    /// Gets or sets the UI language code (e.g. "en", "de").
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    /// <summary>
    /// Gets or sets the included libraries whitelist (comma-separated).
    /// </summary>
    [JsonPropertyName("includedLibraries")]
    public string IncludedLibraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the excluded libraries blacklist (comma-separated).
    /// </summary>
    [JsonPropertyName("excludedLibraries")]
    public string ExcludedLibraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the orphan minimum age in days.
    /// </summary>
    [JsonPropertyName("orphanMinAgeDays")]
    public int OrphanMinAgeDays { get; set; }

    /// <summary>
    /// Gets or sets the plugin log level.
    /// </summary>
    [JsonPropertyName("pluginLogLevel")]
    public string PluginLogLevel { get; set; } = "INFO";

    // === Task Mode Preferences ===

    /// <summary>
    /// Gets or sets the Trickplay task mode.
    /// </summary>
    [JsonPropertyName("trickplayTaskMode")]
    public string TrickplayTaskMode { get; set; } = "DryRun";

    /// <summary>
    /// Gets or sets the Empty Media Folder task mode.
    /// </summary>
    [JsonPropertyName("emptyMediaFolderTaskMode")]
    public string EmptyMediaFolderTaskMode { get; set; } = "DryRun";

    /// <summary>
    /// Gets or sets the Orphaned Subtitle task mode.
    /// </summary>
    [JsonPropertyName("orphanedSubtitleTaskMode")]
    public string OrphanedSubtitleTaskMode { get; set; } = "DryRun";

    /// <summary>
    /// Gets or sets the .strm Repair task mode.
    /// </summary>
    [JsonPropertyName("strmRepairTaskMode")]
    public string StrmRepairTaskMode { get; set; } = "DryRun";

    // === Trash Settings ===

    /// <summary>
    /// Gets or sets a value indicating whether trash is enabled.
    /// </summary>
    [JsonPropertyName("useTrash")]
    public bool UseTrash { get; set; }

    /// <summary>
    /// Gets or sets the trash folder path.
    /// </summary>
    [JsonPropertyName("trashFolderPath")]
    public string TrashFolderPath { get; set; } = ".jellyfin-trash";

    /// <summary>
    /// Gets or sets the trash retention days.
    /// </summary>
    [JsonPropertyName("trashRetentionDays")]
    public int TrashRetentionDays { get; set; } = 30;

    // === Arr Integration ===

    /// <summary>
    /// Gets or sets the Radarr instances.
    /// </summary>
    [JsonPropertyName("radarrInstances")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required for JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Required for JSON deserialization")]
    public List<BackupArrInstance> RadarrInstances { get; set; } = new();

    /// <summary>
    /// Gets or sets the Sonarr instances.
    /// </summary>
    [JsonPropertyName("sonarrInstances")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required for JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Required for JSON deserialization")]
    public List<BackupArrInstance> SonarrInstances { get; set; } = new();

    // === Historical Data ===

    /// <summary>
    /// Gets or sets the growth timeline result (trend graph data).
    /// </summary>
    [JsonPropertyName("growthTimeline")]
    public GrowthTimelineResult? GrowthTimeline { get; set; }

    /// <summary>
    /// Gets or sets the growth timeline baseline (for diff-based tracking).
    /// </summary>
    [JsonPropertyName("growthBaseline")]
    public GrowthTimelineBaseline? GrowthBaseline { get; set; }
}
