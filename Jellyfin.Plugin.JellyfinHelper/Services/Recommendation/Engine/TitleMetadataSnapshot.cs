using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
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

    /// <summary>Gets provider-qualified production companies; null is unknown, empty is verified absence.</summary>
    public ImmutableArray<ProviderStudioReference>? ProductionCompanies { get; init; }

    /// <summary>Gets separately qualified broadcasters; these never count as production companies.</summary>
    public ImmutableArray<ProviderStudioReference>? Networks { get; init; }

    /// <summary>Gets same-title provider credits with independent cast/crew acquisition and completeness.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CreditMetadataSnapshot? Credits { get; init; }

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

    /// <summary>Gets the hashed provider/configuration/language scope of a raw observation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservationScope { get; init; }

    /// <summary>Gets flattened independent observations retained by a combined projection, without recursive projections.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<TitleMetadataSnapshot>? SourceObservations { get; init; }

    /// <summary>Projects individual original fields at a historical or current boundary without renewing their times.</summary>
    /// <param name="cutoff">The latest permitted acquisition time.</param>
    /// <param name="oldest">Optional expiry boundary; historical views do not impose today's expiry.</param>
    /// <returns>Available original evidence, or null when no field can be established.</returns>
    internal TitleMetadataSnapshot? At(DateTime cutoff, DateTime? oldest = null)
    {
        bool Available(DateTime time) => time != default && DateTimeNormalization.ToUtc(time) <= cutoff
            && (!oldest.HasValue || DateTimeNormalization.ToUtc(time) > oldest.Value);
        if (SourceObservations is { IsDefaultOrEmpty: false } sources)
        {
            return TitleMetadataMerger.Combine(sources.OfType<TitleMetadataSnapshot>().Select(source => source.At(cutoff, oldest)).OfType<TitleMetadataSnapshot>().ToArray());
        }

        if (Credits is null && Available(FetchedAtUtc) && (FieldFetchedAtUtc is null || FieldFetchedAtUtc.Values.All(Available)))
        {
            return this;
        }

        var times = ImmutableDictionary.CreateBuilder<string, DateTime>(StringComparer.Ordinal);
        T Field<T>(T value, string name)
        {
            var time = FieldFetchedAtUtc?.GetValueOrDefault(name, FetchedAtUtc) ?? FetchedAtUtc;
            if (value is null || !Available(time))
            {
                return default!;
            }

            times[name] = time;
            return value;
        }

        var credits = Credits is { } captured && captured.TmdbId == TmdbId && captured.MediaType == MediaType ? captured.At(cutoff, oldest) : null;
        var creditTimes = new[] { credits?.Cast?.AcquiredAtUtc, credits?.Crew?.AcquiredAtUtc, credits?.CountriesAcquiredAtUtc }.OfType<DateTime>().ToArray();
        if (creditTimes.Length > 0)
        {
            times[nameof(Credits)] = creditTimes.Max();
        }
        else
        {
            credits = null;
        }

        var projection = this with
        {
            TvdbId = Field(TvdbId > 0 ? (int?)TvdbId : null, nameof(TvdbId)) ?? 0,
            ImdbId = Field(ImdbId, nameof(ImdbId)),
            GenreIds = Field(GenreIds, nameof(GenreIds)),
            SourceGenres = Field(SourceGenres, nameof(SourceGenres)),
            KeywordIds = Field(KeywordIds, nameof(KeywordIds)),
            Keywords = Field(Keywords, nameof(Keywords)),
            KeywordReferences = Field(KeywordReferences, nameof(KeywordReferences)),
            OriginalLanguage = Field(OriginalLanguage, nameof(OriginalLanguage)),
            SeriesType = Field(SeriesType, nameof(SeriesType)),
            AnimeHint = Field(AnimeHint, nameof(AnimeHint)),
            OriginCountries = Field(OriginCountries, nameof(OriginCountries)),
            ProductionCountries = Field(ProductionCountries, nameof(ProductionCountries)),
            ProductionCompanies = Field(ProductionCompanies, nameof(ProductionCompanies)),
            Networks = Field(Networks, nameof(Networks)),
            Overview = Field(Overview, nameof(Overview)),
            Credits = credits,
            SourceObservations = null
        };
        if (times.Count == 0)
        {
            return null;
        }

        var conflicts = ConflictingValues?.Where(pair => Available(FieldFetchedAtUtc?.GetValueOrDefault(pair.Key, FetchedAtUtc) ?? FetchedAtUtc))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return projection with { FetchedAtUtc = times.Values.Max(), FieldFetchedAtUtc = times.ToImmutable(), ConflictingValues = conflicts };
    }

    internal static TitleMetadataSnapshot FromDetail(SeerrMediaDetailResponse detail, string mediaType, DateTime fetchedAtUtc)
        => new()
        {
            TmdbId = detail.Id,
            MediaType = mediaType,
            GenreIds = CaptureGenreIds(detail.Genres),
            KeywordIds = CaptureList(detail.Keywords, keyword => keyword.Id <= 0, keyword => keyword.Id),
            Keywords = CaptureList(detail.Keywords, keyword => string.IsNullOrWhiteSpace(keyword.Name), keyword => keyword.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            KeywordReferences = CaptureList(detail.Keywords, keyword => keyword.Id <= 0 || string.IsNullOrWhiteSpace(keyword.Name), keyword => new ProviderKeyword(keyword.Id, keyword.Name)),
            OriginalLanguage = detail.OriginalLanguage,
            SeriesType = detail.SeriesType,
            OriginCountries = CaptureList(detail.OriginCountry, string.IsNullOrWhiteSpace, country => country, StringComparer.OrdinalIgnoreCase),
            ProductionCountries = CaptureList(detail.ProductionCountries, country => string.IsNullOrWhiteSpace(country.Code), country => country.Code ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            ProductionCompanies = ProducerEvidence.Capture(detail.ProductionCompanies, ProducerEvidence.CompanyKind),
            Networks = ProducerEvidence.Capture(detail.Networks, ProducerEvidence.NetworkKind),
            Credits = detail.Credits is null ? null : CreditMetadataCapture.External(detail.Credits, fetchedAtUtc, detail.Id, mediaType),
            Overview = detail.Overview,
            FetchedAtUtc = fetchedAtUtc
        };

    /// <summary>Distinguishes a validated explicit list from an omitted or malformed provider field.</summary>
    /// <param name="genres">The actual detail field, not a filtered projection.</param>
    /// <returns>Valid IDs, known empty, or unknown; malformed entries cannot clear prior genre evidence.</returns>
    internal static ImmutableArray<int>? CaptureGenreIds(IReadOnlyList<SeerrMediaDetailResponse.GenreRef>? genres)
        => CaptureList(genres, genre => genre.Id <= 0, genre => genre.Id);

    private static ImmutableArray<TValue>? CaptureList<T, TValue>(IReadOnlyList<T>? values, Func<T, bool> invalid, Func<T, TValue> project, IEqualityComparer<TValue>? comparer = null)
        where T : class
        => values is null || values.Any(value => value is null || invalid(value))
            ? null
            : values.Select(project).Distinct(comparer).ToImmutableArray();
}
