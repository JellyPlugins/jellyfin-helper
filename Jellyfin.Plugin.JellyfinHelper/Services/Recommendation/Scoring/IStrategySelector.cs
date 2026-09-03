using System;

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
