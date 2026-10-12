using System;
using System.Globalization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Provides a detailed breakdown of how a recommendation score was computed.
///     Used for debugging, logging, and UI transparency.
/// </summary>
public sealed class ScoreExplanation
{
    /// <summary>
    ///     Tolerance for determining if a contribution is effectively zero.
    ///     Values below this threshold are treated as "no signal".
    /// </summary>
    private const double DominantSignalTolerance = 1e-12;

    /// <summary>Gets or sets the final blended score (0-1).</summary>
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

    /// <summary>Gets or sets the score contribution from people similarity (actors/directors).</summary>
    public double PeopleContribution { get; set; }

    /// <summary>Gets or sets the score contribution from studio match.</summary>
    public double StudioContribution { get; set; }

    /// <summary>Gets or sets the score contribution from franchise (TMDb collection) affinity.</summary>
    public double FranchiseContribution { get; set; }

    /// <summary>Gets or sets the score contribution from Jellyfin box-set collection progression.</summary>
    public double CollectionProgressionContribution { get; set; }

    /// <summary>Gets or sets the score contribution from global popularity (how many users have watched this item).</summary>
    public double PopularityContribution { get; set; }

    /// <summary>Gets or sets the genre penalty multiplier applied (1.0 = no penalty).</summary>
    public double GenrePenaltyMultiplier { get; set; } = 1.0;

    /// <summary>Gets or sets the name of the dominant signal (highest contribution).</summary>
    public string DominantSignal { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the strategy that produced this score.</summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>
    ///     Blends this explanation with another using a linear interpolation factor.
    ///     Result = (1 - alpha) Ã- this + alpha Ã- other.
    /// </summary>
    /// <param name="other">The other explanation to blend with.</param>
    /// <param name="alpha">The blending factor (0 = 100% this, 1 = 100% other).</param>
    /// <returns>A new blended explanation.</returns>
    public ScoreExplanation Blend(ScoreExplanation other, double alpha)
    {
        ArgumentNullException.ThrowIfNull(other);
        alpha = double.IsFinite(alpha) ? Math.Clamp(alpha, 0.0, 1.0) : 0.0;

        var oneMinusAlpha = 1.0 - alpha;
        var blendedGenre = (oneMinusAlpha * GenreContribution) + (alpha * other.GenreContribution);
        var blendedCollab = (oneMinusAlpha * CollaborativeContribution) + (alpha * other.CollaborativeContribution);
        var blendedRating = (oneMinusAlpha * RatingContribution) + (alpha * other.RatingContribution);
        var blendedRecency = (oneMinusAlpha * RecencyContribution) + (alpha * other.RecencyContribution);
        var blendedYearProx = (oneMinusAlpha * YearProximityContribution) + (alpha * other.YearProximityContribution);
        var blendedUserRating = (oneMinusAlpha * UserRatingContribution) + (alpha * other.UserRatingContribution);
        var blendedInteraction = (oneMinusAlpha * InteractionContribution) + (alpha * other.InteractionContribution);
        var blendedPeople = (oneMinusAlpha * PeopleContribution) + (alpha * other.PeopleContribution);
        var blendedStudio = (oneMinusAlpha * StudioContribution) + (alpha * other.StudioContribution);
        var blendedFranchise = (oneMinusAlpha * FranchiseContribution) + (alpha * other.FranchiseContribution);
        var blendedCollection = (oneMinusAlpha * CollectionProgressionContribution) + (alpha * other.CollectionProgressionContribution);
        var blendedPopularity = (oneMinusAlpha * PopularityContribution) + (alpha * other.PopularityContribution);

        return new ScoreExplanation
        {
            FinalScore = (oneMinusAlpha * FinalScore) + (alpha * other.FinalScore),
            GenreContribution = blendedGenre,
            CollaborativeContribution = blendedCollab,
            RatingContribution = blendedRating,
            RecencyContribution = blendedRecency,
            YearProximityContribution = blendedYearProx,
            UserRatingContribution = blendedUserRating,
            InteractionContribution = blendedInteraction,
            PeopleContribution = blendedPeople,
            StudioContribution = blendedStudio,
            FranchiseContribution = blendedFranchise,
            CollectionProgressionContribution = blendedCollection,
            PopularityContribution = blendedPopularity,
            GenrePenaltyMultiplier = (oneMinusAlpha * GenrePenaltyMultiplier) + (alpha * other.GenrePenaltyMultiplier),
            DominantSignal = DetermineDominantSignal(
                blendedGenre,
                blendedCollab,
                blendedRating,
                blendedUserRating,
                blendedRecency,
                blendedYearProx,
                blendedInteraction,
                blendedPeople,
                blendedStudio,
                blendedFranchise,
                blendedCollection,
                blendedPopularity),
            StrategyName = StrategyName // Caller can override
        };
    }

    /// <summary>
    ///     Applies a genre penalty multiplier to all contribution values and the final score.
    ///     Returns a new explanation with the penalty applied.
    /// </summary>
    /// <param name="penaltyMultiplier">The penalty multiplier (0-1).</param>
    /// <returns>A new explanation with all values scaled by the penalty.</returns>
    public ScoreExplanation WithPenalty(double penaltyMultiplier)
    {
        penaltyMultiplier = double.IsFinite(penaltyMultiplier) ? Math.Clamp(penaltyMultiplier, 0.0, 1.0) : 1.0;
        return new ScoreExplanation
        {
            FinalScore = Math.Clamp(FinalScore * penaltyMultiplier, 0.0, 1.0),
            GenreContribution = GenreContribution * penaltyMultiplier,
            CollaborativeContribution = CollaborativeContribution * penaltyMultiplier,
            RatingContribution = RatingContribution * penaltyMultiplier,
            RecencyContribution = RecencyContribution * penaltyMultiplier,
            YearProximityContribution = YearProximityContribution * penaltyMultiplier,
            UserRatingContribution = UserRatingContribution * penaltyMultiplier,
            InteractionContribution = InteractionContribution * penaltyMultiplier,
            PeopleContribution = PeopleContribution * penaltyMultiplier,
            StudioContribution = StudioContribution * penaltyMultiplier,
            FranchiseContribution = FranchiseContribution * penaltyMultiplier,
            CollectionProgressionContribution = CollectionProgressionContribution * penaltyMultiplier,
            PopularityContribution = PopularityContribution * penaltyMultiplier,
            GenrePenaltyMultiplier = penaltyMultiplier,
            // Scaling all contributions by the same factor cannot change which one is largest.
            // Only when the multiplier is zero do all contributions collapse to zero to "None".
            DominantSignal = penaltyMultiplier <= DominantSignalTolerance ? "None" : DominantSignal,
            StrategyName = StrategyName
        };
    }

    /// <summary>
    ///     Determines the dominant signal name from the per-feature contributions. Returns the name of the feature with the highest absolute contribution value.
    /// </summary>
    /// <param name="genreContrib">Genre similarity contribution.</param>
    /// <param name="collabContrib">Collaborative filtering contribution.</param>
    /// <param name="ratingContrib">Community rating contribution.</param>
    /// <param name="userRatingContrib">User personal rating contribution.</param>
    /// <param name="recencyContrib">Recency contribution.</param>
    /// <param name="yearProxContrib">Year proximity contribution.</param>
    /// <param name="interactionContrib">Interaction terms contribution (genreÃ-rating, genreÃ-collab, genreCount, isSeries, completion).</param>
    /// <param name="peopleContrib">People similarity contribution (actors/directors).</param>
    /// <param name="studioContrib">Studio match contribution.</param>
    /// <param name="franchiseContrib">Franchise (TMDb collection) affinity contribution.</param>
    /// <param name="collectionContrib">Box-set collection progression contribution.</param>
    /// <param name="popularityContrib">Global popularity contribution.</param>
    /// <returns>The name of the dominant signal.</returns>
    public static string DetermineDominantSignal(
        double genreContrib,
        double collabContrib,
        double ratingContrib,
        double userRatingContrib,
        double recencyContrib,
        double yearProxContrib,
        double interactionContrib = 0.0,
        double peopleContrib = 0.0,
        double studioContrib = 0.0,
        double franchiseContrib = 0.0,
        double collectionContrib = 0.0,
        double popularityContrib = 0.0)
    {
        // Dominance is ranked by magnitude (Math.Abs): a strongly negative contribution is still the signal that
        // moved the score most, which is the correct "what drove this" answer for diagnostics. A negative driver
        // must not become a positive-sounding reason, so the reason resolver re-checks the sign (contribution
        // must exceed a strictly-positive floor) before naming any dominant signal.
        // Allocation-free comparison - this runs per candidate in the scoring hot path.
        var bestName = "Genre";
        var bestValue = Math.Abs(genreContrib);

        var v = Math.Abs(collabContrib);
        if (v > bestValue)
        {
            bestName = "Collaborative";
            bestValue = v;
        }

        v = Math.Abs(ratingContrib);
        if (v > bestValue)
        {
            bestName = "Rating";
            bestValue = v;
        }

        v = Math.Abs(userRatingContrib);
        if (v > bestValue)
        {
            bestName = "UserRating";
            bestValue = v;
        }

        v = Math.Abs(recencyContrib);
        if (v > bestValue)
        {
            bestName = "Recency";
            bestValue = v;
        }

        v = Math.Abs(yearProxContrib);
        if (v > bestValue)
        {
            bestName = "YearProximity";
            bestValue = v;
        }

        v = Math.Abs(interactionContrib);
        if (v > bestValue)
        {
            bestName = "Interaction";
            bestValue = v;
        }

        v = Math.Abs(peopleContrib);
        if (v > bestValue)
        {
            bestName = "People";
            bestValue = v;
        }

        v = Math.Abs(studioContrib);
        if (v > bestValue)
        {
            bestName = "Studio";
            bestValue = v;
        }

        v = Math.Abs(franchiseContrib);
        if (v > bestValue)
        {
            bestName = "Franchise";
            bestValue = v;
        }

        v = Math.Abs(collectionContrib);
        if (v > bestValue)
        {
            bestName = "CollectionProgression";
            bestValue = v;
        }

        v = Math.Abs(popularityContrib);
        if (v > bestValue)
        {
            bestName = "Popularity";
            bestValue = v;
        }

        // When every contribution is zero, no signal is dominant.
        return bestValue <= DominantSignalTolerance ? "None" : bestName;
    }

    /// <summary>
    ///     Returns a compact debug-friendly string representation.
    /// </summary>
    /// <returns>A formatted string with all score components and the dominant signal.</returns>
    public override string ToString()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{StrategyName}] score={FinalScore:F4} (genre={GenreContribution:F3}, collab={CollaborativeContribution:F3}, rating={RatingContribution:F3}, recency={RecencyContribution:F3}, yearProx={YearProximityContribution:F3}, userRating={UserRatingContribution:F3}, interact={InteractionContribution:F3}, people={PeopleContribution:F3}, studio={StudioContribution:F3}, franchise={FranchiseContribution:F3}, collection={CollectionProgressionContribution:F3}, popularity={PopularityContribution:F3}, penalty={GenrePenaltyMultiplier:F2}) dominant={DominantSignal}");
    }
}