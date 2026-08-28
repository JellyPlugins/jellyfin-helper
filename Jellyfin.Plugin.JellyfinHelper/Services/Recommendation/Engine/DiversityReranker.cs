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
    /// </summary>
    internal const int MmrPoolFactor = 5;

    /// <summary>
    ///     Multiplier for the wider "exploration" candidate band. For count = 20 the widened band spans ranks 20 × MmrPoolFactor ..
    /// </summary>
    internal const int ExplorationPoolFactor = 20;

    // Precondition: result list must not contain duplicate objects (same reference).
    // bestPerSeries maps seriesId to index in result; index validity relies on no mid-loop list compaction.

    /// <summary>
    ///     Deduplicates series entries: when episodes or seasons from the same series appear as separate candidates, keeps only the highest-scored entry per series.
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
    ///     Resolves the series identifier used for deduplication from a candidate item. Episodes and seasons resolve to their parent series id; a series resolves to its own id.
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
    ///     Runs MMR-based diversity re-ranking on the top scored candidates and reserves the tail slots for exploration picks drawn from a widened low-relevance pool.
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
        // Rank the entire candidate list once and split into two disjoint pools: * mmrPool: top count·MmrPoolFactor for the diversity-aware relevance selection.
        var ranked = candidates.OrderByDescending(c => c.Score).ToList();
        var mmrPoolSize = Math.Min(ranked.Count, count * MmrPoolFactor);
        var explorationPoolSize = Math.Min(ranked.Count, count * ExplorationPoolFactor);
        var remaining = ranked.Take(mmrPoolSize).ToList();

        // Multi-dimensional similarity caches: genre (50% weight), studio (30% weight), production year (20% weight).
        var similarity = new SimilarityCache();

        // Fill most slots via MMR, reserving the last few slots for random exploration picks. This guarantees the model receives diverse feedback while keeping the list relevance-dominated.
        var proportionalCap = Math.Max(1, count / EngineConstants.ExplorationSlotDivisor);
        var explorationSlots = Math.Min(
            EngineConstants.ExplorationSlotCount,
            Math.Min(proportionalCap, Math.Max(0, count - 1)));
        var mmrSlotCount = count - explorationSlots;

        RunMmrSelection(remaining, selected, similarity, mmrSlotCount);

        // Build the disjoint exploration pool: * Start with the widened band ranks[mmrPoolSize .. explorationPoolSize].
        if (selected.Count < count)
        {
            FillExplorationSlots(ranked, remaining, selected, count, mmrPoolSize, explorationPoolSize, seed);
        }

        return selected;
    }

    /// <summary>
    ///     Greedily selects mmrSlotCount items from remaining into selected using Maximal Marginal Relevance: each pick maximises relevance minus similarity to already-selected items.
    /// </summary>
    private static void RunMmrSelection(
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> remaining,
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> selected,
        SimilarityCache similarity,
        int mmrSlotCount)
    {
        while (selected.Count < mmrSlotCount && remaining.Count > 0)
        {
            var bestIdx = FindBestMmrIndex(remaining, selected, similarity);

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
    ///     Finds the index in of the item that maximises the MMR score (relevance minus similarity to any already- item), or -1 when is empty.
    /// </summary>
    private static int FindBestMmrIndex(
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> remaining,
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> selected,
        SimilarityCache similarity)
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

        return bestIdx;
    }

    /// <summary>
    ///     Fills the reserved exploration slots (up to ) from a widened candidate band plus the MMR-pool leftovers, sampling randomly with a caller-supplied deterministic seed (or Shared when none is provided).
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

        // Fall back to the MMR-pool leftovers when the widened band is exhausted so that small libraries still get exploration signal (this preserves the previous behaviour as a floor rather than as the only source).
        explorationPool.AddRange(remaining.Where(entry => !mmrSelectedIds.Contains(entry.Item.Id)));

        if (explorationPool.Count <= 0)
        {
            return;
        }

        // Callers pass Engine.ComputeStableSeed(userId, batchGenerationCounter) for offline batches or Engine.ComputeStableSeed(userId, DayNumber) for live requests, so exploration picks are reproducible per user/context and unit tests can pin behaviour.
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
    ///     Caches per-item genre/studio/year metadata and computes multi-dimensional similarity between two items.
    /// </summary>
    private sealed class SimilarityCache
    {
        private readonly Dictionary<Guid, HashSet<string>> _genreSetCache = new();
        private readonly Dictionary<Guid, HashSet<string>> _studioSetCache = new();
        private readonly Dictionary<Guid, int?> _yearCache = new();

        /// <summary>
        ///     Multi-dimensional similarity between two items. Dimensions: genre (50%), studio (30%), era (20%, Gaussian with σ=10yr).
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

        // Multi-dimensional similarity between two items. Dimensions: genre (50%), studio (30%), era (20%, Gaussian with σ=10yr).
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

            // Year-only case: skip renormalisation.
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
