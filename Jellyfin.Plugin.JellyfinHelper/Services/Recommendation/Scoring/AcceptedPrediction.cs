using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>One pinned component/mix version; neither a later global refresh nor a disposed trainer changes it.</summary>
internal sealed class AcceptedPrediction
{
    private readonly HeuristicScoringStrategy _heuristic;

    /// <summary>Initializes a new instance of the <see cref="AcceptedPrediction"/> class with coherent predictors.</summary>
    /// <param name="heuristic">The immutable unpenalized heuristic.</param>
    /// <param name="learned">Pinned linear inference.</param>
    /// <param name="neural">Pinned neural inference, when present.</param>
    /// <param name="mixture">The independently accepted score-space mixture.</param>
    /// <param name="neuralCap">The absolute Neural ceiling at acceptance.</param>
    internal AcceptedPrediction(HeuristicScoringStrategy heuristic, LearnedScoringStrategy.PredictionSnapshot learned, NeuralScoringStrategy.PredictionSnapshot? neural, AdmissionMixture mixture, double neuralCap)
    {
        _heuristic = heuristic;
        Learned = learned;
        Neural = neural;
        Mixture = mixture;
        NeuralCap = neuralCap;
    }

    /// <summary>Gets pinned learned coordinates.</summary>
    internal LearnedScoringStrategy.PredictionSnapshot Learned { get; }

    /// <summary>Gets the exact Neural version validated for this scope.</summary>
    internal NeuralScoringStrategy.PredictionSnapshot? Neural { get; }

    /// <summary>Gets the accepted coefficients before a later configuration ceiling reduction.</summary>
    internal AdmissionMixture Mixture { get; }

    /// <summary>Gets the accepted absolute Neural ceiling.</summary>
    internal double NeuralCap { get; }

    /// <summary>Conservatively applies a new configuration cap without raising tiny admitted coefficients to alpha-min.</summary>
    /// <param name="alphaMax">The current configured ML ceiling.</param>
    /// <returns>The operative coefficients under current bounds.</returns>
    internal AdmissionMixture Resolve(double alphaMax)
    {
        var scale = Mixture.Alpha > alphaMax ? alphaMax / Mixture.Alpha : 1;
        var learned = Mixture.Learned * scale;
        var neural = Neural is null ? 0 : Math.Min(Mixture.Neural * scale, Math.Min(NeuralCap, alphaMax * EnsembleScoringStrategy.NeuralMaxBetaFraction));
        return new AdmissionMixture(learned, neural);
    }

    /// <summary>Scores the pinned model with current configuration's unchanged final genre penalty.</summary>
    /// <param name="features">Prediction-time candidate inputs.</param>
    /// <param name="alphaMax">The current ML ceiling.</param>
    /// <param name="genrePenaltyFloor">The configured final genre penalty floor.</param>
    /// <returns>The actual effective score.</returns>
    internal double Score(CandidateFeatures features, double alphaMax, double genrePenaltyFloor)
    {
        ArgumentNullException.ThrowIfNull(features);
        var mixture = Resolve(alphaMax);
        var h = _heuristic.Score(features);
        if (mixture.Alpha <= 0)
        {
            return h * ScoringHelper.ComputeGenrePenalty(features, genrePenaltyFloor);
        }

        var l = Learned.Score(features);
        var n = mixture.Neural > 0 ? Neural!.Score(features) : 0;
        return mixture.Score(new AdmissionScores(h, l, n, 0)) * ScoringHelper.ComputeGenrePenalty(features, genrePenaltyFloor);
    }
}
