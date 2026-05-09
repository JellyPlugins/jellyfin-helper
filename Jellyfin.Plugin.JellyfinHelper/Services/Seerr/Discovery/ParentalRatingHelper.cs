using System;
using System.Collections.Generic;

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
        10751, // Family
        10762, // Kids (TV)
        10402 // Music
    };

    /// <summary>
    ///     TMDb genre IDs that are explicitly child-safe ONLY when combined with Family/Kids genres.
    ///     Animation alone is NOT sufficient because of adult animation (Family Guy, American Dad, etc.).
    ///     Comedy alone is NOT sufficient because of adult comedies.
    ///     These genres require at least one <see cref="ChildAllowedGenreIds"/> to be present.
    /// </summary>
    private static readonly HashSet<int> ChildConditionalGenreIds = new()
    {
        16, // Animation (child-safe ONLY with Family/Kids - excludes Adult Animation)
        35, // Comedy (child-safe ONLY with Family/Kids - excludes adult comedy)
        12, // Adventure
        14 // Fantasy
    };

    /// <summary>
    ///     TMDb keyword IDs known to indicate adult-oriented animation content.
    ///     These are checked as an additional filter layer.
    /// </summary>
    /// <remarks>
    ///     Note: TMDb discover API does not return keywords in results, so this is used
    ///     as a secondary signal via vote_average thresholds for animation content.
    /// </remarks>
    private static readonly HashSet<int> AdultAnimationGenreCombinations = new()
    {
        // Animation (16) combined with these genres typically indicates adult content:
        80, // Crime (e.g., Archer)
        53, // Thriller
        10752, // War
        10768 // War & Politics
    };

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

        // For strict child accounts (FSK-6 and below): STRICT WHITELIST approach
        // Animation alone is NOT enough (American Dad, Family Guy, Archer are all "Animation")
        // Must have Family (10751) or Kids (10762) or Music (10402) genre
        if (maxParentalRating.Value <= 60)
        {
            var hasPrimaryChildGenre = false;
            var hasConditionalChildGenre = false;
            var hasRestrictedGenre = false;

            foreach (var genreId in candidate.GenreIds)
            {
                if (ChildAllowedGenreIds.Contains(genreId))
                {
                    hasPrimaryChildGenre = true;
                }

                if (ChildConditionalGenreIds.Contains(genreId))
                {
                    hasConditionalChildGenre = true;
                }

                if (TeenRestrictedGenreIds.Contains(genreId))
                {
                    hasRestrictedGenre = true;
                }

                if (AdultAnimationGenreCombinations.Contains(genreId) && candidate.GenreIds.Contains(16))
                {
                    // Animation + Crime/Thriller/War = likely adult animation
                    return true;
                }
            }

            // Must have at least one primary child-safe genre (Family, Kids, Music)
            // OR conditional genres (Animation, Comedy, Adventure, Fantasy) ONLY if Family/Kids is also present
            if (!hasPrimaryChildGenre)
            {
                // No Family/Kids/Music genre at all → exclude
                // Even if it has Animation or Comedy, those alone aren't safe
                if (hasConditionalChildGenre)
                {
                    // Has Animation/Comedy/etc. but no Family/Kids → likely adult content
                    return true;
                }

                // No child-friendly genre at all
                return true;
            }

            // Even with Family genre, still exclude if a restricted genre is present
            if (hasRestrictedGenre)
            {
                return true;
            }

            // Additional safety: very high vote averages for Animation without Kids/Family
            // sometimes indicate cult adult shows. If Animation is present but the main
            // genres are not Kids-focused, apply a stricter check.
            if (candidate.GenreIds.Contains(16) && !candidate.GenreIds.Contains(10762))
            {
                // Animation without explicit "Kids" TV genre - check vote count
                // Adult animations tend to have vote_average between 6-9 with moderate counts
                // While children's content tends to have lower vote averages (5-7)
                // We already required Family genre above, so this is a secondary check
                if (candidate.VoteAverage > 8.0 && !candidate.GenreIds.Contains(10751))
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
}
