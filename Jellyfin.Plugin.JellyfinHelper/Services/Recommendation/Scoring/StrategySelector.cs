using System;

#pragma warning disable SA1649 // File name should match first type name - interface and implementation co-located by design

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Selects alpha-exploration offsets for users, enabling automatic calibration of the ensemble's sigmoid midpoint via cohort-based A/B testing.
/// </summary>
public interface IStrategySelector
{
    /// <summary>
    ///     Gets the alpha offset for the given user's exploration cohort. Returns 0.0 for the control group (80%), +0.12 for explore-high (10%), and -0.12 for explore-low (10%).
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The alpha offset to apply during scoring.</returns>
    double GetAlphaOffset(Guid userId);

    /// <summary>
    ///     Gets the cohort name for the given user (for logging and result tagging).
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>"control", "explore-high", or "explore-low".</returns>
    string GetCohortName(Guid userId);
}

/// <summary>
///     Default implementation of IStrategySelector that splits users into alpha-exploration cohorts for automatic sigmoid midpoint calibration.
/// </summary>
/// <remarks>
///     Bucketing uses a deterministic hash of the user ID so that: 1. The same user always lands in the same cohort (no flickering between requests).
/// </remarks>
internal sealed class StrategySelector : IStrategySelector
{
    /// <summary>
    ///     Alpha offset applied to the explore-high cohort.
    ///     Positive offset shifts toward more ML weight.
    /// </summary>
    internal const double ExploreHighOffset = 0.12;

    /// <summary>
    ///     Alpha offset applied to the explore-low cohort.
    ///     Negative offset shifts toward more heuristic weight.
    /// </summary>
    internal const double ExploreLowOffset = -0.12;

    /// <summary>
    ///     Minimum cumulative training examples before exploration activates. Below this threshold, the sigmoid curve is in its early flat region and exploration would not yield meaningful signal.
    /// </summary>
    internal const int MinExamplesForExploration = 50;

    /// <summary>
    ///     Minimum metrics history snapshots (completed training runs) before exploration activates.
    /// </summary>
    internal const int MinMetricsHistoryForExploration = 2;

    /// <summary>
    ///     Bucket threshold for explore-high cohort (0-9 = 10%).
    /// </summary>
    private const int ExploreHighThreshold = 10;

    /// <summary>
    ///     Bucket threshold for explore-low cohort (10-19 = 10%).
    /// </summary>
    private const int ExploreLowThreshold = 20;

    private readonly EnsembleScoringStrategy _ensemble;

    /// <summary>
    ///     Initializes a new instance of the <see cref="StrategySelector"/> class.
    /// </summary>
    /// <param name="ensemble">The ensemble strategy used to check activation conditions.</param>
    public StrategySelector(EnsembleScoringStrategy ensemble)
    {
        ArgumentNullException.ThrowIfNull(ensemble);
        _ensemble = ensemble;
    }

    /// <inheritdoc />
    public double GetAlphaOffset(Guid userId)
    {
        if (!IsExplorationActive())
        {
            return 0.0;
        }

        var bucket = ComputeBucket(userId);

        if (bucket < ExploreHighThreshold)
        {
            return ExploreHighOffset;
        }

        if (bucket < ExploreLowThreshold)
        {
            return ExploreLowOffset;
        }

        return 0.0;
    }

    /// <inheritdoc />
    public string GetCohortName(Guid userId)
    {
        if (!IsExplorationActive())
        {
            return "control";
        }

        var bucket = ComputeBucket(userId);

        if (bucket < ExploreHighThreshold)
        {
            return "explore-high";
        }

        if (bucket < ExploreLowThreshold)
        {
            return "explore-low";
        }

        return "control";
    }

    /// <summary>
    ///     Determines whether exploration is active based on the ensemble's training maturity.
    /// </summary>
    private bool IsExplorationActive()
    {
        return _ensemble.TrainingExampleCount >= MinExamplesForExploration
               && _ensemble.MetricsHistoryCount >= MinMetricsHistoryForExploration;
    }

    /// <summary>
    ///     Computes a deterministic bucket (0-99) for a user ID. Uses XOR-fold over the raw 16 Guid bytes to produce a stable hash that is independent of the .NET runtime's Guid.GetHashCode implementation (which has changed historically between .NET versions).
    /// </summary>
    private static int ComputeBucket(Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes);

        // XOR-fold 16 bytes into a single uint (4 chunks of 4 bytes)
        var hash = BitConverter.ToUInt32(bytes[..4])
                   ^ BitConverter.ToUInt32(bytes[4..8])
                   ^ BitConverter.ToUInt32(bytes[8..12])
                   ^ BitConverter.ToUInt32(bytes[12..16]);

        return (int)(hash % 100);
    }
}