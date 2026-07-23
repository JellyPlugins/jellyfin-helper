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
    ///     Gets or sets the total unique content runtime in ticks (sum of runtime for each
    ///     distinct played item, counted once regardless of <c>PlayCount</c>).
    ///     This represents "how much unique content was consumed", not "total time spent watching"
    ///     which would require multiplying by re-watch count.
    /// </summary>
    public long TotalWatchTimeTicks { get; set; }

    /// <summary>
    ///     Gets or sets the date of the most recent play activity (UTC).
    /// </summary>
    public DateTime? LastActivityDate { get; set; }

    /// <summary>
    ///     Gets or sets the genre distribution (genre name → watch count).
    ///     The setter preserves <see cref="StringComparer.OrdinalIgnoreCase"/> to ensure
    ///     genre aggregation is always case-insensitive, even when a new dictionary is assigned.
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
    ///     In Jellyfin, users can favorite a whole series (not just individual episodes).
    ///     This set captures those series-level favorites so that the recommendation engine
    ///     can treat them as positive signals even when no individual episode is favorited.
    /// </summary>
    public HashSet<Guid> FavoriteSeriesIds { get; init; } = [];

    /// <summary>
    ///     Gets or sets the average community rating of watched items.
    /// </summary>
    public double AverageCommunityRating { get; set; }

    /// <summary>
    ///     Gets or sets the user's maximum allowed parental rating value.
    ///     Corresponds to the Jellyfin user setting <c>MaxParentalRating</c>.
    ///     When set, recommendation candidates with <c>InheritedParentalRatingValue</c>
    ///     exceeding this value are excluded from scoring.
    ///     Null means no restriction (the user can see all content).
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
    ///     Gets or sets the audio language preference profile.
    ///     Maps normalized ISO 639-1 language codes to chosen/forced counts.
    ///     Built by analyzing which audio tracks the user selected vs. which were available.
    ///     Key distinction: "chosen" (user had alternatives) vs. "forced" (only option).
    ///     Setter preserves <see cref="StringComparer.OrdinalIgnoreCase"/> and coalesces null.
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
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    ///     Computed lazily and cached to avoid repeated LINQ evaluation.
    ///     Note: the cache is invalidated when <see cref="LanguageProfile"/> is reassigned via
    ///     its setter, but not when entries are mutated in-place on the underlying dictionary.
    ///     Callers must reassign <see cref="LanguageProfile"/> to guarantee cache coherence
    ///     after in-place modifications.
    /// </summary>
    [JsonIgnore]
    public string? PrimaryLanguage => _primaryLanguage ??= LanguageProfile.Count > 0
        ? LanguageProfile.MaxBy(kv => kv.Value.WeightedScore).Key
        : null;

    /// <summary>
    ///     Gets the set of languages the user has actively chosen (ChosenCount &gt; 0).
    ///     These represent true preferences - the user had alternatives and picked this language.
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    ///     Computed lazily and cached to avoid repeated collection allocation.
    ///     Returns a read-only view to prevent external mutation of cached state.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> PreferredLanguages => _preferredLanguages ??= new(
        LanguageProfile.Where(kv => kv.Value is { ChosenCount: > 0 }).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the set of languages the user has only used when forced (no alternatives).
    ///     These represent tolerance, not preference.
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    ///     Computed lazily and cached to avoid repeated collection allocation.
    ///     Returns a read-only view to prevent external mutation of cached state.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> ToleratedLanguages => _toleratedLanguages ??= new(
        LanguageProfile.Where(kv => kv.Value is { ForcedCount: > 0, ChosenCount: 0 }).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets or sets the subtitle language preference profile.
    ///     Maps normalized ISO 639-1 language codes to chosen/forced counts for subtitle tracks.
    ///     Built by analyzing which subtitle tracks the user selected vs. which were available.
    ///     Setter preserves <see cref="StringComparer.OrdinalIgnoreCase"/> and coalesces null.
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
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    ///     Computed lazily and cached to avoid repeated LINQ evaluation.
    /// </summary>
    [JsonIgnore]
    public string? PrimarySubtitleLanguage => _primarySubtitleLanguage ??= SubtitleLanguageProfile.Count > 0
        ? SubtitleLanguageProfile.MaxBy(kv => kv.Value.WeightedScore).Key
        : null;

    /// <summary>
    ///     Gets the set of subtitle languages the user has actively chosen (ChosenCount &gt; 0).
    ///     These represent true preferences - the user had alternatives and picked this subtitle language.
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    ///     Computed lazily and cached to avoid repeated collection allocation.
    ///     Returns a read-only view to prevent external mutation of cached state.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> PreferredSubtitleLanguages => _preferredSubtitleLanguages ??= new(
        SubtitleLanguageProfile.Where(kv => kv.Value is { ChosenCount: > 0 }).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the set of subtitle languages the user has only used when forced (no alternatives).
    ///     These represent tolerance, not preference.
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    ///     Computed lazily and cached to avoid repeated collection allocation.
    ///     Returns a read-only view to prevent external mutation of cached state.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string> ToleratedSubtitleLanguages => _toleratedSubtitleLanguages ??= new(
        SubtitleLanguageProfile.Where(kv => kv.Value is { ForcedCount: > 0, ChosenCount: 0 }).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets or sets the people (actors/directors) preference profile.
    ///     Maps person names to the number of distinct items featuring that person
    ///     that the user has watched or favorited.
    ///     Built by analyzing <c>BaseItem.People</c> metadata for each watched item.
    ///     Only includes persons with role type "Actor" or "Director".
    ///     Setter preserves <see cref="StringComparer.OrdinalIgnoreCase"/> and coalesces null.
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
    ///     Gets the user's top preferred people (actors/directors) ordered by frequency.
    ///     Returns the names of people who appear most frequently across the user's
    ///     watched and favorited items. Limited to those appearing in at least 2 items
    ///     to filter out noise from single-watch appearances.
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    ///     Computed lazily and cached to avoid repeated collection allocation.
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