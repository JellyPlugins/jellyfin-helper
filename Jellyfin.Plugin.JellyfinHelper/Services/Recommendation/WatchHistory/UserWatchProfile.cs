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
    /// </summary>
    public Collection<WatchedItemInfo> WatchedItems { get; set; } = [];

    /// <summary>
    ///     Gets or sets the audio language preference profile.
    ///     Maps normalized ISO 639-1 language codes to chosen/forced counts.
    ///     Built by analyzing which audio tracks the user selected vs. which were available.
    ///     Key distinction: "chosen" (user had alternatives) vs. "forced" (only option).
    /// </summary>
    public Dictionary<string, LanguageProfileEntry> LanguageProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the user's primary audio language (highest weighted score), or null if no data.
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    /// </summary>
    [JsonIgnore]
    public string? PrimaryLanguage => LanguageProfile.Count > 0
        ? LanguageProfile.MaxBy(kv => kv.Value.WeightedScore).Key
        : null;

    /// <summary>
    ///     Gets the set of languages the user has actively chosen (ChosenCount &gt; 0).
    ///     These represent true preferences — the user had alternatives and picked this language.
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    /// </summary>
    [JsonIgnore]
    public HashSet<string> PreferredLanguages => new(
        LanguageProfile.Where(kv => kv.Value.ChosenCount > 0).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the set of languages the user has only used when forced (no alternatives).
    ///     These represent tolerance, not preference.
    ///     Excluded from JSON serialization to avoid redundant data in API responses.
    /// </summary>
    [JsonIgnore]
    public HashSet<string> ToleratedLanguages => new(
        LanguageProfile.Where(kv => kv.Value.ForcedCount > 0 && kv.Value.ChosenCount == 0).Select(kv => kv.Key),
        StringComparer.OrdinalIgnoreCase);
}