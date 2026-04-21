namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
///     Defines the available scoring strategies for the recommendation engine.
/// </summary>
public enum ScoringStrategyType
{
    /// <summary>
    ///     Fixed-weight heuristic scoring (default).
    ///     Uses hand-tuned weights for genre, collaborative, rating, recency, and year proximity.
    /// </summary>
    Heuristic = 0,

    /// <summary>
    ///     Adaptive ML scoring using a lightweight perceptron.
    ///     Learns personalized feature weights from user watch history.
    ///     Weights are persisted and improve over time.
    /// </summary>
    Learned = 1
}