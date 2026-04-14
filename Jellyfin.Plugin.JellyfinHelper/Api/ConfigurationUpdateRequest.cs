using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// Request DTO for updating the plugin configuration via the API.
/// Uses arrays for Arr instances to avoid CA2227 while supporting JSON deserialization.
/// </summary>
public class ConfigurationUpdateRequest
{
    /// <summary>
    /// Gets or sets the library names to include (whitelist). Comma-separated.
    /// </summary>
    public string IncludedLibraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library names to exclude (blacklist). Comma-separated.
    /// </summary>
    public string ExcludedLibraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the minimum age in days an orphaned item must have before deletion.
    /// </summary>
    public int OrphanMinAgeDays { get; set; }

    /// <summary>
    /// Gets or sets the execution mode for the Trickplay Folder Cleaner task.
    /// </summary>
    public TaskMode TrickplayTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    /// Gets or sets the execution mode for the Empty Media Folder Cleaner task.
    /// </summary>
    public TaskMode EmptyMediaFolderTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    /// Gets or sets the execution mode for the Orphaned Subtitle Cleaner task.
    /// </summary>
    public TaskMode OrphanedSubtitleTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    /// Gets or sets the execution mode for the .strm File Repair task.
    /// </summary>
    public TaskMode StrmRepairTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    /// Gets or sets a value indicating whether to use a trash folder instead of permanently deleting files.
    /// </summary>
    public bool UseTrash { get; set; }

    /// <summary>
    /// Gets or sets the path to the trash folder.
    /// </summary>
    public string TrashFolderPath { get; set; } = ".jellyfin-trash";

    /// <summary>
    /// Gets or sets the number of days to keep items in the trash before permanent deletion.
    /// </summary>
    public int TrashRetentionDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the legacy Radarr URL (for backwards compatibility).
    /// </summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the legacy Radarr API key (for backwards compatibility).
    /// </summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the legacy Sonarr URL (for backwards compatibility).
    /// </summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the legacy Sonarr API key (for backwards compatibility).
    /// </summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Radarr instances (max 3).
    /// </summary>
    public IReadOnlyList<ArrInstanceConfig> RadarrInstances { get; set; } = [];

    /// <summary>
    /// Gets or sets the Sonarr instances (max 3).
    /// </summary>
    public IReadOnlyList<ArrInstanceConfig> SonarrInstances { get; set; } = [];

    /// <summary>
    /// Gets or sets the UI language code.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Gets or sets the plugin log level (e.g. DEBUG, INFO, WARN, ERROR).
    /// </summary>
    public string PluginLogLevel { get; set; } = "INFO";
}