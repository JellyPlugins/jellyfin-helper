namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     DTO for submitting a discovery media request.
/// </summary>
public sealed class DiscoveryRequestDto
{
    /// <summary>
    ///     Gets or sets the TMDb ID.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    ///     Gets or sets the media type ("movie" or "tv").
    /// </summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>
    ///     Gets or sets the Seerr user ID to submit the request as.
    ///     When null or 0, the request is submitted as the API key owner (admin).
    /// </summary>
    public int? SeerrUserId { get; set; }

    /// <summary>
    ///     Gets or sets the Radarr/Sonarr server ID in Seerr.
    ///     When provided, overrides the default server selection.
    /// </summary>
    public int? ServerId { get; set; }

    /// <summary>
    ///     Gets or sets the quality profile ID to use for the download.
    ///     When provided, overrides the default quality profile.
    /// </summary>
    public int? ProfileId { get; set; }

    /// <summary>
    ///     Gets or sets the root folder path for the download.
    ///     When provided, overrides the default root folder.
    /// </summary>
    public string? RootFolder { get; set; }
}
