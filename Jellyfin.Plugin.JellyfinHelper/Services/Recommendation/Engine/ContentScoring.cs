using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Pure computation methods for content-based scoring signals: collaborative score normalization, community rating, recency, year proximity, user rating, completion ratio, average year, and engagement labels.
/// </summary>
internal static class ContentScoring
{
    /// <summary>
    ///     Process-lifetime counter of parallel-array mismatches in ComputeContentNearestNeighborScore.
    /// </summary>
    private static long _parallelArrayMismatchCount;

    /// <summary>
    ///     Gets the number of parallel-array mismatches observed since process start.
    ///     Test-only accessor - not part of the plugin's public API.
    /// </summary>
    internal static long ParallelArrayMismatchCount => Interlocked.Read(ref _parallelArrayMismatchCount);

    /// <summary>
    ///     Returns a normalized collaborative score (0-1) for a candidate item.
    /// </summary>
    /// <param name="itemId">The candidate item ID.</param>
    /// <param name="coOccurrence">The collaborative co-occurrence map.</param>
    /// <param name="maxCoOccurrence">The pre-computed maximum co-occurrence value.</param>
    /// <returns>A normalized score between 0 and 1.</returns>
    internal static double ComputeCollaborativeScore(
        Guid itemId,
        Dictionary<Guid, double> coOccurrence,
        double maxCoOccurrence)
    {
        if (maxCoOccurrence <= 0 || !coOccurrence.TryGetValue(itemId, out var count))
        {
            return 0;
        }

        return Math.Clamp(count / maxCoOccurrence, 0.0, 1.0);
    }

    /// <summary>
    ///     Normalizes a Rotten Tomatoes critic rating (0-100%) to a 0-1 score. Returns 0.5 (neutral) when the value is null, zero, negative, NaN, or Infinity.
    /// </summary>
    /// <param name="criticRating">The critic rating value (0-100).</param>
    /// <returns>A normalized score between 0 and 1, or 0.5 if unavailable.</returns>
    internal static double NormalizeCriticRating(float? criticRating)
    {
        if (!criticRating.HasValue || !float.IsFinite(criticRating.Value) ||
            criticRating.Value < 0)
        {
            return 0.5; // Neutral fallback - does not penalize items without critic data
        }

        return Math.Clamp(criticRating.Value / 100.0, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes a combined critic score from TMDb community rating and Rotten Tomatoes Tomatometer.
    /// </summary>
    /// <param name="communityRating">TMDb community rating (0-10), or null if unavailable.</param>
    /// <param name="criticRating">Rotten Tomatoes Tomatometer (0-100%), or null if unavailable.</param>
    /// <returns>A combined score between 0 and 1, or 0.5 if no data is available.</returns>
    internal static double ComputeCombinedCriticScore(float? communityRating, float? criticRating)
    {
        var hasCommunity = communityRating.HasValue
                           && float.IsFinite(communityRating.Value)
                           && communityRating.Value >= 0;
        var hasCritic = criticRating.HasValue
                        && float.IsFinite(criticRating.Value)
                        && criticRating.Value >= 0;

        switch (hasCommunity)
        {
            case true when hasCritic:
                {
                    // Both available: 55% TMDb + 45% Tomatometer
                    var tmdb = Math.Clamp(communityRating!.Value / 10.0, 0.0, 1.0);
                    var tomatometer = Math.Clamp(criticRating!.Value / 100.0, 0.0, 1.0);
                    return Math.Clamp((0.55 * tmdb) + (0.45 * tomatometer), 0.0, 1.0);
                }

            case true:
                // Only TMDb available
                return Math.Clamp(communityRating!.Value / 10.0, 0.0, 1.0);
        }

        if (hasCritic)
        {
            // Only Tomatometer available
            return Math.Clamp(criticRating!.Value / 100.0, 0.0, 1.0);
        }

        return 0.5; // Neither available - neutral fallback
    }

    /// <summary>
    ///     Normalizes a community rating (typically 0-10) to a 0-1 score.
    /// </summary>
    /// <param name="communityRating">The community rating value.</param>
    /// <returns>A normalized rating between 0 and 1.</returns>
    internal static double NormalizeRating(float? communityRating)
    {
        if (!communityRating.HasValue || !float.IsFinite(communityRating.Value) ||
            communityRating.Value < 0)
        {
            return 0.5; // neutral default for unrated or NaN items
        }

        return Math.Min(communityRating.Value / 10.0, 1.0);
    }

    /// <summary>
    ///     Computes a recency score based on how recently the item was added or premiered.
    ///     Newer items get a slight boost.
    /// </summary>
    /// <param name="itemDate">
    ///     The item's premiere or creation date. Should be <see cref="DateTimeKind.Utc" />.
    ///     <see cref="DateTimeKind.Unspecified" /> values are subtracted from <see cref="DateTime.UtcNow" />
    ///     without conversion, effectively treating them as UTC.
    /// </param>
    /// <param name="now">
    ///     Reference point for "now" (defaults to <see cref="DateTime.UtcNow" />).
    ///     Exposed for deterministic unit testing.
    /// </param>
    /// <returns>A recency score between 0 and 1.</returns>
    internal static double ComputeRecencyScore(DateTime itemDate, DateTime? now = null)
    {
        var ageInDays = Math.Max(0.0, ((now ?? DateTime.UtcNow) - itemDate).TotalDays);

        // Exponential decay: half-life of ~365 days. ageInDays is clamped to >= 0, so
        // future dates and exact-now dates both return 1.0 (exp(0) == 1) without special-casing.
        return Math.Exp(-EngineConstants.RecencyDecayConstant * ageInDays);
    }

    /// <summary>
    ///     Computes year proximity score: items closer to the user's average watched year score higher.
    /// </summary>
    /// <param name="candidateYear">The candidate item's production year.</param>
    /// <param name="averageYear">The user's average watched production year.</param>
    /// <returns>A proximity score between 0 and 1.</returns>
    internal static double ComputeYearProximity(int? candidateYear, double averageYear)
    {
        if (!candidateYear.HasValue || averageYear <= 0)
        {
            return 0.5; // neutral default
        }

        var diff = Math.Abs(candidateYear.Value - averageYear);

        // Gaussian-like decay with σ ≈ 10 years
        return Math.Exp(-diff * diff / EngineConstants.YearProximityDenominator);
    }

    /// <summary>
    ///     Computes a normalized user rating score (0-1) for a candidate item.
    ///     If the user has not rated this item, returns 0.5 (neutral).
    /// </summary>
    /// <param name="watchedItem">The watched item entry, or null if the user hasn't interacted with it.</param>
    /// <returns>A normalized user rating between 0 and 1.</returns>
    internal static double ComputeUserRatingScore(WatchedItemInfo? watchedItem)
    {
        if (watchedItem?.UserRating is null or <= 0 || double.IsNaN(watchedItem.UserRating.Value) ||
            double.IsInfinity(watchedItem.UserRating.Value))
        {
            return 0.5; // neutral default - no user rating available or NaN/Infinity
        }

        // User ratings are typically 0-10, normalize to 0-1
        return Math.Clamp(watchedItem.UserRating.Value / 10.0, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes a completion-ratio-modulated engagement label for watched items. Instead of a flat label, this interpolates between WatchedLabelFloor and WatchedLabel based on how much of the item the user completed.
    /// </summary>
    /// <param name="completionRatio">The watch completion ratio (0-1).</param>
    /// <returns>
    ///     An engagement label between <see cref="EngineConstants.WatchedLabelFloor" /> and
    ///     <see cref="EngineConstants.WatchedLabel" />.
    /// </returns>
    internal static double ComputeEngagementLabel(double completionRatio)
    {
        // Clamp input to valid range
        var ratio = Math.Clamp(completionRatio, 0.0, 1.0);

        // Linear interpolation: floor + ratio * (ceiling - floor) At 0% completion: WatchedLabelFloor (0.5) - user chose to watch, still positive At 100% completion: WatchedLabel (0.85) - strong positive signal.
        return EngineConstants.WatchedLabelFloor +
               (ratio * (EngineConstants.WatchedLabel - EngineConstants.WatchedLabelFloor));
    }

    /// <summary>
    ///     Computes the watch completion ratio for a candidate item. Returns 0 if the user has never started the item (new candidate), or a ratio of played ticks to runtime ticks for partially watched items.
    /// </summary>
    /// <param name="watchedItem">The watched item entry, or null if the user hasn't interacted with it.</param>
    /// <returns>A completion ratio between 0 and 1.</returns>
    internal static double ComputeCompletionRatio(WatchedItemInfo? watchedItem)
    {
        if (watchedItem is null)
        {
            return 0.0; // not started - neutral for candidates
        }

        // Jellyfin resets PlaybackPositionTicks to 0 when an item is marked as played,
        // so rely on the Played flag rather than tick math for fully-watched items.
        if (watchedItem.Played)
        {
            return 1.0;
        }

        if (watchedItem.RuntimeTicks <= 0)
        {
            return 0.0; // no runtime info - neutral for candidates
        }

        return Math.Clamp((double)watchedItem.PlaybackPositionTicks / watchedItem.RuntimeTicks, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes the average production year from the user's watched and favorited items.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>The average production year, or 0 if no years are available.</returns>
    internal static double ComputeAverageYear(UserWatchProfile profile)
    {
        long sum = 0;
        var count = 0;

        foreach (var w in profile.WatchedItems.Where(w => (w.Played || w.IsFavorite) && w.Year is > 0))
        {
            sum += w.Year!.Value;
            count++;
        }

        return count > 0 ? (double)sum / count : 0;
    }

    /// <summary>
    ///     Computes the content-based nearest-neighbor score for a candidate item.
    /// </summary>
    /// <remarks>
    ///     Unlike GenreSimilarity (which compares against the aggregated user profile), this captures item-to-item affinity: a niche anime in a mostly-action user's library will still boost similar anime candidates because of the specific item-level match.
    /// </remarks>
    /// <param name="candidateGenres">The candidate's genre set (case-insensitive).</param>
    /// <param name="candidatePeople">The candidate's people/cast set (case-insensitive), or null if unavailable.</param>
    /// <param name="candidateStudios">The candidate's studios array, or null/empty if unavailable.</param>
    /// <param name="watchedGenreSets">Pre-computed genre sets for each watched item.</param>
    /// <param name="watchedPeopleSets">Pre-computed people sets for each watched item (parallel to genre sets).</param>
    /// <param name="watchedStudioSets">Pre-computed studio sets for each watched item (parallel to genre sets).</param>
    /// <returns>A composite similarity score between 0 and 1.</returns>
    internal static double ComputeContentNearestNeighborScore(
        HashSet<string> candidateGenres,
        HashSet<string>? candidatePeople,
        HashSet<string>? candidateStudios,
        IReadOnlyList<HashSet<string>> watchedGenreSets,
        IReadOnlyList<HashSet<string>> watchedPeopleSets,
        IReadOnlyList<HashSet<string>> watchedStudioSets)
    {
        if (watchedGenreSets.Count == 0)
        {
            return 0.0;
        }

        // Parallel-array invariant: all three lists MUST be the same length (populated in the same loop in Engine.GenerateForUser and the training-data builders).
        ReportParallelArrayMismatch(watchedGenreSets, watchedPeopleSets, watchedStudioSets);

        var maxComposite = 0.0;

        for (var i = 0; i < watchedGenreSets.Count; i++)
        {
            var composite = ComputeCompositeSimilarity(
                candidateGenres,
                candidatePeople,
                candidateStudios,
                watchedGenreSets[i],
                i < watchedPeopleSets.Count ? watchedPeopleSets[i] : null,
                i < watchedStudioSets.Count ? watchedStudioSets[i] : null);
            if (composite > maxComposite)
            {
                maxComposite = composite;
            }
        }

        return Math.Clamp(maxComposite, 0.0, 1.0);
    }

    /// <summary>
    ///     Detects and reports a parallel-array length mismatch across the watched-item set lists.
    /// </summary>
    /// <param name="watchedGenreSets">Pre-computed genre sets for each watched item.</param>
    /// <param name="watchedPeopleSets">Pre-computed people sets for each watched item (parallel to genre sets).</param>
    /// <param name="watchedStudioSets">Pre-computed studio sets for each watched item (parallel to genre sets).</param>
    private static void ReportParallelArrayMismatch(
        IReadOnlyList<HashSet<string>> watchedGenreSets,
        IReadOnlyList<HashSet<string>> watchedPeopleSets,
        IReadOnlyList<HashSet<string>> watchedStudioSets)
    {
        var mismatch = watchedGenreSets.Count != watchedPeopleSets.Count
            || watchedGenreSets.Count != watchedStudioSets.Count;
        Debug.Assert(
            !mismatch,
            $"Parallel array length mismatch: genres={watchedGenreSets.Count}, people={watchedPeopleSets.Count}, studios={watchedStudioSets.Count}. "
                + "All three watched-item set lists must have the same length.");
        if (mismatch)
        {
            var afterIncrement = Interlocked.Increment(ref _parallelArrayMismatchCount);
            if (afterIncrement == 1)
            {
                Trace.TraceWarning(
                    "ContentScoring.ComputeContentNearestNeighborScore observed a parallel-array length mismatch "
                    + "(genres={0}, people={1}, studios={2}). "
                    + "This is always a bug - the score is degrading gracefully by treating missing entries as absent. "
                    + "Subsequent mismatches are counted silently via ParallelArrayMismatchCount.",
                    watchedGenreSets.Count,
                    watchedPeopleSets.Count,
                    watchedStudioSets.Count);
            }
        }
    }

    /// <summary>
    ///     Computes the weighted composite similarity between the candidate and a single watched item: genre Jaccard (50%), people Jaccard (30%), and binary studio overlap (20%).
    /// </summary>
    /// <param name="candidateGenres">The candidate's genre set (case-insensitive).</param>
    /// <param name="candidatePeople">The candidate's people/cast set (case-insensitive), or null if unavailable.</param>
    /// <param name="candidateStudios">The candidate's studios array, or null/empty if unavailable.</param>
    /// <param name="watchedGenreSet">The watched item's genre set.</param>
    /// <param name="watchedPeopleSet">The watched item's people set, or null if the parallel entry is missing.</param>
    /// <param name="watchedStudioSet">The watched item's studio set, or null if the parallel entry is missing.</param>
    /// <returns>The composite similarity for this watched item.</returns>
    private static double ComputeCompositeSimilarity(
        HashSet<string> candidateGenres,
        HashSet<string>? candidatePeople,
        HashSet<string>? candidateStudios,
        HashSet<string> watchedGenreSet,
        HashSet<string>? watchedPeopleSet,
        HashSet<string>? watchedStudioSet)
    {
        // Genre Jaccard (50% of composite)
        var genreJaccard = SimilarityComputer.ComputeJaccardFromSets(candidateGenres, watchedGenreSet);

        // People Jaccard (30% of composite). Missing parallel entry -> 0 contribution
        // but the genre dimension keeps working.
        var peopleJaccard = candidatePeople is { Count: > 0 } && watchedPeopleSet is not null
            ? SimilarityComputer.ComputeJaccardFromSets(candidatePeople, watchedPeopleSet)
            : 0.0;

        // Studio overlap (20% of composite) - binary: any shared studio = 1.0
        var studioOverlap = 0.0;
        if (candidateStudios is { Count: > 0 }
            && watchedStudioSet is { Count: > 0 })
        {
            studioOverlap = candidateStudios.Overlaps(watchedStudioSet) ? 1.0 : 0.0;
        }

        return (0.50 * genreJaccard) + (0.30 * peopleJaccard) + (0.20 * studioOverlap);
    }

    /// <summary>
    ///     Computes a popularity proxy score from collaborative and critic signals. When collaborative data is available, uses a scaled collaborative score.
    /// </summary>
    /// <param name="collaborativeScore">The normalized collaborative score (0-1).</param>
    /// <param name="combinedCriticScore">The combined critic score (0-1).</param>
    /// <returns>A popularity score between 0 and 1.</returns>
    internal static double ComputePopularityScore(double collaborativeScore, double combinedCriticScore)
    {
        return collaborativeScore > 0
            ? Math.Clamp(collaborativeScore * 0.8, 0.0, 1.0)
            : Math.Clamp(combinedCriticScore * 0.3, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes user level engagement aggregates used for the three interaction features.
    /// </summary>
    /// <param name="profile">The user profile.</param>
    /// <returns>Average completion, abandon rate and whether user has enough history.</returns>
    internal static (double AvgCompletion, double AbandonRate, bool IsActive) ComputeUserEngagementAggregates(UserWatchProfile profile)
    {
        var meaningful = profile.WatchedItems.Where(w => w.HasMeaningfulInteraction()).ToList();
        if (meaningful.Count == 0)
        {
            return (0.5, 0.0, false);
        }

        var withCompletion = meaningful.Where(w => w.RuntimeTicks > 0 || w.Played).ToList();
        var avgCompletion = withCompletion.Count > 0
            ? withCompletion.Average(ComputeCompletionRatio)
            : 0.5;

        var abandonCount = withCompletion.Count(w =>
        {
            var r = ComputeCompletionRatio(w);
            return r > 0.0 && r < CandidateFeatures.AbandonedThreshold;
        });
        var abandonRate = withCompletion.Count > 0
            ? (double)abandonCount / withCompletion.Count
            : 0.0;

        var isActive = meaningful.Count >= 10;
        return (Math.Clamp(avgCompletion, 0.0, 1.0), Math.Clamp(abandonRate, 0.0, 1.0), isActive);
    }

    /// <summary>
    ///     Computes genre level engagement for a candidate. Returns familiarity, avg completion and abandon rate for the candidate genres.
    /// </summary>
    /// <param name="candidateGenres">Candidate genres.</param>
    /// <param name="profile">User profile.</param>
    /// <returns>Familiarity, avg completion and abandon rate.</returns>
    internal static (double Familiarity, double AvgCompletion, double AbandonRate) ComputeGenreEngagement(
        IReadOnlyList<string> candidateGenres,
        UserWatchProfile profile)
    {
        if (candidateGenres.Count == 0 || profile.WatchedItems.Count == 0)
        {
            return (0.0, 0.5, 0.0);
        }

        var candidateSet = new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase);
        var matching = profile.WatchedItems
            .Where(w => w.HasMeaningfulInteraction() && w.Genres is not null && w.Genres.Any(candidateSet.Contains))
            .ToList();

        if (matching.Count == 0)
        {
            return (0.0, 0.5, 0.0);
        }

        var familiarity = Math.Clamp((double)matching.Count / Math.Max(profile.WatchedItems.Count(w => w.HasMeaningfulInteraction()), 1), 0.0, 1.0);
        var withCompletion = matching.Where(w => w.RuntimeTicks > 0 || w.Played).ToList();
        var avgCompletion = withCompletion.Count > 0 ? withCompletion.Average(ComputeCompletionRatio) : 0.5;
        var abandonCount = withCompletion.Count(w =>
        {
            var r = ComputeCompletionRatio(w);
            return r > 0.0 && r < CandidateFeatures.AbandonedThreshold;
        });
        var abandonRate = withCompletion.Count > 0 ? (double)abandonCount / withCompletion.Count : 0.0;
        return (Math.Clamp(familiarity, 0.0, 1.0), Math.Clamp(avgCompletion, 0.0, 1.0), Math.Clamp(abandonRate, 0.0, 1.0));
    }

    /// <summary>
    ///     Computes series affinity as max Jaccard to progressing series (30 to 80 percent watched).
    /// </summary>
    /// <param name="candidate">Candidate item.</param>
    /// <param name="profile">User profile.</param>
    /// <param name="seriesEpisodeCounts">Series episode counts.</param>
    /// <param name="peopleLookup">People lookup.</param>
    /// <returns>Series affinity 0 to 1.</returns>
    internal static double ComputeSeriesAffinity(
        BaseItem candidate,
        UserWatchProfile profile,
        IReadOnlyDictionary<Guid, int> seriesEpisodeCounts,
        Dictionary<Guid, HashSet<string>> peopleLookup)
    {
        if (candidate is not Series)
        {
            return 0.0;
        }

        var progressing = new List<Guid>();
        var watchedBySeries = new Dictionary<Guid, List<WatchedItemInfo>>();
        foreach (var w in profile.WatchedItems.Where(w => w.SeriesId.HasValue && w.HasMeaningfulInteraction()))
        {
            if (!watchedBySeries.TryGetValue(w.SeriesId!.Value, out var list))
            {
                list = new List<WatchedItemInfo>();
                watchedBySeries[w.SeriesId!.Value] = list;
            }

            list.Add(w);
        }

        foreach (var kv in watchedBySeries)
        {
            if (!seriesEpisodeCounts.TryGetValue(kv.Key, out var total) || total <= 0)
            {
                continue;
            }

            var watchedCount = kv.Value.Select(v => v.ItemId).Distinct().Count();
            var ratio = (double)watchedCount / total;
            if (ratio >= 0.3 && ratio <= 0.8)
            {
                progressing.Add(kv.Key);
            }
        }

        if (progressing.Count == 0)
        {
            return 0.0;
        }

        var candidateGenres = candidate.Genres ?? Array.Empty<string>();
        var candidateGenreSet = new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase);
        peopleLookup.TryGetValue(candidate.Id, out var candidatePeople);
        var candidatePeopleSet = candidatePeople is not null ? new HashSet<string>(candidatePeople, StringComparer.OrdinalIgnoreCase) : null;

        var best = 0.0;
        foreach (var seriesId in progressing)
        {
            var seriesGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seriesPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in watchedBySeries[seriesId])
            {
                if (w.Genres is not null)
                {
                    foreach (var g in w.Genres)
                    {
                        seriesGenres.Add(g);
                    }
                }

                if (peopleLookup.TryGetValue(w.ItemId, out var p))
                {
                    foreach (var person in p)
                    {
                        seriesPeople.Add(person);
                    }
                }

                if (peopleLookup.TryGetValue(seriesId, out var sp))
                {
                    foreach (var person in sp)
                    {
                        seriesPeople.Add(person);
                    }
                }
            }

            var genreJaccard = SimilarityComputer.ComputeJaccardFromSets(candidateGenreSet, seriesGenres);
            var peopleJaccard = candidatePeopleSet is not null && seriesPeople.Count > 0
                ? SimilarityComputer.ComputeJaccardFromSets(candidatePeopleSet, seriesPeople)
                : 0.0;
            var composite = (0.6 * genreJaccard) + (0.4 * peopleJaccard);
            if (composite > best)
            {
                best = composite;
            }
        }

        return Math.Clamp(best, 0.0, 1.0);
    }
}
