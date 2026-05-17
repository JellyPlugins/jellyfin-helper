using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     DTO for dismissing a discovery recommendation.
/// </summary>
public sealed class DiscoveryDismissDto
{
    /// <summary>
    ///     Gets or sets the TMDb ID of the item to dismiss.
    /// </summary>
    [Required]
    [Range(1, int.MaxValue, ErrorMessage = "TmdbId must be greater than 0.")]
    public int TmdbId { get; set; }

    /// <summary>
    ///     Gets or sets the media type ("movie" or "tv").
    /// </summary>
    [Required]
    [RegularExpression("^(movie|tv)$", ErrorMessage = "MediaType must be either 'movie' or 'tv'.")]
    public string MediaType { get; set; } = "movie";
}
