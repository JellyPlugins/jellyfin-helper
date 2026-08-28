using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

/// <summary>
///     Aggregated watch profile for a single Jellyfin user.
/// </summary>
public sealed class UserWatchProfile
{
    private Dictionary<string, int> _genreDistribution = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LanguageProfileEntry> _languageProfile = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LanguageProfileEntry> _subtitleLanguageProfile = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _peopleProfile = new(StringComparer.OrdinalIgnoreCase);
    private Collection<WatchedItemInfo> _watchedItems = [];
    private string? _primaryLanguage;
    private HashSet<string>? _preferredLanguages;
    private HashSet<string>? _toleratedLanguages;
    private string? _primarySubtitleLanguage;
    private HashSet<string>? _preferredSubtitleLanguages;
    private HashSet<string>? _toleratedSubtitleLanguages;
    private IReadOnlyList<string>? _topPeople;

    /// <summary>
    ///     Gets or sets the Jellyfin user ID.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    ///     Gets or sets the user's display name.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the total number of watched movies.
    /// </summary>
    public int WatchedMovieCount { get; set; }

    /// <summary>
    ///     Gets or sets the total number of watched episodes.
    /// </summary>
    public int WatchedEpisodeCount { get; set; }

    /// <summary>
    ///     Gets or sets the total number of watched series (at least one episode played).
    /// </summary>
    public int WatchedSeriesCount { get; set; }

    /// <summary>
    ///     Gets or sets the total unique content runtime in ticks (sum of runtime for each distinct played item, counted once regardless of PlayCount).
    /// </summary>
    public long TotalWatchTimeTicks { get; set; }

    /// <summary>
    ///     Gets or sets the date of the most recent play activity (UTC).
    /// </summary>
    public DateTime? LastActivityDate { get; set; }

    /// <summary>
    ///     Gets or sets the genre distribution (genre name to watch count). The setter preserves OrdinalIgnoreCase to ensure genre aggregation is always case-insensitive, even when a new dictionary is assigned.
    /// </summary>
    public Dictionary<string, int> GenreDistribution
    {
        get => _genreDistribution;
        set => _genreDistribution = value is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Gets or sets the number of favorite items.
    /// </summary>
    public int FavoriteCount { get; set; }

    /// <summary>
    ///     Gets the set of series IDs that the user has marked as favorite at the series level.
    /// </summary>
    public HashSet<Guid> FavoriteSeriesIds { get; init; } = [];

    /// <summary>
    ///     Gets or sets the average community rating of watched items.
    /// </summary>
    public double AverageCommunityRating { get; set; }

    /// <summary>
    ///     Gets or sets the user's maximum allowed parental rating value. Corresponds to the Jellyfin user setting MaxParentalRating.
    /// </summary>
    public int? MaxParentalRating { get; set; }

    /// <summary>
    ///     Gets or sets the list of watched items with detailed play data.
    ///     Setter coalesces null to empty to prevent NRE from deserialized cache data.
    /// </summary>
    public Collection<WatchedItemInfo> WatchedItems
    {
        get => _watchedItems;
        set => _watchedItems = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the audio language preference profile. Maps normalized ISO 639-1 language codes to chosen/forced counts.
    /// </summary>
    public Dictionary<string, LanguageProfileEntry> LanguageProfile
    {
        get => _languageProfile;
        set
        {
            _languageProfile = value is null
                ? new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, LanguageProfileEntry>(value, StringComparer.OrdinalIgnoreCase);
            _primaryLanguage = null; // invalidate cache
            _preferredLanguages = null;
            _toleratedLanguages = null;
        }
    }

    /// <summary>
    ///     Gets the user's primary audio language (highest weighted score), or null if no data.
    /// </summary>
    /// <remarks>
    ///     WARNING: This cache is only invalidated when LanguageProfile is reassigned. In-place mutation of LanguageProfileEntry objects will produce stale values.
    /// </remarks>
    [JsonIgnore]
    public string? PrimaryLanguage => _primaryLanguage ??= LanguageProfile.Count > 0
        ? LanguageProfile.MaxBy(kv => kv.Value.WeightedScore).Key
        : null;

    /// <summary>
    ///     Gets the set of languages the user has actively chosen (ChosenCount &gt; 0). These represent true preferences - the user had alternatives and picked this language.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> PreferredLanguages => _preferredLanguages ??= new(
        LanguageProfile.Where(kv => kv.Value is { ChosenCount: > 0 }).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the set of languages the user has only used when forced (no alternatives). These represent tolerance, not preference.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> ToleratedLanguages => _toleratedLanguages ??= new(
        LanguageProfile.Where(kv => kv.Value is { ForcedCount: > 0, ChosenCount: 0 }).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets or sets the subtitle language preference profile. Maps normalized ISO 639-1 language codes to chosen/forced counts for subtitle tracks.
    /// </summary>
    public Dictionary<string, LanguageProfileEntry> SubtitleLanguageProfile
    {
        get => _subtitleLanguageProfile;
        set
        {
            _subtitleLanguageProfile = value is null
                ? new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, LanguageProfileEntry>(value, StringComparer.OrdinalIgnoreCase);
            _primarySubtitleLanguage = null; // invalidate cache
            _preferredSubtitleLanguages = null;
            _toleratedSubtitleLanguages = null;
        }
    }

    /// <summary>
    ///     Gets the user's primary subtitle language (highest weighted score), or null if no data.
    /// </summary>
    [JsonIgnore]
    public string? PrimarySubtitleLanguage => _primarySubtitleLanguage ??= SubtitleLanguageProfile.Count > 0
        ? SubtitleLanguageProfile.MaxBy(kv => kv.Value.WeightedScore).Key
        : null;

    /// <summary>
    ///     Gets the set of subtitle languages the user has actively chosen (ChosenCount &gt; 0).
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> PreferredSubtitleLanguages => _preferredSubtitleLanguages ??= new(
        SubtitleLanguageProfile.Where(kv => kv.Value is { ChosenCount: > 0 }).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the set of subtitle languages the user has only used when forced (no alternatives).
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> ToleratedSubtitleLanguages => _toleratedSubtitleLanguages ??= new(
        SubtitleLanguageProfile.Where(kv => kv.Value is { ForcedCount: > 0, ChosenCount: 0 }).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets or sets the people (actors/directors) preference profile. Maps person names to the number of distinct items featuring that person that the user has watched or favorited.
    /// </summary>
    public Dictionary<string, int> PeopleProfile
    {
        get => _peopleProfile;
        set
        {
            _peopleProfile = value is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(value, StringComparer.OrdinalIgnoreCase);
            _topPeople = null; // invalidate cache
        }
    }

    /// <summary>
    ///     Gets the user's top preferred people (actors/directors) ordered by frequency. Returns the names of people who appear most frequently across the user's watched and favorited items.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> TopPeople => _topPeople ??= PeopleProfile.Count > 0
        ? PeopleProfile
            .Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(kv => kv.Key)
            .ToList()
        : [];
}