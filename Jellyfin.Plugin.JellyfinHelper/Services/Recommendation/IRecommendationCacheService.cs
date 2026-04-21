using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Persists recommendation results to disk for fast retrieval across restarts.
/// </summary>
public interface IRecommendationCacheService
{
    /// <summary>
    ///     Saves all recommendation results to disk.
    /// </summary>
    /// <param name="results">The recommendation results to persist.</param>
    void SaveResults(Collection<RecommendationResult> results);

    /// <summary>
    ///     Loads all cached recommendation results from disk.
    /// </summary>
    /// <returns>The cached results, or null if no cache exists.</returns>
    Collection<RecommendationResult>? LoadResults();
}