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
    ///     more strongly than a single played row.
    ///     <para>
    ///         Note on the ordering vs. the +3.0 favorite additive: the multiplier only acts on
    ///         the (temporal + playCount) portion of the weight and is capped so it cannot invert
    ///         a favorite decision. The reverse is also intentional — an explicit favorite click
    ///         will outrank a watched-through non-favorite. That asymmetry is by design: a
    ///         favorite is a direct user signal, progression is an inferred one. Callers who need
    ///         the two to be comparable magnitudes should re-tune both constants together rather
    ///         than folding progression into the favorite additive.
    ///     </para>
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
    private static readonly double GenreDecayConstant = Math.Log(2.0) / EngineConstants.GenreDecayHalfLifeDays;

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

        // Shared helper builds the per-series played counter (see BuildWatchedEpisodesPerSeries).
        // Both this method and BuildPeoplePreferenceWeights use the exact same aggregation, so
        // extracting the loop guarantees train/serve parity between the Genre- and
        // People-similarity features on the same profile.
        var watchedEpisodesPerSeries = BuildWatchedEpisodesPerSeries(profile, seriesEpisodeCounts);

        // Build genre preferences with temporal decay - recent watches count more
        var now = DateTime.UtcNow;
        foreach (var item in profile.WatchedItems)
        {
            // Eligibility must include PlayCount > 0 so the SAME rows that contribute to
            // watchedEpisodesPerSeries above also contribute their genres here. Without this
            // symmetry a series with several PlayCount>0 but Played=false episodes would
            // inflate the progression multiplier for OTHER episodes without ever having its
            // own genres counted — a genre-signal leak.
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
            // With hardening pass: switched from linear
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
    ///     reinforce each other: an existing entry gets an additive boost proportional
    ///     to the strongest incoming co-occurrence path from other known genres,
    ///     and genres that were absent from the direct preference vector but co-occur
    ///     with known ones are introduced with a derived weight.
    ///     <para>
    ///         <b>Design rationale (v3 hardening pass):</b> the previous implementation
    ///         only inserted <i>new</i> genres (guarded by <c>vector.ContainsKey</c>) and
    ///         therefore did nothing for the overwhelmingly common case in which every
    ///         co-occurrence neighbour was already a direct-watched genre — the expansion
    ///         call was effectively a no-op for anything but very sparse profiles. The
    ///         current implementation applies an <b>additive</b> boost (capped so it
    ///         cannot exceed a fresh direct-watch signal) to existing entries so that
    ///         a strongly co-occurring pair like Action↔Adventure reinforces both peers
    ///         relative to a weakly co-occurring third genre. This makes the proximity
    ///         signal observable in the final normalised vector, which is what
    ///         <see cref="BuildGenrePreferenceVector"/>'s downstream ML feature relies on.
    ///         The additive boost is kept below the raw direct-watch peer weight so an
    ///         explicitly-watched genre always outranks a purely-inferred one — the
    ///         same monotonicity guarantee the favorite additive maintains against
    ///         re-watch signals elsewhere in this file.
    ///     </para>
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

        // proximityFactor caps every derived boost at 15% of the source genre's own weight,
        // so a co-occurrence link can never lift a neighbour above the source itself.
        // minCooccurrences filters one-off pairs that would otherwise inject noise from a
        // single mis-tagged item.
        const double proximityFactor = 0.15;
        const int minCooccurrences = 2;

        // Take a snapshot of the current (pre-expansion) weights so proximity boosts are
        // computed from the direct-watch signal only. Reading from a mutating vector while
        // iterating would let earlier boosts feed later ones, cascading a mild pair into a
        // dominant signal.
        var baseWeights = new Dictionary<string, double>(vector, StringComparer.OrdinalIgnoreCase);

        // Aggregate the strongest incoming proximity contribution for each target genre.
        // "Strongest" (Math.Max) rather than "sum": a genre that co-occurs with three known
        // peers should not get triple-boosted — that would inflate hubs like "Drama" or
        // "Action" purely by virtue of appearing on many multi-genre items. The strongest
        // path already captures the reinforcement without double-counting overlapping evidence.
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

        // Apply contributions.
        //   * For genres already in the vector (direct-watch signal exists): ADD the derived
        //     contribution to reinforce the existing weight. proximityFactor (0.15) caps the
        //     reinforcement at 15 % of the source peer's weight, and the source peer's own
        //     max weight in the vector is the direct-watch peak, so a reinforced genre can
        //     never overtake a genre whose direct-watch signal is strictly stronger.
        //   * For genres NOT in the vector (pure inference from co-occurrence): INSERT with
        //     the derived weight so soft-related genres surface for candidates the user never
        //     explicitly watched. This preserves the "expand into unseen genres" behaviour the
        //     original implementation intended but never actually applied because of the
        //     ContainsKey skip.
        //
        // Contributions are applied last (after the read snapshot) so the iteration order of
        // baseWeights does not influence the final result — an important invariant for
        // train/serve parity given that Dictionary enumeration order is not part of the
        // .NET contract.
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
            // Same eligibility rule as BuildGenrePreferenceVector so a PlayCount>0 row
            // that contributes its genres also contributes its studios. Keeps the four
            // preference builders (genre / studio / tag / people) internally consistent.
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
            // Aligned with BuildGenrePreferenceVector — a PlayCount>0 row that contributes
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
            // Aligned with BuildGenrePreferenceVector so the unweighted set used for
            // reason-display and the weighted set used for ML scoring cover exactly the
            // same source rows — otherwise the Reason ("because you like <actor>") could
            // reference an actor that never got ML weight, or vice-versa.
            if (!IsEligibleForPreferenceWeighting(w))
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
    ///         The previous <see cref="BuildPeoplePreferenceSet"/> flattens all people
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

        // Shared helper — same aggregation as BuildGenrePreferenceVector so both feature
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

            // F-04 phantom guard: keep in lock-step with BuildGenrePreferenceVector.
            if (IsPhantomSeriesRow(w, seriesEpisodeCounts))
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
    ///     Returns true when the watched-item row should count as a "completed episode"
    ///     for the series-progression multiplier. Stricter than
    ///     <see cref="WatchedItemInfo.HasPlaybackActivity"/>: PlaybackPositionTicks > 0
    ///     alone is treated as a partial start and does NOT count, because a user briefly
    ///     opening every episode of a series would otherwise inflate playedEps to totalEps
    ///     and unlock the maximum <c>ProgressionCeiling</c> (1.5) even though no episode
    ///     was actually finished.
    ///     <para>
    ///         The two eligible signals are:
    ///         <list type="bullet">
    ///             <item><description><c>Played</c> — Jellyfin's own "watched" flag, set on completion.</description></item>
    ///             <item><description><c>PlayCount &gt; 0</c> — the user has finished the episode at least once.</description></item>
    ///         </list>
    ///         Favorites are explicitly excluded because favoriting an episode does not
    ///         imply completion; the favorite additive is applied elsewhere as its own
    ///         signal so it is not lost through this filter.
    ///     </para>
    /// </summary>
    /// <param name="row">The watched-item row to classify.</param>
    /// <returns>True when the row represents an actually-completed episode.</returns>
    private static bool IsEpisodeCompletedForProgression(WatchedItemInfo row)
    {
        return row.Played || row.PlayCount > 0;
    }

    /// <summary>
    ///     Eligibility predicate for the genre / people preference-weighting loops.
    ///     Superset of <see cref="IsEpisodeCompletedForProgression"/>: any row that counts as
    ///     a completed episode also contributes its genres and people, PLUS explicit favorites
    ///     (which signal intent regardless of playback state). Guarantees that every row
    ///     included in <c>watchedEpisodesPerSeries</c> also contributes its own signal, so a
    ///     PlayCount>0 row cannot inflate the progression multiplier of another row while
    ///     silently withholding its own genres — the signal-leak bug this predicate closes.
    /// </summary>
    /// <param name="row">The watched-item row to classify.</param>
    /// <returns>True when the row is eligible for genre / people preference weighting.</returns>
    private static bool IsEligibleForPreferenceWeighting(WatchedItemInfo row)
    {
        return row.IsFavorite || IsEpisodeCompletedForProgression(row);
    }

    /// <summary>
    ///     F-04 phantom-row guard. Returns true when the row belongs to a series that has been
    ///     deleted from the library — the caller passes <paramref name="seriesEpisodeCounts"/>
    ///     from <c>Engine.LoadCandidateItems</c> so any <c>SeriesId</c> absent from that map is
    ///     by definition stale. Only <see cref="BuildGenrePreferenceVector"/> and
    ///     <see cref="BuildPeoplePreferenceWeights"/> receive the series map — the studio / tag /
    ///     (unweighted) people paths still call the 1-arg <see cref="IsEligibleForPreferenceWeighting"/>
    ///     unchanged for backwards compatibility.
    ///     <para>
    ///         Rows without a <see cref="WatchedItemInfo.SeriesId"/> (movies, standalone items) are
    ///         never treated as phantoms here — their existence is validated by the item-lookup
    ///         maps in the caller. Only episode / series rows benefit from this guard.
    ///     </para>
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
    ///     Pre-aggregates the number of completed episodes per series for the user, using the
    ///     strict <see cref="IsEpisodeCompletedForProgression"/> predicate (Played or PlayCount &gt; 0).
    ///     Returns <c>null</c> when the caller did not supply a <paramref name="seriesEpisodeCounts"/>
    ///     map — signalling the downstream progression-multiplier helper to fall back to the neutral
    ///     <c>1.0</c> weight instead of computing a ratio.
    ///     <para>
    ///         Extracted so <see cref="BuildGenrePreferenceVector"/> and
    ///         <see cref="BuildPeoplePreferenceWeights"/> share <b>one</b> aggregation pass by construction.
    ///         Any future tweak to the completion predicate now propagates to both feature pipelines
    ///         automatically; the previous duplicated loops needed a code-review convention to stay in sync.
    ///     </para>
    /// </summary>
    /// <param name="profile">The user watch profile whose rows we aggregate.</param>
    /// <param name="seriesEpisodeCounts">Optional caller-supplied totals; <c>null</c> disables aggregation.</param>
    /// <returns>
    ///     A dictionary <c>seriesId → completedEpisodes</c>, or <c>null</c> when
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

            // Skip rows for series no longer in the library (phantom data from deleted series).
            // ComputeProgressionMultiplier's Math.Min(1.0, rawRatio) already clamps overshoot
            // for the still-existing series case, so no per-row cap is required here — the
            // skip is what actually matters, and it must happen BEFORE we bump the counter.
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
        // Explicit-favorite rows that are NOT themselves completed episodes bypass the
        // progression scaling entirely. Rationale:
        //
        //   * BuildGenrePreferenceVector adds a separate FAVORITE additive (+3.0) after
        //     the multiplier, so an unplayed-favorite MOVIE (SeriesId is null anyway) or
        //     a favorite-but-not-completed EPISODE would otherwise have its temporal +
        //     playCount weight scaled by the multiplier before the additive kicks in —
        //     but for episode rows the multiplier is derived from OTHER episodes of the
        //     same series, which the user did not necessarily engage with. A user who
        //     favorited a single pilot episode of an otherwise-abandoned series would
        //     see their favorite click dampened to ProgressionFloor (0.3) before the
        //     additive is applied — silently contradicting the "favorite always keeps
        //     full weight" invariant documented in BuildPeoplePreferenceWeights.
        //
        //   * BuildPeoplePreferenceWeights has NO separate favorite additive at all —
        //     each row contributes exactly progressionMultiplier per person. Without
        //     this guard an unplayed favorite episode of an abandoned series would
        //     contribute only 0.3 per person, which is objectively wrong: the user's
        //     explicit favorite click is a stronger intent signal than an abandoned
        //     series' progression ratio.
        //
        // Guard: item is favorite AND does not qualify as a completed episode
        // (via IsEpisodeCompletedForProgression). Completed favorites (Played or
        // PlayCount > 0) go through the normal ratio path so their signal reflects
        // both the favorite intent AND the completion state — that combination is
        // strictly stronger than either alone.
        if (item.IsFavorite && !IsEpisodeCompletedForProgression(item))
        {
            return 1.0;
        }

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