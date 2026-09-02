namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Immutable point-in-time snapshot of the <see cref="EnsembleScoringStrategy"/>'s live internal state,
///     captured under a single lock so callers see a coherent view rather than a torn read across the
///     per-field getters. Read-only diagnostics: it never influences scoring, training, or persistence.
/// </summary>
public sealed record EnsembleDiagnostics
{
    /// <summary>
    ///     Gets the current blending factor α (weight of the ML strategies; (1 - α) is the heuristic weight).
    /// </summary>
    public double Alpha { get; init; }

    /// <summary>
    ///     Gets the current neural blending factor β (fraction of the ML budget allocated to the neural strategy).
    /// </summary>
    public double NeuralBeta { get; init; }

    /// <summary>
    ///     Gets a value indicating whether alpha progression is currently frozen by the validation-loss quality gate.
    /// </summary>
    public bool QualityGateFrozen { get; init; }

    /// <summary>
    ///     Gets the adaptive sigmoid midpoint offset. Negative values shift the midpoint earlier (ML trusted sooner),
    ///     positive values shift it later (more conservative).
    /// </summary>
    public double SigmoidMidpointOffset { get; init; }

    /// <summary>
    ///     Gets the effective sigmoid midpoint (default midpoint plus <see cref="SigmoidMidpointOffset"/>).
    /// </summary>
    public double EffectiveSigmoidMidpoint { get; init; }

    /// <summary>
    ///     Gets the trend detected from the current rolling metrics history.
    /// </summary>
    public EnsembleScoringStrategy.MetricsTrend Trend { get; init; }

    /// <summary>
    ///     Gets the cumulative number of training examples seen so far.
    /// </summary>
    public int TrainingExampleCount { get; init; }

    /// <summary>
    ///     Gets the number of metrics snapshots currently retained in the rolling history.
    /// </summary>
    public int MetricsHistoryCount { get; init; }

    /// <summary>
    ///     Gets the minimum blending factor α (heuristic-dominant lower bound).
    /// </summary>
    public double AlphaMin { get; init; }

    /// <summary>
    ///     Gets the maximum blending factor α (ML-dominant upper bound).
    /// </summary>
    public double AlphaMax { get; init; }

    /// <summary>
    ///     Gets a value indicating whether a neural sub-strategy was constructed for this ensemble.
    /// </summary>
    public bool NeuralEnabled { get; init; }
}
