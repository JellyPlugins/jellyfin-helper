namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Nonnegative score-space coefficients; maturity caps absolute influence, not a compulsory Learned floor.</summary>
internal readonly record struct AdmissionMixture(double Learned, double Neural)
{
    /// <summary>Gets the operative ML share.</summary>
    internal double Alpha => Learned + Neural;

    /// <summary>Gets the operative fraction of ML assigned to Neural.</summary>
    internal double Beta => Alpha > 0 ? Neural / Alpha : 0;

    /// <summary>Gets the heuristic coefficient.</summary>
    internal double Heuristic => 1.0 - Alpha;

    /// <summary>Scores coherent component predictions using the existing ensemble blend order.</summary>
    /// <param name="scores">Component scores from the same prediction version.</param>
    /// <returns>The complete mixture score.</returns>
    internal double Score(AdmissionScores scores)
    {
        if (Alpha <= 0)
        {
            return scores.Heuristic;
        }

        var ml = Neural > 0 ? ((1.0 - Beta) * scores.Learned) + (Beta * scores.Neural) : scores.Learned;
        return (Alpha * ml) + ((1.0 - Alpha) * scores.Heuristic);
    }
}
