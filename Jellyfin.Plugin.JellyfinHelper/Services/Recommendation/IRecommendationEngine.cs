using System;
using System.Collections.Generic;
using System.Threading;

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
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The recommendation result, or null if the user was not found.</returns>
    RecommendationResult? GetRecommendations(Guid userId, int maxResults = 20, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Generates recommendations for all users.
    /// </summary>
    /// <param name="maxResultsPerUser">Maximum number of recommendations per user.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A read-only list of recommendation results, one per user.</returns>
    IReadOnlyList<RecommendationResult> GetAllRecommendations(int maxResultsPerUser = 20, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Trains the active scoring strategy using implicit feedback from previous recommendations.
    ///     Compares previously recommended items against current watch data:
    ///     items that were recommended and subsequently watched get label 0.85 (positive),
    ///     items that were recommended but remain unwatched get label 0.05 (negative/soft).
    /// </summary>
    /// <param name="previousResults">
    ///     The recommendation results from the previous run (loaded from cache).
    /// </param>
    /// <param name="incremental">
    ///     When true (requires TaskMode=Activate), only new examples since last training are fully
    ///     processed; a random sample of older examples is included to prevent catastrophic forgetting.
    ///     When false (default), all examples are used for full retraining.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the training operation.</param>
    /// <returns>True if training was performed, false if skipped (insufficient training data).</returns>
    bool TrainStrategy(IReadOnlyList<RecommendationResult> previousResults, bool incremental = false, CancellationToken cancellationToken = default);
}