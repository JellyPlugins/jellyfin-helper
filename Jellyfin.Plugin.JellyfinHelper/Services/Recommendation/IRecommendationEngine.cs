using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Generates personalized recommendations based on watch history and content similarity.
/// </summary>
public interface IRecommendationEngine
{
    /// <summary>
    ///     Generates recommendations for a specific user.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="maxResults">Maximum number of recommendations to return.</param>
    /// <returns>The recommendation result, or null if the user was not found.</returns>
    RecommendationResult? GetRecommendations(Guid userId, int maxResults = 20);

    /// <summary>
    ///     Generates recommendations for all users.
    /// </summary>
    /// <param name="maxResultsPerUser">Maximum number of recommendations per user.</param>
    /// <returns>A list of recommendation results, one per user.</returns>
    Collection<RecommendationResult> GetAllRecommendations(int maxResultsPerUser = 20);

    /// <summary>
    ///     Trains the active scoring strategy using implicit feedback from previous recommendations.
    ///     Compares previously recommended items against current watch data:
    ///     items that were recommended and subsequently watched get label 1.0 (positive),
    ///     items that were recommended but remain unwatched get label 0.0 (negative).
    /// </summary>
    /// <param name="previousResults">
    ///     The recommendation results from the previous run (loaded from cache).
    /// </param>
    /// <returns>True if training was performed, false if skipped (insufficient data or unsupported strategy).</returns>
    bool TrainStrategy(Collection<RecommendationResult> previousResults);
}
