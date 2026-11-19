using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     The id bag Trakt attaches to every movie/show, carrying the cross-service identifiers we map on.
/// </summary>
public sealed class TraktIds
{
    /// <summary>Gets or sets the Trakt numeric id.</summary>
    [JsonPropertyName("trakt")]
    public int Trakt { get; set; }

    /// <summary>Gets or sets the Trakt URL slug (preferred for building trakt.tv links).</summary>
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    /// <summary>Gets or sets the TMDb id. Items without one are dropped during mapping.</summary>
    [JsonPropertyName("tmdb")]
    public int? Tmdb { get; set; }

    /// <summary>Gets or sets the TVDB identity of a show when supplied by Trakt.</summary>
    [JsonPropertyName("tvdb")]
    public int? Tvdb { get; set; }

    /// <summary>Gets or sets the IMDb cross-provider identity.</summary>
    [JsonPropertyName("imdb")]
    public string? Imdb { get; set; }
}
