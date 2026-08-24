using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Handles diversity re-ranking (MMR) and series deduplication
///     to ensure recommendation lists are varied and non-repetitive.
/// </summary>
internal static class DiversityReranker
{
    /// <summary>
    ///     Multiplier that widens the MMR candidate pool relative to the requested result count.
    ///     For <c>count = 20</c> the top <c>20 × 5 = 100</c> candidates feed MMR's diversity-aware
    ///     relevance selection. Kept here (not in EngineConstants) because the exploration-pool
    ///     factor is tightly coupled to it and both are internal to this reranker.
    /// </summary>
    internal const int MmrPoolFactor = 5;

    /// <summary>
    ///     Multiplier for the wider "exploration" candidate band. For <c>count = 20</c> the widened
    ///     band spans ranks <c>20 × MmrPoolFactor</c> .. <c>20 × ExplorationPoolFactor</c> - i.e.
    ///     100..400 - so exploration picks can reach beyond MMR's cluster.
    /// </summary>
    internal const int ExplorationPoolFactor = 20;

    // Precondition: result list must not contain duplicate objects (same reference).
    // bestPerSeries maps seriesId to index in result; index validity relies on no mid-loop list compaction.

    /// <summary>
    ///     Deduplicates series entries: when episodes or seasons from the same series
    ///     appear as separate candidates, keeps only the highest-scored entry per series.
    ///     Non-series items (movies, etc.) are passed through unchanged.
    /// </summary>
    /// <param name="scored">The scored candidate list (may contain duplicate series).</param>
    /// <returns>A deduplicated list with at most one entry per series.</returns>
    internal static List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        DeduplicateSeries(
            List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> scored)
    {
        var result = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>(scored.Count);
        var bestPerSeries = new Dictionary<Guid, int>();

        foreach (var entry in scored)
        {
            Guid? seriesId = ResolveSeriesId(entry.Item);

            if (seriesId is null)
            {
                result.Add(entry);
                continue;
            }

            if (bestPerSeries.TryGetValue(seriesId.Value, out var existingIdx))
            {
                if (entry.Score > result[existingIdx].Score)
                {
                    result[existingIdx] = entry;
                    bestPerSeries[seriesId.Value] = existingIdx;
                }
            }
            else
            {
                bestPerSeries[seriesId.Value] = result.Count;
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    ///     Resolves the series identifier used for deduplication from a candidate item.
    ///     Episodes and seasons resolve to their parent series id; a series resolves to its own id.
    ///     Non-series items (and any item with an empty series id) return <c>null</c>.
    /// </summary>
    /// <param name="item">The candidate item.</param>
    /// <returns>The series id to dedupe on, or <c>null</c> for non-series items.</returns>
    private static Guid? ResolveSeriesId(BaseItem item)
    {
        return item switch
        {
            Episode ep => ep.SeriesId != Guid.Empty ? ep.SeriesId : null,
            Season season => season.SeriesId != Guid.Empty ? season.SeriesId : null,
            Series s => s.Id != Guid.Empty ? s.Id : null,
            _ => null
        };
    }

    /// <summary>
    ///     Runs MMR-based diversity re-ranking on the top scored candidates and reserves the tail
    ///     slots for exploration picks drawn from a widened low-relevance pool.
    /// </summary>
    /// <param name="candidates">The scored candidate list.</param>
    /// <param name="count">The target number of recommendations.</param>
    /// <param name="seed">
    ///     Optional deterministic seed for the exploration sampler.
    ///     Callers MUST use a process-stable hash (e.g. <c>Engine.ComputeStableSeed(userId, dayNumber)</c>
    ///     or <c>Engine.ComputeStableSeed(userId, batchGeneration)</c>) - <see cref="HashCode.Combine{T1,T2}"/>
    ///     is randomised per process and would reshuffle the same (userId, day) tuple after every
    ///     Jellyfin restart, defeating the "stable within one day" contract exploration relies on.
    /// </param>
    /// <returns>The diversified selection of at most <paramref name="count"/> scored candidates.</returns>
    internal static List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        ApplyDiversityReranking(
            List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> candidates,
            int count,
            int? seed = null)
    {
        if (count <= 0)
        {
            return [];
        }

        if (candidates.Count <= count)
        {
            return candidates.OrderByDescending(c => c.Score).ToList();
        }

        var selected = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>(count);
        // Rank the entire candidate list once and split into two disjoint pools:
        //   * mmrPool: top count·MmrPoolFactor for the diversity-aware relevance selection.
        //   * explorationPool: everything up to count·ExplorationPoolFactor excluding the mmrPool head,
        //     so exploration can inject picks from ranks MmrPoolFactor·count ... ExplorationPoolFactor·count
        //     that MMR would never see. The two factors live as internal constants above so any future
        //     retune touches a single line and the tests reference the same source of truth.
        var ranked = candidates.OrderByDescending(c => c.Score).ToList();
        var mmrPoolSize = Math.Min(ranked.Count, count * MmrPoolFactor);
        var explorationPoolSize = Math.Min(ranked.Count, count * ExplorationPoolFactor);
        var remaining = ranked.Take(mmrPoolSize).ToList();

        // Multi-dimensional similarity caches: genre (50% weight), studio (30% weight), production year (20% weight).
        // Previously MMR looked at genres only, which meant it would happily surface two Marvel superhero
        // movies (same genre set but same studio and same era). By blending studio and era similarity
        // we now diversify along multiple axes without over-penalising true content variety.
        // Note: we intentionally do NOT diversify by people/cast because a single actor commonly
        // appears in wildly different genres (Christopher Nolan does thrillers AND sci-fi), and
        // penalising same-actor picks would exclude legitimately diverse recommendations.
        var similarity = new SimilarityCache();

        // Fill most slots via MMR, reserving the last few slots for random exploration
        // picks. This guarantees the model receives diverse feedback while keeping the
        // list relevance-dominated.
        //
        // Slot-allocation strategy scales exploration with list size so admins who
        // shrink MaxRecommendationsPerUser to 3-5 items don't end up with 50-66% random
        // exploration. Previously the flat ExplorationSlotCount ceiling meant:
        //   count=2 -> 1 exploration + 1 MMR   (50% random)
        //   count=3 -> 2 exploration + 1 MMR   (66% random)
        //   count>=10 -> 2 exploration + rest MMR
        // Now we cap exploration at max(1, count / ExplorationSlotDivisor) so exploration
        // stays roughly ~10% of the list, matching what count=20 configurations always saw.
        // For tiny lists (count < divisor) exploration is 1 slot; the ceiling is still
        // ExplorationSlotCount so large lists behave identically to before.
        var proportionalCap = Math.Max(1, count / EngineConstants.ExplorationSlotDivisor);
        var explorationSlots = Math.Min(
            EngineConstants.ExplorationSlotCount,
            Math.Min(proportionalCap, Math.Max(0, count - 1)));
        var mmrSlotCount = count - explorationSlots;

        RunMmrSelection(remaining, selected, similarity, mmrSlotCount);

        // Build the disjoint exploration pool:
        //   * Start with the widened band ranks[mmrPoolSize .. explorationPoolSize].
        //   * Add the MMR-pool leftovers (not selected by MMR) so we do not lose valid picks
        //     when the widened band is smaller than the exploration slot count.
        //   * Skip anything MMR already committed to, to avoid duplicate selections.
        if (selected.Count < count)
        {
            FillExplorationSlots(ranked, remaining, selected, count, mmrPoolSize, explorationPoolSize, seed);
        }

        return selected;
    }

    /// <summary>
    ///     Greedily selects <paramref name="mmrSlotCount"/> items from <paramref name="remaining"/>
    ///     into <paramref name="selected"/> using Maximal Marginal Relevance: each pick maximises
    ///     relevance minus similarity to already-selected items. Selected items are swap-removed
    ///     from <paramref name="remaining"/>.
    /// </summary>
    private static void RunMmrSelection(
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> remaining,
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> selected,
        SimilarityCache similarity,
        int mmrSlotCount)
    {
        while (selected.Count < mmrSlotCount && remaining.Count > 0)
        {
            var bestIdx = -1;
            var bestMmrScore = double.MinValue;

            for (var i = 0; i < remaining.Count; i++)
            {
                var relevance = remaining[i].Score;
                var maxSimilarity = 0.0;
                foreach (var selectedItem in selected.Select(selectedEntry => selectedEntry.Item))
                {
                    var sim = similarity.Compute(remaining[i].Item, selectedItem);
                    if (sim > maxSimilarity)
                    {
                        maxSimilarity = sim;
                    }
                }

                var mmrScore = (EngineConstants.MmrLambda * relevance) - ((1.0 - EngineConstants.MmrLambda) * maxSimilarity);

                if (mmrScore > bestMmrScore)
                {
                    bestMmrScore = mmrScore;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                selected.Add(remaining[bestIdx]);

                var lastIdx = remaining.Count - 1;
                if (bestIdx < lastIdx)
                {
                    remaining[bestIdx] = remaining[lastIdx];
                }

                remaining.RemoveAt(lastIdx);
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    ///     Fills the reserved exploration slots (up to <paramref name="count"/>) from a widened
    ///     candidate band plus the MMR-pool leftovers, sampling randomly with a caller-supplied
    ///     deterministic seed (or <see cref="Random.Shared"/> when none is provided).
    /// </summary>
    private static void FillExplorationSlots(
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> ranked,
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> remaining,
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> selected,
        int count,
        int mmrPoolSize,
        int explorationPoolSize,
        int? seed)
    {
        var mmrSelectedIds = new HashSet<Guid>(selected.Count);
        foreach (var s in selected)
        {
            mmrSelectedIds.Add(s.Item.Id);
        }

        var explorationPool = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>();
        for (var i = mmrPoolSize; i < explorationPoolSize; i++)
        {
            explorationPool.Add(ranked[i]);
        }

        // Fall back to the MMR-pool leftovers when the widened band is exhausted so that
        // small libraries still get exploration signal (this preserves the previous behaviour
        // as a floor rather than as the only source).
        explorationPool.AddRange(remaining.Where(entry => !mmrSelectedIds.Contains(entry.Item.Id)));

        if (explorationPool.Count <= 0)
        {
            return;
        }

        // Callers pass Engine.ComputeStableSeed(userId, batchGenerationCounter)
        // for offline batches or Engine.ComputeStableSeed(userId, DayNumber) for live requests, so
        // exploration picks are reproducible per user/context and unit tests can pin behaviour.
        // ComputeStableSeed is used instead of System.HashCode.Combine because HashCode.Combine is
        // randomised per-process - the same (userId, day) tuple would hash to a different seed
        // after each Jellyfin restart, which would reshuffle exploration within a day and break
        // the "stable within one day" contract.
        //
        // The Random.Shared fallback is a deliberate opt-in to non-deterministic exploration
        // - callers that omit the seed argument (currently only exists for callers outside
        // the recommendation engine's own two paths, which both pass a seed) are announcing
        // they want fresh randomness on every invocation. If you introduce a new caller and
        // want reproducibility, thread a seed through instead of relying on this fallback.
        var rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        var explorationCount = Math.Min(count - selected.Count, explorationPool.Count);
        for (var e = 0; e < explorationCount; e++)
        {
            var randIdx = rng.Next(explorationPool.Count);
            selected.Add(explorationPool[randIdx]);

            var lastIdx = explorationPool.Count - 1;
            if (randIdx < lastIdx)
            {
                explorationPool[randIdx] = explorationPool[lastIdx];
            }

            explorationPool.RemoveAt(lastIdx);
        }
    }

    /// <summary>
    ///     Caches per-item genre/studio/year metadata and computes multi-dimensional similarity
    ///     between two items. Extracted verbatim from the MMR reranker so the similarity math and
    ///     its caching stay a single source of truth.
    /// </summary>
    private sealed class SimilarityCache
    {
        private readonly Dictionary<Guid, HashSet<string>> _genreSetCache = new();
        private readonly Dictionary<Guid, HashSet<string>> _studioSetCache = new();
        private readonly Dictionary<Guid, int?> _yearCache = new();

        /// <summary>
        ///     Multi-dimensional similarity between two items.
        ///     Dimensions: genre (50%), studio (30%), era (20%, Gaussian with σ=10yr).
        ///     Returns 0-1 where higher = more similar (should be diversified against).
        /// </summary>
        /// <param name="a">The first item.</param>
        /// <param name="b">The second item.</param>
        /// <returns>The similarity score in the range 0-1.</returns>
        public double Compute(BaseItem a, BaseItem b)
        {
            return ComputeItemSimilarity(
                GetOrCreateGenreSet(a),
                GetOrCreateGenreSet(b),
                GetOrCreateStudioSet(a),
                GetOrCreateStudioSet(b),
                GetOrCreateYear(a),
                GetOrCreateYear(b));
        }

        // Multi-dimensional similarity between two items.
        // Dimensions: genre (50%), studio (30%), era (20%, Gaussian with σ=10yr).
        //
        // Renormalisation policy (asymmetric - closes two opposing failure modes):
        //   * When at least one STRONG dimension (genre or studio) is available on both
        //     items, we renormalise over the actually-available weight. Sparse-metadata
        //     libraries (custom items, home videos) that carry genres but no studio/year
        //     therefore no longer cap at similarity=0.5 - a near-duplicate pair with
        //     matching genres now scores near 1.0 as expected, closing the diversity leak
        //     that motivated the renormalisation in the first place.
        //   * When ONLY year is available, we do NOT renormalise. Year alone is a very
        //     weak signal: two random films from 1995 would otherwise score
        //     0.2·yearSim / 0.2 = yearSim = 1.0 and MMR would treat them as duplicates,
        //     evicting genuinely diverse candidates. Capping the year-only case at its
        //     raw 0.2·yearSim contribution keeps era-only pairs firmly in the "probably
        //     not related" range while still contributing a small signal.
        // Returns 0-1 where higher = more similar (should be diversified against).
        private static double ComputeItemSimilarity(
            HashSet<string> genreA,
            HashSet<string> genreB,
            HashSet<string> studioA,
            HashSet<string> studioB,
            int? yearA,
            int? yearB)
        {
            var weightedSimilarity = 0.0;
            var availableWeight = 0.0;
            var hasStrongDimension = false;

            if (genreA.Count > 0 && genreB.Count > 0)
            {
                weightedSimilarity += 0.5 * SimilarityComputer.ComputeJaccardFromSets(genreA, genreB);
                availableWeight += 0.5;
                hasStrongDimension = true;
            }

            if (studioA.Count > 0 && studioB.Count > 0)
            {
                weightedSimilarity += 0.3 * SimilarityComputer.ComputeJaccardFromSets(studioA, studioB);
                availableWeight += 0.3;
                hasStrongDimension = true;
            }

            if (yearA.HasValue && yearB.HasValue)
            {
                var diff = Math.Abs((double)yearA.Value - yearB.Value);
                var yearSim = Math.Exp(-diff * diff / EngineConstants.YearProximityDenominator);
                weightedSimilarity += 0.2 * yearSim;
                availableWeight += 0.2;
            }

            // No shared dimensions -> not enough data to judge similarity, treat as unrelated.
            if (availableWeight <= 0.0)
            {
                return 0.0;
            }

            // Year-only case: skip renormalisation. Renormalising here would let two same-
            // year items score 1.0 and be treated as duplicates by MMR - production year
            // alone is a much weaker signal than genre or studio, so we return the raw
            // 0.2·yearSim contribution as an intentionally sub-1.0 similarity ceiling.
            if (!hasStrongDimension)
            {
                return weightedSimilarity;
            }

            return weightedSimilarity / availableWeight;
        }

        private HashSet<string> GetOrCreateGenreSet(BaseItem item)
        {
            if (!_genreSetCache.TryGetValue(item.Id, out var set))
            {
                set = item.Genres is { Length: > 0 }
                    ? new HashSet<string>(item.Genres, StringComparer.OrdinalIgnoreCase)
                    : [];
                _genreSetCache[item.Id] = set;
            }

            return set;
        }

        private HashSet<string> GetOrCreateStudioSet(BaseItem item)
        {
            if (!_studioSetCache.TryGetValue(item.Id, out var set))
            {
                set = item.Studios is { Length: > 0 }
                    ? new HashSet<string>(item.Studios, StringComparer.OrdinalIgnoreCase)
                    : [];
                _studioSetCache[item.Id] = set;
            }

            return set;
        }

        private int? GetOrCreateYear(BaseItem item)
        {
            if (!_yearCache.TryGetValue(item.Id, out var y))
            {
                y = item.ProductionYear;
                _yearCache[item.Id] = y;
            }

            return y;
        }
    }
}
