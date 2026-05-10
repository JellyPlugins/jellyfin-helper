using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     A single discovery recommendation for a user — represents a media item
///     not yet in the library that the user would likely enjoy.
/// </summary>
public sealed class DiscoveryRecommendation
{
    /// <summary>
    ///     Gets or sets the TMDb ID of the recommended item.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    ///     Gets or sets the media type ("movie" or "tv").
    /// </summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>
    ///     Gets or sets the display title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the production year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    ///     Gets or sets the computed recommendation score (0-1).
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    ///     Gets or sets the human-readable reason text (fallback when i18n key is not available).
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the i18n key for the reason text.
    /// </summary>
    public string ReasonKey { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets related information for reason text placeholders (e.g. person name, genre name).
    /// </summary>
    public string? RelatedInfo { get; set; }

    /// <summary>
    ///     Gets or sets the list of genre names.
    /// </summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>
    ///     Gets or sets the TMDb community rating (0-10).
    /// </summary>
    public double TmdbRating { get; set; }

    /// <summary>
    ///     Gets or sets the poster path (relative to TMDb CDN).
    /// </summary>
    public string? PosterPath { get; set; }

    /// <summary>
    ///     Gets or sets the short overview/description of the media item (1-3 sentences).
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether this item has already been requested in Seerr.
    /// </summary>
    public bool AlreadyRequested { get; set; }
}