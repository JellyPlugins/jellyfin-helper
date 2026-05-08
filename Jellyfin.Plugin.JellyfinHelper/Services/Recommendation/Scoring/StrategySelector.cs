using System;

#pragma warning disable SA1649 // File name should match first type name - interface and implementation co-located by design

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Selects a scoring strategy for a given user, enabling A/B testing between strategies.
///     Users are deterministically assigned to cohorts based on their user ID hash,
///     ensuring stable cohort membership across requests (same user always gets same strategy).
/// </summary>
public interface IStrategySelector
{
    /// <summary>
    ///     Selects the appropriate scoring strategy for the given user.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The strategy to use for scoring this user's recommendations.</returns>
    IScoringStrategy SelectForUser(Guid userId);

    /// <summary>
    ///     Gets the cohort name for the given user (for logging and result tagging).
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>"control" or "experiment" (or "control" when experiment is disabled).</returns>
    string GetCohortName(Guid userId);
}

/// <summary>
///     Default implementation of <see cref="IStrategySelector"/> that splits users into
///     control (ensemble) and experiment (neural-only) cohorts based on a configurable percentage.
///     When <see cref="_experimentPercentage"/> is 0, all users get the ensemble strategy (no A/B test).
/// </summary>
/// <remarks>
///     Bucketing uses a deterministic hash of the user ID so that:
///     1. The same user always lands in the same cohort (no flickering between requests).
///     2. No persistent state is needed (stateless computation).
///     3. Cohort assignment survives server restarts.
/// </remarks>
internal sealed class StrategySelector : IStrategySelector
{
    private readonly EnsembleScoringStrategy _ensemble;
    private readonly NeuralScoringStrategy _neural;
    private readonly int _experimentPercentage;

    /// <summary>
    ///     Initializes a new instance of the <see cref="StrategySelector"/> class.
    /// </summary>
    /// <param name="ensemble">The ensemble strategy (control group).</param>
    /// <param name="neural">The neural-only strategy (experiment group).</param>
    /// <param name="experimentPercentage">
    ///     Percentage of users (0-100) to route to the neural-only experiment cohort.
    ///     0 = disabled (all users get ensemble). 100 = all users get neural-only.
    /// </param>
    public StrategySelector(
        EnsembleScoringStrategy ensemble,
        NeuralScoringStrategy neural,
        int experimentPercentage)
    {
        ArgumentNullException.ThrowIfNull(ensemble);
        ArgumentNullException.ThrowIfNull(neural);

        _ensemble = ensemble;
        _neural = neural;
        _experimentPercentage = Math.Clamp(experimentPercentage, 0, 100);
    }

    /// <inheritdoc />
    public IScoringStrategy SelectForUser(Guid userId)
    {
        if (_experimentPercentage <= 0)
        {
            return _ensemble;
        }

        if (_experimentPercentage >= 100)
        {
            return _neural;
        }

        var bucket = ComputeBucket(userId);
        return bucket < _experimentPercentage ? _neural : _ensemble;
    }

    /// <inheritdoc />
    public string GetCohortName(Guid userId)
    {
        if (_experimentPercentage <= 0)
        {
            return "control";
        }

        if (_experimentPercentage >= 100)
        {
            return "experiment";
        }

        var bucket = ComputeBucket(userId);
        return bucket < _experimentPercentage ? "experiment" : "control";
    }

    /// <summary>
    ///     Computes a deterministic bucket (0-99) for a user ID.
    ///     Uses Guid.GetHashCode which is deterministic within a process lifetime
    ///     and across .NET versions for the same GUID value.
    ///     Uses unsigned cast to handle int.MinValue edge case without Math.Abs overflow.
    /// </summary>
    private static int ComputeBucket(Guid userId)
    {
        // Use unsigned cast to avoid Math.Abs(int.MinValue) overflow
        var hash = (uint)userId.GetHashCode();
        return (int)(hash % 100);
    }
}