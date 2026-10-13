using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     A trending-list entry from Trakt: a watcher count plus the nested movie or show. Only one of
///     <see cref="Movie"/> or <see cref="Show"/> is populated depending on which trending endpoint was called.
/// </summary>
public sealed class TraktTrendingItem
{
    /// <summary>Gets or sets the number of users currently watching, used as the trending signal.</summary>
    [JsonPropertyName("watchers")]
    public int Watchers { get; set; }

    /// <summary>Gets or sets the nested movie (populated for /movies/trending).</summary>
    [JsonPropertyName("movie")]
    public TraktMediaItem? Movie { get; set; }

    /// <summary>Gets or sets the nested show (populated for /shows/trending).</summary>
    [JsonPropertyName("show")]
    public TraktMediaItem? Show { get; set; }
}
