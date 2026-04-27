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
    // ===== Backing fields for clamped properties =====
    private int _maxRecommendationsPerUser = 20;
    private double _ensembleAlphaMin = 0.3;
    private double _ensembleAlphaMax = 0.75;
    private double _ensembleGenrePenaltyFloor = 0.10;

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
    ///     Gets or sets the maximum number of recommendations to generate per user.
    ///     Default is 20. Valid range: 1–100. Out-of-range values are clamped.
    /// </summary>
    public int MaxRecommendationsPerUser
    {
        get => _maxRecommendationsPerUser;
        set => _maxRecommendationsPerUser = Math.Clamp(value, 1, 100);
    }

    /// <summary>
    ///     Gets or sets a value indicating whether recommendation results should be synced
    ///     to per-user Jellyfin playlists visible in the native UI.
    ///     Only effective when <see cref="RecommendationsTaskMode"/> is <see cref="TaskMode.Activate"/>.
    ///     Default is false (opt-in feature).
    /// </summary>
    public bool SyncRecommendationsToPlaylist { get; set; }

    // RecommendationStrategy removed — Ensemble is always used (combines all methods).
    // XmlSerializer silently ignores unknown XML elements during deserialization,
    // so previously saved "RecommendationStrategy" values are harmlessly discarded.

    /// <summary>
    ///     Gets or sets the minimum alpha value for the ensemble scoring strategy.
    ///     Controls the lower bound of learned model blending (0–1). Default is 0.3.
    ///     Out-of-range values are clamped to [0, 1].
    ///     The min ≤ max invariant is enforced by <see cref="NormalizeAlphaRange"/>
    ///     after deserialization, not by the setter, to avoid XML element-order dependency.
    /// </summary>
    public double EnsembleAlphaMin
    {
        get => _ensembleAlphaMin;
        set => _ensembleAlphaMin = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    ///     Gets or sets the maximum alpha value for the ensemble scoring strategy.
    ///     Controls the upper bound of learned model blending (0–1). Default is 0.75.
    ///     Out-of-range values are clamped to [0, 1].
    ///     The min ≤ max invariant is enforced by <see cref="NormalizeAlphaRange"/>
    ///     after deserialization, not by the setter, to avoid XML element-order dependency.
    /// </summary>
    public double EnsembleAlphaMax
    {
        get => _ensembleAlphaMax;
        set => _ensembleAlphaMax = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    ///     Gets or sets the genre penalty floor for the ensemble scoring strategy.
    ///     Items with zero genre overlap are penalized down to this floor value. Default is 0.10.
    ///     Out-of-range values are clamped to [0, 1].
    /// </summary>
    public double EnsembleGenrePenaltyFloor
    {
        get => _ensembleGenrePenaltyFloor;
        set => _ensembleGenrePenaltyFloor = Math.Clamp(value, 0.0, 1.0);
    }

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
    ///     Normalizes the alpha range to ensure <see cref="EnsembleAlphaMin"/> ≤ <see cref="EnsembleAlphaMax"/>
    ///     regardless of property setter invocation order during XML deserialization.
    ///     <see cref="System.Xml.Serialization.XmlSerializer"/> does not guarantee property order,
    ///     so a persisted config with Min=0.8 and Max=0.6 could produce different final values
    ///     depending on which setter runs first. This method should be called after deserialization.
    /// </summary>
    public void NormalizeAlphaRange()
    {
        if (_ensembleAlphaMin > _ensembleAlphaMax)
        {
            // Swap so that min ≤ max
            (_ensembleAlphaMin, _ensembleAlphaMax) = (_ensembleAlphaMax, _ensembleAlphaMin);
        }
    }

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