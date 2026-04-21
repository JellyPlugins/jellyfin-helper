using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     A single recommendation for a user with score and explanation.
/// </summary>
public sealed class RecommendedItem
{
    /// <summary>
    ///     Gets or sets the Jellyfin item ID.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    ///     Gets or sets the item name/title.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the item type (e.g. "Movie", "Series").
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the combined recommendation score (0.0–1.0).
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    ///     Gets or sets a human-readable reason for the recommendation.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the reason key for i18n translation on the client side.
    /// </summary>
    public string ReasonKey { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the related item name that triggered this recommendation (e.g. "Because you watched X").
    /// </summary>
    public string? RelatedItemName { get; set; }

    /// <summary>
    ///     Gets or sets the genres associated with this item.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "DTO for JSON serialization")]
    public string[] Genres { get; set; } = [];

    /// <summary>
    ///     Gets or sets the production year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    ///     Gets or sets the community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    ///     Gets or sets the primary image tag for poster display.
    /// </summary>
    public string? PrimaryImageTag { get; set; }

    /// <summary>
    ///     Gets or sets the official rating (e.g. "PG-13", "R").
    /// </summary>
    public string? OfficialRating { get; set; }
}