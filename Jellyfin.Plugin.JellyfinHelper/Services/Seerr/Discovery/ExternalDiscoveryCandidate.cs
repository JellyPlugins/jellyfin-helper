using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     A candidate supplied by an external discovery source (e.g. Trakt) to be scored through the shared
///     per-user ensemble pipeline. This is the public projection of the internal TMDb candidate shape so an
///     outside source can feed the scorer without taking a dependency on internal types.
/// </summary>
public sealed class ExternalDiscoveryCandidate
{
    /// <summary>Gets or sets the TMDb id. Required; candidates without one are dropped by the source.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the media type ("movie" or "tv").</summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the display title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the release/first-aired year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the overview/synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets observed rating; null, omitted and invalid source values are unavailable.</summary>
    [JsonPropertyName("voteAverage")]
    public double? VoteAverageValue
    {
        get;
        set => field = value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
    }

    /// <summary>Gets or sets the compatible numeric rating projection.</summary>
    [JsonIgnore]
    public double VoteAverage { get => VoteAverageValue.GetValueOrDefault(); set => VoteAverageValue = value; }

    /// <summary>Gets a value indicating whether the source actually supplied a finite non-negative rating.</summary>
    [JsonIgnore]
    public bool HasRatingMetadata => VoteAverageValue.HasValue;

    /// <summary>Gets or sets observed popularity; explicit zero remains known metadata.</summary>
    [JsonPropertyName("popularity")]
    public double? PopularityValue
    {
        get;
        set => field = value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
    }

    /// <summary>Gets or sets the compatible numeric popularity projection.</summary>
    [JsonIgnore]
    public double Popularity { get => PopularityValue.GetValueOrDefault(); set => PopularityValue = value; }

    /// <summary>Gets a value indicating whether the source actually supplied finite non-negative popularity.</summary>
    [JsonIgnore]
    public bool HasPopularityMetadata => PopularityValue.HasValue;

    /// <summary>Gets or sets the TMDb genre ids (used by the parental filter and genre features).</summary>
    public IReadOnlyList<int> GenreIds { get; set; } = [];

    /// <summary>Gets or sets optional source genre labels, without inventing TMDb genre IDs.</summary>
    public IReadOnlyList<string>? SourceGenres { get; set; }

    /// <summary>Gets or sets a value indicating whether source labels came from the same metadata observation and must follow its rejection.</summary>
    [JsonIgnore]
    public bool SourceGenresFromMetadata { get; set; }

    /// <summary>Gets or sets detached source metadata, including known TVDB/IMDb mappings and original-language hints.</summary>
    public TitleMetadataSnapshot? TitleMetadata { get; set; }

    /// <summary>Gets or sets the poster path (relative to the TMDb CDN).</summary>
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the Trakt URL slug (preferred for building working trakt.tv links).</summary>
    public string? TraktSlug { get; set; }

    /// <summary>
    ///     Gets or sets the rank the source assigned this candidate (0-based, lower is better). Preserves the
    ///     external source's own ordering (e.g. Trakt's recommendation order) so a rank-based pipeline can honor
    ///     it instead of re-ranking by local score. Null when the source supplies no ordering.
    /// </summary>
    public int? SourceRank { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is adult-flagged.</summary>
    public bool Adult { get; set; }
}
