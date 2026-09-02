using System;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Response for GET /JellyfinHelper/Recommendations/Diagnostics/Ensemble. Read-only view of the ensemble
///     scoring strategy's live adaptive state so operators can see which mechanism is currently active.
/// </summary>
public sealed class EnsembleDiagnosticsResponse
{
    /// <summary>
    ///     Gets or sets a value indicating whether ensemble diagnostics are available. False when the active
    ///     strategy is not an ensemble or no state has been produced yet; the remaining fields carry defaults.
    /// </summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets the current blending factor alpha (ML weight; (1 - alpha) is the heuristic weight).</summary>
    public double Alpha { get; set; }

    /// <summary>Gets or sets the current neural blending factor beta (fraction of the ML budget allocated to the neural strategy).</summary>
    public double NeuralBeta { get; set; }

    /// <summary>Gets or sets a value indicating whether alpha progression is currently frozen by the quality gate.</summary>
    public bool QualityGateFrozen { get; set; }

    /// <summary>Gets or sets the adaptive sigmoid midpoint offset (negative trusts ML sooner, positive is more conservative).</summary>
    public double SigmoidMidpointOffset { get; set; }

    /// <summary>Gets or sets the effective sigmoid midpoint (default midpoint plus offset).</summary>
    public double EffectiveSigmoidMidpoint { get; set; }

    /// <summary>Gets or sets the detected training-quality trend as its string name (e.g. "Improving", "Stable").</summary>
    public string Trend { get; set; } = EnsembleScoringStrategy.MetricsTrend.InsufficientData.ToString();

    /// <summary>Gets or sets the cumulative number of training examples seen so far.</summary>
    public int TrainingExampleCount { get; set; }

    /// <summary>Gets or sets the number of metrics snapshots currently retained in the rolling history.</summary>
    public int MetricsHistoryCount { get; set; }

    /// <summary>Gets or sets the minimum blending factor alpha (heuristic-dominant lower bound).</summary>
    public double AlphaMin { get; set; }

    /// <summary>Gets or sets the maximum blending factor alpha (ML-dominant upper bound).</summary>
    public double AlphaMax { get; set; }

    /// <summary>Gets or sets a value indicating whether a neural sub-strategy was constructed for this ensemble.</summary>
    public bool NeuralEnabled { get; set; }

    /// <summary>
    ///     Maps an <see cref="EnsembleDiagnostics"/> snapshot to a populated response with <see cref="Available"/> set to true.
    /// </summary>
    /// <param name="diagnostics">The ensemble diagnostics snapshot to map.</param>
    /// <returns>A populated <see cref="EnsembleDiagnosticsResponse"/>.</returns>
    public static EnsembleDiagnosticsResponse FromDiagnostics(EnsembleDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new EnsembleDiagnosticsResponse
        {
            Available = true,
            Alpha = diagnostics.Alpha,
            NeuralBeta = diagnostics.NeuralBeta,
            QualityGateFrozen = diagnostics.QualityGateFrozen,
            SigmoidMidpointOffset = diagnostics.SigmoidMidpointOffset,
            EffectiveSigmoidMidpoint = diagnostics.EffectiveSigmoidMidpoint,
            Trend = diagnostics.Trend.ToString(),
            TrainingExampleCount = diagnostics.TrainingExampleCount,
            MetricsHistoryCount = diagnostics.MetricsHistoryCount,
            AlphaMin = diagnostics.AlphaMin,
            AlphaMax = diagnostics.AlphaMax,
            NeuralEnabled = diagnostics.NeuralEnabled
        };
    }
}
