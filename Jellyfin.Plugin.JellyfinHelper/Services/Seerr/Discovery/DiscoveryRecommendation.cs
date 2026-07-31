using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     A single discovery recommendation for a user - represents a media item
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
    ///     Gets or sets the computed recommendation score (0-1). Clamped to valid range.
    ///     Non-finite values (NaN, Infinity) are coerced to 0.0 to prevent JSON serialization failures.
    /// </summary>
    public double Score
    {
        get;
        set => field = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
    }

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
    ///     Gets or sets the TMDb community rating (0-10). Clamped to valid range.
    ///     Non-finite values (NaN, Infinity) are coerced to 0.0 to prevent JSON serialization failures.
    /// </summary>
    public double TmdbRating
    {
        get;
        set => field = double.IsFinite(value) ? Math.Clamp(value, 0.0, 10.0) : 0.0;
    }

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

    /// <summary>
    ///     Gets or sets the known people (actors/directors) from credits enrichment.
    ///     Excluded from JSON serialization to the frontend (not needed for display).
    ///     Persisted to the feedback store for PeopleSimilarity training signal.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string>? KnownPeople { get; set; }

    /// <summary>
    ///     Gets or sets the raw TMDb popularity value at the time of discovery.
    ///     Carried through JSON serialization to the discovery cache file (and, incidentally,
    ///     to any frontend response) so that <c>DiscoveryFeedbackStore.RecordShown</c> receives
    ///     a non-zero value even when the recommendations pass through a cache round-trip
    ///     (server restart between generation and the next scheduled run's feedback recording).
    ///     The training pipeline uses this value to reconstruct the exact <c>PopularityScore</c>
    ///     feature used at inference via <c>ExternalCandidateFeatureBuilder.NormalizePopularity</c>.
    ///     <para>
    ///         Previously carried <see cref="JsonIgnoreAttribute"/> to hide the field from the
    ///         frontend. That was fragile: <see cref="DiscoveryCacheService"/> persists this DTO
    ///         to disk via <c>JsonSerializer</c>, so a <see cref="JsonIgnoreAttribute"/> would
    ///         silently drop the value on every cache reload - leaving <c>RecordShown</c> to
    ///         backfill the feedback store with <c>Popularity=0</c> and quietly re-introducing
    ///         the train/serve skew this field was added to eliminate. Frontend consumers simply
    ///         ignore the extra field; the payload cost is a handful of bytes per recommendation.
    ///     </para>
    ///     Non-finite values are coerced to 0 to keep the persisted feedback store clean.
    /// </summary>
    public double Popularity
    {
        get;
        set => field = double.IsFinite(value) && value > 0 ? value : 0.0;
    }

    /// <summary>
    ///     Returns a detached shallow copy of this recommendation. All scalar fields are copied
    ///     by value. <see cref="Genres"/> and <see cref="KnownPeople"/> are already
    ///     <see cref="IReadOnlyList{T}"/> of immutable <see cref="string"/> elements, so the
    ///     references are safe to share - no string copy is needed.
    /// </summary>
    /// <returns>A detached copy of this <see cref="DiscoveryRecommendation"/>.</returns>
    public DiscoveryRecommendation Clone() => new()
    {
        TmdbId = TmdbId,
        MediaType = MediaType,
        Title = Title,
        Year = Year,
        Score = Score,
        Reason = Reason,
        ReasonKey = ReasonKey,
        RelatedInfo = RelatedInfo,
        Genres = Genres,
        TmdbRating = TmdbRating,
        PosterPath = PosterPath,
        Overview = Overview,
        AlreadyRequested = AlreadyRequested,
        KnownPeople = KnownPeople,
        Popularity = Popularity,
    };
}