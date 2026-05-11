using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Represents a single user interaction with a discovery recommendation.
///     Tracks whether the user saw, dismissed, or requested the item.
/// </summary>
public sealed class DiscoveryFeedbackEntry
{
    /// <summary>
    ///     Gets or sets the TMDb ID of the discovery item.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    ///     Gets or sets the media type ("movie" or "tv").
    /// </summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>
    ///     Gets or sets the display title (for logging/debugging; not used in scoring).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the production year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    ///     Gets or sets the list of genre names (Jellyfin-normalized).
    /// </summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>
    ///     Gets or sets the TMDb community rating (0-10) at the time of discovery.
    /// </summary>
    public double TmdbRating { get; set; }

    /// <summary>
    ///     Gets or sets the recommendation score that was computed when this item was shown.
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    ///     Gets or sets the UTC timestamp when this item was first shown to the user.
    /// </summary>
    public DateTime ShownAtUtc { get; set; }

    /// <summary>
    ///     Gets or sets the UTC timestamp when the user dismissed this item.
    ///     Null if not dismissed.
    /// </summary>
    public DateTime? DismissedAtUtc { get; set; }

    /// <summary>
    ///     Gets or sets the UTC timestamp when the user requested this item.
    ///     Null if not requested.
    /// </summary>
    public DateTime? RequestedAtUtc { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the item was later found
    ///     in the user's watch history (requested AND watched).
    /// </summary>
    public bool WasWatched { get; set; }

    /// <summary>
    ///     Gets or sets the known people (actors/directors) associated with this item
    ///     at the time of discovery. Used for PeopleSimilarity during training.
    /// </summary>
    public IReadOnlyList<string> KnownPeople { get; set; } = [];

    /// <summary>
    ///     Gets the interaction status of this entry.
    /// </summary>
    /// <returns>The most significant interaction status.</returns>
    public DiscoveryInteractionStatus GetStatus()
    {
        if (RequestedAtUtc.HasValue && WasWatched)
        {
            return DiscoveryInteractionStatus.RequestedAndWatched;
        }

        if (RequestedAtUtc.HasValue)
        {
            return DiscoveryInteractionStatus.Requested;
        }

        if (DismissedAtUtc.HasValue)
        {
            return DiscoveryInteractionStatus.Dismissed;
        }

        return DiscoveryInteractionStatus.Shown;
    }
}