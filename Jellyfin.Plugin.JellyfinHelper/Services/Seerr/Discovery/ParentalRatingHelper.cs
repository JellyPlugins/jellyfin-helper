using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Provides mapping between Jellyfin parental rating values and TMDb certification
///     parameters for content filtering in discovery queries.
/// </summary>
/// <remarks>
///     Jellyfin uses numeric parental rating values that correspond to various regional
///     rating systems. This helper maps those values to TMDb's certification system
///     which uses country-specific certification strings.
///     <para>
///     Jellyfin numeric values (approximate mapping):
///     <list type="bullet">
///         <item>0-60: FSK 0 / FSK 6 / G / PG (child-safe)</item>
///         <item>61-100: FSK 12 / PG-13 (young teens)</item>
///         <item>101-140: FSK 16 / R (older teens)</item>
///         <item>141+: FSK 18 / NC-17 (adults)</item>
///     </list>
///     </para>
/// </remarks>
internal static class ParentalRatingHelper
{
    /// <summary>
    ///     TMDb genre IDs that are inappropriate for young teen accounts (MaxParentalRating 61-100 / FSK-12).
    ///     These genres are excluded from discovery queries for restricted users even when
    ///     the TMDb certification filter might not catch all edge cases.
    /// </summary>
    private static readonly HashSet<int> TeenRestrictedGenreIds = new()
    {
        27, // Horror
        80, // Crime
        53, // Thriller
        10752, // War (movies)
        10768 // War & Politics (TV)
    };

    /// <summary>
    ///     TMDb genre IDs that are explicitly allowed for strict child accounts (MaxParentalRating ≤ 60 / FSK-6).
    ///     Only items containing at least one of these genres will be shown to young children.
    ///     This whitelist approach is more restrictive than the blacklist and ensures that
    ///     only genuinely child-appropriate content is recommended.
    /// </summary>
    private static readonly HashSet<int> ChildAllowedGenreIds = new()
    {
        16, // Animation
        10751, // Family
        35, // Comedy
        10402, // Music
        10762, // Kids (TV)
        12, // Adventure
        14 // Fantasy
    };

    /// <summary>
    ///     Gets the TMDb certification query parameter for the given parental rating.
    ///     Returns null if no restriction should be applied (unrestricted user).
    /// </summary>
    /// <param name="maxParentalRating">The user's maximum allowed parental rating value from Jellyfin settings.</param>
    /// <returns>
    ///     A query string fragment like "&amp;certification_country=DE&amp;certification.lte=FSK%2012"
    ///     or null if no restriction needed.
    /// </returns>
    internal static string? GetCertificationQueryParam(int? maxParentalRating)
    {
        if (!maxParentalRating.HasValue)
        {
            return null;
        }

        var certification = MapToCertification(maxParentalRating.Value);
        if (certification == null)
        {
            return null;
        }

        // Use German FSK system as it maps well to Jellyfin's numeric values
        // and is well-supported by TMDb's certification database.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"&certification_country=DE&certification.lte={Uri.EscapeDataString(certification)}");
    }

    /// <summary>
    ///     Determines whether a candidate item should be excluded based on parental rating constraints.
    /// </summary>
    /// <param name="candidate">The TMDb discover item to check.</param>
    /// <param name="maxParentalRating">The user's max parental rating (null = unrestricted).</param>
    /// <returns>True if the item should be excluded, false if it passes the filter.</returns>
    internal static bool ShouldExclude(TmdbDiscoverItem candidate, int? maxParentalRating)
    {
        if (!maxParentalRating.HasValue)
        {
            return false;
        }

        // Always exclude adult-flagged content for any restricted user
        if (candidate.Adult)
        {
            return true;
        }

        // For strict child accounts (FSK-6 and below): WHITELIST approach
        // Only allow content that has at least one child-friendly genre
        if (maxParentalRating.Value <= 60)
        {
            var hasAllowedGenre = false;
            foreach (var genreId in candidate.GenreIds)
            {
                if (ChildAllowedGenreIds.Contains(genreId))
                {
                    hasAllowedGenre = true;
                    break;
                }
            }

            if (!hasAllowedGenre)
            {
                return true;
            }

            // Even with an allowed genre, still exclude if a restricted genre is present
            foreach (var genreId in candidate.GenreIds)
            {
                if (TeenRestrictedGenreIds.Contains(genreId))
                {
                    return true;
                }
            }

            return false;
        }

        // For young teen accounts (FSK-12): BLACKLIST approach
        // Exclude specific inappropriate genres
        if (maxParentalRating.Value <= 100)
        {
            foreach (var genreId in candidate.GenreIds)
            {
                if (TeenRestrictedGenreIds.Contains(genreId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Maps a Jellyfin numeric parental rating to a German FSK certification string.
    /// </summary>
    /// <param name="maxParentalRating">The Jellyfin parental rating value.</param>
    /// <returns>The FSK certification string, or null for unrestricted.</returns>
    private static string? MapToCertification(int maxParentalRating)
    {
        return maxParentalRating switch
        {
            <= 0 => "FSK 0",
            <= 60 => "FSK 6",
            <= 100 => "FSK 12",
            <= 140 => "FSK 16",
            _ => null // FSK 18 or no restriction - don't add filter
        };
    }
}