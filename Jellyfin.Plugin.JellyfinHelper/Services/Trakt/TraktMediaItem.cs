using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     A single movie or show as returned by Trakt. The personal recommendation endpoints return these at the
///     top level; the trending endpoints nest one under a wrapper (see <see cref="TraktTrendingItem"/>).
/// </summary>
public sealed class TraktMediaItem
{
    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the release/first-aired year.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>Gets or sets the overview/synopsis.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the Trakt community rating (0-10).</summary>
    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    /// <summary>Gets or sets the content certification (e.g. "PG-13"). Unknown values are allowed through.</summary>
    [JsonPropertyName("certification")]
    public string? Certification { get; set; }

    /// <summary>Gets or sets the genre slugs.</summary>
    [JsonPropertyName("genres")]
    public IReadOnlyList<string>? Genres { get; set; }

    /// <summary>Gets or sets the cross-service id bag (tmdb/slug/trakt).</summary>
    [JsonPropertyName("ids")]
    public TraktIds? Ids { get; set; }
}
