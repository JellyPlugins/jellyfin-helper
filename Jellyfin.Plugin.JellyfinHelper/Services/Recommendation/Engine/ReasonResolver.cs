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
        => DetermineReason(
            candidate,
            explanation,
            genrePreferences,
            default,
            new ReasonPeopleContext(preferredPeople, preferredStudios, peopleLookup, preferredPeopleWeights));

    /// <summary>
    ///     Determines the most relevant human-readable reason, preferring concrete, anchored signals (franchise, collection progression, named country/writer/tag/era) over abstract ones, and falling back to the dominant single signal.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="explanation">The score explanation from the strategy.</param>
    /// <param name="genrePreferences">The user's genre preference vector.</param>
    /// <param name="anchors">Concrete anchors resolved by the caller (collection name, watched-sibling count, matched country/writer/tag, era).</param>
    /// <param name="people">The people/studio preference sources used to surface concrete names.</param>
    /// <returns>A tuple of reason text, i18n key, and optional related item name.</returns>
    internal static (string Reason, string ReasonKey, string? RelatedItem) DetermineReason(
        BaseItem candidate,
        ScoreExplanation explanation,
        Dictionary<string, double> genrePreferences,
        ReasonAnchorContext anchors,
        ReasonPeopleContext people = default)
    {
        var dominant = explanation.DominantSignal;

        // Resolve concrete names for richer reasons
        var topGenre = ResolveTopGenre(candidate, genrePreferences);
        var matchedPerson = ResolveMatchedPerson(candidate, people.PreferredPeople, people.PeopleLookup, people.PreferredPeopleWeights);
        var matchedStudio = ResolveMatchedStudio(candidate, people.PreferredStudios);

        // Anchored franchise/collection reasons first: when the strategy already elected one of these as the
        // dominant signal, it is the strongest "complete the set" driver and must not be masked by a two-signal
        // combination below. These signals carry low default weights, so they are gated on the dominant signal
        // (not the absolute ReasonScoreThreshold, which their contribution can never reach).
        var anchored = ResolveAnchoredReason(explanation, dominant, anchors);
        if (anchored is not null)
        {
            return anchored.Value;
        }

        // Two-signal combinations (genre+people, genre+collab, recency+rating) give a more specific "why" than a
        // single abstract signal, so they win over the remaining single-signal reasons below.
        var combination = ResolveCombinationReason(explanation, topGenre, matchedPerson);
        if (combination is not null)
        {
            return combination.Value;
        }

        return ResolveDominantSignalReason(explanation, dominant, topGenre, matchedPerson, matchedStudio, anchors);
    }

    /// <summary>
    ///     Resolves a concrete franchise/collection-progression reason when that signal dominates and the caller supplied an anchor, or null.
    /// </summary>
    private static (string Reason, string ReasonKey, string? RelatedItem)? ResolveAnchoredReason(
        ScoreExplanation explanation,
        string? dominant,
        ReasonAnchorContext anchors)
    {
        if (string.Equals(dominant, "CollectionProgression", StringComparison.OrdinalIgnoreCase)
            && explanation.CollectionProgressionContribution > EngineConstants.DominantReasonFloor
            && anchors.WatchedSiblingCount > 0)
        {
            // The count reflects collection entries the user has interacted with (played, started, or favorited),
            // not strictly completed or even started, so the wording stays "explored" to cover favorites too.
            var count = anchors.WatchedSiblingCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ($"You've explored {count} from this collection", "reasonCollectionProgress", count);
        }

        if (string.Equals(dominant, "Franchise", StringComparison.OrdinalIgnoreCase)
            && explanation.FranchiseContribution > EngineConstants.DominantReasonFloor)
        {
            return string.IsNullOrWhiteSpace(anchors.CollectionName)
                ? ("From a franchise you follow", "reasonFranchise", null)
                : ($"More from the {anchors.CollectionName} collection", "reasonFranchiseNamed", anchors.CollectionName);
        }

        return null;
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
    ///     Resolves the single-dominant-signal reason, preferring a concrete anchor, falling back to the generic default.
    /// </summary>
    /// <param name="explanation">The score explanation.</param>
    /// <param name="dominant">The dominant signal name.</param>
    /// <param name="topGenre">The resolved top matching genre, if any.</param>
    /// <param name="matchedPerson">The resolved matched person name, if any.</param>
    /// <param name="matchedStudio">The resolved matched studio name, if any.</param>
    /// <param name="anchors">Concrete anchors resolved by the caller.</param>
    /// <returns>The dominant-signal reason tuple.</returns>
    private static (string Reason, string ReasonKey, string? RelatedItem) ResolveDominantSignalReason(
        ScoreExplanation explanation,
        string? dominant,
        string? topGenre,
        string? matchedPerson,
        string? matchedStudio,
        ReasonAnchorContext anchors)
    {
        return ResolveSimpleDominantReason(explanation, dominant, topGenre, anchors)
            ?? ResolvePeopleOrStudioReason(explanation, dominant, matchedPerson, matchedStudio, anchors);
    }

    /// <summary>
    ///     Resolves the single-condition dominant-signal reasons (collaborative, genre, rating, user-rating, recency, year-proximity, interaction), preferring a concrete anchor where one is available, or null when none apply.
    /// </summary>
    private static (string Reason, string ReasonKey, string? RelatedItem)? ResolveSimpleDominantReason(
        ScoreExplanation explanation,
        string? dominant,
        string? topGenre,
        ReasonAnchorContext anchors)
    {
        if (IsDominant(dominant, "Collaborative", explanation.CollaborativeContribution))
        {
            return ("Popular with similar viewers", "reasonCollaborative", null);
        }

        if (IsDominant(dominant, "Popularity", explanation.PopularityContribution, EngineConstants.DominantReasonFloor))
        {
            return ("Popular with other viewers", "reasonPopularAmongViewers", null);
        }

        if (IsDominant(dominant, "Genre", explanation.GenreContribution) && topGenre is not null)
        {
            return ($"Because you enjoy {topGenre}", "reasonGenre", topGenre);
        }

        if (IsDominant(dominant, "Rating", explanation.RatingContribution, EngineConstants.HighRatingThreshold))
        {
            return ("Highly rated", "reasonHighlyRated", null);
        }

        if (IsDominant(dominant, "UserRating", explanation.UserRatingContribution))
        {
            return ("Matches your personal ratings", "reasonUserRating", null);
        }

        if (IsDominant(dominant, "Recency", explanation.RecencyContribution))
        {
            return ("Recently released", "reasonRecent", null);
        }

        if (IsDominant(dominant, "YearProximity", explanation.YearProximityContribution))
        {
            return string.IsNullOrWhiteSpace(anchors.ReleaseEra)
                ? ("Matches the era of content you enjoy", "reasonYearProximity", null)
                : ($"From the {anchors.ReleaseEra} you enjoy", "reasonYearEra", anchors.ReleaseEra);
        }

        if (IsDominant(dominant, "Interaction", explanation.InteractionContribution))
        {
            return ResolveInteractionAnchorReason(anchors);
        }

        return null;
    }

    /// <summary>
    ///     Returns true when <paramref name="dominant"/> names <paramref name="signalName"/> and its contribution clears the threshold. Collapses the repeated name-equals-and-above-threshold test so the dispatch chain stays flat.
    /// </summary>
    private static bool IsDominant(
        string? dominant,
        string signalName,
        double contribution,
        double threshold = EngineConstants.ReasonScoreThreshold)
    {
        return string.Equals(dominant, signalName, StringComparison.OrdinalIgnoreCase)
            && contribution > threshold;
    }

    /// <summary>
    ///     Turns a dominant interaction signal into the most concrete reason an anchor allows (country, writer, tag, language), falling back to the generic viewing-patterns reason.
    /// </summary>
    private static (string Reason, string ReasonKey, string? RelatedItem) ResolveInteractionAnchorReason(
        ReasonAnchorContext anchors)
    {
        if (!string.IsNullOrWhiteSpace(anchors.MatchedCountry))
        {
            return ($"More from {anchors.MatchedCountry}", "reasonCountryNamed", anchors.MatchedCountry);
        }

        if (!string.IsNullOrWhiteSpace(anchors.MatchedWriter))
        {
            return ($"Written by {anchors.MatchedWriter}", "reasonWriterNamed", anchors.MatchedWriter);
        }

        if (!string.IsNullOrWhiteSpace(anchors.MatchedTag))
        {
            return ($"Tagged {anchors.MatchedTag}", "reasonTagNamed", anchors.MatchedTag);
        }

        if (!string.IsNullOrWhiteSpace(anchors.MatchedLanguage))
        {
            return ($"In {anchors.MatchedLanguage}", "reasonLanguageNamed", anchors.MatchedLanguage);
        }

        return ("Matches your viewing patterns", "reasonInteraction", null);
    }

    /// <summary>
    ///     Resolves the people/studio dominant-signal reasons (which may surface a concrete matched name), falling back to the generic default.
    /// </summary>
    private static (string Reason, string ReasonKey, string? RelatedItem) ResolvePeopleOrStudioReason(
        ScoreExplanation explanation,
        string? dominant,
        string? matchedPerson,
        string? matchedStudio,
        ReasonAnchorContext anchors)
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
            // Last concrete fallback before the generic default: an anchor the dominant signal did not consume.
            return ResolveResidualAnchorReason(explanation, anchors);
        }

        if (matchedStudio is not null)
        {
            return ($"From {matchedStudio}", "reasonStudioNamed", matchedStudio);
        }

        return ("From a studio you enjoy", "reasonStudio", null);
    }

    /// <summary>
    ///     Surfaces any concrete anchor (collection, country, writer, tag) before degrading to "Recommended for you", so a candidate with a real signal never shows the generic default.
    /// </summary>
    private static (string Reason, string ReasonKey, string? RelatedItem) ResolveResidualAnchorReason(
        ScoreExplanation explanation,
        ReasonAnchorContext anchors)
    {
        // A collection name alone is not evidence of franchise preference; only surface the franchise reason
        // when the franchise signal itself cleared the threshold. Otherwise a candidate with mere collection
        // metadata (or even a negative franchise contribution) would be mislabeled "More from the … collection".
        if (!string.IsNullOrWhiteSpace(anchors.CollectionName)
            && explanation.FranchiseContribution > EngineConstants.ReasonScoreThreshold)
        {
            return ($"More from the {anchors.CollectionName} collection", "reasonFranchiseNamed", anchors.CollectionName);
        }

        if (!string.IsNullOrWhiteSpace(anchors.MatchedCountry)
            || !string.IsNullOrWhiteSpace(anchors.MatchedWriter)
            || !string.IsNullOrWhiteSpace(anchors.MatchedTag)
            || !string.IsNullOrWhiteSpace(anchors.MatchedLanguage))
        {
            return ResolveInteractionAnchorReason(anchors);
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

    /// <summary>
    ///     People/studio preference sources used to surface concrete names in a reason. Bundled so the public
    ///     entry point stays within the parameter budget; all members are optional and default to no data.
    /// </summary>
    /// <param name="PreferredPeople">Preferred people names for concrete person reasons.</param>
    /// <param name="PreferredStudios">Preferred studio names for concrete studio reasons.</param>
    /// <param name="PeopleLookup">Pre-built people lookup (item id to person names) for resolving names on candidates.</param>
    /// <param name="PreferredPeopleWeights">Per-name weights; when supplied the reason surfaces the highest-weighted matching person.</param>
    internal readonly record struct ReasonPeopleContext(
        HashSet<string>? PreferredPeople = null,
        HashSet<string>? PreferredStudios = null,
        Dictionary<Guid, HashSet<string>>? PeopleLookup = null,
        IReadOnlyDictionary<string, double>? PreferredPeopleWeights = null);
}