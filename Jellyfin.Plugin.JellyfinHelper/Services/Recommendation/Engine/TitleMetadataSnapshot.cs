using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Immutable title metadata, shared across users without carrying playback, permissions or predictions.</summary>
public sealed record TitleMetadataSnapshot
{
    /// <summary>Gets the typed provider title identity.</summary>
    public int TmdbId { get; init; }

    /// <summary>Gets an optional television identity supplied by Jellyfin, Sonarr or Trakt/TVDB mappings.</summary>
    public int TvdbId { get; init; }

    /// <summary>Gets an optional IMDb identity linking configured provider observations.</summary>
    public string? ImdbId { get; init; }

    /// <summary>Gets the provider namespace, movie or tv.</summary>
    public string MediaType { get; init; } = "movie";

    /// <summary>Gets the provider genre IDs; null means unavailable, not an empty genre list.</summary>
    public ImmutableArray<int>? GenreIds { get; init; }

    /// <summary>Gets source genre labels when stable TMDb genre IDs are unavailable.</summary>
    public ImmutableArray<string>? SourceGenres { get; init; }

    /// <summary>Gets the provider keyword IDs, independent of translated labels.</summary>
    public ImmutableArray<int>? KeywordIds { get; init; }

    /// <summary>Gets the provider keyword labels; null means unavailable.</summary>
    public ImmutableArray<string>? Keywords { get; init; }

    /// <summary>Gets paired keyword identities and labels, retaining provenance when translations differ.</summary>
    public ImmutableArray<ProviderKeyword>? KeywordReferences { get; init; }

    /// <summary>Gets the original language, not an audio-language preference.</summary>
    public string? OriginalLanguage { get; init; }

    /// <summary>Gets the provider's series format, not its lifecycle status.</summary>
    public string? SeriesType { get; init; }

    /// <summary>Gets a provider's explicit Anime-mode hint. False clears that hint; it never proves western animation or an age rating.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AnimeHint { get; init; }

    /// <summary>Gets the series' countries of origin.</summary>
    public ImmutableArray<string>? OriginCountries { get; init; }

    /// <summary>Gets the production country codes.</summary>
    public ImmutableArray<string>? ProductionCountries { get; init; }

    /// <summary>Gets the localized synopsis. It is metadata, never executable classification instructions.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the time this metadata was obtained, in UTC.</summary>
    public DateTime FetchedAtUtc { get; init; }

    /// <summary>Gets the acquisition times of individual fields retained across partial provider responses.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, DateTime>? FieldFetchedAtUtc { get; init; }

    /// <summary>Gets preserved incompatible source values; they remain unknown classification evidence.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, ImmutableArray<string>>? ConflictingValues { get; init; }

    /// <summary>Gets the source of the snapshot.</summary>
    public string Source { get; init; } = "seerr";

    internal static TitleMetadataSnapshot FromDetail(SeerrMediaDetailResponse detail, string mediaType, DateTime fetchedAtUtc)
        => new()
        {
            TmdbId = detail.Id,
            MediaType = mediaType,
            GenreIds = detail.Genres?.OfType<SeerrMediaDetailResponse.GenreRef>().Where(genre => genre.Id > 0).Select(genre => genre.Id).Distinct().ToImmutableArray(),
            KeywordIds = detail.Keywords?.OfType<SeerrMediaDetailResponse.GenreRef>().Where(keyword => keyword.Id > 0).Select(keyword => keyword.Id).Distinct().ToImmutableArray(),
            Keywords = detail.Keywords?.OfType<SeerrMediaDetailResponse.GenreRef>().Select(keyword => keyword.Name).OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            KeywordReferences = detail.Keywords?.OfType<SeerrMediaDetailResponse.GenreRef>().Select(keyword => new ProviderKeyword(keyword.Id, keyword.Name)).Distinct().ToImmutableArray(),
            OriginalLanguage = detail.OriginalLanguage,
            SeriesType = detail.SeriesType,
            OriginCountries = detail.OriginCountry?.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            ProductionCountries = detail.ProductionCountries?.OfType<SeerrMediaDetailResponse.CountryRef>().Select(country => country.Code).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            Overview = detail.Overview,
            FetchedAtUtc = fetchedAtUtc
        };
}
