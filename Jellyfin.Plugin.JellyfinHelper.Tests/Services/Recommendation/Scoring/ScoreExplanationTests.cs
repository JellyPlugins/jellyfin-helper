using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="ScoreExplanation"/>: Blend(), WithPenalty(), DetermineDominantSignal().
/// </summary>
public sealed class ScoreExplanationTests
{
    // ============================================================
    // DetermineDominantSignal Tests
    // ============================================================

    [Fact]
    public void DetermineDominantSignal_GenreHighest_ReturnsGenre()
    {
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.9,
            collabContrib: 0.1,
            ratingContrib: 0.2,
            userRatingContrib: 0.1,
            recencyContrib: 0.05,
            yearProxContrib: 0.03);

        Assert.Equal("Genre", result);
    }

    [Fact]
    public void DetermineDominantSignal_CollaborativeHighest_ReturnsCollaborative()
    {
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.1,
            collabContrib: 0.8,
            ratingContrib: 0.2,
            userRatingContrib: 0.1,
            recencyContrib: 0.05,
            yearProxContrib: 0.03);

        Assert.Equal("Collaborative", result);
    }

    [Fact]
    public void DetermineDominantSignal_RatingHighest_ReturnsRating()
    {
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.1,
            collabContrib: 0.1,
            ratingContrib: 0.9,
            userRatingContrib: 0.1,
            recencyContrib: 0.05,
            yearProxContrib: 0.03);

        Assert.Equal("Rating", result);
    }

    [Fact]
    public void DetermineDominantSignal_UserRatingHighest_ReturnsUserRating()
    {
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.1,
            collabContrib: 0.1,
            ratingContrib: 0.2,
            userRatingContrib: 0.95,
            recencyContrib: 0.05,
            yearProxContrib: 0.03);

        Assert.Equal("UserRating", result);
    }

    [Fact]
    public void DetermineDominantSignal_RecencyHighest_ReturnsRecency()
    {
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.1,
            collabContrib: 0.1,
            ratingContrib: 0.2,
            userRatingContrib: 0.1,
            recencyContrib: 0.85,
            yearProxContrib: 0.03);

        Assert.Equal("Recency", result);
    }

    [Fact]
    public void DetermineDominantSignal_YearProximityHighest_ReturnsYearProximity()
    {
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.1,
            collabContrib: 0.1,
            ratingContrib: 0.2,
            userRatingContrib: 0.1,
            recencyContrib: 0.05,
            yearProxContrib: 0.95);

        Assert.Equal("YearProximity", result);
    }

    [Fact]
    public void DetermineDominantSignal_InteractionHighest_ReturnsInteraction()
    {
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.1,
            collabContrib: 0.1,
            ratingContrib: 0.2,
            userRatingContrib: 0.1,
            recencyContrib: 0.05,
            yearProxContrib: 0.03,
            interactionContrib: 0.99);

        Assert.Equal("Interaction", result);
    }

    [Fact]
    public void DetermineDominantSignal_PeopleHighest_ReturnsPeople()
    {
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.1,
            collabContrib: 0.1,
            ratingContrib: 0.2,
            userRatingContrib: 0.1,
            recencyContrib: 0.05,
            yearProxContrib: 0.03,
            interactionContrib: 0.1,
            peopleContrib: 0.95);

        Assert.Equal("People", result);
    }

    [Fact]
    public void DetermineDominantSignal_StudioHighest_ReturnsStudio()
    {
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.1,
            collabContrib: 0.1,
            ratingContrib: 0.2,
            userRatingContrib: 0.1,
            recencyContrib: 0.05,
            yearProxContrib: 0.03,
            interactionContrib: 0.1,
            peopleContrib: 0.1,
            studioContrib: 0.99);

        Assert.Equal("Studio", result);
    }

    [Fact]
    public void DetermineDominantSignal_NegativeValues_UsesAbsolute()
    {
        // Negative contribution with largest absolute value should win
        var result = ScoreExplanation.DetermineDominantSignal(
            genreContrib: 0.1,
            collabContrib: -0.95,
            ratingContrib: 0.2,
            userRatingContrib: 0.1,
            recencyContrib: 0.05,
            yearProxContrib: 0.03);

        Assert.Equal("Collaborative", result);
    }

    [Fact]
    public void DetermineDominantSignal_AllZeros_ReturnsGenre()
    {
        // When all are zero, Genre wins by default (first checked)
        var result = ScoreExplanation.DetermineDominantSignal(0, 0, 0, 0, 0, 0);
        Assert.Equal("Genre", result);
    }

    [Fact]
    public void DetermineDominantSignal_OptionalDefaultsToZero()
    {
        // Without optional params, interaction/people/studio default to 0
        var result = ScoreExplanation.DetermineDominantSignal(0.5, 0.1, 0.2, 0.1, 0.05, 0.03);
        Assert.Equal("Genre", result);
    }

    // ============================================================
    // Blend Tests
    // ============================================================

    [Fact]
    public void Blend_AlphaZero_ReturnsThis()
    {
        var a = CreateExplanation(0.8, 0.5, 0.3, 0.2, 0.1, 0.4, 0.15, 0.05, 0.1, 0.02);
        var b = CreateExplanation(0.2, 0.1, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3, 0.9);

        var result = a.Blend(b, 0.0);

        Assert.Equal(a.FinalScore, result.FinalScore, 10);
        Assert.Equal(a.GenreContribution, result.GenreContribution, 10);
        Assert.Equal(a.CollaborativeContribution, result.CollaborativeContribution, 10);
        Assert.Equal(a.RatingContribution, result.RatingContribution, 10);
        Assert.Equal(a.RecencyContribution, result.RecencyContribution, 10);
        Assert.Equal(a.YearProximityContribution, result.YearProximityContribution, 10);
        Assert.Equal(a.UserRatingContribution, result.UserRatingContribution, 10);
        Assert.Equal(a.InteractionContribution, result.InteractionContribution, 10);
        Assert.Equal(a.PeopleContribution, result.PeopleContribution, 10);
        Assert.Equal(a.StudioContribution, result.StudioContribution, 10);
    }

    [Fact]
    public void Blend_AlphaOne_ReturnsOther()
    {
        var a = CreateExplanation(0.8, 0.5, 0.3, 0.2, 0.1, 0.4, 0.15, 0.05, 0.1, 0.02);
        var b = CreateExplanation(0.2, 0.1, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3, 0.9);

        var result = a.Blend(b, 1.0);

        Assert.Equal(b.FinalScore, result.FinalScore, 10);
        Assert.Equal(b.GenreContribution, result.GenreContribution, 10);
        Assert.Equal(b.CollaborativeContribution, result.CollaborativeContribution, 10);
        Assert.Equal(b.RatingContribution, result.RatingContribution, 10);
        Assert.Equal(b.RecencyContribution, result.RecencyContribution, 10);
        Assert.Equal(b.YearProximityContribution, result.YearProximityContribution, 10);
        Assert.Equal(b.UserRatingContribution, result.UserRatingContribution, 10);
        Assert.Equal(b.InteractionContribution, result.InteractionContribution, 10);
        Assert.Equal(b.PeopleContribution, result.PeopleContribution, 10);
        Assert.Equal(b.StudioContribution, result.StudioContribution, 10);
    }

    [Fact]
    public void Blend_AlphaHalf_ReturnsMidpoint()
    {
        var a = CreateExplanation(1.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
        var b = CreateExplanation(0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);

        var result = a.Blend(b, 0.5);

        Assert.Equal(0.5, result.FinalScore, 10);
        Assert.Equal(0.5, result.GenreContribution, 10);
        Assert.Equal(0.5, result.CollaborativeContribution, 10);
    }

    [Fact]
    public void Blend_GenrePenaltyMultiplier_IsResetToOne()
    {
        var a = CreateExplanation(0.5, 0.3, 0.2, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1);
        a.GenrePenaltyMultiplier = 0.5;
        var b = CreateExplanation(0.5, 0.3, 0.2, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1);
        b.GenrePenaltyMultiplier = 0.3;

        var result = a.Blend(b, 0.5);

        // Penalty is applied separately after blending
        Assert.Equal(1.0, result.GenrePenaltyMultiplier, 10);
    }

    [Fact]
    public void Blend_SetsCorrectDominantSignal()
    {
        // a: genre dominant, b: rating dominant
        var a = CreateExplanation(0.5, 0.9, 0.1, 0.1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
        var b = CreateExplanation(0.5, 0.1, 0.1, 0.9, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);

        // alpha=0.5 → genre=0.5, rating=0.5 → tie, Genre wins (checked first)
        var result = a.Blend(b, 0.5);
        // Both are 0.5 → first one checked (Genre) wins
        Assert.Equal("Genre", result.DominantSignal);
    }

    [Fact]
    public void Blend_PreservesStrategyName()
    {
        var a = CreateExplanation(0.5, 0.3, 0.2, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1);
        a.StrategyName = "TestStrategy";
        var b = CreateExplanation(0.5, 0.3, 0.2, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1);

        var result = a.Blend(b, 0.5);

        Assert.Equal("TestStrategy", result.StrategyName);
    }

    // ============================================================
    // WithPenalty Tests
    // ============================================================

    [Fact]
    public void WithPenalty_One_ReturnsUnchanged()
    {
        var explanation = CreateExplanation(0.8, 0.5, 0.3, 0.2, 0.1, 0.4, 0.15, 0.05, 0.1, 0.02);

        var result = explanation.WithPenalty(1.0);

        Assert.Equal(explanation.FinalScore, result.FinalScore, 10);
        Assert.Equal(explanation.GenreContribution, result.GenreContribution, 10);
        Assert.Equal(explanation.CollaborativeContribution, result.CollaborativeContribution, 10);
        Assert.Equal(1.0, result.GenrePenaltyMultiplier, 10);
    }

    [Fact]
    public void WithPenalty_Half_ScalesAllContributions()
    {
        var explanation = CreateExplanation(0.8, 0.6, 0.4, 0.3, 0.2, 0.5, 0.1, 0.05, 0.1, 0.02);

        var result = explanation.WithPenalty(0.5);

        Assert.Equal(0.4, result.FinalScore, 10);
        Assert.Equal(0.3, result.GenreContribution, 10);
        Assert.Equal(0.2, result.CollaborativeContribution, 10);
        Assert.Equal(0.15, result.RatingContribution, 10);
        Assert.Equal(0.1, result.RecencyContribution, 10);
        Assert.Equal(0.25, result.YearProximityContribution, 10);
        Assert.Equal(0.05, result.UserRatingContribution, 10);
        Assert.Equal(0.025, result.InteractionContribution, 10);
        Assert.Equal(0.05, result.PeopleContribution, 10);
        Assert.Equal(0.01, result.StudioContribution, 10);
        Assert.Equal(0.5, result.GenrePenaltyMultiplier, 10);
    }

    [Fact]
    public void WithPenalty_Zero_ReturnsAllZeros()
    {
        var explanation = CreateExplanation(0.8, 0.5, 0.3, 0.2, 0.1, 0.4, 0.15, 0.05, 0.1, 0.02);

        var result = explanation.WithPenalty(0.0);

        Assert.Equal(0.0, result.FinalScore, 10);
        Assert.Equal(0.0, result.GenreContribution, 10);
        Assert.Equal(0.0, result.CollaborativeContribution, 10);
        Assert.Equal(0.0, result.GenrePenaltyMultiplier, 10);
    }

    [Fact]
    public void WithPenalty_FinalScore_ClampedToZeroOne()
    {
        // FinalScore = 0.9, penalty = 1.5 → penalty clamped to 1.0 → 0.9 × 1.0 = 0.9
        var explanation = new ScoreExplanation { FinalScore = 0.9 };

        var result = explanation.WithPenalty(1.5);

        // Penalty is clamped to [0, 1], so 1.5 becomes 1.0 — FinalScore stays 0.9
        Assert.Equal(0.9, result.FinalScore, 10);
    }

    [Fact]
    public void WithPenalty_PreservesDominantSignalAndStrategyName()
    {
        var explanation = CreateExplanation(0.5, 0.3, 0.2, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1);
        explanation.DominantSignal = "Genre";
        explanation.StrategyName = "TestStrategy";

        var result = explanation.WithPenalty(0.5);

        Assert.Equal("Genre", result.DominantSignal);
        Assert.Equal("TestStrategy", result.StrategyName);
    }

    // ============================================================
    // ToString Tests
    // ============================================================

    [Fact]
    public void ToString_ContainsAllComponents()
    {
        var explanation = CreateExplanation(0.75, 0.5, 0.3, 0.2, 0.1, 0.4, 0.15, 0.05, 0.1, 0.02);
        explanation.StrategyName = "Test";
        explanation.DominantSignal = "Genre";

        var str = explanation.ToString();

        Assert.Contains("[Test]", str);
        Assert.Contains("score=", str);
        Assert.Contains("genre=", str);
        Assert.Contains("collab=", str);
        Assert.Contains("rating=", str);
        Assert.Contains("recency=", str);
        Assert.Contains("yearProx=", str);
        Assert.Contains("userRating=", str);
        Assert.Contains("interact=", str);
        Assert.Contains("people=", str);
        Assert.Contains("studio=", str);
        Assert.Contains("penalty=", str);
        Assert.Contains("dominant=Genre", str);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static ScoreExplanation CreateExplanation(
        double finalScore,
        double genre,
        double collab,
        double rating,
        double recency,
        double yearProx,
        double userRating,
        double interaction,
        double people,
        double studio)
    {
        return new ScoreExplanation
        {
            FinalScore = finalScore,
            GenreContribution = genre,
            CollaborativeContribution = collab,
            RatingContribution = rating,
            RecencyContribution = recency,
            YearProximityContribution = yearProx,
            UserRatingContribution = userRating,
            InteractionContribution = interaction,
            PeopleContribution = people,
            StudioContribution = studio
        };
    }
}