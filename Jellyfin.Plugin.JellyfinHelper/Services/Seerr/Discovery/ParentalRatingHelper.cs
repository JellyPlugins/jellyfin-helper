using System;
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
    ///     TMDb genre IDs that indicate adult-oriented animation when combined with Animation (16).
    ///     Derived from <see cref="TeenRestrictedGenreIds"/> minus Horror (27) which is already
    ///     excluded by the teen blacklist. Kept as a reference alias to prevent the two sets
    ///     from drifting apart as new genre rules are added.
    /// </summary>
    /// <remarks>
    ///     Note: TMDb discover API does not return keywords in results, so this is used
    ///     as a secondary signal via vote_average thresholds for animation content.
    /// </remarks>
    private static readonly HashSet<int> AdultAnimationGenreCombinations = new(
        TeenRestrictedGenreIds.Where(id => id != 27)); // Exclude Horror (already filtered at teen level)

    /// <summary>
    ///     Determines whether a candidate item should be excluded based on parental rating constraints.
    /// </summary>
    /// <param name="candidate">The TMDb discover item to check.</param>
    /// <param name="maxParentalRating">
    ///     The user's max parental rating value, or <c>null</c> for unrestricted/adult users.
    ///     Callers MUST pass <c>null</c> for users without parental rating restrictions (141+)
    ///     to avoid inadvertently filtering adult-flagged TMDb content for unrestricted accounts.
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

                if (AdultAnimationGenreCombinations.Contains(genreId) && candidate.GenreIds.Contains(16))
                {
                    // Animation + Crime/Thriller/War = likely adult animation
                    return true;
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

            // Additional safety: very high vote averages for Animation without Kids/Family
            // sometimes indicate cult adult shows. If Animation is present but the main
            // genres are not Kids-focused, apply a stricter check.
            // Adult animations tend to have vote_average between 6-9 with moderate counts
            // While children's content tends to have lower vote averages (5-7)
            // We already required Family genre above, so this is a secondary check
            if (candidate.GenreIds.Contains(16)
                && !candidate.GenreIds.Contains(10762)
                && candidate.VoteAverage > 8.0
                && !candidate.GenreIds.Contains(10751))
            {
                return true;
            }

            return false;
        }

        // For young teen accounts (FSK-12): BLACKLIST approach
        // Exclude specific inappropriate genres
        if (maxParentalRating.Value <= 100 && candidate.GenreIds.Any(TeenRestrictedGenreIds.Contains))
        {
            return true;
        }

        return false;
    }
}
