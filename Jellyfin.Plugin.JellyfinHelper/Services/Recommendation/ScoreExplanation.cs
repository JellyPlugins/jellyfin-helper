namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Provides a detailed breakdown of how a recommendation score was computed.
///     Used for debugging, logging, and UI transparency.
/// </summary>
public sealed class ScoreExplanation
{
    /// <summary>Gets or sets the final blended score (0–1).</summary>
    public double FinalScore { get; set; }

    /// <summary>Gets or sets the score contribution from genre similarity.</summary>
    public double GenreContribution { get; set; }

    /// <summary>Gets or sets the score contribution from collaborative filtering.</summary>
    public double CollaborativeContribution { get; set; }

    /// <summary>Gets or sets the score contribution from community rating.</summary>
    public double RatingContribution { get; set; }

    /// <summary>Gets or sets the score contribution from recency.</summary>
    public double RecencyContribution { get; set; }

    /// <summary>Gets or sets the score contribution from year proximity.</summary>
    public double YearProximityContribution { get; set; }

    /// <summary>Gets or sets the score contribution from user personal rating.</summary>
    public double UserRatingContribution { get; set; }

    /// <summary>Gets or sets the score contribution from interaction terms.</summary>
    public double InteractionContribution { get; set; }

    /// <summary>Gets or sets the genre penalty multiplier applied (1.0 = no penalty).</summary>
    public double GenrePenaltyMultiplier { get; set; } = 1.0;

    /// <summary>Gets or sets the name of the dominant signal (highest contribution).</summary>
    public string DominantSignal { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the strategy that produced this score.</summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>
    ///     Returns a compact debug-friendly string representation.
    /// </summary>
    public override string ToString()
    {
        return $"[{StrategyName}] score={FinalScore:F4} " +
               $"(genre={GenreContribution:F3}, collab={CollaborativeContribution:F3}, " +
               $"rating={RatingContribution:F3}, recency={RecencyContribution:F3}, " +
               $"yearProx={YearProximityContribution:F3}, userRating={UserRatingContribution:F3}, " +
               $"interact={InteractionContribution:F3}, penalty={GenrePenaltyMultiplier:F2}) " +
               $"dominant={DominantSignal}";
    }
}