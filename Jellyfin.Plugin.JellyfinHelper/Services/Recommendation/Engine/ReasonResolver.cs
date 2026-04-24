using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Determines human-readable recommendation reasons from score explanations,
///     and provides utility methods for response preparation.
/// </summary>
internal static class ReasonResolver
{
    /// <summary>
    ///     Determines the most relevant human-readable reason for a recommendation
    ///     based on the dominant signal from the score explanation.
    ///     Supports combination reasons when multiple signals are strong, and includes
    ///     concrete entity names (genre, person, studio) when available.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="explanation">The score explanation from the strategy.</param>
    /// <param name="genrePreferences">The user's genre preference vector.</param>
    /// <param name="preferredPeople">Optional set of preferred people names for concrete person reasons.</param>
    /// <param name="preferredStudios">Optional set of preferred studio names for concrete studio reasons.</param>
    /// <param name="peopleLookup">Optional pre-built people lookup (item ID → person names) for resolving concrete person names on candidates.</param>
    /// <returns>A tuple of reason text, i18n key, and optional related item name.</returns>
    internal static (string Reason, string ReasonKey, string? RelatedItem) DetermineReason(
        BaseItem candidate,
        ScoreExplanation explanation,
        Dictionary<string, double> genrePreferences,
        HashSet<string>? preferredPeople = null,
        HashSet<string>? preferredStudios = null,
        Dictionary<Guid, HashSet<string>>? peopleLookup = null)
    {
        var dominant = explanation.DominantSignal;

        // Resolve concrete names for richer reasons
        var topGenre = ResolveTopGenre(candidate, genrePreferences);
        var matchedPerson = ResolveMatchedPerson(candidate, preferredPeople, peopleLookup);
        var matchedStudio = ResolveMatchedStudio(candidate, preferredStudios);

        // === Combination reasons (two strong signals) ===
        // These provide more specific "why" than single-signal reasons.

        // Genre + People: "Featuring actors you like in Action"
        if (topGenre is not null
            && explanation.GenreContribution > EngineConstants.ReasonScoreThreshold
            && explanation.PeopleContribution > EngineConstants.ReasonScoreThreshold)
        {
            if (matchedPerson is not null)
            {
                return ($"Featuring {matchedPerson} in {topGenre}", "reasonGenreAndPerson", $"{matchedPerson} | {topGenre}");
            }

            return ($"Features actors you like in {topGenre}", "reasonGenreAndPeople", topGenre);
        }

        // Genre + Collaborative: "Popular Action among similar viewers"
        if (topGenre is not null
            && explanation.GenreContribution > EngineConstants.ReasonScoreThreshold
            && explanation.CollaborativeContribution > EngineConstants.ReasonScoreThreshold)
        {
            return ($"Popular {topGenre} among similar viewers", "reasonGenreAndCollab", topGenre);
        }

        // Recency + Rating: "Trending — new and highly rated"
        if (explanation.RecencyContribution > EngineConstants.ReasonScoreThreshold
            && explanation.RatingContribution > EngineConstants.ReasonScoreThreshold)
        {
            return ("Trending — new and highly rated", "reasonTrending", null);
        }

        // === Single dominant signal reasons ===

        if (string.Equals(dominant, "Collaborative", StringComparison.OrdinalIgnoreCase)
            && explanation.CollaborativeContribution > EngineConstants.ReasonScoreThreshold)
        {
            return ("Popular with similar viewers", "reasonCollaborative", null);
        }

        if (string.Equals(dominant, "Genre", StringComparison.OrdinalIgnoreCase)
            && explanation.GenreContribution > EngineConstants.ReasonScoreThreshold
            && topGenre is not null)
        {
            return ($"Because you enjoy {topGenre}", "reasonGenre", topGenre);
        }

        if (string.Equals(dominant, "Rating", StringComparison.OrdinalIgnoreCase)
            && explanation.RatingContribution > EngineConstants.HighRatingThreshold)
        {
            return ("Highly rated", "reasonHighlyRated", null);
        }

        if (string.Equals(dominant, "UserRating", StringComparison.OrdinalIgnoreCase)
            && explanation.UserRatingContribution > EngineConstants.ReasonScoreThreshold)
        {
            return ("Matches your personal ratings", "reasonUserRating", null);
        }

        if (string.Equals(dominant, "Recency", StringComparison.OrdinalIgnoreCase)
            && explanation.RecencyContribution > EngineConstants.ReasonScoreThreshold)
        {
            return ("Recently released", "reasonRecent", null);
        }

        if (string.Equals(dominant, "YearProximity", StringComparison.OrdinalIgnoreCase)
            && explanation.YearProximityContribution > EngineConstants.ReasonScoreThreshold)
        {
            return ("Matches the era of content you enjoy", "reasonYearProximity", null);
        }

        if (string.Equals(dominant, "Interaction", StringComparison.OrdinalIgnoreCase)
            && explanation.InteractionContribution > EngineConstants.ReasonScoreThreshold)
        {
            return ("Matches your viewing patterns", "reasonInteraction", null);
        }

        if (string.Equals(dominant, "People", StringComparison.OrdinalIgnoreCase)
            && explanation.PeopleContribution > EngineConstants.ReasonScoreThreshold)
        {
            if (matchedPerson is not null)
            {
                return ($"Featuring {matchedPerson}", "reasonPersonNamed", matchedPerson);
            }

            return ("Features actors/directors you enjoy", "reasonPeople", null);
        }

        if (string.Equals(dominant, "Studio", StringComparison.OrdinalIgnoreCase)
            && explanation.StudioContribution > EngineConstants.ReasonScoreThreshold)
        {
            if (matchedStudio is not null)
            {
                return ($"From {matchedStudio}", "reasonStudioNamed", matchedStudio);
            }

            return ("From a studio you enjoy", "reasonStudio", null);
        }

        return ("Recommended for you", "reasonDefault", null);
    }

    /// <summary>
    ///     Resolves the top matching genre from the candidate's genres against the user's preferences.
    /// </summary>
    private static string? ResolveTopGenre(BaseItem candidate, Dictionary<string, double> genrePreferences)
    {
        if (candidate.Genres is not { Length: > 0 })
        {
            return null;
        }

        return candidate.Genres
            .Select(g => (Genre: g, Score: genrePreferences.TryGetValue(g, out var s) ? s : (double?)null))
            .Where(x => x.Score.HasValue)
            .OrderByDescending(x => x.Score!.Value)
            .Select(x => x.Genre)
            .FirstOrDefault();
    }

    /// <summary>
    ///     Resolves a concrete person name from the candidate that matches the user's preferred people.
    ///     Uses the pre-built <paramref name="peopleLookup"/> (item ID → person names) to avoid
    ///     library-manager queries during scoring. Requires both <paramref name="preferredPeople"/>
    ///     and <paramref name="peopleLookup"/> to be non-null to return a match.
    ///     Returns the first matching person name, or null if no match or data unavailable.
    /// </summary>
    private static string? ResolveMatchedPerson(
        BaseItem candidate,
        HashSet<string>? preferredPeople,
        Dictionary<Guid, HashSet<string>>? peopleLookup)
    {
        if (preferredPeople is null || preferredPeople.Count == 0)
        {
            return null;
        }

        // Use the pre-built people lookup to find which person on this candidate
        // matches the user's preferred people — avoids expensive library queries.
        if (peopleLookup is not null && peopleLookup.TryGetValue(candidate.Id, out var candidatePeople))
        {
            return candidatePeople.FirstOrDefault(p => preferredPeople.Contains(p));
        }

        return null;
    }

    /// <summary>
    ///     Resolves a concrete studio name from the candidate that matches the user's preferred studios.
    ///     Returns the first matching studio name, or null if no match or data unavailable.
    /// </summary>
    private static string? ResolveMatchedStudio(BaseItem candidate, HashSet<string>? preferredStudios)
    {
        if (preferredStudios is null || preferredStudios.Count == 0
            || candidate.Studios is not { Length: > 0 })
        {
            return null;
        }

        return candidate.Studios
            .FirstOrDefault(s => !string.IsNullOrEmpty(s) && preferredStudios.Contains(s));
    }

    /// <summary>
    ///     Creates a copy of the profile without the full watched items list (for the API response),
    ///     keeping only aggregated stats.
    /// </summary>
    /// <param name="profile">The original user watch profile.</param>
    /// <returns>A copy of the profile with an empty watched items list.</returns>
    internal static UserWatchProfile StripWatchedItemsForResponse(UserWatchProfile profile)
    {
        return new UserWatchProfile
        {
            UserId = profile.UserId,
            UserName = profile.UserName,
            WatchedMovieCount = profile.WatchedMovieCount,
            WatchedEpisodeCount = profile.WatchedEpisodeCount,
            WatchedSeriesCount = profile.WatchedSeriesCount,
            TotalWatchTimeTicks = profile.TotalWatchTimeTicks,
            LastActivityDate = profile.LastActivityDate,
            GenreDistribution = new Dictionary<string, int>(profile.GenreDistribution),
            FavoriteCount = profile.FavoriteCount,
            FavoriteSeriesIds = profile.FavoriteSeriesIds,
            AverageCommunityRating = profile.AverageCommunityRating,
            MaxParentalRating = profile.MaxParentalRating,
            WatchedItems = []
        };
    }
}