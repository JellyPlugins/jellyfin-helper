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
    ///     Determines the most relevant human-readable reason for a recommendation based on the dominant signal from the score explanation.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="explanation">The score explanation from the strategy.</param>
    /// <param name="genrePreferences">The user's genre preference vector.</param>
    /// <param name="preferredPeople">Optional set of preferred people names for concrete person reasons.</param>
    /// <param name="preferredStudios">Optional set of preferred studio names for concrete studio reasons.</param>
    /// <param name="peopleLookup">Optional pre-built people lookup (item ID to person names) for resolving concrete person names on candidates.</param>
    /// <param name="preferredPeopleWeights">
    ///     Optional per-name weights (matches the map fed into <c>ComputePeopleSimilarity</c>).
    ///     When supplied, the reason text surfaces the highest-weighted matching person instead
    ///     of the first arbitrary hit, so a heavyweight director beats a cameo actor.
    /// </param>
    /// <returns>A tuple of reason text, i18n key, and optional related item name.</returns>
    internal static (string Reason, string ReasonKey, string? RelatedItem) DetermineReason(
        BaseItem candidate,
        ScoreExplanation explanation,
        Dictionary<string, double> genrePreferences,
        HashSet<string>? preferredPeople = null,
        HashSet<string>? preferredStudios = null,
        Dictionary<Guid, HashSet<string>>? peopleLookup = null,
        IReadOnlyDictionary<string, double>? preferredPeopleWeights = null)
    {
        var dominant = explanation.DominantSignal;

        // Resolve concrete names for richer reasons
        var topGenre = ResolveTopGenre(candidate, genrePreferences);
        var matchedPerson = ResolveMatchedPerson(candidate, preferredPeople, peopleLookup, preferredPeopleWeights);
        var matchedStudio = ResolveMatchedStudio(candidate, preferredStudios);

        // These provide more specific "why" than single-signal reasons.
        var combination = ResolveCombinationReason(explanation, topGenre, matchedPerson);
        if (combination is not null)
        {
            return combination.Value;
        }

        return ResolveDominantSignalReason(explanation, dominant, topGenre, matchedPerson, matchedStudio);
    }

    /// <summary>
    ///     Resolves a two-signal combination reason (genre+people, genre+collaborative, recency+rating), or null when no combination applies.
    /// </summary>
    /// <param name="explanation">The score explanation.</param>
    /// <param name="topGenre">The resolved top matching genre, if any.</param>
    /// <param name="matchedPerson">The resolved matched person name, if any.</param>
    /// <returns>The combination reason tuple, or <c>null</c>.</returns>
    private static (string Reason, string ReasonKey, string? RelatedItem)? ResolveCombinationReason(
        ScoreExplanation explanation,
        string? topGenre,
        string? matchedPerson)
    {
        // Genre + People: "Featuring actors you like in Action"
        if (topGenre is not null
            && explanation is
            {
                GenreContribution: > EngineConstants.ReasonScoreThreshold,
                PeopleContribution: > EngineConstants.ReasonScoreThreshold
            })
        {
            if (matchedPerson is not null)
            {
                return ($"Featuring {matchedPerson} in {topGenre}", "reasonGenreAndPerson",
                    $"{matchedPerson} | {topGenre}");
            }

            return ($"Features actors you like in {topGenre}", "reasonGenreAndPeople", topGenre);
        }

        // Genre + Collaborative: "Popular Action among similar viewers"
        if (topGenre is not null
            && explanation is
            {
                GenreContribution: > EngineConstants.ReasonScoreThreshold,
                CollaborativeContribution: > EngineConstants.ReasonScoreThreshold
            })
        {
            return ($"Popular {topGenre} among similar viewers", "reasonGenreAndCollab", topGenre);
        }

        // Recency + Rating: "Trending - new and highly rated"
        if (explanation is
            {
                RecencyContribution: > EngineConstants.ReasonScoreThreshold,
                RatingContribution: > EngineConstants.ReasonScoreThreshold
            })
        {
            return ("Trending - new and highly rated", "reasonTrending", null);
        }

        return null;
    }

    /// <summary>
    ///     Resolves the single-dominant-signal reason, falling back to the generic default. Extracted
    ///     verbatim from <see cref="DetermineReason"/>.
    /// </summary>
    /// <param name="explanation">The score explanation.</param>
    /// <param name="dominant">The dominant signal name.</param>
    /// <param name="topGenre">The resolved top matching genre, if any.</param>
    /// <param name="matchedPerson">The resolved matched person name, if any.</param>
    /// <param name="matchedStudio">The resolved matched studio name, if any.</param>
    /// <returns>The dominant-signal reason tuple.</returns>
    private static (string Reason, string ReasonKey, string? RelatedItem) ResolveDominantSignalReason(
        ScoreExplanation explanation,
        string? dominant,
        string? topGenre,
        string? matchedPerson,
        string? matchedStudio)
    {
        return ResolveSimpleDominantReason(explanation, dominant, topGenre)
            ?? ResolvePeopleOrStudioReason(explanation, dominant, matchedPerson, matchedStudio);
    }

    /// <summary>
    ///     Resolves the single-condition dominant-signal reasons (collaborative, genre, rating, user-rating, recency, year-proximity, interaction), or null when none apply.
    /// </summary>
    private static (string Reason, string ReasonKey, string? RelatedItem)? ResolveSimpleDominantReason(
        ScoreExplanation explanation,
        string? dominant,
        string? topGenre)
    {
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

        return null;
    }

    /// <summary>
    ///     Resolves the people/studio dominant-signal reasons (which may surface a concrete matched name), falling back to the generic default.
    /// </summary>
    private static (string Reason, string ReasonKey, string? RelatedItem) ResolvePeopleOrStudioReason(
        ScoreExplanation explanation,
        string? dominant,
        string? matchedPerson,
        string? matchedStudio)
    {
        if (string.Equals(dominant, "People", StringComparison.OrdinalIgnoreCase)
            && explanation.PeopleContribution > EngineConstants.ReasonScoreThreshold)
        {
            if (matchedPerson is not null)
            {
                return ($"Featuring {matchedPerson}", "reasonPersonNamed", matchedPerson);
            }

            return ("Features actors/directors you enjoy", "reasonPeople", null);
        }

        if (!string.Equals(dominant, "Studio", StringComparison.OrdinalIgnoreCase)
            || explanation.StudioContribution <= EngineConstants.ReasonScoreThreshold)
        {
            return ("Recommended for you", "reasonDefault", null);
        }

        if (matchedStudio is not null)
        {
            return ($"From {matchedStudio}", "reasonStudioNamed", matchedStudio);
        }

        return ("From a studio you enjoy", "reasonStudio", null);
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
    /// </summary>
    private static string? ResolveMatchedPerson(
        BaseItem candidate,
        HashSet<string>? preferredPeople,
        Dictionary<Guid, HashSet<string>>? peopleLookup,
        IReadOnlyDictionary<string, double>? preferredPeopleWeights)
    {
        if (preferredPeople is null || preferredPeople.Count == 0)
        {
            return null;
        }

        if (peopleLookup is null || !peopleLookup.TryGetValue(candidate.Id, out var candidatePeople))
        {
            return null;
        }

        // When weights are available, pick the heaviest match so the reason reflects the
        // person that actually dominated PeopleSimilarity, not an arbitrary weight-1 cameo.
        if (preferredPeopleWeights is { Count: > 0 })
        {
            var bestName = ResolveHeaviestMatch(candidatePeople, preferredPeople, preferredPeopleWeights);
            if (bestName is not null)
            {
                return bestName;
            }
        }

        return candidatePeople.FirstOrDefault(preferredPeople.Contains);
    }

    /// <summary>
    ///     Returns the highest-weighted preferred person present on the candidate, or null when none of the candidate's people are preferred.
    /// </summary>
    /// <param name="candidatePeople">The candidate's person names.</param>
    /// <param name="preferredPeople">The user's preferred people set.</param>
    /// <param name="preferredPeopleWeights">Per-name weights for the preferred people.</param>
    /// <returns>The heaviest matching person name, or <c>null</c>.</returns>
    private static string? ResolveHeaviestMatch(
        HashSet<string> candidatePeople,
        HashSet<string> preferredPeople,
        IReadOnlyDictionary<string, double> preferredPeopleWeights)
    {
        string? bestName = null;
        var bestWeight = double.NegativeInfinity;
        foreach (var name in candidatePeople)
        {
            if (!preferredPeople.Contains(name))
            {
                continue;
            }

            var weight = preferredPeopleWeights.TryGetValue(name, out var w) ? w : 0.0;
            if (weight > bestWeight)
            {
                bestWeight = weight;
                bestName = name;
            }
        }

        return bestName;
    }

    /// <summary>
    ///     Resolves a concrete studio name from the candidate that matches the user's preferred studios.
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
            GenreDistribution = new Dictionary<string, int>(
                profile.GenreDistribution,
                profile.GenreDistribution.Comparer),
            // Language / subtitle / people profiles are aggregated stats too (like GenreDistribution), so they belong in the stripped response for consistency - omitting them made the API report empty language/subtitle/people aggregates while GenreDistribution was correct.
            LanguageProfile = profile.LanguageProfile,
            SubtitleLanguageProfile = profile.SubtitleLanguageProfile,
            PeopleProfile = profile.PeopleProfile,
            FavoriteCount = profile.FavoriteCount,
            FavoriteSeriesIds = [.. profile.FavoriteSeriesIds],
            AverageCommunityRating = profile.AverageCommunityRating,
            MaxParentalRating = profile.MaxParentalRating,
            WatchedItems = []
        };
    }
}