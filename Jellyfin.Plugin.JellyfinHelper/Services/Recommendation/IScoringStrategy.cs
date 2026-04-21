using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Defines a pluggable scoring strategy for ranking recommendation candidates.
///     Implementations receive pre-computed feature signals and return a final score.
/// </summary>
public interface IScoringStrategy
{
    /// <summary>
    ///     Gets the human-readable name of this strategy (shown in UI).
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Gets the i18n key for the strategy name.
    /// </summary>
    string NameKey { get; }

    /// <summary>
    ///     Computes the final recommendation score for a candidate item given its feature signals.
    /// </summary>
    /// <param name="features">The pre-computed feature signals for the candidate.</param>
    /// <returns>A score between 0.0 and 1.0, where higher means more recommended.</returns>
    double Score(CandidateFeatures features);

    /// <summary>
    ///     Optional: Train/update the strategy's internal weights from labelled examples.
    ///     Strategies that do not support learning should return false.
    /// </summary>
    /// <param name="examples">Training examples (features + positive/negative label).</param>
    /// <returns>True if training was performed, false if the strategy does not support learning.</returns>
    bool Train(IReadOnlyList<TrainingExample> examples);
}