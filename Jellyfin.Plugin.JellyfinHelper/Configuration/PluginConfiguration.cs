using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
///     Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    ///     Gets or sets the library names to include (allow list). Empty means all libraries are included.
    ///     Comma-separated list of library names.
    /// </summary>
    public string IncludedLibraries { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the library names to exclude (exclude list).
    ///     Comma-separated list of library names.
    /// </summary>
    public string ExcludedLibraries { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the minimum age in days an orphaned item must have before it is eligible for deletion.
    ///     This protects against race conditions with active downloads. Default is 0 (immediate).
    /// </summary>
    public int OrphanMinAgeDays { get; set; }

    // ===== New TaskMode properties (replace old booleans) =====

    /// <summary>
    ///     Gets or sets the execution mode for the Trickplay Folder Cleaner task.
    ///     Default is <see cref="TaskMode.DryRun" /> (safe mode).
    /// </summary>
    public TaskMode TrickplayTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets or sets the execution mode for the Empty Media Folder Cleaner task.
    ///     Default is <see cref="TaskMode.DryRun" /> (safe mode).
    /// </summary>
    public TaskMode EmptyMediaFolderTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets or sets the execution mode for the Orphaned Subtitle Cleaner task.
    ///     Default is <see cref="TaskMode.DryRun" /> (safe mode).
    /// </summary>
    public TaskMode OrphanedSubtitleTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets or sets the execution mode for the Link Repair task (.strm files and symlinks).
    ///     Default is <see cref="TaskMode.DryRun" /> (safe mode).
    /// </summary>
    public TaskMode LinkRepairTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets or sets the execution mode for the Seerr Cleanup task.
    ///     Default is <see cref="TaskMode.Deactivate" /> because this task interacts with an external service.
    /// </summary>
    public TaskMode SeerrCleanupTaskMode { get; set; } = TaskMode.Deactivate;

    /// <summary>
    ///     Gets or sets the maximum age in days for Seerr requests before they are cleaned up.
    ///     Default is 365 days (1 year).
    /// </summary>
    public int SeerrCleanupAgeDays { get; set; } = 365;

    /// <summary>
    ///     Gets or sets the base URL of the Jellyseerr/Overseerr/Seerr instance.
    /// </summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the API key for the Jellyseerr/Overseerr/Seerr instance.
    /// </summary>
    public string SeerrApiKey { get; set; } = string.Empty;

    // ===== Config version for migration =====

    /// <summary>
    ///     Gets or sets the configuration version for migration tracking.
    /// </summary>
    public int ConfigVersion { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether to use a trash folder instead of permanently deleting files.
    /// </summary>
    public bool UseTrash { get; set; }

    /// <summary>
    ///     Gets or sets the path to the trash folder. Defaults to ".jellyfin-trash" inside the library root.
    /// </summary>
    public string TrashFolderPath { get; set; } = ".jellyfin-trash";

    /// <summary>
    ///     Gets or sets the number of days to keep items in the trash before permanent deletion.
    ///     Default is 30 days.
    /// </summary>
    public int TrashRetentionDays { get; set; } = 30;

    /// <summary>
    ///     Gets the list of Radarr instances (max 3).
    ///     Using <see cref="List{T}" /> instead of <c>Collection&lt;T&gt;</c> because
    ///     <c>System.Text.Json</c> cannot reliably round-trip <c>Collection&lt;T&gt;</c>
    ///     (items are lost on deserialization when the property has a default initializer).
    ///     The setter coalesces null to an empty list to prevent NullReferenceException
    ///     when JSON deserialization provides an explicit null value.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA1002:DoNotExposeGenericLists",
        Justification = "Collection<T> breaks System.Text.Json round-trip deserialization")]
    public List<ArrInstanceConfig> RadarrInstances { get; init; } = [];

    /// <summary>
    ///     Gets the list of Sonarr instances (max 3).
    ///     Using <see cref="List{T}" /> instead of <c>Collection&lt;T&gt;</c> because
    ///     <c>System.Text.Json</c> cannot reliably round-trip <c>Collection&lt;T&gt;</c>
    ///     (items are lost on deserialization when the property has a default initializer).
    ///     The setter coalesces null to an empty list to prevent NullReferenceException
    ///     when JSON deserialization provides an explicit null value.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA1002:DoNotExposeGenericLists",
        Justification = "Collection<T> breaks System.Text.Json round-trip deserialization")]
    public List<ArrInstanceConfig> SonarrInstances { get; init; } = [];

    /// <summary>
    ///     Gets or sets the UI language code. Default is "en".
    ///     Supported: en, de, fr, es, pt, zh, tr.
    /// </summary>
    public string Language { get; set; } = "en";

    // ===== Smart Recommendations =====

    /// <summary>
    ///     Gets or sets the execution mode for the Smart Recommendations task.
    ///     Default is <see cref="TaskMode.DryRun" /> (safe mode — generates but does not persist).
    /// </summary>
    public TaskMode RecommendationsTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets or sets the scoring strategy for recommendations.
    ///     Valid values: "ensemble" (default), "heuristic", "learned".
    /// </summary>
    public string RecommendationStrategy { get; set; } = "ensemble";

    /// <summary>
    ///     Gets or sets the minimum alpha value for the ensemble scoring strategy.
    ///     Controls the lower bound of learned model blending (0–1). Default is 0.3.
    /// </summary>
    public double EnsembleAlphaMin { get; set; } = 0.3;

    /// <summary>
    ///     Gets or sets the maximum alpha value for the ensemble scoring strategy.
    ///     Controls the upper bound of learned model blending (0–1). Default is 0.8.
    /// </summary>
    public double EnsembleAlphaMax { get; set; } = 0.8;

    /// <summary>
    ///     Gets or sets the genre penalty floor for the ensemble scoring strategy.
    ///     Items with zero genre overlap are penalized down to this floor value. Default is 0.10.
    /// </summary>
    public double EnsembleGenrePenaltyFloor { get; set; } = 0.10;

    /// <summary>
    ///     Gets or sets the minimum log level for the plugin's in-memory log buffer.
    ///     Supported values: DEBUG, INFO, WARN, ERROR. Default is "INFO".
    /// </summary>
    public string PluginLogLevel { get; set; } = "INFO";

    /// <summary>
    ///     Gets or sets the total bytes freed by all cleanup operations since the plugin was installed.
    ///     This value is persisted and accumulated across runs.
    /// </summary>
    public long TotalBytesFreed { get; set; }

    /// <summary>
    ///     Gets or sets the total number of items deleted by all cleanup operations since the plugin was installed.
    /// </summary>
    public int TotalItemsDeleted { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp of the last cleanup run.
    /// </summary>
    public DateTime LastCleanupTimestamp { get; set; } = DateTime.MinValue;

    /// <summary>
    ///     Migrates legacy single-instance Radarr/Sonarr settings to the new multi-instance lists
    ///     and returns the effective list of configured Radarr instances (max 3).
    /// </summary>
    /// <returns>A read-only list of configured Radarr instances.</returns>
    public IReadOnlyList<ArrInstanceConfig> GetEffectiveRadarrInstances()
    {
        var effective = RadarrInstances
            .Where(i => !string.IsNullOrWhiteSpace(i.Url) && !string.IsNullOrWhiteSpace(i.ApiKey))
            .Take(3)
            .ToList();
        return effective.AsReadOnly();
    }

    /// <summary>
    ///     Migrates legacy single-instance Sonarr settings to the new multi-instance lists
    ///     and returns the effective list of configured Sonarr instances (max 3).
    /// </summary>
    /// <returns>A read-only list of configured Sonarr instances.</returns>
    public IReadOnlyList<ArrInstanceConfig> GetEffectiveSonarrInstances()
    {
        var effective = SonarrInstances
            .Where(i => !string.IsNullOrWhiteSpace(i.Url) && !string.IsNullOrWhiteSpace(i.ApiKey))
            .Take(3)
            .ToList();
        return effective.AsReadOnly();
    }
}