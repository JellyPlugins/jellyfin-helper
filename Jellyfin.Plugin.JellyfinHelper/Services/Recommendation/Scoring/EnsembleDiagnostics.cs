using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Immutable point-in-time snapshot of the <see cref="EnsembleScoringStrategy"/>'s live internal state,
///     captured under a single lock so callers see a coherent view rather than a torn read across the
///     per-field getters. Reading it never mutates scoring, training, or persistence state.
/// </summary>
public sealed record EnsembleDiagnostics
{
    /// <summary>
    ///     Gets the stored blending target α, before quality suppression and user exploration.
    /// </summary>
    public double Alpha { get; init; }

    /// <summary>Gets the selected user's requested exploration offset (zero for an unscoped snapshot).</summary>
    public double AlphaOffset { get; init; }

    /// <summary>Gets the separately admitted score-space ML budget, when the independent lifecycle owns this model.</summary>
    public double? AdmittedAlpha { get; init; }

    /// <summary>Gets the separately admitted within-ML Neural fraction.</summary>
    public double? AdmittedNeuralBeta { get; init; }

    /// <summary>Gets or initializes a value indicating whether legacy exploration offsets are applicable to this scoring version.</summary>
    public bool AllowsExploration { get; init; } = true;

    /// <summary>Gets the accepted compound-model revision, or zero for a legacy checkpoint.</summary>
    public long ModelRevision { get; init; }

    /// <summary>Gets the operating evidence level, distinguishing bounded provisional serving from verified ranking.</summary>
    public string ValidationLevel { get; init; } = "Legacy";

    /// <summary>Gets the latest independent admission/retention reason, when this lifecycle has evaluated a challenger.</summary>
    public string? AdmissionReason { get; init; }

    /// <summary>Gets unique titles retained for coefficient calibration.</summary>
    public int CalibrationTitleCount { get; init; }

    /// <summary>Gets unique fresh titles available for independent acceptance.</summary>
    public int AcceptanceTitleCount { get; init; }

    /// <summary>Gets the candidate's measured acceptance loss, or null for unavailable evidence.</summary>
    public double? CandidateValidationLoss { get; init; }

    /// <summary>Gets the operative ML budget after quality suppression and offset clamping.</summary>
    public double EffectiveAlpha => QualityGateFrozen ? 0 : AdmittedAlpha ?? ResolveEffectiveAlpha(Alpha, AlphaOffset, AlphaMin, AlphaMax, false);

    /// <summary>Gets the operative fraction of ML assigned to Neural, or zero without that strategy.</summary>
    public double EffectiveNeuralBeta => NeuralEnabled ? AdmittedNeuralBeta ?? NeuralBeta : 0.0;

    /// <summary>Gets the heuristic coefficient in the current scoring mixture.</summary>
    public double HeuristicShare => 1.0 - EffectiveAlpha;

    /// <summary>Gets the Learned coefficient in the current scoring mixture.</summary>
    public double LearnedShare => EffectiveAlpha * (1.0 - EffectiveNeuralBeta);

    /// <summary>Gets the Neural coefficient in the current scoring mixture.</summary>
    public double NeuralShare => EffectiveAlpha * EffectiveNeuralBeta;

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

    /// <summary>Gets a value indicating whether this snapshot has personal fitting coordinates rather than inherited priors.</summary>
    internal bool HasOwnOperatingModel { get; init; }

    /// <summary>Resolves the same frozen and near-zero-offset rules used by scoring.</summary>
    /// <param name="alpha">The stored target.</param>
    /// <param name="offset">The user exploration offset.</param>
    /// <param name="minimum">The configured lower bound for nonzero exploration.</param>
    /// <param name="maximum">The configured upper bound.</param>
    /// <param name="frozen">Whether quality suppression disables ML.</param>
    /// <returns>The effective ML coefficient.</returns>
    internal static double ResolveEffectiveAlpha(double alpha, double offset, double minimum, double maximum, bool frozen)
    {
        if (frozen)
        {
            return 0.0;
        }

        return Math.Abs(offset) < 1e-10 ? alpha : Math.Clamp(alpha + offset, minimum, maximum);
    }
}
