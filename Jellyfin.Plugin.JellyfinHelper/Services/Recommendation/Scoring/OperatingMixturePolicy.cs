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

    /// <summary>Returns the normal within-ML floor without bypassing the global activation ramp.</summary>
    /// <param name="count">This scope's observed maturity.</param>
    /// <param name="ownsNeural">Whether this scope fits the global network.</param>
    /// <param name="available">Whether a trained, usable predictor is available.</param>
    /// <returns>The bounded serving floor, never a fixed number of recommendation slots.</returns>
    internal static double NeuralMinimum(int count, bool ownsNeural, bool available)
        => Math.Min(EnsembleScoringStrategy.NeuralPerUserBetaFloor, NeuralTarget(count, ownsNeural, available));

    /// <summary>Reserves the usable Neural floor inside the existing ML budget, not on top of it.</summary>
    /// <param name="mixture">The bounded provisional mixture.</param>
    /// <param name="minimum">The eligible within-ML floor.</param>
    /// <param name="alphaCap">The protected total ML ceiling.</param>
    /// <param name="neuralCap">The protected absolute Neural ceiling.</param>
    /// <returns>A budget-preserving mixture; a missing/unusable predictor receives no forced weight.</returns>
    internal static AdmissionMixture WithNeuralMinimum(AdmissionMixture mixture, double minimum, double alphaCap, double neuralCap)
    {
        var alpha = Math.Min(mixture.Alpha, alphaCap);
        if (alpha <= 0)
        {
            return default;
        }

        var neural = Math.Min(alpha, Math.Min(neuralCap, Math.Max(mixture.Neural, alpha * minimum)));
        if (neural / alpha < minimum)
        {
            neural = Math.Min(neuralCap, Math.BitIncrement(neural));
        }

        var learned = alpha - neural;
        if (learned + neural > alpha)
        {
            learned = Math.BitDecrement(learned);
        }

        return new AdmissionMixture(learned, neural);
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
