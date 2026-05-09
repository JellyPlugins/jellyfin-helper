using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Seerr /api/v1/discover/* response envelope.
/// </summary>
internal sealed class TmdbDiscoverResponse
{
    /// <summary>
    ///     Gets or sets the current page number.
    /// </summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>
    ///     Gets or sets the total number of pages available.
    /// </summary>
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    /// <summary>
    ///     Gets or sets the total number of results across all pages.
    /// </summary>
    [JsonPropertyName("totalResults")]
    public int TotalResults { get; set; }

    /// <summary>
    ///     Gets or sets the list of discover results on this page.
    /// </summary>
    [JsonPropertyName("results")]
    public List<TmdbDiscoverItem> Results { get; set; } = [];
}