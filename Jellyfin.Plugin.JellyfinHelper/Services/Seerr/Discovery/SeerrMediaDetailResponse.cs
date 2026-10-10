using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Response model for Seerr /api/v1/movie/{id} and /api/v1/tv/{id} endpoints.
///     Contains credits (cast/crew) data needed for people-based scoring, plus the metadata
///     (genres, poster, rating, availability) used to enrich external candidates before filtering.
/// </summary>
internal sealed class SeerrMediaDetailResponse
{
    /// <summary>
    ///     Gets or sets the TMDb ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the credits information containing cast and crew.
    /// </summary>
    [JsonPropertyName("credits")]
    public SeerrCredits? Credits { get; set; }

    /// <summary>
    ///     Gets or sets the genres. Null when the endpoint omits them; never trusted blindly.
    /// </summary>
    [JsonPropertyName("genres")]
    public List<GenreRef>? Genres { get; set; }

    /// <summary>
    ///     Gets or sets the poster path (relative to TMDb CDN).
    /// </summary>
    [JsonPropertyName("posterPath")]
    public string? PosterPath { get; set; }

    /// <summary>
    ///     Gets or sets the overview/synopsis in Seerr's configured language. Used to replace an external
    ///     source's own-language overview (e.g. Trakt's always-English text) so cards read in the Seerr locale.
    /// </summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>
    ///     Gets or sets the average vote score. Zero when omitted.
    /// </summary>
    [JsonPropertyName("voteAverage")]
    public double VoteAverage { get; set; }

    /// <summary>
    ///     Gets or sets the popularity score. Zero when omitted.
    /// </summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>
    ///     Gets or sets whether this is adult content. Null when the endpoint omits the flag;
    ///     callers must only latch it to true, never clear on null.
    /// </summary>
    [JsonPropertyName("adult")]
    public bool? Adult { get; set; }

    /// <summary>
    ///     Gets or sets the Seerr media availability info. Null when Seerr never saw this title.
    /// </summary>
    [JsonPropertyName("mediaInfo")]
    public TmdbDiscoverMediaInfo? MediaInfo { get; set; }

    /// <summary>
    ///     A single genre entry in a Seerr detail response.
    /// </summary>
    public sealed class GenreRef
    {
        /// <summary>
        ///     Gets or sets the TMDb genre ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        ///     Gets or sets the genre name.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}