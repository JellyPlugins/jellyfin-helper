using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     A single result from TMDb discover/search via Seerr.
/// </summary>
internal sealed class TmdbDiscoverItem
{
    private List<int> _genreIds = [];

    /// <summary>
    ///     Gets or sets the TMDb ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the media type ("movie" or "tv").
    /// </summary>
    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "movie";

    /// <summary>
    ///     Gets or sets the movie title (null for TV).
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    ///     Gets or sets the TV series name (null for movies).
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets the display title, preferring Title over Name.
    /// </summary>
    [JsonIgnore]
    public string DisplayTitle => Title ?? Name ?? "Unknown";

    /// <summary>
    ///     Gets or sets the TMDb genre IDs. Setter null-coalesces to empty list to prevent NullReferenceException when JSON deserialization yields a null value for this field.
    /// </summary>
    [JsonPropertyName("genreIds")]
    public List<int> GenreIds
    {
        get => _genreIds;
        set => _genreIds = value ?? [];
    }

    /// <summary>
    ///     Gets or sets the average vote score (0-10).
    /// </summary>
    [JsonPropertyName("voteAverage")]
    public double VoteAverage { get; set; }

    /// <summary>
    ///     Gets or sets the popularity score.
    /// </summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>
    ///     Gets or sets the movie release date.
    ///     Uses a custom converter to handle empty strings from TMDb/Seerr gracefully.
    /// </summary>
    [JsonPropertyName("releaseDate")]
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? ReleaseDate { get; set; }

    /// <summary>
    ///     Gets or sets the TV first air date.
    ///     Uses a custom converter to handle empty strings from TMDb/Seerr gracefully.
    /// </summary>
    [JsonPropertyName("firstAirDate")]
    [JsonConverter(typeof(NullableDateTimeConverter))]
    public DateTime? FirstAirDate { get; set; }

    /// <summary>
    ///     Gets the effective release date, preferring ReleaseDate over FirstAirDate.
    /// </summary>
    [JsonIgnore]
    public DateTime? EffectiveReleaseDate => ReleaseDate ?? FirstAirDate;

    /// <summary>
    ///     Gets or sets the poster path (relative to TMDb CDN).
    /// </summary>
    [JsonPropertyName("posterPath")]
    public string? PosterPath { get; set; }

    /// <summary>
    ///     Gets or sets the overview/description.
    /// </summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether this is adult content.
    ///     TMDb marks explicit adult content with this flag.
    /// </summary>
    [JsonPropertyName("adult")]
    public bool Adult { get; set; }

    /// <summary>
    ///     Gets or sets known people names (populated from search results where cast data is embedded).
    /// </summary>
    [JsonIgnore]
    public List<string>? KnownPeople { get; set; }
}