namespace Jellyfin.Plugin.JellyfinHelper.Services.Arr;

/// <summary>
/// Represents a series from Sonarr.
/// </summary>
public class ArrSeries
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    public int Year { get; set; }

    /// <summary>Gets or sets the IMDb ID.</summary>
    public string ImdbId { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVDB ID.</summary>
    public int TvdbId { get; set; }

    /// <summary>
    ///     Gets or sets the TMDb ID.
    ///     Populated from Sonarr v3.0.9+ / v4+ API responses. Older Sonarr versions
    ///     do not include this field (value remains 0). Always populated for Radarr movies.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode file count.</summary>
    public int EpisodeFileCount { get; set; }

    /// <summary>Gets or sets the total episode count.</summary>
    public int TotalEpisodeCount { get; set; }
}
