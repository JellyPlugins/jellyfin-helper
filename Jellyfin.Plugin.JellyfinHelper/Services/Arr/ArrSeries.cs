using System.Collections.Generic;

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
    ///     Gets or sets the TMDb ID. Populated from Sonarr v4+ API responses (added in v4.0.12.2823, June 2024).
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode file count.</summary>
    public int EpisodeFileCount { get; set; }

    /// <summary>Gets or sets the total episode count.</summary>
    public int TotalEpisodeCount { get; set; }

    /// <summary>Gets or sets optional TVDB/Sonarr genre labels.</summary>
    public IReadOnlyList<string>? Genres { get; set; }

    /// <summary>Gets or sets the provider overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets Sonarr's parsing/series mode (anime, standard or daily), not a TMDb narrative format.</summary>
    public string? SeriesType { get; set; }
}
