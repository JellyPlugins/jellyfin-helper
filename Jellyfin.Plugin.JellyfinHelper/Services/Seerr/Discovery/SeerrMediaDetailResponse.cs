using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Response model for Seerr /api/v1/movie/{id} and /api/v1/tv/{id} endpoints.
///     Contains credits (cast/crew) data needed for people-based scoring, plus the metadata
///     (genres, poster, rating, availability) used to enrich external candidates before filtering.
/// </summary>
internal sealed class SeerrMediaDetailResponse
{
    private bool _collectionPropertyPresent;

    /// <summary>
    ///     Gets or sets the TMDb ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the movie title, distinguishing movie details from partial/search or TV payloads.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the required movie release-date field; an empty value is valid for an unreleased movie.</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

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
    ///     Gets or sets observed rating, preserving unavailable metadata separately from zero.
    /// </summary>
    [JsonPropertyName("voteAverage")]
    public double? VoteAverageValue
    {
        get;
        set => field = value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
    }

    /// <summary>Gets or sets the compatible numeric rating projection.</summary>
    [JsonIgnore]
    public double VoteAverage { get => VoteAverageValue.GetValueOrDefault(); set => VoteAverageValue = value; }

    /// <summary>Gets a value indicating whether a finite non-negative rating was supplied.</summary>
    [JsonIgnore]
    public bool HasRatingMetadata => VoteAverageValue.HasValue;

    /// <summary>
    ///     Gets or sets observed popularity, preserving unavailable metadata separately from zero.
    /// </summary>
    [JsonPropertyName("popularity")]
    public double? PopularityValue
    {
        get;
        set => field = value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
    }

    /// <summary>Gets or sets the compatible numeric popularity projection.</summary>
    [JsonIgnore]
    public double Popularity { get => PopularityValue.GetValueOrDefault(); set => PopularityValue = value; }

    /// <summary>Gets a value indicating whether finite non-negative popularity was supplied.</summary>
    [JsonIgnore]
    public bool HasPopularityMetadata => PopularityValue.HasValue;

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

    /// <summary>Gets or sets movie release certifications using the provider's documented country/release structure.</summary>
    [JsonPropertyName("releases")]
    public JsonElement Releases { get; set; }

    /// <summary>Gets or sets TV content certifications using the provider's documented country/rating structure.</summary>
    [JsonPropertyName("contentRatings")]
    public JsonElement ContentRatings { get; set; }

    /// <summary>Gets or sets provider keywords, preserving their IDs and names as separate metadata.</summary>
    [JsonPropertyName("keywords")]
    public List<GenreRef>? Keywords { get; set; }

    /// <summary>Gets or sets production companies, available independently of genre or age classifications.</summary>
    [JsonPropertyName("productionCompanies")]
    public List<GenreRef>? ProductionCompanies { get; set; }

    /// <summary>Gets or sets separately identified broadcast networks, never production-company evidence.</summary>
    [JsonPropertyName("networks")]
    public List<GenreRef>? Networks { get; set; }

    /// <summary>Gets or sets the original language; it does not establish available audio tracks.</summary>
    [JsonPropertyName("originalLanguage")]
    public string? OriginalLanguage { get; set; }

    /// <summary>Gets or sets the TV narrative format (for example Reality or Scripted).</summary>
    [JsonPropertyName("type")]
    public string? SeriesType { get; set; }

    /// <summary>Gets or sets series origin-country codes, separate from production countries.</summary>
    [JsonPropertyName("originCountry")]
    public List<string>? OriginCountry { get; set; }

    /// <summary>Gets or sets production-country references.</summary>
    [JsonPropertyName("productionCountries")]
    public List<CountryRef>? ProductionCountries { get; set; }

    /// <summary>Gets or sets the movie collection; an explicit null means the provider confirmed no collection.</summary>
    [JsonPropertyName("collection")]
    public GenreRef? Collection
    {
        get;
        set
        {
            field = value;
            _collectionPropertyPresent = true;
        }
    }

    /// <summary>Gets a value indicating whether collection membership was stated or its absence confirmed by complete movie details. Seerr omits the field for standalone movies; partial responses stay unknown.</summary>
    [JsonIgnore]
    public bool HasCollectionMetadata => _collectionPropertyPresent ? Collection is null || Collection.Id > 0 : HasCompleteMovieDetails;

    private bool HasCompleteMovieDetails => Id > 0 && !string.IsNullOrWhiteSpace(Title) && ReleaseDate is not null
        && Adult.HasValue && Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.TitleMetadataSnapshot.CaptureGenreIds(Genres).HasValue
        && Credits is { Cast: { } cast, Crew: { } crew }
        && cast.All(person => person is not null && person.Id > 0 && !string.IsNullOrWhiteSpace(person.Name))
        && crew.All(person => person is not null && person.Id > 0 && !string.IsNullOrWhiteSpace(person.Name) && !string.IsNullOrWhiteSpace(person.Job))
        && Keywords is not null && Keywords.All(keyword => keyword is not null && keyword.Id > 0 && !string.IsNullOrWhiteSpace(keyword.Name))
        && Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.ProducerEvidence.Capture(ProductionCompanies, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.ProducerEvidence.CompanyKind).HasValue
        && Releases.ValueKind == JsonValueKind.Object;

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

    /// <summary>A production country in the documented Seerr/TMDb response shape.</summary>
    public sealed class CountryRef
    {
        /// <summary>Gets or sets the ISO country code.</summary>
        [JsonPropertyName("iso_3166_1")]
        public string? Code { get; set; }
    }
}
