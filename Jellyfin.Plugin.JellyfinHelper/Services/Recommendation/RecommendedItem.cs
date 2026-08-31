using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     A single recommendation for a user. Cached fields keep training and inference in sync.
/// </summary>
public sealed class RecommendedItem
{
    private IReadOnlyList<string> _audioLanguages = [];
    private IReadOnlyList<Guid> _boxSetIds = [];
    private IReadOnlyList<string> _genres = [];
    private IReadOnlyList<string> _inheritedTags = [];
    private IReadOnlyList<string> _peopleNames = [];
    private IReadOnlyList<double> _peopleWeights = [];
    private IReadOnlyList<string> _productionCountries = [];
    private IReadOnlyList<string> _studios = [];
    private IReadOnlyList<string> _subtitleLanguages = [];
    private IReadOnlyList<string> _tags = [];
    private IReadOnlyList<string> _writerNames = [];

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
    ///     Gets or sets the combined recommendation score (0.0-1.0).
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
    ///     Gets or sets the community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    ///     Gets or sets the Rotten Tomatoes critic rating. Cached for training.
    /// </summary>
    public float? CriticRating { get; set; }

    /// <summary>
    ///     Gets or sets the primary image tag for poster display.
    /// </summary>
    public string? PrimaryImageTag { get; set; }

    /// <summary>
    ///     Gets or sets the official rating.
    /// </summary>
    public string? OfficialRating { get; set; }

    /// <summary>
    ///     Gets or sets the premiere date.
    /// </summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>
    ///     Gets or sets the people names. Cached for training.
    /// </summary>
    public IReadOnlyList<string> PeopleNames
    {
        get => _peopleNames;
        set => _peopleNames = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the studio names. Cached for training.
    /// </summary>
    public IReadOnlyList<string> Studios
    {
        get => _studios;
        set => _studios = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the tags. Cached for training.
    /// </summary>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        set => _tags = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the audio language codes. Cached for training.
    /// </summary>
    public IReadOnlyList<string> AudioLanguages
    {
        get => _audioLanguages;
        set => _audioLanguages = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the subtitle language codes. Cached for training.
    /// </summary>
    public IReadOnlyList<string> SubtitleLanguages
    {
        get => _subtitleLanguages;
        set => _subtitleLanguages = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the BoxSet ids. Cached for training.
    /// </summary>
    public IReadOnlyList<Guid> BoxSetIds
    {
        get => _boxSetIds;
        set => _boxSetIds = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the date the item was added to the library.
    /// </summary>
    public DateTime? DateCreated { get; set; }

    /// <summary>
    ///     Gets or sets the TMDb collection name if any.
    /// </summary>
    public string? TmdbCollectionName { get; set; }

    /// <summary>
    ///     Gets or sets the production countries. Cached for training.
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
    ///     Gets or sets the writer names. Cached for training.
    /// </summary>
    public IReadOnlyList<string> WriterNames
    {
        get => _writerNames;
        set => _writerNames = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the billing weights aligned positionally to PeopleNames (higher = more top-billed, derived from PersonInfo.SortOrder).
    /// </summary>
    public IReadOnlyList<double> PeopleWeights
    {
        get => _peopleWeights;
        set => _peopleWeights = value ?? [];
    }
}
