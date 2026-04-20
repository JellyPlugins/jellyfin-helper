using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
///     Represents the complete backup payload for export and import.
///     Contains configuration preferences, historical trend data, and Arr integration settings.
///     Cleanup statistics are intentionally excluded (reset on fresh install).
/// </summary>
public class BackupData
{
    /// <summary>
    ///     Gets or sets the backup format version. Used for future compatibility checks.
    /// </summary>
    [JsonPropertyName("backupVersion")]
    public int BackupVersion { get; set; } = 1;

    /// <summary>
    ///     Gets or sets the UTC timestamp when this backup was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    ///     Gets the plugin version that created this backup.
    /// </summary>
    [JsonPropertyName("pluginVersion")]
    public string PluginVersion { get; init; } = string.Empty;

    // === Configuration Preferences ===

    /// <summary>
    ///     Gets or sets the UI language code (e.g. "en", "de").
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    /// <summary>
    ///     Gets or sets the included libraries allow list (comma-separated).
    /// </summary>
    [JsonPropertyName("includedLibraries")]
    public string IncludedLibraries { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the excluded libraries exclude list (comma-separated).
    /// </summary>
    [JsonPropertyName("excludedLibraries")]
    public string ExcludedLibraries { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the orphan minimum age in days.
    /// </summary>
    [JsonPropertyName("orphanMinAgeDays")]
    public int OrphanMinAgeDays { get; set; }

    /// <summary>
    ///     Gets or sets the plugin log level.
    /// </summary>
    [JsonPropertyName("pluginLogLevel")]
    public string PluginLogLevel { get; set; } = "INFO";

    // === Task Mode Preferences ===

    /// <summary>
    ///     Gets or sets the Trickplay task mode.
    /// </summary>
    [JsonPropertyName("trickplayTaskMode")]
    public string TrickplayTaskMode { get; set; } = "DryRun";

    /// <summary>
    ///     Gets or sets the Empty Media Folder task mode.
    /// </summary>
    [JsonPropertyName("emptyMediaFolderTaskMode")]
    public string EmptyMediaFolderTaskMode { get; set; } = "DryRun";

    /// <summary>
    ///     Gets or sets the Orphaned Subtitle task mode.
    /// </summary>
    [JsonPropertyName("orphanedSubtitleTaskMode")]
    public string OrphanedSubtitleTaskMode { get; set; } = "DryRun";

    /// <summary>
    ///     Gets or sets the Link Repair task mode.
    /// </summary>
    [JsonPropertyName("linkRepairTaskMode")]
    public string LinkRepairTaskMode { get; set; } = "DryRun";

    // === Trash Settings ===

    /// <summary>
    ///     Gets a value indicating whether trash is enabled.
    /// </summary>
    [JsonPropertyName("useTrash")]
    public bool UseTrash { get; init; }

    /// <summary>
    ///     Gets or sets the trash folder path.
    /// </summary>
    [JsonPropertyName("trashFolderPath")]
    public string TrashFolderPath { get; set; } = ".jellyfin-trash";

    /// <summary>
    ///     Gets or sets the trash retention days.
    /// </summary>
    [JsonPropertyName("trashRetentionDays")]
    public int TrashRetentionDays { get; set; } = 30;

    // === Seerr Settings ===

    /// <summary>
    ///     Gets or sets the Seerr Cleanup task mode.
    /// </summary>
    [JsonPropertyName("seerrCleanupTaskMode")]
    public string SeerrCleanupTaskMode { get; set; } = "Deactivate";

    /// <summary>
    ///     Gets or sets the Seerr instance URL.
    /// </summary>
    [JsonPropertyName("seerrUrl")]
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the Seerr API key.
    /// </summary>
    [JsonPropertyName("seerrApiKey")]
    public string SeerrApiKey { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the Seerr cleanup age threshold in days.
    /// </summary>
    [JsonPropertyName("seerrCleanupAgeDays")]
    public int SeerrCleanupAgeDays { get; set; } = 365;

    // === Arr Integration ===

    /// <summary>
    ///     Gets the Radarr instances.
    /// </summary>
    [JsonPropertyName("radarrInstances")]
    [SuppressMessage(
        "Design",
        "CA1002:Do not expose generic lists",
        Justification = "Required for JSON deserialization")]
    public List<BackupArrInstance> RadarrInstances { get; init; } = [];

    /// <summary>
    ///     Gets the Sonarr instances.
    /// </summary>
    [JsonPropertyName("sonarrInstances")]
    [SuppressMessage(
        "Design",
        "CA1002:Do not expose generic lists",
        Justification = "Required for JSON deserialization")]
    public List<BackupArrInstance> SonarrInstances { get; init; } = [];

    // === Historical Data ===

    /// <summary>
    ///     Gets or sets the growth timeline result (trend graph data).
    /// </summary>
    [JsonPropertyName("growthTimeline")]
    public GrowthTimelineResult? GrowthTimeline { get; set; }

    /// <summary>
    ///     Gets or sets the growth timeline baseline (for diff-based tracking).
    /// </summary>
    [JsonPropertyName("growthBaseline")]
    public GrowthTimelineBaseline? GrowthBaseline { get; set; }
}