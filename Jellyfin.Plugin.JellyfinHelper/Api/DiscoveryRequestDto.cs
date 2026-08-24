using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     DTO for submitting a discovery media request.
/// </summary>
public sealed class DiscoveryRequestDto : IValidatableObject
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
    ///     Defaults to "movie" when omitted from the request payload.
    /// </summary>
    [Required]
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
    ///     Seerr uses 0-based server IDs (first configured server has ID 0).
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "ServerId must be 0 or greater when provided.")]
    public int? ServerId { get; set; }

    /// <summary>
    ///     Gets or sets the quality profile ID to use for the download.
    ///     When provided, overrides the default quality profile.
    ///     Seerr quality profile IDs can start at 0 depending on the Arr instance configuration.
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "ProfileId must be 0 or greater when provided.")]
    public int? ProfileId { get; set; }

    /// <summary>
    ///     Gets or sets the root folder path for the download.
    ///     When provided, overrides the default root folder.
    /// </summary>
    [StringLength(512, ErrorMessage = "RootFolder path exceeds maximum length of 512 characters.")]
    public string? RootFolder { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(RootFolder))
        {
            yield break;
        }

        var path = RootFolder.Trim();

        if (path.Contains("..", StringComparison.Ordinal) || path.TrimStart().StartsWith('~'))
        {
            yield return new ValidationResult("Invalid root folder path.", [nameof(RootFolder)]);
            yield break;
        }

        if (path.Where(char.IsControl).Any())
        {
            yield return new ValidationResult("Root folder path contains invalid characters.", [nameof(RootFolder)]);
            yield break;
        }
    }
}
