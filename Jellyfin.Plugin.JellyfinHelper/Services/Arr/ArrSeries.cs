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
    ///     Populated from Sonarr v4+ API responses (added in v4.0.12.2823, June 2024).
    ///     Sonarr v3 does NOT include this field - value remains 0 and is excluded
    ///     from exclusion-set filtering via the <c>TmdbId > 0</c> guard.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode file count.</summary>
    public int EpisodeFileCount { get; set; }

    /// <summary>Gets or sets the total episode count.</summary>
    public int TotalEpisodeCount { get; set; }
}
