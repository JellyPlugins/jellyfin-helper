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
    ///     Half-life for genre preference temporal decay in days (~180 days).
    ///     Genres watched recently contribute more than genres watched months ago.
    /// </summary>
    private const double GenreDecayHalfLifeDays = 180.0;

    /// <summary>
    ///     Upper cap for the raw <c>PlayCount</c> value fed into the log1p transform.
    ///     Guards against pathological metadata (e.g. stuck counters) that would otherwise
    ///     let a single genre balloon its raw weight before normalization.
    ///     <para>
    ///         Modeled as an <see cref="int"/> because <c>WatchedItemInfo.PlayCount</c> is also
    ///         an <see cref="int"/>. Keeping the type identity explicit prevents a double→int→double
    ///         round-trip when clamping and makes the "PlayCount is a counter" invariant obvious.
    ///     </para>
    /// </summary>
    private const int PlayCountMaxForLog1p = 100;

    /// <summary>
    ///     Lower bound of the progression multiplier: even an abandoned series must still
    ///     contribute a fraction of its signal so users with mostly-abandoned watch history
    ///     don't end up with an empty preference vector.
    /// </summary>
    private const double ProgressionFloor = 0.3;

    /// <summary>
    ///     Upper bound of the progression multiplier: a fully-completed series gets a modest
    ///     boost above the baseline "1.0" for movies, so binge-watched shows shape preferences
    ///     more strongly than a single played row. Kept below <c>2.0</c> so the multiplier
    ///     cannot overpower the additive favorite boost of <c>3.0</c>.
    /// </summary>
    private const double ProgressionCeiling = 1.5;

    /// <summary>
    ///     Linear span from floor to ceiling, i.e. how much the raw ratio moves the multiplier.
    /// </summary>
    private const double ProgressionSpan = ProgressionCeiling - ProgressionFloor;

    /// <summary>
    ///     Target maximum contribution of the PlayCount log1p boost, chosen so that heavy
    ///     re-watchers produce a meaningful signal that is <b>not</b> drowned out by the
    ///     favorite additive (<see cref="EngineConstants.FavoriteGenreBoostFactor"/> = 3.0)
    ///     while still remaining sub-favorite so that an explicit favorite click always
    ///     outweighs a pure re-watching pattern.
    ///     <para>
    ///         Rationale for 2.0 (v3 C1 hardening pass): the original v3 C1 scale (1.0) was
    ///         calibrated to match the pre-v3 linear cap (<c>min(PlayCount, 5) × 0.2 = 1.0</c>),
    ///         which - combined with the +3.0 favorite additive - meant that a single ⭐ click
    ///         outweighed 100 re-watches by a factor of 3×. Detailed component analysis of the
    ///         weight formula
    ///         <c>weight = temporalWeight + playCountBoost + (fav ? 3.0 : 0)</c>
    ///         showed that PlayCount 5 vs. PlayCount 30 differed by only 4-13% of the total
    ///         weight for favorited items, effectively making the log1p refinement invisible
    ///         to the ML feature.  Raising the ceiling to 2.0 gives PlayCount 30 a ~1.5 boost
    ///         (≈50% of the favorite additive) so re-watching signals become measurable
    ///         without inverting the favorite/re-watch ordering.
    ///     </para>
    /// </summary>
    private const double PlayCountLog1pCeiling = 2.0;

    /// <summary>Decay constant derived from half-life: ln(2) / halfLife.</summary>
    private static readonly double GenreDecayConstant = Math.Log(2.0) / GenreDecayHalfLifeDays;

    /// <summary>
    ///     Scale factor for the log1p PlayCount contribution. Calibrated so a fully-capped
    ///     PlayCount (100) contributes exactly <see cref="PlayCountLog1pCeiling"/> (2.0),
    ///     placing heavy re-watchers roughly halfway to the favorite additive so re-watching
    ///     patterns are visible to the downstream ML feature without dominating explicit
    ///     favorite signals. Approximate contributions with this scale:
    ///     <list type="bullet">
    ///         <item><description>PlayCount 1 → 0.30 (baseline single-watch weight)</description></item>
    ///         <item><description>PlayCount 5 → 0.78 (comparable to a fresh 1-day-old temporalWeight)</description></item>
    ///         <item><description>PlayCount 30 → 1.49 (dedicated re-watcher signal, ≈50% of favorite additive)</description></item>
    ///         <item><description>PlayCount 100 → 2.00 (theoretical ceiling; clamp beyond)</description></item>
    ///     </list>
    /// </summary>
    private static readonly double PlayCountLog1pScale = PlayCountLog1pCeiling / Math.Log(1.0 + PlayCountMaxForLog1p);

    /// <summary>
    ///     Builds a normalized genre preference vector from the user's watch history.
    ///     Each genre gets a weight based on recency, play count, and favorites.
    ///     Recent watches count more than old ones (180-day half-life exponential decay).
    ///     Re-watched items get a PlayCount boost. Favorites get an additional boost.
    ///     Items that are favorited but not yet played are also included - the user
    ///     explicitly expressed interest, so their genres should influence preferences.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <param name="seriesEpisodeCounts">
    ///     Optional map <c>seriesId → totalEpisodeCount</c> supplied by the caller (typically
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

        // Pre-aggregate how many episodes of each series the user has meaningfully engaged with.
        // Used together with seriesEpisodeCounts to derive a per-row progression multiplier below.
        // Built lazily and only when both callers-side data is available; single-user scenarios
        // without a seriesEpisodeCounts map fall through to the pre-existing weight formula.
        Dictionary<Guid, int>? watchedEpisodesPerSeries = null;
        if (seriesEpisodeCounts is { Count: > 0 })
        {
            watchedEpisodesPerSeries = new Dictionary<Guid, int>();
            foreach (var row in profile.WatchedItems)
            {
                if (row.SeriesId is not { } sid || !row.HasPlaybackActivity())
                {
                    continue;
                }

                watchedEpisodesPerSeries.TryGetValue(sid, out var c);
                watchedEpisodesPerSeries[sid] = c + 1;
            }
        }

        // Build genre preferences with temporal decay - recent watches count more
        var now = DateTime.UtcNow;
        foreach (var item in profile.WatchedItems)
        {
            // Include items that are played OR favorited - favorites signal explicit interest
            if (item is { Played: false, IsFavorite: false })
            {
                continue;
            }

            if (item.Genres is not { Count: > 0 })
            {
                continue;
            }

            // Compute temporal weight: exponential decay with ~180-day half-life.
            // Unplayed favorites (IsFavorite && !Played) represent current intent - the user
            // explicitly flagged interest without having watched yet, so they should not be
            // age-penalized. Played items without a timestamp are rare edge cases and default
            // to ~1 year as a conservative fallback.
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
            // Roadmap v3 (C1) with hardening pass: switched from linear
            // (min(PlayCount,5) × 0.2, capped at 1.0) to log1p so that a 30×-rewatched
            // item does not linearly dominate the genre vector, then raised the ceiling
            // to 2.0 so the signal survives the +3.0 favorite additive further below.
            // Approximate contributions (see PlayCountLog1pCeiling constant for rationale):
            //   PlayCount  1 → 0.30
            //   PlayCount  5 → 0.78
            //   PlayCount 30 → 1.49
            //   PlayCount 100 → 2.00 (theoretical ceiling; clamp beyond)
            // Clamp at 100 to prevent pathological metadata (e.g. stuck play counters) from
            // producing unbounded weights before final normalization.
            var clampedPlayCount = Math.Clamp(item.PlayCount, 0, PlayCountMaxForLog1p);
            var playCountBoost = Math.Log(1.0 + clampedPlayCount) * PlayCountLog1pScale;

            // Progression multiplier: for episode rows (item.SeriesId set), dampen or amplify
            // the implicit temporal+playCount signal by how much of the series the user has
            // actually consumed. The FAVORITE additive stays independent (an explicit ⭐ click
            // must not be diluted by an abandoned series) and is added after multiplication.
            //
            // Formula: clamp(0.3 + rawRatio * 1.2, 0.3, 1.5), see ComputeProgressionMultiplier
            // for the full rationale.
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

            foreach (var genre in item.Genres.Where(static g => !string.IsNullOrWhiteSpace(g)))
            {
                vector.TryGetValue(genre, out var current);
                vector[genre] = current + weight;
            }
        }

        // Merge GenreDistribution as base weights for genres not covered by WatchedItems.
        // This ensures backward compatibility and catches genres from items whose
        // WatchedItemInfo has no Genres array (e.g. episodes inheriting parent series genres).
        // Counts are scaled into the same 0–1 dynamic range as watch-derived weights
        // so they supplement rather than dominate after normalization.
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

        if (vector.Count == 0)
        {
            return vector;
        }

        // Expand first, normalize afterwards so proximity-derived weights participate in
        // the same max-normalization pass as the base entries. Doing it in the other order
        // would leave secondary genres in `[0, 0.15]` while primary genres are in `[0, 1]`,
        // producing a non-normalized vector that drifts SimilarityComputer's `userNorm`.
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
    ///     Expands genre preferences with co-occurrence proximity weights.
    ///     Genres that frequently appear together on items in watch history
    ///     get derived weights, enabling soft matching for related genres
    ///     the user has not directly watched (e.g. Fantasy for a SciFi fan).
    /// </summary>
    /// <param name="vector">The genre preference vector to expand (modified in-place).</param>
    /// <param name="profile">The user watch profile for co-occurrence data.</param>
    private static void ExpandGenreProximity(Dictionary<string, double> vector, UserWatchProfile profile)
    {
        if (profile.WatchedItems.Count < 10 || vector.Count < 2)
        {
            return;
        }

        var cooccurrence = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in profile.WatchedItems)
        {
            if (item is { Played: false, IsFavorite: false })
            {
                continue;
            }

            if (item.Genres is not { Count: > 0 })
            {
                continue;
            }

            // De-duplicate genres to prevent malformed metadata like ["Action", "Action", "Comedy"]
            // from inflating co-occurrence counts (Action↔Comedy would be counted twice otherwise).
            var distinctGenres = item.Genres
                .Where(static g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (distinctGenres.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < distinctGenres.Length; i++)
            {
                var g1 = distinctGenres[i];

                if (!cooccurrence.TryGetValue(g1, out var neighbors))
                {
                    neighbors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    cooccurrence[g1] = neighbors;
                }

                for (var j = i + 1; j < distinctGenres.Length; j++)
                {
                    var g2 = distinctGenres[j];

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
            }
        }

        const double proximityFactor = 0.15;
        const int minCooccurrences = 2;
        var expansions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var (knownGenre, weight) in vector)
        {
            if (!cooccurrence.TryGetValue(knownGenre, out var neighbors))
            {
                continue;
            }

            foreach (var (neighborGenre, count) in neighbors)
            {
                if (count < minCooccurrences || vector.ContainsKey(neighborGenre))
                {
                    continue;
                }

                var derived = weight * proximityFactor * Math.Min(count / 5.0, 1.0);
                expansions.TryGetValue(neighborGenre, out var existing);
                expansions[neighborGenre] = Math.Max(existing, derived);
            }
        }

        foreach (var (genre, derivedWeight) in expansions)
        {
            vector[genre] = derivedWeight;
        }
    }

    /// <summary>
    ///     Builds a set of studio names the user prefers, derived from their watched and favorited items.
    ///     Looks up the actual BaseItem objects from the candidate lookup to access Studios metadata.
    ///     <para>
    ///         Asymmetric weighting vs. genre/people: this method returns an unweighted
    ///         <see cref="HashSet{T}"/> — a studio that appeared in a series with 2/30 watched episodes
    ///         contributes exactly the same as a studio accumulated across 20 fully-watched series.
    ///         Genre and people preferences apply the same progression multiplier that
    ///         <see cref="BuildGenrePreferenceVector"/> and <see cref="BuildPeoplePreferenceWeights"/>
    ///         use, but studios stay flat because the downstream consumer is a binary
    ///         <c>StudioMatch</c> feature (<c>candidate.Studios.Any(preferredStudios.Contains)</c>)
    ///         and a weighted set adds no value to a boolean comparison. Feature-importance reports
    ///         consistently rank <c>StudioMatch</c> below <c>GenreSimilarity</c> and
    ///         <c>PeopleSimilarity</c>, so the modelling cost/benefit of turning this into a weighted
    ///         dictionary is not justified as of v3.0.0.0. Revisit if a future importance report
    ///         shows Studio contributing meaningfully.
    ///     </para>
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
            // Include items that are played OR favorited
            if (w is { Played: false, IsFavorite: false })
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

            // Also try series match (episodes → parent series)
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
    ///     Looks up the actual BaseItem objects from the candidate lookup to access Tags metadata.
    ///     Used for tag-based content similarity scoring.
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
            // Include items that are played OR favorited
            if (w is { Played: false, IsFavorite: false })
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

            // Series match (episodes → parent series)
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
    ///     Uses the pre-built people lookup to avoid additional library queries.
    ///     Includes people from both directly watched/favorited items and series the user has watched episodes of.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="peopleLookup">Pre-built candidate people lookup (item ID → person names).</param>
    /// <returns>A HashSet of preferred person names (case-insensitive).</returns>
    internal static HashSet<string> BuildPeoplePreferenceSet(
        UserWatchProfile userProfile,
        Dictionary<Guid, HashSet<string>> peopleLookup)
    {
        var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            // Include items that are played OR favorited
            if (w is { Played: false, IsFavorite: false })
            {
                continue;
            }

            // Direct item match (movies, episodes)
            if (peopleLookup.TryGetValue(w.ItemId, out var itemPeople))
            {
                people.UnionWith(itemPeople.Where(static p => !string.IsNullOrWhiteSpace(p)));
            }

            // Series match (episodes → parent series)
            if (w.SeriesId.HasValue && peopleLookup.TryGetValue(w.SeriesId.Value, out var seriesPeople))
            {
                people.UnionWith(seriesPeople.Where(static p => !string.IsNullOrWhiteSpace(p)));
            }
        }

        return people;
    }

    /// <summary>
    ///     Builds a weighted preference map of person names (actors/directors) from the user's watched
    ///     and favorited items. Each person's weight equals the number of DISTINCT watched/favorited items
    ///     they appear on, i.e. an "Actor X" that shows up in 8 different Nolan films gets weight 8, while
    ///     an actor from a single one-off watch gets weight 1.
    ///     <para>
    ///         Roadmap v3 (C2): the previous <see cref="BuildPeoplePreferenceSet"/> flattens all people
    ///         into a HashSet, giving a one-off appearance the same influence as a director the user
    ///         has watched dozens of times. This weighted variant preserves the frequency signal so
    ///         <see cref="SimilarityComputer.ComputePeopleSimilarity(System.Collections.Generic.HashSet{string},System.Collections.Generic.IReadOnlyDictionary{string,double})"/>
    ///         can score candidates against a user's dominant collaborators much higher than random cameo overlaps.
    ///     </para>
    ///     <para>
    ///         Uses the SAME source data as <see cref="BuildPeoplePreferenceSet"/> (watched-or-favorited
    ///         items × <paramref name="peopleLookup"/>) rather than <see cref="UserWatchProfile.PeopleProfile"/>,
    ///         because the two pipelines are populated at different points in the plugin lifecycle and can
    ///         drift; keeping the same source guarantees the weighted map is a strict super-set of the
    ///         unweighted HashSet (same keys, plus counts).
    ///     </para>
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="peopleLookup">Pre-built candidate people lookup (item ID → person names).</param>
    /// <param name="seriesEpisodeCounts">
    ///     Optional map <c>seriesId → totalEpisodeCount</c>. When provided, episode rows
    ///     contribute <c>ProgressionMultiplier(playedInSeries / totalEpisodes)</c> to each
    ///     person's weight instead of a flat 1.0, so people from series watched to completion
    ///     get a higher weight than people from series the user abandoned early.
    ///     Kept in perfect symmetry with the same parameter on
    ///     <see cref="BuildGenrePreferenceVector"/>, guaranteeing that the People- and
    ///     Genre-similarity feature see the same underlying "how much did the user actually
    ///     engage with this series" signal.
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

        // Same per-series played counter as in BuildGenrePreferenceVector so the two feature
        // pipelines see identical progression ratios. Built lazily; skipped entirely when the
        // caller did not supply seriesEpisodeCounts (backward-compatible path).
        Dictionary<Guid, int>? watchedEpisodesPerSeries = null;
        if (seriesEpisodeCounts is { Count: > 0 })
        {
            watchedEpisodesPerSeries = new Dictionary<Guid, int>();
            foreach (var row in userProfile.WatchedItems)
            {
                if (row.SeriesId is not { } sid || !row.HasPlaybackActivity())
                {
                    continue;
                }

                watchedEpisodesPerSeries.TryGetValue(sid, out var c);
                watchedEpisodesPerSeries[sid] = c + 1;
            }
        }

        foreach (var w in userProfile.WatchedItems)
        {
            // Include items that are played OR favorited — same eligibility rule as BuildPeoplePreferenceSet.
            if (w is { Played: false, IsFavorite: false })
            {
                continue;
            }

            // Merge people from the item itself AND its parent series (episodes → series).
            // De-duplicate per watched row so the same person on the same item is not
            // double-counted just because both item-level and series-level lookups return them.
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

            if (perRowPeople is null || perRowPeople.Count == 0)
            {
                continue;
            }

            // Progression multiplier: episodes contribute proportionally to how much of the
            // series the user has actually watched. Favorites are their own row and keep the
            // full weight (the multiplier degrades gracefully to 1.0 for non-series rows).
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
    ///     Builds the genre exposure analysis for a user. This is computed once per user
    ///     and reused for all candidate items to avoid redundant computation.
    ///     Returns a neutral (invalid) analysis when the user has insufficient watch history.
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
        // Insufficient history → all features default to 0 (neutral)
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
    ///     Computes the three genre exposure features for a single candidate item.
    ///     Uses a pre-built <see cref="GenreExposureAnalysis" /> to avoid redundant computation.
    ///     All three features are soft, continuous values in [0, 1] - they never hard-block
    ///     any genre, only provide graduated signals that the ML models can learn to weight.
    /// </summary>
    /// <param name="candidateGenres">The genres of the candidate item.</param>
    /// <param name="analysis">The pre-built genre exposure analysis for the user.</param>
    /// <returns>A tuple of (underexposure, dominanceRatio, affinityGap) all in [0, 1].</returns>
    internal static (double Underexposure, double DominanceRatio, double AffinityGap) ComputeGenreExposureFeatures(
        IReadOnlyList<string> candidateGenres,
        GenreExposureAnalysis analysis)
    {
        // Insufficient data or no candidate genres → all neutral
        if (!analysis.IsValid || candidateGenres.Count == 0)
        {
            return (0.0, 0.0, 0.0);
        }

        var underexposedCount = 0;
        var dominantCount = 0;
        var candidateWeightSum = 0.0;
        var validCount = 0;

        foreach (var genre in candidateGenres)
        {
            // Guard against null/whitespace entries that may come from external metadata providers.
            // TryGetValue would throw ArgumentNullException on null keys, and empty strings
            // would dilute the underexposure/dominance ratios.
            if (string.IsNullOrWhiteSpace(genre))
            {
                continue;
            }

            validCount++;

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
    ///     Derives the per-row progression multiplier for a watched item.
    ///     Non-episode rows and rows without accompanying series-episode metadata get a
    ///     neutral multiplier of <c>1.0</c> (identical to the pre-progression weight).
    ///     Episode rows return <c>clamp(ProgressionFloor + rawRatio * ProgressionSpan, ProgressionFloor, ProgressionCeiling)</c>
    ///     where <c>rawRatio = playedInSeries / totalEpisodes</c>.
    ///     <para>
    ///         Design intent: a series watched to completion should have its genres, studios and
    ///         collaborators drive user preferences <b>more</b> than a series abandoned after two
    ///         episodes. A hard 0.0 floor for abandoned series would completely erase genre signals
    ///         from users with mostly-abandoned watch history, which is worse than a mildly damped
    ///         signal — we choose a floor of <c>0.3</c> so the abandoned-series signal is still
    ///         audible but clearly weaker than a completed watch (multiplier <c>1.5</c>).
    ///     </para>
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
        // No series context or caller opted out (null map) → neutral, preserves pre-existing weight.
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
        // rawRatio=0 → ProgressionFloor (0.3), rawRatio=0.5 → 0.9, rawRatio=1 → 1.5.
        return Math.Clamp(
            ProgressionFloor + (rawRatio * ProgressionSpan),
            ProgressionFloor,
            ProgressionCeiling);
    }

    /// <summary>
    ///     Pre-computed genre exposure analysis for a user, reusable across all candidate items.
    ///     Built once per user by <see cref="BuildGenreExposureAnalysis" /> and passed to
    ///     <see cref="ComputeGenreExposureFeatures" /> for each candidate.
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