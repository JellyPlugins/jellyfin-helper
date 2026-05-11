namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     DTO for dismissing a discovery recommendation.
/// </summary>
public sealed class DiscoveryDismissDto
{
    /// <summary>
    ///     Gets or sets the TMDb ID of the item to dismiss.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    ///     Gets or sets the media type ("movie" or "tv").
    /// </summary>
    public string MediaType { get; set; } = "movie";
}