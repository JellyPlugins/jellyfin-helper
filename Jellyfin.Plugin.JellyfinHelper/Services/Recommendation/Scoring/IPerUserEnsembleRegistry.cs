using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Holds one <see cref="EnsembleScoringStrategy"/> per user, so each user gets individually trained
///     learned weights and individual blend factors (α, β, example count, quality gate, trend, midpoint)
///     while sharing one globally-trained neural MLP by reference.
/// </summary>
/// <remarks>
///     A user with too few training examples (below <see cref="EnsembleScoringStrategy"/>'s learned-model
///     minimum) has no per-user model; the registry returns the global ensemble as a cold-start fallback
///     so their scores are byte-identical to the previous global-only behaviour.
/// </remarks>
public interface IPerUserEnsembleRegistry : IDisposable
{
    /// <summary>
    ///     Gets the global (shared) ensemble. Trained on the pooled examples of all users. It is the
    ///     cold-start fallback, the warm-start source for new per-user models, and the owner of the shared
    ///     neural sub-strategy's lifetime.
    /// </summary>
    EnsembleScoringStrategy GlobalEnsemble { get; }

    /// <summary>
    ///     Returns the scoring strategy to use for a given user at score time: the user's per-user ensemble
    ///     when one exists (a persisted per-user weights file is present or already loaded), otherwise the
    ///     global ensemble. Never creates an empty per-user model on this read path.
    /// </summary>
    /// <param name="userId">The user to score for.</param>
    /// <returns>The user's ensemble, or the global ensemble for cold-start users.</returns>
    IScoringStrategy GetScoringStrategyForUser(Guid userId);

    /// <summary>
    ///     Returns the concrete <see cref="EnsembleScoringStrategy"/> for a user (per-user when it exists,
    ///     else the global ensemble). Used by the cohort-offset and diagnostics paths that need the concrete
    ///     type rather than the <see cref="IScoringStrategy"/> abstraction.
    /// </summary>
    /// <param name="userId">The user to resolve.</param>
    /// <returns>The user's ensemble, or the global ensemble for cold-start users.</returns>
    EnsembleScoringStrategy GetEnsembleForUser(Guid userId);

    /// <summary>
    ///     Returns the user's per-user ensemble, creating and warm-starting it from the global model on first
    ///     use. Called only from the training path once a user has crossed the per-user data threshold; the
    ///     score path never creates models.
    /// </summary>
    /// <param name="userId">The user to train a per-user model for.</param>
    /// <returns>The user's trainable per-user ensemble.</returns>
    EnsembleScoringStrategy GetOrCreateTrainableEnsembleForUser(Guid userId);

    /// <summary>
    ///     Returns a diagnostics snapshot for a user: the per-user ensemble's state when one exists, else the
    ///     global ensemble's state.
    /// </summary>
    /// <param name="userId">The user to snapshot.</param>
    /// <returns>The diagnostics snapshot.</returns>
    EnsembleDiagnostics GetDiagnostics(Guid userId);

    /// <summary>
    ///     Resolves the user's ensemble once and returns both its diagnostics snapshot and whether that
    ///     ensemble is a dedicated per-user model (as opposed to the shared global fallback). Preferred over
    ///     calling <see cref="GetDiagnostics"/> and <see cref="HasPerUserModel"/> separately, which can
    ///     disagree if the user's model appears between the two calls.
    /// </summary>
    /// <param name="userId">The user to snapshot.</param>
    /// <returns>The diagnostics snapshot paired with the per-user flag.</returns>
    (EnsembleDiagnostics Diagnostics, bool IsPerUser) GetUserModelDiagnostics(Guid userId);

    /// <summary>
    ///     Returns whether the given user currently has a dedicated per-user model (a persisted per-user
    ///     weights file exists or one is already materialized this session). False means the user falls back
    ///     to the global model. Used by diagnostics to label the snapshot honestly.
    /// </summary>
    /// <param name="userId">The user to check.</param>
    /// <returns><see langword="true"/> when a per-user model exists; otherwise <see langword="false"/>.</returns>
    bool HasPerUserModel(Guid userId);

    /// <summary>
    ///     Deletes per-user model/state files (and evicts cached instances) for users that no longer exist,
    ///     reconciling persisted files against the live user list. Best-effort: it never throws.
    /// </summary>
    /// <param name="liveUserIds">The set of user ids that currently exist.</param>
    void PruneOrphans(IReadOnlyCollection<Guid> liveUserIds);
}
