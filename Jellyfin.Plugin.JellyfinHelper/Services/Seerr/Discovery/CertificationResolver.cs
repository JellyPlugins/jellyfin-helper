using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>Resolves actual provider certifications through Jellyfin's own rating system, not genre heuristics.</summary>
internal static class CertificationResolver
{
    /// <summary>Returns the most restrictive recognized provider certification, or null when none can be verified.</summary>
    /// <param name="detail">The fetched provider detail payload.</param>
    /// <param name="localization">Jellyfin's authoritative rating mapper.</param>
    /// <param name="adultFlag">A latched adult flag from the original provider candidate.</param>
    /// <returns>A verified rating score in the same scale as the user policy.</returns>
    internal static int? Resolve(SeerrMediaDetailResponse detail, ILocalizationManager? localization, bool adultFlag = false)
    {
        if (localization is null)
        {
            return null;
        }

        int? maximum = null;
        foreach (var certification in ReadMovieCertifications(detail.Releases).Concat(ReadTelevisionCertifications(detail.ContentRatings)))
        {
            Accumulate(certification.Value, certification.Country, localization, ref maximum);
        }

        return ApplyAdultRating(detail.Adult == true || adultFlag, localization, maximum);
    }

    private static IEnumerable<(JsonElement Value, string Country)> ReadMovieCertifications(JsonElement releases)
        => TryResults(releases, out var countries)
            ? countries.EnumerateArray().SelectMany(ReadMovieCountryCertifications)
            : [];

    private static IEnumerable<(JsonElement Value, string Country)> ReadMovieCountryCertifications(JsonElement country)
    {
        if (!CountryCode(country, out var code) || !country.TryGetProperty("release_dates", out var releases) || releases.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var release in releases.EnumerateArray().Where(release => release.ValueKind == JsonValueKind.Object && release.TryGetProperty("certification", out _)))
        {
            yield return (release.GetProperty("certification"), code);
        }
    }

    private static IEnumerable<(JsonElement Value, string Country)> ReadTelevisionCertifications(JsonElement ratings)
        => TryResults(ratings, out var countries)
            ? countries.EnumerateArray().Select(ReadTelevisionCountryCertification).Where(certification => certification.HasValue).Select(certification => certification.GetValueOrDefault())
            : [];

    private static (JsonElement Value, string Country)? ReadTelevisionCountryCertification(JsonElement country)
        => CountryCode(country, out var code) && country.TryGetProperty("rating", out var rating) ? (rating, code) : null;

    private static int? ApplyAdultRating(bool adultFlag, ILocalizationManager localization, int? maximum)
    {
        if (!adultFlag)
        {
            return maximum;
        }

        var adult = localization.GetRatingScore("XXX", "US");
        return adult is null ? null : Math.Max(maximum ?? adult.Score, adult.Score);
    }

    private static bool TryResults(JsonElement value, out JsonElement results)
    {
        results = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("results", out results) && results.ValueKind == JsonValueKind.Array;
    }

    private static bool CountryCode(JsonElement value, out string code)
    {
        code = string.Empty;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("iso_3166_1", out var country) || country.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        code = country.GetString()?.Trim() ?? string.Empty;
        return code.Length == 2 && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]);
    }

    private static void Accumulate(JsonElement certification, string country, ILocalizationManager localization, ref int? maximum)
    {
        if (certification.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = certification.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var mapped = localization.GetRatingScore(text, country);
        if (mapped is not null && mapped.Score >= 0)
        {
            maximum = Math.Max(maximum ?? mapped.Score, mapped.Score);
        }
    }
}
