using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Read-only projection of <see cref="PluginConfiguration"/> returned by GET /Configuration.
///     API keys are replaced with a masked placeholder so they never leave the server in plain text.
///     Clients that need to submit a new key must send a non-masked value via POST /Configuration;
///     receiving the placeholder back means "key already set, send the real value to change it".
/// </summary>
public sealed class ConfigurationResponse
{
    /// <summary>
    ///     Placeholder emitted in place of any non-empty API key. Fixed length by design: it does
    ///     NOT reflect the real key length, so the mask never leaks how long the stored secret is.
    ///     Applies uniformly to the Seerr key and every Radarr/Sonarr instance key.
    /// </summary>
    internal const string ApiKeyMask = "********";

    /// <summary>Gets the excluded libraries (comma-separated).</summary>
    public string ExcludedLibraries { get; init; } = string.Empty;

    /// <summary>Gets the orphan minimum age in days.</summary>
    public int OrphanMinAgeDays { get; init; }

    /// <summary>Gets the trickplay task mode.</summary>
    public TaskMode TrickplayTaskMode { get; init; }

    /// <summary>Gets the empty media folder task mode.</summary>
    public TaskMode EmptyMediaFolderTaskMode { get; init; }

    /// <summary>Gets the orphaned subtitle task mode.</summary>
    public TaskMode OrphanedSubtitleTaskMode { get; init; }

    /// <summary>Gets the link repair task mode.</summary>
    public TaskMode LinkRepairTaskMode { get; init; }

    /// <summary>Gets the Seerr cleanup task mode.</summary>
    public TaskMode SeerrCleanupTaskMode { get; init; }

    /// <summary>Gets the Seerr cleanup age threshold in days.</summary>
    public int SeerrCleanupAgeDays { get; init; }

    /// <summary>Gets the Seerr instance URL.</summary>
    public string SeerrUrl { get; init; } = string.Empty;

    /// <summary>
    ///     Gets the Seerr API key placeholder.
    ///     Returns <see cref="ApiKeyMask"/> when a key is configured, empty string otherwise.
    /// </summary>
    public string SeerrApiKey { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether trash is enabled.</summary>
    public bool UseTrash { get; init; }

    /// <summary>Gets the trash folder path.</summary>
    public string TrashFolderPath { get; init; } = string.Empty;

    /// <summary>Gets the trash retention in days.</summary>
    public int TrashRetentionDays { get; init; }

    /// <summary>Gets the UI language code.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Gets the plugin log level.</summary>
    public string PluginLogLevel { get; init; } = string.Empty;

    /// <summary>Gets the recommendations task mode.</summary>
    public TaskMode RecommendationsTaskMode { get; init; }

    /// <summary>Gets the maximum recommendations per user.</summary>
    public int MaxRecommendationsPerUser { get; init; }

    /// <summary>Gets a value indicating whether recommendations are synced to playlists.</summary>
    public bool SyncRecommendationsToPlaylist { get; init; }

    /// <summary>Gets a value indicating whether non-admin users can access the discovery page.</summary>
    public bool DiscoveryUserAccessEnabled { get; init; }

    /// <summary>Gets the configuration version for migration tracking.</summary>
    public int ConfigVersion { get; init; }

    /// <summary>Gets the minimum alpha value for the ensemble scoring strategy.</summary>
    public double EnsembleAlphaMin { get; init; }

    /// <summary>Gets the maximum alpha value for the ensemble scoring strategy.</summary>
    public double EnsembleAlphaMax { get; init; }

    /// <summary>Gets the genre penalty floor for the ensemble scoring strategy.</summary>
    public double EnsembleGenrePenaltyFloor { get; init; }

    /// <summary>Gets the total bytes freed by all cleanup operations.</summary>
    public long TotalBytesFreed { get; init; }

    /// <summary>Gets the total items deleted by all cleanup operations.</summary>
    public int TotalItemsDeleted { get; init; }

    /// <summary>Gets the timestamp of the last cleanup run.</summary>
    public DateTime LastCleanupTimestamp { get; init; }

    /// <summary>Gets the Radarr instances with masked API keys.</summary>
    public IReadOnlyList<MaskedArrInstanceConfig> RadarrInstances { get; init; } = [];

    /// <summary>Gets the Sonarr instances with masked API keys.</summary>
    public IReadOnlyList<MaskedArrInstanceConfig> SonarrInstances { get; init; } = [];

    /// <summary>
    ///     Builds a <see cref="ConfigurationResponse"/> from a live <see cref="PluginConfiguration"/>,
    ///     replacing every non-empty API key with <see cref="ApiKeyMask"/>.
    /// </summary>
    /// <param name="config">The live plugin configuration.</param>
    /// <returns>The masked response DTO.</returns>
    public static ConfigurationResponse FromConfig(PluginConfiguration config)
    {
        return new ConfigurationResponse
        {
            ExcludedLibraries = config.ExcludedLibraries,
            OrphanMinAgeDays = config.OrphanMinAgeDays,
            TrickplayTaskMode = config.TrickplayTaskMode,
            EmptyMediaFolderTaskMode = config.EmptyMediaFolderTaskMode,
            OrphanedSubtitleTaskMode = config.OrphanedSubtitleTaskMode,
            LinkRepairTaskMode = config.LinkRepairTaskMode,
            SeerrCleanupTaskMode = config.SeerrCleanupTaskMode,
            SeerrCleanupAgeDays = config.SeerrCleanupAgeDays,
            SeerrUrl = config.SeerrUrl,
            SeerrApiKey = string.IsNullOrWhiteSpace(config.SeerrApiKey) ? string.Empty : ApiKeyMask,
            UseTrash = config.UseTrash,
            TrashFolderPath = config.TrashFolderPath,
            TrashRetentionDays = config.TrashRetentionDays,
            Language = config.Language,
            PluginLogLevel = config.PluginLogLevel,
            RecommendationsTaskMode = config.RecommendationsTaskMode,
            MaxRecommendationsPerUser = config.MaxRecommendationsPerUser,
            SyncRecommendationsToPlaylist = config.SyncRecommendationsToPlaylist,
            DiscoveryUserAccessEnabled = config.DiscoveryUserAccessEnabled,
            ConfigVersion = config.ConfigVersion,
            EnsembleAlphaMin = config.EnsembleAlphaMin,
            EnsembleAlphaMax = config.EnsembleAlphaMax,
            EnsembleGenrePenaltyFloor = config.EnsembleGenrePenaltyFloor,
            TotalBytesFreed = config.TotalBytesFreed,
            TotalItemsDeleted = config.TotalItemsDeleted,
            LastCleanupTimestamp = config.LastCleanupTimestamp,
            RadarrInstances = config.RadarrInstances
                .Select(i => new MaskedArrInstanceConfig
                {
                    Name = i.Name,
                    Url = i.Url,
                    ApiKey = string.IsNullOrWhiteSpace(i.ApiKey) ? string.Empty : ApiKeyMask
                })
                .ToList(),
            SonarrInstances = config.SonarrInstances
                .Select(i => new MaskedArrInstanceConfig
                {
                    Name = i.Name,
                    Url = i.Url,
                    ApiKey = string.IsNullOrWhiteSpace(i.ApiKey) ? string.Empty : ApiKeyMask
                })
                .ToList()
        };
    }
}
