using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
///     Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // Records raw-vs-clamped setter deltas so Plugin startup can surface them as a single warning instead of silently swallowing hand-edited out-of-range XML values.
    private readonly ConcurrentQueue<ClampReportEntry> _clampReports = new();

    private int _orphanMinAgeDays;
    private int _maxRecommendationsPerUser = 20;
    private double _ensembleAlphaMin = 0.3;
    private double _ensembleAlphaMax = 0.75;
    private double _ensembleGenrePenaltyFloor = 0.10;
    private int _seerrCleanupAgeDays = 365;
    private int _trashRetentionDays = 30;
    private List<ArrInstanceConfig> _radarrInstances = [];
    private List<ArrInstanceConfig> _sonarrInstances = [];

    /// <summary>
    ///     Gets or sets the library names to exclude (exclude list). Comma-separated list of library names.
    /// </summary>
    public string ExcludedLibraries { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the minimum age in days an orphaned item must have before it is eligible for deletion.
    /// </summary>
    public int OrphanMinAgeDays
    {
        get => _orphanMinAgeDays;
        set => _orphanMinAgeDays = ClampAndReport(nameof(OrphanMinAgeDays), value, 0, 3650);
    }

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
    ///     Gets or sets the execution mode for the Seerr Cleanup task. Default is Deactivate because this task interacts with an external service.
    /// </summary>
    public TaskMode SeerrCleanupTaskMode { get; set; } = TaskMode.Deactivate;

    /// <summary>
    ///     Gets or sets the maximum age in days for Seerr requests before they are cleaned up.
    /// </summary>
    public int SeerrCleanupAgeDays
    {
        get => _seerrCleanupAgeDays;
        set => _seerrCleanupAgeDays = ClampAndReport(nameof(SeerrCleanupAgeDays), value, 0, 3650);
    }

    /// <summary>
    ///     Gets or sets the base URL of the Jellyseerr/Overseerr/Seerr instance.
    /// </summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the API key for the Jellyseerr/Overseerr/Seerr instance.
    /// </summary>
    public string SeerrApiKey { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether non-admin users can access the Seerr Discovery page and submit media requests.
    /// </summary>
    public bool DiscoveryUserAccessEnabled { get; set; }

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
    /// </summary>
    public int TrashRetentionDays
    {
        get => _trashRetentionDays;
        set => _trashRetentionDays = ClampAndReport(nameof(TrashRetentionDays), value, 0, 3650);
    }

    /// <summary>
    ///     Gets or sets the list of Radarr instances (max 3).
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA1002:DoNotExposeGenericLists",
        Justification = "Collection<T> breaks System.Text.Json round-trip deserialization")]
    public List<ArrInstanceConfig> RadarrInstances
    {
        get => _radarrInstances;
        set => _radarrInstances = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the list of Sonarr instances (max 3).
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA1002:DoNotExposeGenericLists",
        Justification = "Collection<T> breaks System.Text.Json round-trip deserialization")]
    public List<ArrInstanceConfig> SonarrInstances
    {
        get => _sonarrInstances;
        set => _sonarrInstances = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the UI language code. Default is "en".
    ///     Supported: en, de, fr, es, pt, zh, tr.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    ///     Gets or sets the execution mode for the Smart Recommendations task. Default is DryRun (safe mode - generates but does not persist).
    /// </summary>
    public TaskMode RecommendationsTaskMode { get; set; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets or sets the maximum number of recommendations to generate per user.
    ///     Default is 20. Valid range: 1-100. Out-of-range values are clamped.
    /// </summary>
    public int MaxRecommendationsPerUser
    {
        get => _maxRecommendationsPerUser;
        set => _maxRecommendationsPerUser = ClampAndReport(nameof(MaxRecommendationsPerUser), value, 1, 100);
    }

    /// <summary>
    ///     Gets or sets a value indicating whether recommendation results should be synced to per-user Jellyfin playlists visible in the native UI.
    /// </summary>
    public bool SyncRecommendationsToPlaylist { get; set; }

    // RecommendationStrategy removed - Ensemble is always used (combines all methods). XmlSerializer silently ignores unknown XML elements during deserialization, so previously saved "RecommendationStrategy" values are harmlessly discarded.

    /// <summary>
    ///     Gets or sets the minimum alpha value for the ensemble scoring strategy. Controls the lower bound of learned model blending (0-1).
    /// </summary>
    public double EnsembleAlphaMin
    {
        get => _ensembleAlphaMin;
        set
        {
            _ensembleAlphaMin = ClampAndReport(nameof(EnsembleAlphaMin), value, 0.0, 1.0);
            NormalizeAlphaRange();
        }
    }

    /// <summary>
    ///     Gets or sets the maximum alpha value for the ensemble scoring strategy. Controls the upper bound of learned model blending (0-1).
    /// </summary>
    public double EnsembleAlphaMax
    {
        get => _ensembleAlphaMax;
        set
        {
            _ensembleAlphaMax = ClampAndReport(nameof(EnsembleAlphaMax), value, 0.0, 1.0);
            NormalizeAlphaRange();
        }
    }

    /// <summary>
    ///     Gets or sets the genre penalty floor for the ensemble scoring strategy. Items with zero genre overlap are penalized down to this floor value.
    /// </summary>
    public double EnsembleGenrePenaltyFloor
    {
        get => _ensembleGenrePenaltyFloor;
        set => _ensembleGenrePenaltyFloor = ClampAndReport(nameof(EnsembleGenrePenaltyFloor), value, 0.0, 1.0);
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
    ///     Always stored and compared as UTC (<see cref="DateTimeKind.Utc"/>).
    /// </summary>
    public DateTime LastCleanupTimestamp { get; set; } = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    /// <summary>
    ///     Normalizes the alpha range to ensure EnsembleAlphaMin &lt;= EnsembleAlphaMax regardless of property setter invocation order during XML deserialization.
    /// </summary>
    public void NormalizeAlphaRange()
    {
        if (_ensembleAlphaMin > _ensembleAlphaMax)
        {
            var originalMin = _ensembleAlphaMin;
            var originalMax = _ensembleAlphaMax;
            (_ensembleAlphaMin, _ensembleAlphaMax) = (_ensembleAlphaMax, _ensembleAlphaMin);

            _clampReports.Enqueue(new ClampReportEntry(
                "EnsembleAlphaRange (swapped Min > Max)",
                $"Min={originalMin:G4}, Max={originalMax:G4}",
                $"Min={_ensembleAlphaMin:G4}, Max={_ensembleAlphaMax:G4}"));
        }
    }

    /// <summary>
    ///     Returns the list of configured Radarr instances that have both a URL and an API key, capped at 3.
    /// </summary>
    /// <returns>A read-only list of configured Radarr instances.</returns>
    public IReadOnlyList<ArrInstanceConfig> GetEffectiveRadarrInstances()
    {
        var effective = (RadarrInstances ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Url) && !string.IsNullOrWhiteSpace(i.ApiKey))
            .Take(3)
            .ToList();
        return effective.AsReadOnly();
    }

    /// <summary>
    ///     Returns the list of configured Sonarr instances that have both a URL and an API key, capped at 3.
    /// </summary>
    /// <returns>A read-only list of configured Sonarr instances.</returns>
    public IReadOnlyList<ArrInstanceConfig> GetEffectiveSonarrInstances()
    {
        var effective = (SonarrInstances ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Url) && !string.IsNullOrWhiteSpace(i.ApiKey))
            .Take(3)
            .ToList();
        return effective.AsReadOnly();
    }

    /// <summary>
    ///     Returns any raw-vs-clamped setter deltas recorded since the last call and clears the internal buffer.
    /// </summary>
    /// <returns>The recorded clamp reports; empty when nothing was clamped.</returns>
    public IReadOnlyList<ClampReportEntry> DrainClampReports()
    {
        var items = new List<ClampReportEntry>();
        while (_clampReports.TryDequeue(out var item))
        {
            items.Add(item);
        }

        return items;
    }

    // Records the delta only when clamping actually changed the value; the API-driven path
    // (ConfigurationController) never triggers this because its own validation runs first.
    private int ClampAndReport(string propertyName, int raw, int min, int max)
    {
        var clamped = Math.Clamp(raw, min, max);
        if (clamped != raw)
        {
            _clampReports.Enqueue(new ClampReportEntry(
                propertyName,
                raw.ToString(CultureInfo.InvariantCulture),
                clamped.ToString(CultureInfo.InvariantCulture)));
        }

        return clamped;
    }

    private double ClampAndReport(string propertyName, double raw, double min, double max)
    {
        // Math.Clamp passes NaN through unchanged, which would poison downstream consumers (ensemble alpha blend, genre penalty).
        var clamped = double.IsNaN(raw) ? min : Math.Clamp(raw, min, max);
        if (double.IsNaN(raw) || raw < min || raw > max)
        {
            _clampReports.Enqueue(new ClampReportEntry(
                propertyName,
                raw.ToString("G17", CultureInfo.InvariantCulture),
                clamped.ToString("G17", CultureInfo.InvariantCulture)));
        }

        return clamped;
    }
}
