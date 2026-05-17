using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Page info for paginated Seerr user responses.
/// </summary>
internal sealed class SeerrUserPageInfo
{
    /// <summary>
    ///     Gets or sets the total number of results.
    /// </summary>
    [JsonPropertyName("results")]
    public int Results { get; set; }

    /// <summary>
    ///     Gets or sets the total number of pages.
    /// </summary>
    [JsonPropertyName("pages")]
    public int Pages { get; set; }
}