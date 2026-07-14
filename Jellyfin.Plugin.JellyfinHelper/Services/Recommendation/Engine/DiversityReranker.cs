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
            Guid? seriesId = entry.Item switch
            {
                Episode ep => ep.SeriesId != Guid.Empty ? ep.SeriesId : null,
                Season season => season.SeriesId != Guid.Empty ? season.SeriesId : null,
                Series s => s.Id != Guid.Empty ? s.Id : null,
                _ => null
            };

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
    ///     Runs MMR-based diversity re-ranking on the top scored candidates and reserves the tail
    ///     slots for exploration picks drawn from a widened low-relevance pool.
    /// </summary>
    /// <param name="candidates">The scored candidate list.</param>
    /// <param name="count">The target number of recommendations.</param>
    /// <param name="seed">
    ///     Optional deterministic seed for the exploration sampler.
    ///     Callers MUST use a process-stable hash (e.g. <c>Engine.ComputeStableSeed(userId, dayNumber)</c>
    ///     or <c>Engine.ComputeStableSeed(userId, batchGeneration)</c>) — <see cref="HashCode.Combine{T1,T2}"/>
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
        //   * mmrPool: top count·5 for the diversity-aware relevance selection.
        //   * explorationPool: everything up to count·20 excluding the mmrPool head, so exploration
        //     can inject picks from ranks 5·count … 20·count that MMR would never see.
        var ranked = candidates.OrderByDescending(c => c.Score).ToList();
        var mmrPoolSize = Math.Min(ranked.Count, count * 5);
        var explorationPoolSize = Math.Min(ranked.Count, count * 20);
        var remaining = ranked.Take(mmrPoolSize).ToList();

        // Multi-dimensional similarity caches: genre (50% weight), studio (30% weight), production year (20% weight).
        // Previously MMR looked at genres only, which meant it would happily surface two Marvel superhero
        // movies (same genre set but same studio and same era). By blending studio and era similarity
        // we now diversify along multiple axes without over-penalising true content variety.
        // Note: we intentionally do NOT diversify by people/cast because a single actor commonly
        // appears in wildly different genres (Christopher Nolan does thrillers AND sci-fi), and
        // penalising same-actor picks would exclude legitimately diverse recommendations.
        var genreSetCache = new Dictionary<Guid, HashSet<string>>();
        var studioSetCache = new Dictionary<Guid, HashSet<string>>();
        var yearCache = new Dictionary<Guid, int?>();

        HashSet<string> GetOrCreateGenreSet(BaseItem item)
        {
            if (!genreSetCache.TryGetValue(item.Id, out var set))
            {
                set = item.Genres is { Length: > 0 }
                    ? new HashSet<string>(item.Genres, StringComparer.OrdinalIgnoreCase)
                    : [];
                genreSetCache[item.Id] = set;
            }

            return set;
        }

        HashSet<string> GetOrCreateStudioSet(BaseItem item)
        {
            if (!studioSetCache.TryGetValue(item.Id, out var set))
            {
                set = item.Studios is { Length: > 0 }
                    ? new HashSet<string>(item.Studios, StringComparer.OrdinalIgnoreCase)
                    : [];
                studioSetCache[item.Id] = set;
            }

            return set;
        }

        int? GetOrCreateYear(BaseItem item)
        {
            if (!yearCache.TryGetValue(item.Id, out var y))
            {
                y = item.ProductionYear;
                yearCache[item.Id] = y;
            }

            return y;
        }

        // Multi-dimensional similarity between two items.
        // Dimensions: genre (50%), studio (30%), era (20%, Gaussian with σ=10yr).
        // Weights are renormalized over the dimensions that are actually available on
        // BOTH items. Without renormalization, two items with identical genres but no
        // studio/year metadata would cap at similarity=0.5, letting MMR treat them as
        // only "half similar" — a bug that surfaced for sparse-metadata libraries
        // (custom items, home videos) where near-duplicates could slip through diversity
        // re-ranking. Items with rich metadata (typical TMDb-sourced content) are
        // unaffected: their availableWeight always equals 1.0 so the computation is
        // bit-identical to the previous formula.
        // Returns 0-1 where higher = more similar (should be diversified against).
        static double ComputeItemSimilarity(
            HashSet<string> genreA,
            HashSet<string> genreB,
            HashSet<string> studioA,
            HashSet<string> studioB,
            int? yearA,
            int? yearB)
        {
            var weightedSimilarity = 0.0;
            var availableWeight = 0.0;

            if (genreA.Count > 0 && genreB.Count > 0)
            {
                weightedSimilarity += 0.5 * SimilarityComputer.ComputeJaccardFromSets(genreA, genreB);
                availableWeight += 0.5;
            }

            if (studioA.Count > 0 && studioB.Count > 0)
            {
                weightedSimilarity += 0.3 * SimilarityComputer.ComputeJaccardFromSets(studioA, studioB);
                availableWeight += 0.3;
            }

            if (yearA.HasValue && yearB.HasValue)
            {
                var diff = Math.Abs(yearA.Value - yearB.Value);
                var yearSim = Math.Exp(-diff * diff / EngineConstants.YearProximityDenominator);
                weightedSimilarity += 0.2 * yearSim;
                availableWeight += 0.2;
            }

            // No shared dimensions → not enough data to judge similarity, treat as unrelated.
            return availableWeight > 0.0 ? weightedSimilarity / availableWeight : 0.0;
        }

        // Fill most slots via MMR, reserving the last ExplorationSlotCount slots
        // for random exploration picks. This guarantees the model receives diverse
        // feedback even when MMR converges on a narrow genre cluster.
        // Cap exploration at count-1 so small lists still get at least one relevance-driven pick.
        var explorationSlots = Math.Min(EngineConstants.ExplorationSlotCount, Math.Max(0, count - 1));
        var mmrSlotCount = count - explorationSlots;

        while (selected.Count < mmrSlotCount && remaining.Count > 0)
        {
            var bestIdx = -1;
            var bestMmrScore = double.MinValue;

            for (var i = 0; i < remaining.Count; i++)
            {
                var relevance = remaining[i].Score;
                var candidateGenres = GetOrCreateGenreSet(remaining[i].Item);
                var candidateStudios = GetOrCreateStudioSet(remaining[i].Item);
                var candidateYear = GetOrCreateYear(remaining[i].Item);

                var maxSimilarity = 0.0;
                foreach (var selectedEntry in selected)
                {
                    var selectedGenres = GetOrCreateGenreSet(selectedEntry.Item);
                    var selectedStudios = GetOrCreateStudioSet(selectedEntry.Item);
                    var selectedYear = GetOrCreateYear(selectedEntry.Item);
                    var sim = ComputeItemSimilarity(
                        candidateGenres,
                        selectedGenres,
                        candidateStudios,
                        selectedStudios,
                        candidateYear,
                        selectedYear);
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

        // Build the disjoint exploration pool:
        //   * Start with the widened band ranks[mmrPoolSize .. explorationPoolSize].
        //   * Add the MMR-pool leftovers (not selected by MMR) so we do not lose valid picks
        //     when the widened band is smaller than the exploration slot count.
        //   * Skip anything MMR already committed to, to avoid duplicate selections.
        // This is the concrete implementation of FIX-2: exploration reaches beyond the MMR cluster.
        if (selected.Count < count)
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
            foreach (var entry in remaining)
            {
                if (!mmrSelectedIds.Contains(entry.Item.Id))
                {
                    explorationPool.Add(entry);
                }
            }

            if (explorationPool.Count > 0)
            {
                // FIX-3: seedable RNG. Callers pass Engine.ComputeStableSeed(userId, batchGenerationCounter)
                // for offline batches or Engine.ComputeStableSeed(userId, DayNumber) for live requests, so
                // exploration picks are reproducible per user/context and unit tests can pin behaviour.
                // ComputeStableSeed is used instead of System.HashCode.Combine because HashCode.Combine is
                // randomised per-process — the same (userId, day) tuple would hash to a different seed
                // after each Jellyfin restart, which would reshuffle exploration within a day and break
                // the "stable within one day" contract.
                //
                // The Random.Shared fallback is a deliberate opt-in to non-deterministic exploration
                // — callers that omit the seed argument (currently only exists for callers outside
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
        }

        return selected;
    }
}
