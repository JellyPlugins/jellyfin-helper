using System;
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
        if (TryResults(detail.Releases, out var movies))
        {
            foreach (var country in movies.EnumerateArray())
            {
                if (!CountryCode(country, out var code) || !country.TryGetProperty("release_dates", out var releases) || releases.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var release in releases.EnumerateArray())
                {
                    if (release.ValueKind == JsonValueKind.Object && release.TryGetProperty("certification", out var certification))
                    {
                        Accumulate(certification, code, localization, ref maximum);
                    }
                }
            }
        }

        if (TryResults(detail.ContentRatings, out var television))
        {
            foreach (var country in television.EnumerateArray())
            {
                if (CountryCode(country, out var code) && country.TryGetProperty("rating", out var rating))
                {
                    Accumulate(rating, code, localization, ref maximum);
                }
            }
        }

        if (detail.Adult == true || adultFlag)
        {
            var adult = localization.GetRatingScore("XXX", "US");
            if (adult is null)
            {
                return null;
            }

            maximum = Math.Max(maximum ?? adult.Score, adult.Score);
        }

        return maximum;
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
