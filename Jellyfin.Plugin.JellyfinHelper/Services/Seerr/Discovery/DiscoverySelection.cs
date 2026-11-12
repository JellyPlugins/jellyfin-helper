using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>Selects relevant, varied personal suggestions without changing an external source's own ranking.</summary>
internal static class DiscoverySelection
{
    internal const double MinimumPersonalScore = 0.10;
    internal const int MaximumPerCollection = 2;

    /// <summary>Applies bounded genre diversity and collection saturation after final scoring.</summary>
    /// <param name="candidates">Final-scored candidates, already ordered by source rank when applicable.</param>
    /// <param name="count">Maximum pool size.</param>
    /// <param name="preserveSourceRank">Keeps Trakt's order and soft quality policy unchanged.</param>
    /// <returns>A new selection; the candidates and their scores are never modified.</returns>
    internal static List<DiscoveryRecommendation> Select(IReadOnlyList<DiscoveryRecommendation> candidates, int count, bool preserveSourceRank = false)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (count <= 0)
        {
            return [];
        }

        if (preserveSourceRank)
        {
            return candidates.Take(count).ToList();
        }

        var remaining = candidates
            .Where(candidate => !candidate.IsPersonalized || candidate.Score >= MinimumPersonalScore)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.MediaType, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TmdbId)
            .ToList();
        var selected = new List<DiscoveryRecommendation>(Math.Min(count, remaining.Count));
        var genres = remaining.ToDictionary(candidate => candidate, candidate => GenreNormalizer.Set(candidate.Genres));
        var collections = new Dictionary<int, int>();
        while (selected.Count < count && remaining.Count > 0)
        {
            var best = BestCandidate(remaining, selected, genres, collections);
            if (best < 0)
            {
                break;
            }

            var next = remaining[best];
            selected.Add(next);
            remaining.RemoveAt(best);
            if (next.TmdbCollectionId > 0)
            {
                collections[next.TmdbCollectionId] = collections.GetValueOrDefault(next.TmdbCollectionId) + 1;
            }
        }

        // Diversity determines membership, not the displayed Match order.
        return selected.OrderByDescending(candidate => candidate.Score).ToList();
    }

    private static int BestCandidate(List<DiscoveryRecommendation> remaining, List<DiscoveryRecommendation> selected, Dictionary<DiscoveryRecommendation, HashSet<string>> genres, Dictionary<int, int> collections)
    {
        var best = -1;
        var bestUtility = double.NegativeInfinity;
        for (var i = 0; i < remaining.Count; i++)
        {
            var candidate = remaining[i];
            if (candidate.TmdbCollectionId > 0 && collections.GetValueOrDefault(candidate.TmdbCollectionId) >= MaximumPerCollection)
            {
                continue;
            }

            var similarity = selected.Count == 0 ? 0.0 : selected.Max(previous => SimilarityComputer.ComputeJaccardFromSets(genres[candidate], genres[previous]));
            var utility = (0.8 * candidate.Score) - (0.2 * similarity);
            if (utility > bestUtility)
            {
                best = i;
                bestUtility = utility;
            }
        }

        return best;
    }
}
