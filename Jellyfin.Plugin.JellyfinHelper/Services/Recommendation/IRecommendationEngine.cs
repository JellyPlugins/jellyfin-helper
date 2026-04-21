using System;
using System.Collections.Generic;
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
}