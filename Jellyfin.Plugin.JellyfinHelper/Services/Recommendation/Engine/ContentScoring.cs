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
    ///     Smoothing constant for per-genre engagement confidence shrinkage. At n samples the measured
    ///     completion/abandon rate is trusted by n / (n + K); K = 3 means 3 samples give half confidence.
    /// </summary>
    internal const int GenreEngagementShrinkageK = 3;

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
    ///     Normalizes a raw 0-10 user rating to 0-1, or null when the rating is absent, non-positive or
    ///     non-finite. Shared by the genre-level rating aggregate so training and inference treat a
    ///     missing rating identically (it contributes no sample rather than a fabricated value).
    /// </summary>
    /// <param name="userRating">The raw user rating (0-10), or null.</param>
    /// <returns>The normalized rating in 0-1, or null when there is no usable rating.</returns>
    private static double? NormalizeUserRating(double? userRating)
    {
        if (userRating is null or <= 0 || double.IsNaN(userRating.Value) || double.IsInfinity(userRating.Value))
        {
            return null;
        }

        return Math.Clamp(userRating.Value / 10.0, 0.0, 1.0);
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
    ///     Builds the genre-engagement exclude set for a series example: the series id plus every watched
    ///     record whose SeriesId is that series. Derived from the profile rather than a prebuilt episode
    ///     lookup so it stays leak-safe even if such a lookup is later narrowed by a filter; a series'
    ///     own episodes must never feed its own familiarity, completion or abandon signal.
    /// </summary>
    /// <param name="seriesId">The series' id.</param>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>The set of item ids to exclude from the series' genre-engagement aggregate.</returns>
    internal static HashSet<Guid> BuildSeriesExcludeSet(Guid seriesId, UserWatchProfile profile)
    {
        var set = new HashSet<Guid> { seriesId };
        foreach (var w in profile.WatchedItems)
        {
            if (w.SeriesId == seriesId)
            {
                set.Add(w.ItemId);
            }
        }

        return set;
    }

    /// <summary>
    ///     Computes genre level engagement for a candidate. Returns familiarity, avg completion and abandon rate for the candidate genres.
    /// </summary>
    /// <param name="candidateGenres">Candidate genres.</param>
    /// <param name="profile">User profile.</param>
    /// <param name="excludeItemIds">Optional set of item IDs to exclude from the aggregate (e.g. the target item in training paths to prevent label leakage).</param>
    /// <returns>Familiarity, avg completion and abandon rate.</returns>
    internal static (double Familiarity, double AvgCompletion, double AbandonRate) ComputeGenreEngagement(
        IReadOnlyList<string> candidateGenres,
        UserWatchProfile profile,
        HashSet<Guid>? excludeItemIds = null)
    {
        if (candidateGenres.Count == 0 || profile.WatchedItems.Count == 0)
        {
            return (0.0, 0.5, 0.0);
        }

        var candidateSet = new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase);
        var matching = profile.WatchedItems
            .Where(w => w.HasMeaningfulInteraction()
                     && w.Genres is not null
                     && w.Genres.Any(candidateSet.Contains)
                     && (excludeItemIds is null || !excludeItemIds.Contains(w.ItemId)))
            .ToList();

        if (matching.Count == 0)
        {
            return (0.0, 0.5, 0.0);
        }

        var totalMeaningful = profile.WatchedItems.Count(w => w.HasMeaningfulInteraction()
            && (excludeItemIds is null || !excludeItemIds.Contains(w.ItemId)));
        var familiarity = Math.Clamp((double)matching.Count / Math.Max(totalMeaningful, 1), 0.0, 1.0);
        var withCompletion = matching.Where(w => w.RuntimeTicks > 0 || w.Played).ToList();
        var avgCompletion = withCompletion.Count > 0 ? withCompletion.Average(ComputeCompletionRatio) : 0.5;
        var abandonCount = withCompletion.Count(w =>
        {
            var r = ComputeCompletionRatio(w);
            return r > 0.0 && r < CandidateFeatures.AbandonedThreshold;
        });
        var abandonRate = withCompletion.Count > 0 ? (double)abandonCount / withCompletion.Count : 0.0;

        // Confidence shrinkage: with few samples in this genre, avgCompletion/abandonRate are noisy
        // estimates (one abandoned episode makes abandonRate 1.0). Shrink them toward their neutral
        // values (0.5 / 0.0) by n / (n + k) so a thin genre contributes a damped signal and a rich
        // genre contributes its full measured value. This is per-genre on purpose: noise is local to
        // each genre, so a user-global activity flag would over- or under-damp genres unevenly.
        var confidence = withCompletion.Count / (double)(withCompletion.Count + GenreEngagementShrinkageK);
        avgCompletion = 0.5 + (confidence * (avgCompletion - 0.5));
        abandonRate *= confidence;

        return (Math.Clamp(familiarity, 0.0, 1.0), Math.Clamp(avgCompletion, 0.0, 1.0), Math.Clamp(abandonRate, 0.0, 1.0));
    }

    /// <summary>
    ///     Cached genre-engagement scoring using a <see cref="GenreEngagementContext"/> built once per
    ///     user. Produces the same value as the direct
    ///     <see cref="ComputeGenreEngagement(IReadOnlyList{string}, UserWatchProfile, HashSet{Guid})"/>
    ///     overload with no exclusion (the inference case), iterating the precomputed items in the same
    ///     order so the arithmetic is identical.
    /// </summary>
    /// <param name="candidateGenres">Candidate genres.</param>
    /// <param name="context">Pre-built per-user genre-engagement context.</param>
    /// <returns>Familiarity, avg completion and abandon rate.</returns>
    internal static (double Familiarity, double AvgCompletion, double AbandonRate) ComputeGenreEngagement(
        IReadOnlyList<string> candidateGenres,
        GenreEngagementContext context)
    {
        var totalMeaningful = context.MeaningfulWatches.Count;
        if (candidateGenres.Count == 0 || totalMeaningful == 0)
        {
            return (0.0, 0.5, 0.0);
        }

        var candidateSet = new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase);
        var matchCount = 0;
        var withCompletionCount = 0;
        var completionSum = 0.0;
        var abandonCount = 0;
        foreach (var w in context.MeaningfulWatches)
        {
            if (w.Genres is null || !w.Genres.Any(candidateSet.Contains))
            {
                continue;
            }

            matchCount++;
            if (!w.CountsForCompletion)
            {
                continue;
            }

            withCompletionCount++;
            completionSum += w.Completion;
            if (w.Completion > 0.0 && w.Completion < CandidateFeatures.AbandonedThreshold)
            {
                abandonCount++;
            }
        }

        if (matchCount == 0)
        {
            return (0.0, 0.5, 0.0);
        }

        var familiarity = Math.Clamp((double)matchCount / Math.Max(totalMeaningful, 1), 0.0, 1.0);
        var avgCompletion = withCompletionCount > 0 ? completionSum / withCompletionCount : 0.5;
        var abandonRate = withCompletionCount > 0 ? (double)abandonCount / withCompletionCount : 0.0;

        var confidence = withCompletionCount / (double)(withCompletionCount + GenreEngagementShrinkageK);
        avgCompletion = 0.5 + (confidence * (avgCompletion - 0.5));
        abandonRate *= confidence;

        return (Math.Clamp(familiarity, 0.0, 1.0), Math.Clamp(avgCompletion, 0.0, 1.0), Math.Clamp(abandonRate, 0.0, 1.0));
    }

    /// <summary>
    ///     Builds the user-invariant inputs for genre engagement once per scoring pass. The candidate
    ///     loop only varies the candidate genres, while the meaningful-interaction set and each item's
    ///     completion ratio are fixed per user. Precompute them once and reuse across all candidates via
    ///     the cached <see cref="ComputeGenreEngagement(IReadOnlyList{string}, GenreEngagementContext)"/>
    ///     overload, avoiding a full WatchedItems rescan (and completion-ratio recompute) per candidate.
    /// </summary>
    /// <param name="profile">User profile.</param>
    /// <returns>The per-user genre-engagement context.</returns>
    internal static GenreEngagementContext BuildGenreEngagementContext(UserWatchProfile profile)
    {
        var meaningful = new List<MeaningfulWatch>();
        foreach (var w in profile.WatchedItems)
        {
            if (!w.HasMeaningfulInteraction())
            {
                continue;
            }

            var genreSet = w.Genres is { Count: > 0 }
                ? new HashSet<string>(w.Genres, StringComparer.OrdinalIgnoreCase)
                : null;
            var countsForCompletion = w.RuntimeTicks > 0 || w.Played;
            var completion = countsForCompletion ? ComputeCompletionRatio(w) : 0.0;
            var normalizedRating = NormalizeUserRating(w.UserRating);
            meaningful.Add(new MeaningfulWatch(genreSet, countsForCompletion, completion, normalizedRating));
        }

        return new GenreEngagementContext(meaningful);
    }

    /// <summary>
    ///     Computes the user's average normalized rating (0-1) across the candidate's genres, drawn from
    ///     items the user actually rated. Only rated items contribute; the mean is shrunk toward neutral
    ///     0.5 by the same n/(n+K) confidence as genre engagement so a single rating does not dominate.
    ///     Returns 0.5 when there is no rated overlap.
    /// </summary>
    /// <param name="candidateGenres">Candidate genres.</param>
    /// <param name="profile">User profile.</param>
    /// <param name="excludeItemIds">Item IDs to exclude (the target's own records in training paths).</param>
    /// <returns>The genre-level user-rating score in 0-1 (0.5 neutral).</returns>
    internal static double ComputeGenreRatingScore(
        IReadOnlyList<string> candidateGenres,
        UserWatchProfile profile,
        HashSet<Guid>? excludeItemIds = null)
    {
        if (candidateGenres.Count == 0 || profile.WatchedItems.Count == 0)
        {
            return 0.5;
        }

        var candidateSet = new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase);
        var ratingSum = 0.0;
        var ratingCount = 0;
        foreach (var w in profile.WatchedItems)
        {
            if (!w.HasMeaningfulInteraction()
                || w.Genres is null
                || !w.Genres.Any(candidateSet.Contains)
                || (excludeItemIds is not null && excludeItemIds.Contains(w.ItemId)))
            {
                continue;
            }

            var normalized = NormalizeUserRating(w.UserRating);
            if (normalized is null)
            {
                continue;
            }

            ratingSum += normalized.Value;
            ratingCount++;
        }

        return ShrinkRatingToNeutral(ratingSum, ratingCount);
    }

    /// <summary>
    ///     Cached genre-level user-rating score using a <see cref="GenreEngagementContext"/>. Produces the
    ///     same value as the direct <see cref="ComputeGenreRatingScore(IReadOnlyList{string}, UserWatchProfile, HashSet{Guid})"/>
    ///     overload with no exclusion (the inference case), iterating the precomputed items in the same
    ///     order so the arithmetic is identical.
    /// </summary>
    /// <param name="candidateGenres">Candidate genres.</param>
    /// <param name="context">Pre-built per-user genre-engagement context.</param>
    /// <returns>The genre-level user-rating score in 0-1 (0.5 neutral).</returns>
    internal static double ComputeGenreRatingScore(
        IReadOnlyList<string> candidateGenres,
        GenreEngagementContext context)
    {
        if (candidateGenres.Count == 0 || context.MeaningfulWatches.Count == 0)
        {
            return 0.5;
        }

        var candidateSet = new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase);
        var ratingSum = 0.0;
        var ratingCount = 0;
        foreach (var w in context.MeaningfulWatches)
        {
            if (w.Genres is null || !w.Genres.Any(candidateSet.Contains) || w.NormalizedRating is null)
            {
                continue;
            }

            ratingSum += w.NormalizedRating.Value;
            ratingCount++;
        }

        return ShrinkRatingToNeutral(ratingSum, ratingCount);
    }

    /// <summary>
    ///     Shrinks a summed genre rating toward neutral 0.5 by n/(n+K) confidence. Shared by both
    ///     <see cref="ComputeGenreRatingScore(IReadOnlyList{string}, UserWatchProfile, HashSet{Guid})"/>
    ///     overloads so the direct and cached paths are arithmetically identical.
    /// </summary>
    /// <param name="ratingSum">Sum of normalized ratings over the matched, rated items.</param>
    /// <param name="ratingCount">Number of matched, rated items.</param>
    /// <returns>The shrunk score in 0-1.</returns>
    private static double ShrinkRatingToNeutral(double ratingSum, int ratingCount)
    {
        if (ratingCount == 0)
        {
            return 0.5;
        }

        var avgRating = ratingSum / ratingCount;
        var confidence = ratingCount / (double)(ratingCount + GenreEngagementShrinkageK);
        return Math.Clamp(0.5 + (confidence * (avgRating - 0.5)), 0.0, 1.0);
    }

    /// <summary>
    ///     Builds the user-invariant series-affinity inputs once per scoring pass. Pass the returned
    ///     context into <see cref="ComputeSeriesAffinity(BaseItem, SeriesAffinityContext, Dictionary{Guid, HashSet{string}})"/>
    ///     inside the candidate loop to avoid rebuilding watched-series lookups per candidate.
    /// </summary>
    /// <param name="profile">The user watch profile.</param>
    /// <param name="seriesEpisodeCounts">Library-wide per-series total episode count map.</param>
    /// <returns>A <see cref="SeriesAffinityContext"/> holding the pre-built lookups for this user.</returns>
    internal static SeriesAffinityContext BuildSeriesAffinityContext(
        UserWatchProfile profile,
        IReadOnlyDictionary<Guid, int> seriesEpisodeCounts)
    {
        var watchedBySeries = BuildWatchedBySeriesLookup(profile);
        var progressing = GetProgressingSeriesIds(watchedBySeries, seriesEpisodeCounts);
        return new SeriesAffinityContext(watchedBySeries, progressing);
    }

    /// <summary>
    ///     Scores series affinity using a pre-built <see cref="SeriesAffinityContext"/>.
    ///     Use this overload inside the candidate loop (avoids rebuilding watched-series lookups per candidate).
    /// </summary>
    /// <param name="candidate">Candidate item.</param>
    /// <param name="context">Pre-built series-affinity context for the current user.</param>
    /// <param name="peopleLookup">People lookup.</param>
    /// <returns>Series affinity 0 to 1.</returns>
    internal static double ComputeSeriesAffinity(
        BaseItem candidate,
        SeriesAffinityContext context,
        Dictionary<Guid, HashSet<string>> peopleLookup)
    {
        if (candidate is not Series || context.ProgressingSeriesIds.Count == 0)
        {
            return 0.0;
        }

        var candidateGenreSet = new HashSet<string>(
            candidate.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        peopleLookup.TryGetValue(candidate.Id, out var candidatePeople);
        var candidatePeopleSet = candidatePeople is not null
            ? new HashSet<string>(candidatePeople, StringComparer.OrdinalIgnoreCase)
            : null;

        return ComputeBestSeriesJaccard(
            context.ProgressingSeriesIds, context.WatchedBySeries, peopleLookup, candidateGenreSet, candidatePeopleSet);
    }

    /// <summary>
    ///     Scores series affinity from raw candidate data without a <see cref="BaseItem"/>. Use in training paths
    ///     where only genre and people metadata are available, not a live <see cref="BaseItem"/> instance.
    /// </summary>
    /// <param name="isSeries">True if the candidate is a series.</param>
    /// <param name="candidateId">Candidate item ID (used for people lookup).</param>
    /// <param name="candidateGenres">Candidate genre list.</param>
    /// <param name="context">Pre-built series-affinity context for the current user.</param>
    /// <param name="peopleLookup">People lookup (item ID to cast/director names).</param>
    /// <param name="excludeSeriesId">Optional series ID to exclude from the progressing-series comparison (e.g. the candidate's own series in training paths, preventing self-leakage).</param>
    /// <returns>Series affinity 0 to 1.</returns>
    internal static double ComputeSeriesAffinity(
        bool isSeries,
        Guid candidateId,
        IReadOnlyList<string> candidateGenres,
        SeriesAffinityContext context,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        Guid? excludeSeriesId = null)
    {
        if (!isSeries || context.ProgressingSeriesIds.Count == 0)
        {
            return 0.0;
        }

        var candidateGenreSet = candidateGenres.Count > 0
            ? new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        peopleLookup.TryGetValue(candidateId, out var candidatePeople);
        var candidatePeopleSet = candidatePeople is not null
            ? new HashSet<string>(candidatePeople, StringComparer.OrdinalIgnoreCase)
            : null;

        return ComputeBestSeriesJaccard(
            context.ProgressingSeriesIds, context.WatchedBySeries, peopleLookup, candidateGenreSet, candidatePeopleSet, excludeSeriesId);
    }

    private static Dictionary<Guid, List<WatchedItemInfo>> BuildWatchedBySeriesLookup(UserWatchProfile profile)
    {
        var watchedBySeries = new Dictionary<Guid, List<WatchedItemInfo>>();
        foreach (var w in profile.WatchedItems.Where(w => w.SeriesId.HasValue && w.HasMeaningfulInteraction()))
        {
            var sid = w.SeriesId.GetValueOrDefault();
            if (!watchedBySeries.TryGetValue(sid, out var list))
            {
                list = new List<WatchedItemInfo>();
                watchedBySeries[sid] = list;
            }

            list.Add(w);
        }

        return watchedBySeries;
    }

    private static List<Guid> GetProgressingSeriesIds(
        Dictionary<Guid, List<WatchedItemInfo>> watchedBySeries,
        IReadOnlyDictionary<Guid, int> seriesEpisodeCounts)
    {
        var progressing = new List<Guid>();
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

        return progressing;
    }

    private static double ComputeBestSeriesJaccard(
        List<Guid> progressing,
        Dictionary<Guid, List<WatchedItemInfo>> watchedBySeries,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        HashSet<string> candidateGenreSet,
        HashSet<string>? candidatePeopleSet,
        Guid? excludeSeriesId = null)
    {
        var best = 0.0;
        foreach (var seriesId in progressing)
        {
            // Skip the candidate's own series so a watched series cannot be scored for affinity to
            // itself (self-leakage in training examples built from that same series).
            if (excludeSeriesId.HasValue && seriesId == excludeSeriesId.Value)
            {
                continue;
            }

            CollectSeriesFeatureSets(seriesId, watchedBySeries[seriesId], peopleLookup, out var seriesGenres, out var seriesPeople);
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

    private static void CollectSeriesFeatureSets(
        Guid seriesId,
        List<WatchedItemInfo> items,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        out HashSet<string> genres,
        out HashSet<string> people)
    {
        genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The series' own people are invariant across its episodes, so add them once rather than
        // re-adding the same set inside the per-episode loop below.
        AddPeopleFromLookup(seriesId, peopleLookup, people);
        foreach (var w in items)
        {
            if (w.Genres is not null)
            {
                foreach (var g in w.Genres)
                {
                    genres.Add(g);
                }
            }

            AddPeopleFromLookup(w.ItemId, peopleLookup, people);
        }
    }

    private static void AddPeopleFromLookup(Guid id, Dictionary<Guid, HashSet<string>> peopleLookup, HashSet<string> target)
    {
        if (peopleLookup.TryGetValue(id, out var found))
        {
            foreach (var person in found)
            {
                target.Add(person);
            }
        }
    }

    /// <summary>
    ///     A single meaningfully-interacted watched item reduced to just the fields genre engagement
    ///     needs, so the candidate loop reads precomputed values instead of re-deriving them per candidate.
    /// </summary>
    /// <param name="Genres">The item's genres as a case-insensitive set, or null when it has none.</param>
    /// <param name="CountsForCompletion">Whether the item contributes to the completion/abandon aggregate (has runtime or is played).</param>
    /// <param name="Completion">The item's precomputed completion ratio (only meaningful when <paramref name="CountsForCompletion"/> is true).</param>
    /// <param name="NormalizedRating">The item's user rating normalized to 0-1, or null when the user did not rate it.</param>
    internal readonly record struct MeaningfulWatch(
        HashSet<string>? Genres,
        bool CountsForCompletion,
        double Completion,
        double? NormalizedRating);

    /// <summary>
    ///     Pre-computed per-user series-affinity inputs. Build once per scoring pass with
    ///     <see cref="ContentScoring.BuildSeriesAffinityContext"/> and pass into the
    ///     <see cref="ContentScoring.ComputeSeriesAffinity(BaseItem, SeriesAffinityContext, Dictionary{Guid, HashSet{string}})"/>
    ///     overload to avoid rebuilding watched-series lookups for every candidate.
    /// </summary>
    /// <param name="WatchedBySeries">Maps each series ID to the user's watched episodes in that series.</param>
    /// <param name="ProgressingSeriesIds">Series IDs where the user has watched between 30 % and 80 % of episodes.</param>
    internal sealed record SeriesAffinityContext(
        Dictionary<Guid, List<WatchedItemInfo>> WatchedBySeries,
        List<Guid> ProgressingSeriesIds);

    /// <summary>
    ///     User-invariant genre-engagement inputs built once per scoring pass. Holds the user's
    ///     meaningfully-interacted watched items in their original order so the cached
    ///     <see cref="ComputeGenreEngagement(IReadOnlyList{string}, GenreEngagementContext)"/> overload
    ///     reproduces the direct method's arithmetic exactly.
    /// </summary>
    /// <param name="MeaningfulWatches">Meaningful watched items, in profile order.</param>
    internal sealed record GenreEngagementContext(
        List<MeaningfulWatch> MeaningfulWatches);
}
