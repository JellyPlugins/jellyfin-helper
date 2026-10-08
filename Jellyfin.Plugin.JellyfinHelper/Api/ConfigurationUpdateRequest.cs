using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request DTO for updating the plugin configuration via the API. Uses arrays for Arr instances to avoid CA2227 while supporting JSON deserialization.
/// </summary>
public class ConfigurationUpdateRequest
{
    /// <summary>
    ///     Gets the library names to exclude (exclude list). Comma-separated.
    ///     Libraries in this list will be skipped by all cleanup tasks.
    /// </summary>
    public string ExcludedLibraries { get; init; } = string.Empty;

    /// <summary>
    ///     Gets the minimum age in days an orphaned item must have before deletion.
    /// </summary>
    public int OrphanMinAgeDays { get; init; }

    /// <summary>
    ///     Gets the execution mode for the Trickplay Folder Cleaner task.
    /// </summary>
    public TaskMode TrickplayTaskMode { get; init; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets the execution mode for the Empty Media Folder Cleaner task.
    /// </summary>
    public TaskMode EmptyMediaFolderTaskMode { get; init; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets the execution mode for the Orphaned Subtitle Cleaner task.
    /// </summary>
    public TaskMode OrphanedSubtitleTaskMode { get; init; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets the execution mode for the Link Repair task (.strm files and symlinks).
    /// </summary>
    public TaskMode LinkRepairTaskMode { get; init; } = TaskMode.DryRun;

    /// <summary>
    ///     Gets the execution mode for the Smart Recommendations task.
    /// </summary>
    /// <remarks>Nullable so older UI clients won't silently reset it.</remarks>
    public TaskMode? RecommendationsTaskMode { get; init; }

    /// <summary>
    ///     Gets a value indicating whether recommendation results should be synced to Jellyfin playlists.
    /// </summary>
    /// <remarks>Nullable so older UI clients won't silently reset it.</remarks>
    public bool? SyncRecommendationsToPlaylist { get; init; }

    /// <summary>
    ///     Gets a value indicating whether non-admin users can access the Seerr Discovery page.
    /// </summary>
    /// <remarks>Nullable so older UI clients won't silently reset it.</remarks>
    public bool? DiscoveryUserAccessEnabled { get; init; }

    /// <summary>
    ///     Gets the execution mode for the Seerr Cleanup task.
    /// </summary>
    public TaskMode SeerrCleanupTaskMode { get; init; } = TaskMode.Deactivate;

    /// <summary>
    ///     Gets the maximum age in days for Seerr requests before they are cleaned up.
    /// </summary>
    public int SeerrCleanupAgeDays { get; init; } = 365;

    /// <summary>
    ///     Gets the base URL of the Jellyseerr/Overseerr/Seerr instance.
    /// </summary>
    public string SeerrUrl { get; init; } = string.Empty;

    /// <summary>
    ///     Gets the API key for the Jellyseerr/Overseerr/Seerr instance.
    /// </summary>
    public string SeerrApiKey { get; init; } = string.Empty;

    /// <summary>
    ///     Gets a value indicating whether TLS certificate validation is skipped for the Seerr instance.
    ///     Nullable so an absent field is distinguishable from an explicit false; absent means false.
    /// </summary>
    public bool? SeerrSkipCertificateValidation { get; init; }

    /// <summary>
    ///     Gets a value indicating whether the Trakt discovery source is enabled.
    /// </summary>
    /// <remarks>
    ///     Accepted for backward compatibility but IGNORED on save: the server derives TraktEnabled from whether
    ///     a client id and secret are stored (see ApplyTraktSettings). The admin UI no longer sends this field.
    /// </remarks>
    public bool? TraktEnabled { get; init; }

    /// <summary>
    ///     Gets a value indicating whether Trakt sourcing is enabled (user-facing master switch). Null when the
    ///     client omits it (partial PUT) so the stored value is preserved; a non-null value is persisted verbatim.
    /// </summary>
    public bool? TraktSourcingEnabled { get; init; }

    /// <summary>
    ///     Gets the Trakt OAuth application client id. Nullable: a client without the Trakt card (Discovery
    ///     sidebar off) sends null so the stored value is preserved rather than cleared.
    /// </summary>
    public string? TraktClientId { get; init; }

    /// <summary>
    ///     Gets the Trakt OAuth application client secret (mask sentinel preserves the stored value). Nullable:
    ///     a client without the Trakt card sends null so the stored value is preserved rather than cleared.
    /// </summary>
    public string? TraktClientSecret { get; init; }

    /// <summary>
    ///     Gets the Trakt HTTP request timeout in seconds. Nullable so older clients do not reset it.
    /// </summary>
    public int? TraktTimeoutSeconds { get; init; }

    /// <summary>
    ///     Gets the number of Trakt items fetched per list. Nullable so older clients do not reset it.
    /// </summary>
    public int? TraktLimit { get; init; }

    /// <summary>
    ///     Gets a value indicating whether to use a trash folder instead of permanently deleting files.
    /// </summary>
    public bool UseTrash { get; init; }

    /// <summary>
    ///     Gets the path to the trash folder.
    /// </summary>
    public string TrashFolderPath { get; init; } = ".jellyfin-trash";

    /// <summary>
    ///     Gets the number of days to keep items in the trash before permanent deletion.
    /// </summary>
    public int TrashRetentionDays { get; init; } = 30;

    /// <summary>
    ///     Gets the Radarr instances (max 3).
    /// </summary>
    public IReadOnlyList<ArrInstanceConfig> RadarrInstances { get; init; } = [];

    /// <summary>
    ///     Gets the Sonarr instances (max 3).
    /// </summary>
    public IReadOnlyList<ArrInstanceConfig> SonarrInstances { get; init; } = [];

    /// <summary>
    ///     Gets the UI language code.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    ///     Gets the minimum alpha value for the ensemble scoring strategy (0-1).
    ///     Nullable so older clients do not silently reset it.
    /// </summary>
    public double? EnsembleAlphaMin { get; init; }

    /// <summary>
    ///     Gets the maximum alpha value for the ensemble scoring strategy (0-1).
    ///     Nullable so older clients do not silently reset it.
    /// </summary>
    public double? EnsembleAlphaMax { get; init; }

    /// <summary>
    ///     Gets the genre penalty floor for the ensemble scoring strategy (0-1).
    ///     Nullable so older clients do not silently reset it.
    /// </summary>
    public double? EnsembleGenrePenaltyFloor { get; init; }

    /// <summary>
    ///     Gets the plugin log level (e.g. DEBUG, INFO, WARN, ERROR).
    /// </summary>
    public string? PluginLogLevel { get; init; }
}