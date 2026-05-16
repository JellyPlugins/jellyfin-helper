using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     DTO for submitting a discovery media request.
/// </summary>
public sealed class DiscoveryRequestDto
{
    /// <summary>
    ///     Gets or sets the TMDb ID.
    /// </summary>
    [Required]
    [Range(1, int.MaxValue, ErrorMessage = "TmdbId must be greater than 0.")]
    public int TmdbId { get; set; }

    /// <summary>
    ///     Gets or sets the media type ("movie" or "tv").
    ///     Case-insensitive; the controller normalizes to lowercase before processing.
    ///     Defaults to "movie" when omitted from the request payload. The controller
    ///     performs an additional explicit validation guard independent of model binding.
    /// </summary>
    [RegularExpression("^(?i)(movie|tv)$", ErrorMessage = "MediaType must be either 'movie' or 'tv'.")]
    public string MediaType { get; set; } = "movie";

    /// <summary>
    ///     Gets or sets the Seerr user ID to submit the request as.
    ///     When null, the request is submitted as the API key owner (admin).
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "SeerrUserId must be greater than 0 when provided.")]
    public int? SeerrUserId { get; set; }

    /// <summary>
    ///     Gets or sets the Radarr/Sonarr server ID in Seerr.
    ///     When provided, overrides the default server selection.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "ServerId must be greater than 0 when provided.")]
    public int? ServerId { get; set; }

    /// <summary>
    ///     Gets or sets the quality profile ID to use for the download.
    ///     When provided, overrides the default quality profile.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "ProfileId must be greater than 0 when provided.")]
    public int? ProfileId { get; set; }

    /// <summary>
    ///     Gets or sets the root folder path for the download.
    ///     When provided, overrides the default root folder.
    /// </summary>
    [StringLength(512, ErrorMessage = "RootFolder path must not exceed 512 characters.")]
    public string? RootFolder { get; set; }
}