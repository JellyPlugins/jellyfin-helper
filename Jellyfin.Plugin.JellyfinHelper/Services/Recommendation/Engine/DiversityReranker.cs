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
    ///     Applies MMR (Maximal Marginal Relevance) re-ranking to balance relevance with diversity.
    ///     Greedily selects items maximizing: λ × relevance - (1 - λ) × max_similarity_to_selected.
    /// </summary>
    /// <param name="candidates">All scored candidates.</param>
    /// <param name="count">Number of items to select.</param>
    /// <returns>The diversity-reranked top items.</returns>
    internal static List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        ApplyDiversityReranking(
            List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> candidates,
            int count)
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
        var remaining = candidates.OrderByDescending(c => c.Score).Take(count * 3).ToList();

        var genreSetCache = new Dictionary<Guid, HashSet<string>>();

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

        while (selected.Count < count && remaining.Count > 0)
        {
            var bestIdx = -1;
            var bestMmrScore = double.MinValue;

            for (var i = 0; i < remaining.Count; i++)
            {
                var relevance = remaining[i].Score;
                var candidateSet = GetOrCreateGenreSet(remaining[i].Item);

                var maxSimilarity = 0.0;
                foreach (var selectedItem in selected.Select(s => s.Item))
                {
                    var selectedSet = GetOrCreateGenreSet(selectedItem);
                    var sim = SimilarityComputer.ComputeJaccardFromSets(candidateSet, selectedSet);
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

        return selected;
    }
}