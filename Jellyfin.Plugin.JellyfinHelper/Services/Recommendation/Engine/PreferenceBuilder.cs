using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Builds user preference vectors and sets from watch history:
///     genre preferences, studio preferences, tag preferences, and people preferences.
/// </summary>
internal static class PreferenceBuilder
{
    /// <summary>
    ///     Upper cap for the raw PlayCount fed into the log1p transform. Guards against pathological metadata (e.g.
    /// </summary>
    private const int PlayCountMaxForLog1p = 100;

    /// <summary>
    ///     Lower bound of the progression multiplier: even an abandoned series must still contribute a fraction of its signal so users with mostly-abandoned watch history don't end up with an empty preference vector.
    /// </summary>
    private const double ProgressionFloor = 0.3;

    /// <summary>
    ///     Upper bound of the progression multiplier: a fully-completed series gets a modest boost above the baseline "1.0" for movies, so binge-watched shows shape preferences more strongly than a single played row.
    /// </summary>
    private const double ProgressionCeiling = 1.5;

    /// <summary>
    ///     Linear span from floor to ceiling, i.e. how much the raw ratio moves the multiplier.
    /// </summary>
    private const double ProgressionSpan = ProgressionCeiling - ProgressionFloor;

    /// <summary>
    ///     Target maximum contribution of the PlayCount log1p boost, chosen so heavy re-watchers produce a meaningful signal not drowned out by the favorite additive (FavoriteGenreBoostFactor = 3.0), while staying sub-favorite so an explicit favorite click always.
    /// </summary>
    private const double PlayCountLog1pCeiling = 2.0;

    /// <summary>Decay constant derived from half-life: ln(2) / halfLife.</summary>
    private static readonly double GenreDecayConstant = Math.Log(2.0) / EngineConstants.GenreDecayHalfLifeDays;

    /// <summary>
    ///     Scale factor for the log1p PlayCount contribution.
    /// </summary>
    private static readonly double PlayCountLog1pScale = PlayCountLog1pCeiling / Math.Log(1.0 + PlayCountMaxForLog1p);

    /// <summary>
    ///     Builds a normalized genre preference vector from watch history. Each genre is weighted by recency (180-day half-life exponential decay), play count (log1p boost), and favorites (additive boost).
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <param name="seriesEpisodeCounts">
    ///     Optional map <c>seriesId -> totalEpisodeCount</c> supplied by the caller (typically
    ///     built once in <c>Engine.LoadCandidateItems</c>). When provided, episode rows are
    ///     weighted by <c>ProgressionMultiplier(playedInSeries / totalEpisodes)</c> so a
    ///     series watched to completion drives genre preferences more strongly than a series
    ///     the user abandoned after two episodes. When null, all episode rows keep their raw
    ///     weight (backward compatible).
    /// </param>
    /// <returns>A dictionary mapping genre names to normalized weights (0-1).</returns>
    internal static Dictionary<string, double> BuildGenrePreferenceVector(
        UserWatchProfile profile,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts = null)
    {
        var vector = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (profile.WatchedItems.Count == 0 && profile.GenreDistribution.Count == 0)
        {
            return vector;
        }

        // Shared per-series played counter. BuildGenrePreferenceVector and BuildPeoplePreferenceWeights use the exact same aggregation, so extracting the loop guarantees train/serve parity between the Genre- and People-similarity features.
        var watchedEpisodesPerSeries = BuildWatchedEpisodesPerSeries(profile, seriesEpisodeCounts);

        // Build genre preferences with temporal decay - recent watches count more
        var now = DateTime.UtcNow;
        AccumulateWatchedGenreWeights(vector, profile, seriesEpisodeCounts, watchedEpisodesPerSeries, now);

        // Merge GenreDistribution as base weights for genres not in WatchedItems (backward compat; catches genres from items whose WatchedItemInfo has no Genres array, e.g.
        MergeGenreDistributionWeights(vector, profile);

        if (vector.Count == 0)
        {
            return vector;
        }

        // Expand first, normalize afterwards so proximity-derived weights participate in the same max-normalization pass as the base entries.
        ExpandGenreProximity(vector, profile);

        var maxWeight = vector.Values.Max();
        if (maxWeight <= 0)
        {
            return vector;
        }

        foreach (var genre in vector.Keys.ToList())
        {
            vector[genre] /= maxWeight;
        }

        return vector;
    }

    /// <summary>
    ///     Accumulates temporal-decay genre weights from the profile's watched items into .
    /// </summary>
    /// <param name="vector">The genre weight accumulator to add into.</param>
    /// <param name="profile">The user's watch profile.</param>
    /// <param name="seriesEpisodeCounts">Optional series episode-count map.</param>
    /// <param name="watchedEpisodesPerSeries">Precomputed per-series watched-episode counter.</param>
    /// <param name="now">The reference timestamp for temporal decay.</param>
    private static void AccumulateWatchedGenreWeights(
        Dictionary<string, double> vector,
        UserWatchProfile profile,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        Dictionary<Guid, int>? watchedEpisodesPerSeries,
        DateTime now)
    {
        foreach (var item in profile.WatchedItems)
        {
            // Eligibility must include PlayCount > 0 so the SAME rows contributing to watchedEpisodesPerSeries also contribute their genres here.
            if (!IsEligibleForPreferenceWeighting(item))
            {
                continue;
            }

            // F-04 phantom guard: skip rows whose series has been deleted from the library.
            if (IsPhantomSeriesRow(item, seriesEpisodeCounts))
            {
                continue;
            }

            if (item.Genres is not { Count: > 0 })
            {
                continue;
            }

            var weight = ComputeGenreItemWeight(item, now, seriesEpisodeCounts, watchedEpisodesPerSeries);

            foreach (var genre in item.Genres.Where(static g => !string.IsNullOrWhiteSpace(g)))
            {
                vector.TryGetValue(genre, out var current);
                vector[genre] = current + weight;
            }
        }
    }

    /// <summary>
    ///     Merges the profile's GenreDistribution counts as base weights for genres not already present in .
    /// </summary>
    /// <param name="vector">The genre weight accumulator to supplement.</param>
    /// <param name="profile">The user's watch profile.</param>
    private static void MergeGenreDistributionWeights(Dictionary<string, double> vector, UserWatchProfile profile)
    {
        if (profile.GenreDistribution.Count > 0)
        {
            var maxCount = profile.GenreDistribution.Values.Max();
            if (maxCount > 0)
            {
                foreach (var (genre, count) in profile.GenreDistribution)
                {
                    if (string.IsNullOrWhiteSpace(genre) || count <= 0 || vector.ContainsKey(genre))
                    {
                        continue;
                    }

                    vector[genre] = (double)count / maxCount;
                }
            }
        }
    }

    /// <summary>
    ///     Computes the per-item genre weight (temporal decay + play-count boost, scaled by the series progression multiplier, plus an additive favorite boost).
    /// </summary>
    /// <param name="item">The watched item row.</param>
    /// <param name="now">The reference timestamp for temporal decay.</param>
    /// <param name="seriesEpisodeCounts">Optional series episode-count map.</param>
    /// <param name="watchedEpisodesPerSeries">Precomputed per-series watched-episode counter.</param>
    /// <returns>The genre weight contributed by this item.</returns>
    private static double ComputeGenreItemWeight(
        WatchedItemInfo item,
        DateTime now,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        Dictionary<Guid, int>? watchedEpisodesPerSeries)
    {
        // Temporal weight: exponential decay with ~180-day half-life. Unplayed favorites represent current intent, so they are not age-penalized.
        double temporalWeight;
        if (item.LastPlayedDate.HasValue)
        {
            var ageDays = Math.Max(0, (now - item.LastPlayedDate.Value).TotalDays);
            temporalWeight = Math.Exp(-GenreDecayConstant * ageDays);
        }
        else if (item.IsFavorite)
        {
            temporalWeight = 1.0;
        }
        else
        {
            temporalWeight = Math.Exp(-GenreDecayConstant * 365.0);
        }

        // PlayCount boost: re-watched items signal stronger preference.
        var clampedPlayCount = Math.Clamp(item.PlayCount, 0, PlayCountMaxForLog1p);
        var playCountBoost = Math.Log(1.0 + clampedPlayCount) * PlayCountLog1pScale;

        // Progression multiplier: for episode rows, dampen or amplify the (temporal+playCount) signal by how much of the series the user consumed.
        var progressionMultiplier = ComputeProgressionMultiplier(
            item,
            seriesEpisodeCounts,
            watchedEpisodesPerSeries);
        var weight = (temporalWeight + playCountBoost) * progressionMultiplier;

        // Favorite boost - additive, never touched by the progression multiplier so an
        // explicit favorite click always outweighs a mediocre re-watch pattern.
        if (item.IsFavorite)
        {
            weight += EngineConstants.FavoriteGenreBoostFactor;
        }

        return weight;
    }

    /// <summary>
    ///     Expands genre preferences with co-occurrence proximity weights.
    /// </summary>
    /// <param name="vector">The genre preference vector to expand (modified in-place).</param>
    /// <param name="profile">The user watch profile for co-occurrence data.</param>
    private static void ExpandGenreProximity(Dictionary<string, double> vector, UserWatchProfile profile)
    {
        if (profile.WatchedItems.Count < 10 || vector.Count < 2)
        {
            return;
        }

        var cooccurrence = BuildGenreCooccurrence(profile);

        // proximityFactor caps every derived boost at 15% of the source genre's own weight, so a co-occurrence link can never lift a neighbour above the source itself.
        const double proximityFactor = 0.15;
        const int minCooccurrences = 2;

        // Snapshot pre-expansion weights so proximity boosts are computed from the direct-watch signal only. Reading a mutating vector while iterating would let earlier boosts feed later ones, cascading a mild pair into a dominant signal.
        var baseWeights = new Dictionary<string, double>(vector, StringComparer.OrdinalIgnoreCase);

        // Aggregate the strongest incoming proximity contribution per target genre.
        var proximityContributions = AggregateProximityContributions(
            baseWeights,
            cooccurrence,
            proximityFactor,
            minCooccurrences);

        // Apply contributions. * Genres already in the vector (direct-watch): ADD the derived contribution.
        foreach (var (targetGenre, derivedWeight) in proximityContributions)
        {
            if (vector.TryGetValue(targetGenre, out var existingWeight))
            {
                vector[targetGenre] = existingWeight + derivedWeight;
            }
            else
            {
                vector[targetGenre] = derivedWeight;
            }
        }
    }

    /// <summary>
    ///     Builds the symmetric genre co-occurrence counts from eligible watched items. Extracted
    ///     verbatim from <see cref="ExpandGenreProximity"/>.
    /// </summary>
    /// <param name="profile">The user watch profile.</param>
    /// <returns>A map genre -> (neighbour genre -> co-occurrence count).</returns>
    private static Dictionary<string, Dictionary<string, int>> BuildGenreCooccurrence(UserWatchProfile profile)
    {
        var cooccurrence = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in profile.WatchedItems)
        {
            var distinctGenres = GetEligibleDistinctGenres(item);
            if (distinctGenres.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < distinctGenres.Length; i++)
            {
                for (var j = i + 1; j < distinctGenres.Length; j++)
                {
                    RecordCooccurrencePair(cooccurrence, distinctGenres[i], distinctGenres[j]);
                }
            }
        }

        return cooccurrence;
    }

    /// <summary>
    ///     Returns the de-duplicated, non-blank genres of a watched item that qualifies for co-occurrence (played or favorited with genre metadata), or an empty array otherwise.
    /// </summary>
    private static string[] GetEligibleDistinctGenres(WatchedItemInfo item)
    {
        if (item is { Played: false, IsFavorite: false })
        {
            return [];
        }

        if (item.Genres is not { Count: > 0 })
        {
            return [];
        }

        // De-duplicate genres to prevent malformed metadata like ["Action", "Action", "Comedy"]
        // from inflating co-occurrence counts (Action↔Comedy would be counted twice otherwise).
        return item.Genres
            .Where(static g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    ///     Records a symmetric +1 co-occurrence between two genres in the map. Extracted verbatim
    ///     from <see cref="BuildGenreCooccurrence"/>.
    /// </summary>
    private static void RecordCooccurrencePair(
        Dictionary<string, Dictionary<string, int>> cooccurrence,
        string g1,
        string g2)
    {
        if (!cooccurrence.TryGetValue(g1, out var neighbors))
        {
            neighbors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            cooccurrence[g1] = neighbors;
        }

        neighbors.TryGetValue(g2, out var cnt);
        neighbors[g2] = cnt + 1;

        if (!cooccurrence.TryGetValue(g2, out var neighbors2))
        {
            neighbors2 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            cooccurrence[g2] = neighbors2;
        }

        neighbors2.TryGetValue(g1, out var cnt2);
        neighbors2[g1] = cnt2 + 1;
    }

    /// <summary>
    ///     Aggregates the strongest incoming proximity contribution per target genre. Extracted verbatim from ExpandGenreProximity; the derivation formula is unchanged.
    /// </summary>
    /// <param name="baseWeights">Pre-expansion direct-watch genre weights.</param>
    /// <param name="cooccurrence">The genre co-occurrence map.</param>
    /// <param name="proximityFactor">Cap on the derived boost relative to the source weight.</param>
    /// <param name="minCooccurrences">Minimum co-occurrence count for a pair to contribute.</param>
    /// <returns>A map target genre -> strongest derived proximity contribution.</returns>
    private static Dictionary<string, double> AggregateProximityContributions(
        Dictionary<string, double> baseWeights,
        Dictionary<string, Dictionary<string, int>> cooccurrence,
        double proximityFactor,
        int minCooccurrences)
    {
        var proximityContributions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var (knownGenre, weight) in baseWeights)
        {
            if (!cooccurrence.TryGetValue(knownGenre, out var neighbors))
            {
                continue;
            }

            foreach (var (neighborGenre, count) in neighbors)
            {
                if (count < minCooccurrences)
                {
                    continue;
                }

                var derived = weight * proximityFactor * Math.Min(count / 5.0, 1.0);
                proximityContributions.TryGetValue(neighborGenre, out var existing);
                if (derived > existing)
                {
                    proximityContributions[neighborGenre] = derived;
                }
            }
        }

        return proximityContributions;
    }

    /// <summary>
    ///     Builds a set of studio names the user prefers, derived from their watched and favorited items.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="candidateLookup">Pre-built candidate lookup by item ID (shared across calls for performance).</param>
    /// <returns>A HashSet of preferred studio names (case-insensitive).</returns>
    internal static HashSet<string> BuildStudioPreferenceSet(
        UserWatchProfile userProfile,
        Dictionary<Guid, BaseItem> candidateLookup)
    {
        var studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect studios from watched and favorited movies and series
        foreach (var w in userProfile.WatchedItems)
        {
            // Same eligibility as BuildGenrePreferenceVector so a PlayCount>0 row that contributes its genres also contributes its studios.
            if (!IsEligibleForPreferenceWeighting(w))
            {
                continue;
            }

            // Try direct item match (movies)
            if (candidateLookup.TryGetValue(w.ItemId, out var item) && item.Studios is { Length: > 0 })
            {
                foreach (var s in item.Studios.Where(static s => !string.IsNullOrWhiteSpace(s)))
                {
                    studios.Add(s);
                }
            }

            // Also try series match (episodes -> parent series)
            if (!w.SeriesId.HasValue || !candidateLookup.TryGetValue(w.SeriesId.Value, out var seriesItem)
                                     || seriesItem.Studios is not { Length: > 0 })
            {
                continue;
            }

            foreach (var s in seriesItem.Studios.Where(static s => !string.IsNullOrWhiteSpace(s)))
            {
                studios.Add(s);
            }
        }

        return studios;
    }

    /// <summary>
    ///     Builds a set of tags the user prefers, derived from their watched and favorited items.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="candidateLookup">Pre-built candidate lookup by item ID (shared across calls for performance).</param>
    /// <returns>A HashSet of preferred tag names (case-insensitive).</returns>
    internal static HashSet<string> BuildTagPreferenceSet(
        UserWatchProfile userProfile,
        Dictionary<Guid, BaseItem> candidateLookup)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            // Aligned with BuildGenrePreferenceVector - a PlayCount>0 row that contributes
            // its genres should also contribute its tags for consistent similarity signals.
            if (!IsEligibleForPreferenceWeighting(w))
            {
                continue;
            }

            // Direct item match (movies)
            if (candidateLookup.TryGetValue(w.ItemId, out var item) && item.Tags is { Length: > 0 })
            {
                foreach (var t in item.Tags.Where(static t => !string.IsNullOrWhiteSpace(t)))
                {
                    tags.Add(t);
                }
            }

            // Series match (episodes -> parent series)
            if (!w.SeriesId.HasValue || !candidateLookup.TryGetValue(w.SeriesId.Value, out var seriesItem)
                                     || seriesItem.Tags is not { Length: > 0 })
            {
                continue;
            }

            foreach (var t in seriesItem.Tags.Where(static t => !string.IsNullOrWhiteSpace(t)))
            {
                tags.Add(t);
            }
        }

        return tags;
    }

    /// <summary>
    ///     Builds a set of preferred person names (actors/directors) from the user's watched and favorited items.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="peopleLookup">Pre-built candidate people lookup (item ID -> person names).</param>
    /// <returns>A HashSet of preferred person names (case-insensitive).</returns>
    internal static HashSet<string> BuildPeoplePreferenceSet(
        UserWatchProfile userProfile,
        Dictionary<Guid, HashSet<string>> peopleLookup)
    {
        var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            // Aligned with BuildGenrePreferenceVector so the unweighted set (reason-display) and the weighted set (ML scoring) cover the same source rows - otherwise the Reason ("because you like <actor>") could reference an actor with no ML weight, or vice-versa.
            if (!IsEligibleForPreferenceWeighting(w))
            {
                continue;
            }

            // Direct item match (movies, episodes)
            if (peopleLookup.TryGetValue(w.ItemId, out var itemPeople))
            {
                people.UnionWith(itemPeople.Where(static p => !string.IsNullOrWhiteSpace(p)));
            }

            // Series match (episodes -> parent series)
            if (w.SeriesId.HasValue && peopleLookup.TryGetValue(w.SeriesId.Value, out var seriesPeople))
            {
                people.UnionWith(seriesPeople.Where(static p => !string.IsNullOrWhiteSpace(p)));
            }
        }

        return people;
    }

    /// <summary>
    ///     Builds a weighted preference map of person names (actors/directors) from the user's watched/favorited items.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="peopleLookup">Pre-built candidate people lookup (item ID -> person names).</param>
    /// <param name="seriesEpisodeCounts">
    ///     Optional map <c>seriesId -> totalEpisodeCount</c>. When provided, episode rows
    ///     contribute <c>ProgressionMultiplier(playedInSeries / totalEpisodes)</c> instead of a
    ///     flat 1.0, so people from completed series get higher weight than from abandoned ones.
    ///     Kept in perfect symmetry with the same parameter on
    ///     <see cref="BuildGenrePreferenceVector"/> so the People- and Genre-similarity features see
    ///     the same series-engagement signal.
    /// </param>
    /// <returns>
    ///     A case-insensitive dictionary mapping person names to their weighted occurrence
    ///     across the user's watched/favorited items. Empty when the user has no eligible history.
    /// </returns>
    internal static Dictionary<string, double> BuildPeoplePreferenceWeights(
        UserWatchProfile userProfile,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts = null)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Shared helper - same aggregation as BuildGenrePreferenceVector so both feature
        // pipelines receive identical progression ratios by construction, not by convention.
        var watchedEpisodesPerSeries = BuildWatchedEpisodesPerSeries(userProfile, seriesEpisodeCounts);

        foreach (var w in userProfile.WatchedItems)
        {
            // Same eligibility as BuildGenrePreferenceVector so both feature pipelines see
            // exactly the same "which rows contribute" answer for a given profile.
            if (!IsEligibleForPreferenceWeighting(w))
            {
                continue;
            }

            // Keep in lock-step with BuildGenrePreferenceVector.
            if (IsPhantomSeriesRow(w, seriesEpisodeCounts))
            {
                continue;
            }

            // Merge people from the item itself AND its parent series (episodes -> series). De-duplicate per watched row so the same person on the same item is not double-counted just because both item-level and series-level lookups return them.
            var perRowPeople = BuildPerRowPeople(w, peopleLookup);

            if (perRowPeople is null || perRowPeople.Count == 0)
            {
                continue;
            }

            // Progression multiplier: episodes contribute proportionally to how much of the series the user has actually watched.
            var progressionMultiplier = ComputeProgressionMultiplier(
                w,
                seriesEpisodeCounts,
                watchedEpisodesPerSeries);

            foreach (var name in perRowPeople)
            {
                weights.TryGetValue(name, out var current);
                weights[name] = current + progressionMultiplier;
            }
        }

        return weights;
    }

    /// <summary>
    ///     Merges the de-duplicated person names for a single watched row from both the item-level and parent-series people lookups.
    /// </summary>
    /// <param name="w">The watched item row.</param>
    /// <param name="peopleLookup">Item id -> person name set lookup.</param>
    /// <returns>The de-duplicated person names for the row, or <c>null</c> when none apply.</returns>
    private static HashSet<string>? BuildPerRowPeople(
        WatchedItemInfo w,
        Dictionary<Guid, HashSet<string>> peopleLookup)
    {
        HashSet<string>? perRowPeople = null;

        if (peopleLookup.TryGetValue(w.ItemId, out var itemPeople) && itemPeople.Count > 0)
        {
            perRowPeople = new HashSet<string>(
                itemPeople.Where(static p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);
        }

        if (w.SeriesId.HasValue
            && peopleLookup.TryGetValue(w.SeriesId.Value, out var seriesPeople)
            && seriesPeople.Count > 0)
        {
            perRowPeople ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in seriesPeople.Where(static p => !string.IsNullOrWhiteSpace(p)))
            {
                perRowPeople.Add(name);
            }
        }

        return perRowPeople;
    }

    /// <summary>
    ///     Builds a max-normalized franchise preference map (TMDb collection name -> weight in [0,1]) from the user's watched/favorited movies.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A case-insensitive franchise -> normalized-weight map (empty when no signal).</returns>
    internal static Dictionary<string, double> BuildFranchisePreferenceVector(UserWatchProfile profile)
    {
        var vector = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (profile.WatchedItems.Count == 0)
        {
            return vector;
        }

        foreach (var item in profile.WatchedItems)
        {
            if (!IsEligibleForPreferenceWeighting(item)
                || string.IsNullOrWhiteSpace(item.TmdbCollectionName))
            {
                continue;
            }

            var weight = ComputePreferenceRowWeight(item);
            vector.TryGetValue(item.TmdbCollectionName, out var current);
            vector[item.TmdbCollectionName] = current + weight;
        }

        NormalizeByMax(vector);
        return vector;
    }

    /// <summary>
    ///     Builds a max-normalized production-country preference map (country -> weight in [0,1]) from the user's watched/favorited items, using the same weighting as BuildFranchisePreferenceVector.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A case-insensitive country -> normalized-weight map (empty when no signal).</returns>
    internal static Dictionary<string, double> BuildProductionCountryPreferenceVector(UserWatchProfile profile)
    {
        var vector = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (profile.WatchedItems.Count == 0)
        {
            return vector;
        }

        foreach (var item in profile.WatchedItems)
        {
            if (!IsEligibleForPreferenceWeighting(item)
                || item.ProductionCountries is not { Count: > 0 })
            {
                continue;
            }

            var weight = ComputePreferenceRowWeight(item);
            foreach (var country in item.ProductionCountries.Where(static c => !string.IsNullOrWhiteSpace(c)))
            {
                vector.TryGetValue(country, out var current);
                vector[country] = current + weight;
            }
        }

        NormalizeByMax(vector);
        return vector;
    }

    /// <summary>
    ///     Builds a set of preferred inherited tags from the user's watched/favorited items, reading InheritedTags directly.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A case-insensitive set of preferred inherited tags (empty when no signal).</returns>
    internal static HashSet<string> BuildInheritedTagPreferenceSet(UserWatchProfile profile)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in profile.WatchedItems)
        {
            if (!IsEligibleForPreferenceWeighting(item)
                || item.InheritedTags is not { Count: > 0 })
            {
                continue;
            }

            foreach (var t in item.InheritedTags.Where(static t => !string.IsNullOrWhiteSpace(t)))
            {
                tags.Add(t);
            }
        }

        return tags;
    }

    /// <summary>
    ///     Builds a weighted writer preference map (writer name -> weight) from the user's watched/favorited items, reading WriterNames directly.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A case-insensitive writer -> weight map (empty when no signal).</returns>
    internal static Dictionary<string, double> BuildWriterPreferenceWeights(UserWatchProfile profile)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in profile.WatchedItems)
        {
            if (!IsEligibleForPreferenceWeighting(item)
                || item.WriterNames is not { Count: > 0 })
            {
                continue;
            }

            var weight = ComputePreferenceRowWeight(item);
            var seenThisRow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in item.WriterNames.Where(static n => !string.IsNullOrWhiteSpace(n)))
            {
                if (!seenThisRow.Add(name))
                {
                    continue;
                }

                weights.TryGetValue(name, out var current);
                weights[name] = current + weight;
            }
        }

        return weights;
    }

    /// <summary>
    ///     Computes the per-row preference weight shared by the franchise/country/writer builders, using the SAME temporal-decay + play-count-log1p + favorite-additive composition as BuildGenrePreferenceVector (without series-progression, which needs a series.
    /// </summary>
    /// <param name="item">The watched item row.</param>
    /// <returns>A non-negative preference weight.</returns>
    private static double ComputePreferenceRowWeight(WatchedItemInfo item)
    {
        double temporalWeight;
        if (item.LastPlayedDate.HasValue)
        {
            var ageDays = Math.Max(0.0, (DateTime.UtcNow - item.LastPlayedDate.Value).TotalDays);
            temporalWeight = Math.Exp(-GenreDecayConstant * ageDays);
        }
        else
        {
            temporalWeight = item.IsFavorite ? 1.0 : Math.Exp(-GenreDecayConstant * 365.0);
        }

        var clampedPlayCount = Math.Clamp(item.PlayCount, 0, PlayCountMaxForLog1p);
        var playCountBoost = Math.Log(1.0 + clampedPlayCount) * PlayCountLog1pScale;

        var weight = temporalWeight + playCountBoost;
        if (item.IsFavorite)
        {
            weight += EngineConstants.FavoriteGenreBoostFactor;
        }

        return weight;
    }

    /// <summary>
    ///     Max-normalizes a weight map in place so its largest value becomes 1.0, matching the normalization tail of BuildGenrePreferenceVector.
    /// </summary>
    /// <param name="vector">The weight map to normalize in place.</param>
    private static void NormalizeByMax(Dictionary<string, double> vector)
    {
        if (vector.Count == 0)
        {
            return;
        }

        var maxWeight = vector.Values.Max();
        if (maxWeight <= 0.0)
        {
            return;
        }

        foreach (var key in vector.Keys.ToList())
        {
            vector[key] /= maxWeight;
        }
    }

    /// <summary>
    ///     Builds the genre exposure analysis for a user. This is computed once per user and reused for all candidate items to avoid redundant computation.
    /// </summary>
    /// <param name="genrePreferences">
    ///     The user's normalized genre preference vector from
    ///     <see cref="BuildGenrePreferenceVector" />.
    /// </param>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A reusable genre exposure analysis.</returns>
    internal static GenreExposureAnalysis BuildGenreExposureAnalysis(
        Dictionary<string, double> genrePreferences,
        UserWatchProfile profile)
    {
        // Insufficient history -> all features default to 0 (neutral)
        if (profile.WatchedItems.Count < EngineConstants.MinWatchCountForGenreExposure
            || genrePreferences.Count == 0)
        {
            return new GenreExposureAnalysis
            {
                UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AveragePreferenceWeight = 0,
                GenrePreferences = genrePreferences,
                IsValid = false
            };
        }

        // Compute total genre weight for share calculation
        var totalWeight = genrePreferences.Values.Sum();

        // Identify underexposed genres: those with < threshold share of total weight
        var underexposed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (totalWeight > 0)
        {
            foreach (var (genre, weight) in genrePreferences)
            {
                if (weight / totalWeight < EngineConstants.GenreUnderexposureThreshold)
                {
                    underexposed.Add(genre);
                }
            }
        }

        // Identify top-N dominant genres by preference weight
        var dominant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sortedGenres = genrePreferences
            .OrderByDescending(kvp => kvp.Value)
            .Take(EngineConstants.GenreDominanceTopN);
        foreach (var kvp in sortedGenres)
        {
            dominant.Add(kvp.Key);
        }

        // Average preference weight across all genres
        var avgWeight = totalWeight / genrePreferences.Count;

        return new GenreExposureAnalysis
        {
            UnderexposedGenres = underexposed,
            DominantGenres = dominant,
            AveragePreferenceWeight = avgWeight,
            GenrePreferences = genrePreferences,
            IsValid = true
        };
    }

    /// <summary>
    ///     Computes the three genre exposure features for a single candidate item. Uses a pre-built GenreExposureAnalysis to avoid redundant computation.
    /// </summary>
    /// <param name="candidateGenres">The genres of the candidate item.</param>
    /// <param name="analysis">The pre-built genre exposure analysis for the user.</param>
    /// <returns>A tuple of (underexposure, dominanceRatio, affinityGap) all in [0, 1].</returns>
    internal static (double Underexposure, double DominanceRatio, double AffinityGap) ComputeGenreExposureFeatures(
        IReadOnlyList<string> candidateGenres,
        GenreExposureAnalysis analysis)
    {
        // Insufficient data or no candidate genres -> all neutral
        if (!analysis.IsValid || candidateGenres.Count == 0)
        {
            return (0.0, 0.0, 0.0);
        }

        var underexposedCount = 0;
        var dominantCount = 0;
        var candidateWeightSum = 0.0;

        // De-duplicate candidate genres case-insensitively BEFORE counting, mirroring the sibling SimilarityComputer.ComputeGenreSimilarity.
        var distinctGenres = new HashSet<string>(
            candidateGenres.Where(static g => !string.IsNullOrWhiteSpace(g)),
            StringComparer.OrdinalIgnoreCase);
        var validCount = distinctGenres.Count;

        foreach (var genre in distinctGenres)
        {
            if (analysis.UnderexposedGenres.Contains(genre))
            {
                underexposedCount++;
            }

            if (analysis.DominantGenres.Contains(genre))
            {
                dominantCount++;
            }

            // Look up the user's preference weight for this genre (0 if never watched)
            analysis.GenrePreferences.TryGetValue(genre, out var weight);
            candidateWeightSum += weight;
        }

        if (validCount == 0)
        {
            return (0.0, 0.0, 0.0);
        }

        // GenreUnderexposure: fraction of candidate genres that are underexposed
        var underexposure = (double)underexposedCount / validCount;

        // GenreDominanceRatio: fraction of candidate genres in user's top-N
        var dominanceRatio = (double)dominantCount / validCount;

        // GenreAffinityGap: how far below the user's average the candidate's genres are
        // Candidate average weight vs. user's overall average weight
        var candidateAvgWeight = candidateWeightSum / validCount;
        var affinityGap = 0.0;
        if (analysis.AveragePreferenceWeight > 0 && candidateAvgWeight < analysis.AveragePreferenceWeight)
        {
            // Normalize: 0 = at average, 1 = zero weight (complete gap)
            affinityGap = 1.0 - (candidateAvgWeight / analysis.AveragePreferenceWeight);
        }

        return (
            Math.Clamp(underexposure, 0.0, 1.0),
            Math.Clamp(dominanceRatio, 0.0, 1.0),
            Math.Clamp(affinityGap, 0.0, 1.0));
    }

    /// <summary>
    ///     Returns true when the row should count as a "completed episode" for the series-progression multiplier.
    /// </summary>
    /// <param name="row">The watched-item row to classify.</param>
    /// <returns>True when the row represents an actually-completed episode.</returns>
    private static bool IsEpisodeCompletedForProgression(WatchedItemInfo row)
    {
        return row.Played || row.PlayCount > 0;
    }

    /// <summary>
    ///     Eligibility predicate for the genre / people preference-weighting loops.
    /// </summary>
    /// <param name="row">The watched-item row to classify.</param>
    /// <returns>True when the row is eligible for genre / people preference weighting.</returns>
    private static bool IsEligibleForPreferenceWeighting(WatchedItemInfo row)
    {
        return row.IsFavorite || IsEpisodeCompletedForProgression(row);
    }

    /// <summary>
    ///     F-04 phantom-row guard. Returns true when the row belongs to a series deleted from the library: the caller passes seriesEpisodeCounts from Engine.LoadCandidateItems, so any SeriesId absent from that map is stale.
    /// </summary>
    private static bool IsPhantomSeriesRow(
        WatchedItemInfo row,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts)
    {
        if (seriesEpisodeCounts is null || row.SeriesId is not { } sid)
        {
            return false;
        }

        return !seriesEpisodeCounts.ContainsKey(sid);
    }

    /// <summary>
    ///     Pre-aggregates the number of completed episodes per series for the user, using the strict IsEpisodeCompletedForProgression predicate (Played or PlayCount &gt; 0).
    /// </summary>
    /// <param name="profile">The user watch profile whose rows we aggregate.</param>
    /// <param name="seriesEpisodeCounts">Optional caller-supplied totals; <c>null</c> disables aggregation.</param>
    /// <returns>
    ///     A dictionary <c>seriesId -> completedEpisodes</c>, or <c>null</c> when
    ///     <paramref name="seriesEpisodeCounts"/> was <c>null</c> or empty.
    /// </returns>
    private static Dictionary<Guid, int>? BuildWatchedEpisodesPerSeries(
        UserWatchProfile profile,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts)
    {
        if (seriesEpisodeCounts is not { Count: > 0 })
        {
            return null;
        }

        var watched = new Dictionary<Guid, int>();
        foreach (var row in profile.WatchedItems)
        {
            if (row.SeriesId is not { } sid || !IsEpisodeCompletedForProgression(row))
            {
                continue;
            }

            // Skip rows for series no longer in the library (phantom data). The skip must happen BEFORE bumping the counter; ComputeProgressionMultiplier's Math.Min(1.0, rawRatio) already clamps overshoot for still-existing series, so no per-row cap is needed here.
            if (!seriesEpisodeCounts.ContainsKey(sid))
            {
                continue;
            }

            watched.TryGetValue(sid, out var c);
            watched[sid] = c + 1;
        }

        return watched;
    }

    /// <summary>
    ///     Derives the per-row progression multiplier for a watched item. Non-episode rows and rows without accompanying series-episode metadata get a neutral multiplier of 1.0 (identical to the pre-progression weight).
    /// </summary>
    /// <param name="item">The watched item row currently being weighted.</param>
    /// <param name="seriesEpisodeCounts">Optional per-series total-episode map from the caller.</param>
    /// <param name="watchedEpisodesPerSeries">Pre-aggregated per-series watched counter.</param>
    /// <returns>A multiplier in <c>[ProgressionFloor, ProgressionCeiling]</c> or <c>1.0</c> when no data.</returns>
    private static double ComputeProgressionMultiplier(
        WatchedItemInfo item,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        Dictionary<Guid, int>? watchedEpisodesPerSeries)
    {
        // Explicit-favorite rows that are NOT themselves completed episodes bypass progression scaling: * BuildGenrePreferenceVector adds the FAVORITE additive (+3.0) AFTER the multiplier.
        if (item.IsFavorite && !IsEpisodeCompletedForProgression(item))
        {
            return 1.0;
        }

        // No series context or caller opted out (null map) -> neutral, preserves pre-existing weight.
        if (item.SeriesId is not { } sid
            || seriesEpisodeCounts is null
            || watchedEpisodesPerSeries is null)
        {
            return 1.0;
        }

        if (!seriesEpisodeCounts.TryGetValue(sid, out var totalEps) || totalEps <= 0)
        {
            return 1.0;
        }

        if (!watchedEpisodesPerSeries.TryGetValue(sid, out var playedEps) || playedEps <= 0)
        {
            return 1.0;
        }

        // Ratio guarded against pathological metadata where playedEps > totalEps.
        var rawRatio = Math.Min(1.0, (double)playedEps / totalEps);

        // Map ratio in [0,1] to multiplier in [ProgressionFloor, ProgressionCeiling].
        // rawRatio=0 -> ProgressionFloor (0.3), rawRatio=0.5 -> 0.9, rawRatio=1 -> 1.5.
        return Math.Clamp(
            ProgressionFloor + (rawRatio * ProgressionSpan),
            ProgressionFloor,
            ProgressionCeiling);
    }

    /// <summary>
    ///     Pre-computed genre exposure analysis for a user, reusable across all candidate items.
    /// </summary>
    internal sealed class GenreExposureAnalysis
    {
        /// <summary>Gets the set of underexposed genres (below threshold watch share).</summary>
        internal required HashSet<string> UnderexposedGenres { get; init; }

        /// <summary>Gets the user's top-N dominant genres by watch count.</summary>
        internal required HashSet<string> DominantGenres { get; init; }

        /// <summary>Gets the average preference weight across all genres.</summary>
        internal required double AveragePreferenceWeight { get; init; }

        /// <summary>Gets the full genre preference vector for per-genre weight lookups.</summary>
        internal required Dictionary<string, double> GenrePreferences { get; init; }

        /// <summary>Gets a value indicating whether the analysis is valid (user has enough history).</summary>
        internal required bool IsValid { get; init; }
    }
}