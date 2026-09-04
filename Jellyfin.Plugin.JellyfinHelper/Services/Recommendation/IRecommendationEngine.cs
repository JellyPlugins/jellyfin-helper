using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

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

    /// <summary>
    ///     Gets a read-only snapshot of the active ensemble scoring strategy's live internal state (alpha, neural beta,
    ///     quality-gate freeze, sigmoid midpoint, trend, and training counts) for operator diagnostics.
    /// </summary>
    /// <returns>An <see cref="EnsembleDiagnostics"/> snapshot, or null when the active strategy is not an ensemble.</returns>
    EnsembleDiagnostics? GetEnsembleDiagnostics();

    /// <summary>
    ///     Gets a diagnostics snapshot for a specific user's model: the per-user ensemble's state when the
    ///     user has a dedicated model, otherwise the global ensemble's state (cold-start fallback).
    /// </summary>
    /// <param name="userId">The user whose model state to snapshot.</param>
    /// <returns>An <see cref="EnsembleDiagnostics"/> snapshot, or null when the active strategy is not an ensemble.</returns>
    EnsembleDiagnostics? GetEnsembleDiagnostics(Guid userId);

    /// <summary>
    ///     Returns whether the given user currently has a dedicated, individually-trained model (as opposed to
    ///     falling back to the shared global model). Used to label diagnostics honestly.
    /// </summary>
    /// <param name="userId">The user to check.</param>
    /// <returns><see langword="true"/> when the user has a per-user model; otherwise <see langword="false"/>.</returns>
    bool HasPerUserModel(Guid userId);
}