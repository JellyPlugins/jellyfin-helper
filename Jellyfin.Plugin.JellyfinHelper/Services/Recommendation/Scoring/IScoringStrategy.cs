using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

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
    ///     Computes the recommendation score with a detailed explanation of how each
    ///     feature contributed to the final score. Useful for debugging and transparency.
    /// </summary>
    /// <param name="features">The pre-computed feature signals for the candidate.</param>
    /// <returns>A detailed score explanation including per-feature contributions.</returns>
    ScoreExplanation ScoreWithExplanation(CandidateFeatures features);
}

/// <summary>
///     Optional interface for scoring strategies that support learning from labelled examples.
///     Separates the training concern from the scoring concern (Interface Segregation Principle).
/// </summary>
public interface ITrainableStrategy
{
    /// <summary>
    ///     Train/update the strategy's internal weights from labelled examples.
    /// </summary>
    /// <param name="examples">Training examples (features + positive/negative label).</param>
    /// <returns>True if training was performed, false if insufficient data.</returns>
    bool Train(IReadOnlyList<TrainingExample> examples);
}