using System.Collections.Generic;
using System.Linq;

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
    ///     TMDb genre IDs that are explicitly allowed for strict child accounts (MaxParentalRating 60 or lower, FSK-6).
    ///     Only items containing at least one of these genres will be shown to young children.
    ///     This whitelist approach is more restrictive than the blacklist and ensures that
    ///     only genuinely child-appropriate content is recommended.
    /// </summary>
    private static readonly HashSet<int> ChildAllowedGenreIds = new()
    {
        10751, // Family
        10762, // Kids (TV)
        10402 // Music
    };

    /// <summary>
    ///     Determines whether a candidate item should be excluded based on parental rating constraints.
    /// </summary>
    /// <param name="candidate">The TMDb discover item to check.</param>
    /// <param name="maxParentalRating">
    ///     The user's max parental rating value.
    ///     Use <c>null</c> or any value &gt;= 141 for unrestricted/adult users;
    ///     both are treated identically (no filtering applied).
    /// </param>
    /// <returns>True if the item should be excluded, false if it passes the filter.</returns>
    internal static bool ShouldExclude(TmdbDiscoverItem candidate, int? maxParentalRating)
    {
        if (!maxParentalRating.HasValue || maxParentalRating.Value >= 141)
        {
            return false;
        }

        // Always exclude adult-flagged content for any restricted user
        if (candidate.Adult)
        {
            return true;
        }

        // For strict child accounts (FSK-6 and below): STRICT WHITELIST approach
        // Animation alone is NOT enough (American Dad, Family Guy, Archer are all "Animation")
        // Must have Family (10751) or Kids (10762) or Music (10402) genre
        if (maxParentalRating.Value <= 60)
        {
            return ShouldExcludeForStrictChild(candidate);
        }

        // For young teen accounts (FSK-12): BLACKLIST approach
        // Exclude specific inappropriate genres
        if (maxParentalRating.Value <= 100 && candidate.GenreIds.Any(TeenRestrictedGenreIds.Contains))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Applies the strict-whitelist rule for FSK-6-and-below child accounts: the item must
    ///     contain at least one primary child-safe genre (Family, Kids, Music) and must not
    ///     contain any teen-restricted genre.
    /// </summary>
    /// <param name="candidate">The TMDb discover item to check.</param>
    /// <returns>True if the item should be excluded, false if it passes the filter.</returns>
    private static bool ShouldExcludeForStrictChild(TmdbDiscoverItem candidate)
    {
        var hasPrimaryChildGenre = false;
        var hasRestrictedGenre = false;

        foreach (var genreId in candidate.GenreIds)
        {
            if (ChildAllowedGenreIds.Contains(genreId))
            {
                hasPrimaryChildGenre = true;
            }

            if (TeenRestrictedGenreIds.Contains(genreId))
            {
                hasRestrictedGenre = true;
            }
        }

        // Must have at least one primary child-safe genre (Family, Kids, Music).
        // Conditional genres (Animation/Comedy/Adventure/Fantasy) alone are NOT safe
        // because of adult animation (Family Guy, Archer) and adult comedies.
        if (!hasPrimaryChildGenre)
        {
            return true;
        }

        // Even with Family genre, still exclude if a restricted genre is present
        if (hasRestrictedGenre)
        {
            return true;
        }

        return false;
    }
}
