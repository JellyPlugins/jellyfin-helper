using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Shared operational maturity, bounded participation and gradual correction; not ranking verification.</summary>
internal static class OperatingMixturePolicy
{
    /// <summary>Length of the historical Neural activation ramp.</summary>
    internal const double NeuralRampExamples = 100;

    /// <summary>Computes the common global/personal maturity ceiling without counting repeated task runs.</summary>
    /// <param name="count">Distinct observed fitting titles.</param>
    /// <param name="midpoint">Configured/adapted midpoint.</param>
    /// <param name="minimum">Operational lower bound.</param>
    /// <param name="maximum">Protected ML ceiling.</param>
    /// <returns>The historical sigmoid ceiling; an unfitted cold prior starts explicitly at the minimum.</returns>
    internal static double Maturity(int count, double midpoint, double minimum, double maximum)
        => minimum + ((maximum - minimum) / (1 + Math.Exp(-EnsembleScoringStrategy.AlphaSigmoidK * (count - midpoint))));

    /// <summary>Computes Neural participation only for a trained predictor; personal scopes may borrow a small global prior.</summary>
    /// <param name="count">This scope's observed maturity, never the global count for a personal model.</param>
    /// <param name="ownsNeural">Whether this scope trains the global network.</param>
    /// <param name="available">Whether a technically valid trained predictor exists.</param>
    /// <returns>A within-ML fraction, not a title allocation.</returns>
    internal static double NeuralTarget(int count, bool ownsNeural, bool available)
    {
        if (!available)
        {
            return 0;
        }

        var progress = Math.Clamp((count - EnsembleScoringStrategy.NeuralActivationThreshold) / NeuralRampExamples, 0, 1);
        return ownsNeural
            ? EnsembleScoringStrategy.NeuralMaxBetaFraction * progress
            : EnsembleScoringStrategy.NeuralPerUserBetaFloor + ((EnsembleScoringStrategy.NeuralMaxBetaFraction - EnsembleScoringStrategy.NeuralPerUserBetaFloor) * progress);
    }

    /// <summary>Softly damps maturity using calibration-only loss, never presenting it as independent ranking proof.</summary>
    /// <param name="ceiling">The maturity ceiling.</param>
    /// <param name="minimum">The operational lower bound.</param>
    /// <param name="beta">Maturity-bounded Neural fraction.</param>
    /// <param name="calibration">Measured predictions on permanent calibration titles.</param>
    /// <returns>The bounded target.</returns>
    internal static AdmissionMixture Target(double ceiling, double minimum, double beta, EnsembleAdmissionEvaluator.ServingCalibration calibration)
    {
        var threshold = EnsembleScoringStrategy.ValidationLossThreshold;
        var quality = Math.Clamp(1 - (Math.Max(0, calibration.LearnedLoss - threshold) / threshold), 0, 1);
        var alpha = minimum + ((ceiling - minimum) * quality);
        if (calibration.NeuralLoss is > EnsembleScoringStrategy.ValidationLossThreshold)
        {
            beta *= .5;
        }

        return new AdmissionMixture(alpha * (1 - beta), alpha * beta);
    }

    /// <summary>Moves an existing provisional mixture gradually toward a target on changed evidence only.</summary>
    /// <param name="current">Current operative coefficients.</param>
    /// <param name="target">Calibration-assessed target.</param>
    /// <returns>A convex bounded adjustment; no minimum is forced onto a verified tiny mixture.</returns>
    internal static AdmissionMixture Approach(AdmissionMixture current, AdmissionMixture target)
        => new((current.Learned + target.Learned) * .5, (current.Neural + target.Neural) * .5);
}
