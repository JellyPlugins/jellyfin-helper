using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
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

    /// <summary>
    /// Gets or sets a value indicating whether the Trickplay Folder Cleaner task runs in dry-run mode.
    /// Default is true (safe mode).
    /// </summary>
    public bool DryRunTrickplay { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Empty Media Folder Cleaner task runs in dry-run mode.
    /// Default is true (safe mode).
    /// </summary>
    public bool DryRunEmptyMediaFolders { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Orphaned Subtitle Cleaner task runs in dry-run mode.
    /// Default is true (safe mode).
    /// </summary>
    public bool DryRunOrphanedSubtitles { get; set; } = true;

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
    /// Gets or sets a value indicating whether the orphaned subtitle cleaner is enabled.
    /// </summary>
    public bool EnableSubtitleCleaner { get; set; } = true;

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
    /// Gets the list of Radarr instances (max 3).
    /// </summary>
    public Collection<ArrInstanceConfig> RadarrInstances { get; } = new();

    /// <summary>
    /// Gets the list of Sonarr instances (max 3).
    /// </summary>
    public Collection<ArrInstanceConfig> SonarrInstances { get; } = new();

    /// <summary>
    /// Gets or sets the UI language code. Default is "en".
    /// Supported: en, de, fr, es, pt, zh, tr.
    /// </summary>
    public string Language { get; set; } = "en";

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