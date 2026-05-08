using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

/// <summary>
///     Represents watch data for a single media item by a specific user.
/// </summary>
public sealed class WatchedItemInfo
{
    private IReadOnlyList<string> _genres = [];

    /// <summary>
    ///     Gets or sets the Jellyfin item ID.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    ///     Gets or sets the item name/title.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the item type (e.g. "Movie", "Episode", "Series").
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the number of times the user has played this item.
    /// </summary>
    public int PlayCount { get; set; }

    /// <summary>
    ///     Gets or sets the date the user last played this item (UTC).
    /// </summary>
    public DateTime? LastPlayedDate { get; set; }

    /// <summary>
    ///     Gets or sets the playback position in ticks (for partially watched items).
    /// </summary>
    public long PlaybackPositionTicks { get; set; }

    /// <summary>
    ///     Gets or sets the total runtime of the item in ticks.
    /// </summary>
    public long RuntimeTicks { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the item is marked as played/watched.
    /// </summary>
    public bool Played { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the item is a user favorite.
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    ///     Gets or sets the user's personal rating (if any).
    /// </summary>
    public double? UserRating { get; set; }

    /// <summary>
    ///     Gets or sets the community rating from metadata providers.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    ///     Gets or sets the genres associated with this item.
    ///     Setter coalesces null to empty to prevent NRE from deserialized cache data.
    /// </summary>
    public IReadOnlyList<string> Genres
    {
        get => _genres;
        set => _genres = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the production year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    ///     Gets or sets the parent series ID (for episodes only).
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    ///     Gets or sets the date the item was added to the Jellyfin library.
    ///     Used for LibraryAddedRecency feature computation in training (Phase 2 organic items).
    /// </summary>
    public DateTime? DateCreated { get; set; }

    /// <summary>
    ///     Gets or sets the primary image tag for poster display.
    /// </summary>
    public string? PrimaryImageTag { get; set; }

    /// <summary>
    ///     Determines whether this item represents a meaningful user interaction.
    ///     Centralized predicate used across the recommendation engine to ensure consistent
    ///     filtering logic (TrainingService, Engine, PreferenceBuilder).
    ///     An item has meaningful interaction if: Played, IsFavorite, PlayCount &gt; 0,
    ///     or PlaybackPositionTicks &gt; 0.
    /// </summary>
    /// <returns>True if the user has meaningfully interacted with this item.</returns>
    public bool HasMeaningfulInteraction()
        => Played || IsFavorite || PlayCount > 0 || PlaybackPositionTicks > 0;

    /// <summary>
    ///     Determines whether this item has real playback activity (excluding favorite-only items).
    ///     Used for temporal affinity calculations where actual viewing timestamps matter.
    ///     An item has playback activity if: Played, PlayCount &gt; 0, or PlaybackPositionTicks &gt; 0.
    /// </summary>
    /// <returns>True if the user has actually started playing this item.</returns>
    public bool HasPlaybackActivity()
        => Played || PlayCount > 0 || PlaybackPositionTicks > 0;
}
