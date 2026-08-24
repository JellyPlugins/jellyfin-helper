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
    ///     Upper cap for the raw <c>PlayCount</c> fed into the log1p transform. Guards against
    ///     pathological metadata (e.g. stuck counters) inflating a genre's raw weight before normalization.
    ///     <para>
    ///         Typed as <see cref="int"/> to match <c>WatchedItemInfo.PlayCount</c>, avoiding a
    ///         double->int->double round-trip when clamping.
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
    ///         The multiplier only acts on the (temporal + playCount) portion and is capped so it
    ///         cannot invert a favorite decision. By design an explicit favorite (direct signal)
    ///         outranks a watched-through non-favorite (inferred signal). Callers needing the two
    ///         comparable should re-tune both constants together rather than folding progression
    ///         into the favorite additive.
    ///     </para>
    /// </summary>
    private const double ProgressionCeiling = 1.5;

    /// <summary>
    ///     Linear span from floor to ceiling, i.e. how much the raw ratio moves the multiplier.
    /// </summary>
    private const double ProgressionSpan = ProgressionCeiling - ProgressionFloor;

    /// <summary>
    ///     Target maximum contribution of the PlayCount log1p boost, chosen so heavy re-watchers produce
    ///     a meaningful signal not drowned out by the favorite additive
    ///     (<see cref="EngineConstants.FavoriteGenreBoostFactor"/> = 3.0), while staying sub-favorite so
    ///     an explicit favorite click always outweighs a pure re-watching pattern.
    ///     <para>
    ///         Rationale for 2.0 (v3 C1 hardening pass): the original scale (1.0) matched the pre-v3
    ///         linear cap (<c>min(PlayCount, 5) × 0.2 = 1.0</c>), which - with the +3.0 favorite additive
    ///         in <c>weight = temporalWeight + playCountBoost + (fav ? 3.0 : 0)</c> - made PlayCount 5 vs.
    ///         30 differ by only 4-13% of the total for favorited items, hiding the log1p refinement from
    ///         the ML feature. Raising the ceiling to 2.0 gives PlayCount 30 a ~1.5 boost (≈50% of the
    ///         favorite additive) so re-watching is measurable without inverting the favorite/re-watch ordering.
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
    ///         <item><description>PlayCount 1 -> 0.30 (baseline single-watch weight)</description></item>
    ///         <item><description>PlayCount 5 -> 0.78 (comparable to a fresh 1-day-old temporalWeight)</description></item>
    ///         <item><description>PlayCount 30 -> 1.49 (dedicated re-watcher signal, ≈50% of favorite additive)</description></item>
    ///         <item><description>PlayCount 100 -> 2.00 (theoretical ceiling; clamp beyond)</description></item>
    ///     </list>
    /// </summary>
    private static readonly double PlayCountLog1pScale = PlayCountLog1pCeiling / Math.Log(1.0 + PlayCountMaxForLog1p);

    /// <summary>
    ///     Builds a normalized genre preference vector from watch history. Each genre is weighted
    ///     by recency (180-day half-life exponential decay), play count (log1p boost), and favorites
    ///     (additive boost). Favorited-but-unplayed items are included since the user explicitly
    ///     expressed interest.
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

        // Shared per-series played counter. BuildGenrePreferenceVector and
        // BuildPeoplePreferenceWeights use the exact same aggregation, so extracting the loop
        // guarantees train/serve parity between the Genre- and People-similarity features.
        var watchedEpisodesPerSeries = BuildWatchedEpisodesPerSeries(profile, seriesEpisodeCounts);

        // Build genre preferences with temporal decay - recent watches count more
        var now = DateTime.UtcNow;
        foreach (var item in profile.WatchedItems)
        {
            // Eligibility must include PlayCount > 0 so the SAME rows contributing to
            // watchedEpisodesPerSeries also contribute their genres here. Without this symmetry a
            // series with PlayCount>0 but Played=false episodes would inflate the progression
            // multiplier for OTHER episodes without ever counting its own genres - a signal leak.
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

            var weight = ComputeGenreRowWeight(item, now, seriesEpisodeCounts, watchedEpisodesPerSeries);

            foreach (var genre in item.Genres.Where(static g => !string.IsNullOrWhiteSpace(g)))
            {
                vector.TryGetValue(genre, out var current);
                vector[genre] = current + weight;
            }
        }

        MergeGenreDistribution(vector, profile);

        if (vector.Count == 0)
        {
            return vector;
        }

        // Expand first, normalize afterwards so proximity-derived weights participate in
        // the same max-normalization pass as the base entries. Doing it in the other order
        // would leave secondary genres in `[0, 0.15]` while primary genres are in `[0, 1]`,
        // producing a non-normalized vector that drifts SimilarityComputer's `userNorm`.
        ExpandGenreProximity(vector, profile);

        NormalizeByMax(vector);

        return vector;
    }

    /// <summary>
    ///     Expands genre preferences with co-occurrence proximity weights. Genres frequently appearing
    ///     together reinforce each other: an existing entry gets an additive boost proportional to the
    ///     strongest incoming co-occurrence path, and absent genres that co-occur with known ones are
    ///     introduced with a derived weight.
    ///     <para>
    ///         <b>Design rationale (v3 hardening pass):</b> the previous version only inserted <i>new</i>
    ///         genres (guarded by <c>vector.ContainsKey</c>), a near no-op since neighbours were usually
    ///         already direct-watched. The current version applies an <b>additive</b> boost (capped so it
    ///         cannot exceed a fresh direct-watch signal) to existing entries too, so a strongly
    ///         co-occurring pair like Action↔Adventure reinforces both peers relative to a weakly
    ///         co-occurring third genre. The cap keeps the boost below the raw direct-watch peer weight,
    ///         so an explicitly-watched genre always outranks a purely-inferred one - the same
    ///         monotonicity the favorite additive maintains against re-watch signals.
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

        var cooccurrence = BuildGenreCooccurrence(profile);

        // proximityFactor caps every derived boost at 15% of the source genre's own weight,
        // so a co-occurrence link can never lift a neighbour above the source itself.
        // minCooccurrences filters one-off pairs that would otherwise inject noise from a
        // single mis-tagged item.
        const double proximityFactor = 0.15;
        const int minCooccurrences = 2;

        // Snapshot pre-expansion weights so proximity boosts are computed from the direct-watch
        // signal only. Reading a mutating vector while iterating would let earlier boosts feed
        // later ones, cascading a mild pair into a dominant signal.
        var baseWeights = new Dictionary<string, double>(vector, StringComparer.OrdinalIgnoreCase);

        // Aggregate the strongest incoming proximity contribution per target genre. "Strongest"
        // (Math.Max) rather than "sum": a genre co-occurring with three known peers should not be
        // triple-boosted, which would inflate hubs like "Drama"/"Action" purely from appearing on
        // many multi-genre items. The strongest path captures reinforcement without double-counting.
        var proximityContributions = BuildProximityContributions(
            baseWeights,
            cooccurrence,
            proximityFactor,
            minCooccurrences);

        // Apply contributions.
        //   * Genres already in the vector (direct-watch): ADD the derived contribution. proximityFactor
        //     (0.15) caps it at 15% of the source peer's weight, whose max is the direct-watch peak, so a
        //     reinforced genre can never overtake one with a strictly stronger direct-watch signal.
        //   * Genres NOT in the vector (pure inference): INSERT with the derived weight so soft-related
        //     genres surface for candidates never explicitly watched (the "expand into unseen genres"
        //     behaviour the original ContainsKey skip never applied).
        // Applied last (after the read snapshot) so baseWeights iteration order does not influence the
        // result - an invariant for train/serve parity, since Dictionary enumeration order is not part
        // of the .NET contract.
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
    ///         Asymmetric weighting vs. genre/people: this returns an unweighted
    ///         <see cref="HashSet{T}"/>, so a studio from a 2/30-watched series counts the same as one
    ///         accumulated across 20 fully-watched series. Genre and people preferences apply the
    ///         progression multiplier, but studios stay flat because the downstream consumer is a binary
    ///         <c>StudioMatch</c> feature (<c>candidate.Studios.Any(preferredStudios.Contains)</c>) where
    ///         a weighted set adds no value. Feature-importance reports rank <c>StudioMatch</c> below
    ///         <c>GenreSimilarity</c> and <c>PeopleSimilarity</c>, so weighting is not justified as of
    ///         v3.0.0.0. Revisit if a future importance report shows Studio contributing meaningfully.
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
            // Same eligibility as BuildGenrePreferenceVector so a PlayCount>0 row that contributes
            // its genres also contributes its studios. Keeps the four preference builders
            // (genre / studio / tag / people) internally consistent.
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
    ///     Uses the pre-built people lookup to avoid additional library queries.
    ///     Includes people from both directly watched/favorited items and series the user has watched episodes of.
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
            // Aligned with BuildGenrePreferenceVector so the unweighted set (reason-display) and
            // the weighted set (ML scoring) cover the same source rows - otherwise the Reason
            // ("because you like <actor>") could reference an actor with no ML weight, or vice-versa.
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
    ///     Builds a weighted preference map of person names (actors/directors) from the user's
    ///     watched/favorited items. Each person's weight equals the number of DISTINCT watched/favorited
    ///     items they appear on (an actor in 8 Nolan films gets weight 8; a one-off watch gets weight 1).
    ///     <para>
    ///         <see cref="BuildPeoplePreferenceSet"/> flattens people into a HashSet, giving a one-off
    ///         appearance the same influence as a director watched dozens of times. This weighted variant
    ///         preserves the frequency signal so
    ///         <see cref="SimilarityComputer.ComputePeopleSimilarity(System.Collections.Generic.HashSet{string},System.Collections.Generic.IReadOnlyDictionary{string,double})"/>
    ///         can score a user's dominant collaborators above random cameo overlaps.
    ///     </para>
    ///     <para>
    ///         Uses the SAME source as <see cref="BuildPeoplePreferenceSet"/> (watched-or-favorited items ×
    ///         <paramref name="peopleLookup"/>) rather than <see cref="UserWatchProfile.PeopleProfile"/>,
    ///         which is populated at a different lifecycle point and can drift; this keeps the weighted map
    ///         a strict super-set of the unweighted HashSet (same keys, plus counts).
    ///     </para>
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

            // Merge people from the item itself AND its parent series (episodes -> series).
            // De-duplicate per watched row so the same person on the same item is not
            // double-counted just because both item-level and series-level lookups return them.
            var perRowPeople = CollectPerRowPeople(w, peopleLookup);

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
    ///     Builds a max-normalized franchise preference map (TMDb collection name -> weight in [0,1])
    ///     from the user's watched/favorited movies. Mirrors <see cref="BuildGenrePreferenceVector"/>'s
    ///     temporal-decay + play-count + favorite-boost composition so franchise affinity is comparable
    ///     in magnitude to genre affinity. Source is <see cref="WatchedItemInfo.TmdbCollectionName"/>
    ///     directly (no BaseItem lookup needed).
    ///     <para>Empty watch history or items without a collection name yield an empty map - a candidate
    ///     with no franchise, or a franchise the user never engaged with, scores 0.0 (no crash, no bias).</para>
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
    ///     Builds a max-normalized production-country preference map (country -> weight in [0,1]) from the
    ///     user's watched/favorited items, using the same weighting as
    ///     <see cref="BuildFranchisePreferenceVector"/>. Source is
    ///     <see cref="WatchedItemInfo.ProductionCountries"/> directly.
    ///     <para>Empty history or items without countries yield an empty map -> candidate scores 0.0.</para>
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
    ///     Builds a set of preferred inherited tags from the user's watched/favorited items, reading
    ///     <see cref="WatchedItemInfo.InheritedTags"/> directly. Mirrors <see cref="BuildTagPreferenceSet"/>
    ///     but over the inherited (own + parent/collection/library-folder) tag set.
    ///     <para>Empty history or items without inherited tags yield an empty set -> candidate scores 0.0.</para>
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
    ///     Builds a weighted writer preference map (writer name -> weight) from the user's watched/favorited
    ///     items, reading <see cref="WatchedItemInfo.WriterNames"/> directly. Kept separate from the
    ///     actor/director people profile so writer affinity does not dilute
    ///     <see cref="SimilarityComputer.ComputePeopleSimilarity(System.Collections.Generic.HashSet{string},System.Collections.Generic.IReadOnlyDictionary{string,double})"/>.
    ///     Each writer is counted once per row (progression-weighted), mirroring
    ///     <see cref="BuildPeoplePreferenceWeights"/>.
    ///     <para>Empty history or items without writers yield an empty map -> candidate scores 0.0.</para>
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
    ///     Computes the per-row preference weight shared by the franchise/country/writer builders,
    ///     using the SAME temporal-decay + play-count-log1p + favorite-additive composition as
    ///     <see cref="BuildGenrePreferenceVector"/> (without series-progression, which needs a series
    ///     episode-count map not threaded here - it degrades to the neutral 1.0 multiplier anyway).
    ///     All arithmetic is finite (Log/Exp of clamped non-negative inputs), so no NaN is produced.
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
    ///     Max-normalizes a weight map in place so its largest value becomes 1.0, matching the
    ///     normalization tail of <see cref="BuildGenrePreferenceVector"/>. Guarded against empty maps
    ///     and non-positive maxima (no division by zero).
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
        // Insufficient data or no candidate genres -> all neutral
        if (!analysis.IsValid || candidateGenres.Count == 0)
        {
            return (0.0, 0.0, 0.0);
        }

        var underexposedCount = 0;
        var dominantCount = 0;
        var candidateWeightSum = 0.0;

        // De-duplicate candidate genres case-insensitively BEFORE counting, mirroring the sibling
        // SimilarityComputer.ComputeGenreSimilarity. Without this, a candidate carrying duplicate
        // genre entries (e.g. ["Action","Action","Drama"] from a metadata provider) would inflate
        // validCount / dominantCount / candidateWeightSum, corrupting all three exposure features and
        // making them disagree with the deduping GenreSimilarity on the same item's effective genre set.
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
    ///     Returns true when the row should count as a "completed episode" for the series-progression
    ///     multiplier. Stricter than <see cref="WatchedItemInfo.HasPlaybackActivity"/>:
    ///     PlaybackPositionTicks > 0 alone is a partial start and does NOT count, else briefly opening
    ///     every episode would inflate playedEps to totalEps and unlock the max <c>ProgressionCeiling</c>
    ///     (1.5) with nothing finished.
    ///     <para>
    ///         The two eligible signals are:
    ///         <list type="bullet">
    ///             <item><description><c>Played</c> - Jellyfin's own "watched" flag, set on completion.</description></item>
    ///             <item><description><c>PlayCount &gt; 0</c> - finished the episode at least once.</description></item>
    ///         </list>
    ///         Favorites are excluded (favoriting does not imply completion); the favorite additive is
    ///         applied elsewhere so it is not lost through this filter.
    ///     </para>
    /// </summary>
    /// <param name="row">The watched-item row to classify.</param>
    /// <returns>True when the row represents an actually-completed episode.</returns>
    private static bool IsEpisodeCompletedForProgression(WatchedItemInfo row)
    {
        return row.Played || row.PlayCount > 0;
    }

    /// <summary>
    ///     Eligibility predicate for the genre / people preference-weighting loops. Superset of
    ///     <see cref="IsEpisodeCompletedForProgression"/>: any completed-episode row contributes its
    ///     genres and people, PLUS explicit favorites (intent regardless of playback state).
    ///     Guarantees every row in <c>watchedEpisodesPerSeries</c> also contributes its own signal, so
    ///     a PlayCount>0 row cannot inflate another row's progression multiplier while withholding its
    ///     own genres - the signal-leak bug this predicate closes.
    /// </summary>
    /// <param name="row">The watched-item row to classify.</param>
    /// <returns>True when the row is eligible for genre / people preference weighting.</returns>
    private static bool IsEligibleForPreferenceWeighting(WatchedItemInfo row)
    {
        return row.IsFavorite || IsEpisodeCompletedForProgression(row);
    }

    /// <summary>
    ///     F-04 phantom-row guard. Returns true when the row belongs to a series deleted from the
    ///     library: the caller passes <paramref name="seriesEpisodeCounts"/> from
    ///     <c>Engine.LoadCandidateItems</c>, so any <c>SeriesId</c> absent from that map is stale.
    ///     Only <see cref="BuildGenrePreferenceVector"/> and <see cref="BuildPeoplePreferenceWeights"/>
    ///     receive the series map; the studio / tag / (unweighted) people paths still call the 1-arg
    ///     <see cref="IsEligibleForPreferenceWeighting"/> unchanged for backwards compatibility.
    ///     <para>
    ///         Rows without a <see cref="WatchedItemInfo.SeriesId"/> (movies, standalone items) are
    ///         never phantoms here - the item-lookup maps in the caller validate them. Only
    ///         episode / series rows benefit from this guard.
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
    ///     map - signalling the downstream progression-multiplier helper to fall back to the neutral
    ///     <c>1.0</c> weight instead of computing a ratio.
    ///     <para>
    ///         Extracted so <see cref="BuildGenrePreferenceVector"/> and
    ///         <see cref="BuildPeoplePreferenceWeights"/> share <b>one</b> aggregation pass, so any tweak
    ///         to the completion predicate propagates to both pipelines automatically (the previous
    ///         duplicated loops needed a code-review convention to stay in sync).
    ///     </para>
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

            // Skip rows for series no longer in the library (phantom data). The skip must happen
            // BEFORE bumping the counter; ComputeProgressionMultiplier's Math.Min(1.0, rawRatio)
            // already clamps overshoot for still-existing series, so no per-row cap is needed here.
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
    ///         Design intent: a completed series should drive preferences <b>more</b> than one
    ///         abandoned after two episodes. A hard 0.0 floor would erase genre signals for users
    ///         with mostly-abandoned history, worse than a mildly damped signal, so the floor is
    ///         <c>0.3</c> (audible but clearly weaker than a completed watch's <c>1.5</c>).
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
        // Explicit-favorite rows that are NOT themselves completed episodes bypass progression scaling:
        //   * BuildGenrePreferenceVector adds the FAVORITE additive (+3.0) AFTER the multiplier. An
        //     episode row's multiplier derives from OTHER episodes of the series (which the user may not
        //     have engaged with); scaling first would dampen a favorited-pilot of an abandoned series to
        //     ProgressionFloor (0.3) before the additive, breaking "favorite always keeps full weight".
        //   * BuildPeoplePreferenceWeights has NO favorite additive - each row contributes exactly
        //     progressionMultiplier per person. Without this guard an unplayed favorite episode of an
        //     abandoned series would contribute only 0.3 per person, wrongly ranking the explicit favorite
        //     below the abandoned series' progression ratio.
        // Completed favorites (Played or PlayCount > 0) take the normal ratio path so their signal
        // reflects favorite intent AND completion - strictly stronger than either alone.
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