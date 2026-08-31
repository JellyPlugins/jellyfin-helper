using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

/// <summary>
///     Represents watch data for a single media item by a specific user.
/// </summary>
public sealed class WatchedItemInfo
{
    private IReadOnlyList<string> _genres = [];
    private IReadOnlyList<string> _inheritedTags = [];
    private IReadOnlyList<string> _productionCountries = [];
    private IReadOnlyList<string> _writerNames = [];
    private IReadOnlyList<string> _peopleNames = [];
    private IReadOnlyList<double> _peopleWeights = [];

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
    ///     Gets or sets the date the item was added to the library.
    /// </summary>
    public DateTime? DateCreated { get; set; }

    /// <summary>
    ///     Gets or sets the primary image tag for poster display.
    /// </summary>
    public string? PrimaryImageTag { get; set; }

    /// <summary>
    ///     Gets or sets the TMDb collection name if any.
    /// </summary>
    public string? TmdbCollectionName { get; set; }

    /// <summary>
    ///     Gets or sets the production countries.
    /// </summary>
    public IReadOnlyList<string> ProductionCountries
    {
        get => _productionCountries;
        set => _productionCountries = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the inherited tags (own tags unioned with parent/collection/library-folder tags).
    /// </summary>
    public IReadOnlyList<string> InheritedTags
    {
        get => _inheritedTags;
        set => _inheritedTags = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the series status.
    /// </summary>
    public string? SeriesStatus { get; set; }

    /// <summary>
    ///     Gets or sets the series end date if any.
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>
    ///     Gets or sets the writer names.
    /// </summary>
    public IReadOnlyList<string> WriterNames
    {
        get => _writerNames;
        set => _writerNames = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the billed cast and director names.
    /// </summary>
    public IReadOnlyList<string> PeopleNames
    {
        get => _peopleNames;
        set => _peopleNames = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the billing weights aligned to PeopleNames.
    /// </summary>
    public IReadOnlyList<double> PeopleWeights
    {
        get => _peopleWeights;
        set => _peopleWeights = value ?? [];
    }

    /// <summary>
    ///     Checks if the user meaningfully interacted with this item.
    /// </summary>
    /// <returns>True if played, favorited or has any playback.</returns>
    public bool HasMeaningfulInteraction()
        => Played || IsFavorite || PlayCount > 0 || PlaybackPositionTicks > 0;

    /// <summary>
    ///     Checks if the user actually played this item.
    /// </summary>
    /// <returns>True if there is real playback.</returns>
    public bool HasPlaybackActivity()
        => Played || PlayCount > 0 || PlaybackPositionTicks > 0;
}
