using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     The three blend bounds a per-user ensemble is constructed with: the minimum and maximum blending
///     factor and the genre-penalty floor. Grouped so the registry takes one cohesive value instead of three
///     loose scalars that always travel together.
/// </summary>
/// <param name="AlphaMin">Minimum blending factor.</param>
/// <param name="AlphaMax">Maximum blending factor.</param>
/// <param name="GenrePenaltyFloor">Genre penalty floor.</param>
public readonly record struct EnsembleBlendBounds(double AlphaMin, double AlphaMax, double GenrePenaltyFloor)
{
    /// <summary>
    ///     Reads the blend bounds from plugin configuration, falling back to the ensemble defaults for any
    ///     value the configuration does not supply. This is the same resolution the global ensemble uses, so
    ///     per-user ensembles share its bounds.
    /// </summary>
    /// <param name="config">The plugin configuration, or null when unavailable.</param>
    /// <returns>The resolved blend bounds.</returns>
    public static EnsembleBlendBounds FromConfiguration(PluginConfiguration? config) => new(
        config?.EnsembleAlphaMin ?? EnsembleScoringStrategy.DefaultAlphaMin,
        config?.EnsembleAlphaMax ?? EnsembleScoringStrategy.DefaultAlphaMax,
        config?.EnsembleGenrePenaltyFloor ?? EnsembleScoringStrategy.DefaultGenrePenaltyFloor);
}
