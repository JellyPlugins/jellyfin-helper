namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>Applies verified Jellyfin-scale certifications to external candidates; genres are not age ratings.</summary>
internal static class ParentalRatingHelper
{
    /// <summary>Only an effectively unbounded score limit is equivalent to an unrestricted account.</summary>
    internal const int UnrestrictedThreshold = int.MaxValue;

    /// <summary>Rejects unknown or excessive certifications for a restricted profile.</summary>
    /// <param name="candidate">The provider candidate with a server-resolved certification.</param>
    /// <param name="maxParentalRating">The current Jellyfin account rating limit, or null for unrestricted access.</param>
    /// <returns>True when the candidate cannot be verified as allowed.</returns>
    internal static bool ShouldExclude(TmdbDiscoverItem candidate, int? maxParentalRating)
        => maxParentalRating.HasValue && maxParentalRating.Value < UnrestrictedThreshold
            && (!candidate.CertificationScore.HasValue || candidate.CertificationScore.Value < 0
                || candidate.CertificationScore.Value > maxParentalRating.Value);
}
