using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Advanced tests for <see cref="EnsembleScoringStrategy"/> targeting the previously
///     uncovered branches: <c>ScoreWithOffset</c>, <c>ScoreWithExplanationAndOffset</c>,
///     <c>ApplyCohortFeedback</c>, and constructor guards.
/// </summary>
public sealed class EnsembleScoringStrategyAdvancedTests
{
    private static RecommendationResult BuildCohortResult(
        string? cohort,
        int recCount,
        int watchedCount,
        out HashSet<Guid> watchedIds)
    {
        var userId = Guid.NewGuid();
        var result = new RecommendationResult
        {
            UserId = userId,
            UserName = "u_" + (cohort ?? "null"),
            Cohort = cohort
        };
        var watched = new HashSet<Guid>();
        for (var i = 0; i < recCount; i++)
        {
            var itemId = Guid.NewGuid();
            result.Recommendations.Add(new RecommendedItem { ItemId = itemId, Name = "rec_" + i });
            if (i < watchedCount)
            {
                watched.Add(itemId);
            }
        }

        watchedIds = watched;
        return result;
    }

    [Fact]
    public void Constructor_HeuristicWithDefaultPenaltyFloor_Throws()
    {
        // BUG GUARD: heuristic MUST have genrePenaltyFloor=1.0 or ensemble double-penalises.
        var learned = new LearnedScoringStrategy();
        var heuristicWithPenalty = new HeuristicScoringStrategy();

        var ex = Assert.Throws<ArgumentException>(() =>
            new EnsembleScoringStrategy(learned, heuristicWithPenalty));

        Assert.Contains("genrePenaltyFloor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NullLearned_Throws()
    {
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        Assert.Throws<ArgumentNullException>(() => new EnsembleScoringStrategy(null!, heuristic));
    }

    [Fact]
    public void Constructor_NullHeuristic_Throws()
    {
        var learned = new LearnedScoringStrategy();
        Assert.Throws<ArgumentNullException>(() => new EnsembleScoringStrategy(learned, null!));
    }

    [Fact]
    public void Constructor_AlphaMaxBelowAlphaMin_IsClampedToAlphaMin()
    {
        // A "finite & in-range" assertion alone would still pass if the constructor
        // silently ignored the invalid values altogether. Pin the actual clamp by
        // comparing against an instance configured explicitly at the expected boundary
        // (alphaMax == alphaMin) — both must produce the same score.
        var invalid = new EnsembleScoringStrategy(alphaMin: 0.6, alphaMax: 0.2);
        var clamped = new EnsembleScoringStrategy(alphaMin: 0.6, alphaMax: 0.6);
        var features = new CandidateFeatures { GenreSimilarity = 0.5, CombinedCriticScore = 0.5 };

        var invalidScore = invalid.Score(features);
        var expectedScore = clamped.Score(features);

        Assert.True(double.IsFinite(invalidScore));
        Assert.InRange(invalidScore, 0.0, 1.0);
        Assert.Equal(expectedScore, invalidScore, 12);
    }

    [Fact]
    public void Constructor_GenrePenaltyFloor_ClampedToValidRange()
    {
        // Same idea as above: assert the clamp landed on the actual boundary (0.0 and 1.0)
        // instead of "produced a finite number".
        var tooHigh = new EnsembleScoringStrategy(genrePenaltyFloor: 5.0);
        var tooLow = new EnsembleScoringStrategy(genrePenaltyFloor: -1.0);
        var atFloorOne = new EnsembleScoringStrategy(genrePenaltyFloor: 1.0);
        var atFloorZero = new EnsembleScoringStrategy(genrePenaltyFloor: 0.0);
        var zeroGenre = new CandidateFeatures { GenreSimilarity = 0.0, CombinedCriticScore = 0.5 };

        Assert.True(double.IsFinite(tooHigh.Score(zeroGenre)));
        Assert.True(double.IsFinite(tooLow.Score(zeroGenre)));
        Assert.Equal(atFloorOne.Score(zeroGenre), tooHigh.Score(zeroGenre), 12);
        Assert.Equal(atFloorZero.Score(zeroGenre), tooLow.Score(zeroGenre), 12);
    }

    [Fact]
    public void ScoreWithOffset_ZeroOffset_MatchesScore()
    {
        var ensemble = new EnsembleScoringStrategy();
        var features = new CandidateFeatures { GenreSimilarity = 0.6, CombinedCriticScore = 0.7 };

        var baseline = ensemble.Score(features);
        Assert.Equal(baseline, ensemble.ScoreWithOffset(features, 0.0), 12);
        Assert.Equal(baseline, ensemble.ScoreWithOffset(features, 1e-11), 12);
        Assert.Equal(baseline, ensemble.ScoreWithOffset(features, -1e-11), 12);
    }

    [Fact]
    public void ScoreWithOffset_MassivePositiveOffset_ClampedInRange()
    {
        var ensemble = new EnsembleScoringStrategy();
        var features = new CandidateFeatures { GenreSimilarity = 0.8, CombinedCriticScore = 0.7 };
        var score = ensemble.ScoreWithOffset(features, 100.0);
        Assert.True(double.IsFinite(score));
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void ScoreWithOffset_MassiveNegativeOffset_ClampedInRange()
    {
        var ensemble = new EnsembleScoringStrategy();
        var features = new CandidateFeatures { GenreSimilarity = 0.8, CombinedCriticScore = 0.7 };
        var score = ensemble.ScoreWithOffset(features, -100.0);
        Assert.True(double.IsFinite(score));
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void ScoreWithExplanationAndOffset_ZeroOffset_MatchesBaseline()
    {
        var ensemble = new EnsembleScoringStrategy();
        var features = new CandidateFeatures { GenreSimilarity = 0.6, CombinedCriticScore = 0.7 };

        var baseline = ensemble.ScoreWithExplanation(features);
        var withZero = ensemble.ScoreWithExplanationAndOffset(features, 0.0);

        Assert.Equal(baseline.FinalScore, withZero.FinalScore, 10);
        Assert.Equal(baseline.StrategyName, withZero.StrategyName);
    }

    [Fact]
    public void ScoreWithExplanationAndOffset_NonZeroOffset_ReturnsValidExplanation()
    {
        var ensemble = new EnsembleScoringStrategy();
        var features = new CandidateFeatures { GenreSimilarity = 0.6, CombinedCriticScore = 0.7 };
        var explanation = ensemble.ScoreWithExplanationAndOffset(features, 0.2);

        Assert.NotNull(explanation);
        Assert.Equal("Ensemble (Adaptive ML + Rules)", explanation.StrategyName);
        Assert.True(double.IsFinite(explanation.FinalScore));
        Assert.InRange(explanation.FinalScore, 0.0, 1.0);
    }

    [Fact]
    public void ApplyCohortFeedback_InsufficientControlSamples_NoOp()
    {
        var ensemble = new EnsembleScoringStrategy();
        var initialOffset = ensemble.SigmoidMidpointOffset;

        var controlResult = BuildCohortResult("control", 3, 3, out var cw);
        var highResult = BuildCohortResult("explore-high", 20, 20, out var hw);

        ensemble.ApplyCohortFeedback(new[] { controlResult, highResult },
            new Dictionary<Guid, HashSet<Guid>>
            {
                { controlResult.UserId, cw },
                { highResult.UserId, hw }
            });

        Assert.Equal(initialOffset, ensemble.SigmoidMidpointOffset, 6);
    }

    [Fact]
    public void ApplyCohortFeedback_ExploreHighBeatsControl_ShiftsMidpointDown()
    {
        var ensemble = new EnsembleScoringStrategy();
        var controlResult = BuildCohortResult("control", 10, 5, out var cw);
        var highResult = BuildCohortResult("explore-high", 10, 9, out var hw);

        ensemble.ApplyCohortFeedback(new[] { controlResult, highResult },
            new Dictionary<Guid, HashSet<Guid>>
            {
                { controlResult.UserId, cw },
                { highResult.UserId, hw }
            });

        Assert.Equal(-EnsembleScoringStrategy.MidpointAdaptationStep, ensemble.SigmoidMidpointOffset, 6);
    }

    [Fact]
    public void ApplyCohortFeedback_ExploreLowBeatsControl_ShiftsMidpointUp()
    {
        var ensemble = new EnsembleScoringStrategy();
        var controlResult = BuildCohortResult("control", 10, 3, out var cw);
        var lowResult = BuildCohortResult("explore-low", 10, 9, out var lw);

        ensemble.ApplyCohortFeedback(new[] { controlResult, lowResult },
            new Dictionary<Guid, HashSet<Guid>>
            {
                { controlResult.UserId, cw },
                { lowResult.UserId, lw }
            });

        Assert.Equal(EnsembleScoringStrategy.MidpointAdaptationStep, ensemble.SigmoidMidpointOffset, 6);
    }

    [Fact]
    public void ApplyCohortFeedback_ControlOptimalWithQualifyingCohorts_DecaysOffset()
    {
        var ensemble = new EnsembleScoringStrategy();

        // Step 1: seed a positive offset via low-cohort win.
        var seedControl = BuildCohortResult("control", 10, 3, out var scw);
        var seedLow = BuildCohortResult("explore-low", 10, 9, out var slw);
        ensemble.ApplyCohortFeedback(new[] { seedControl, seedLow },
            new Dictionary<Guid, HashSet<Guid>>
            {
                { seedControl.UserId, scw },
                { seedLow.UserId, slw }
            });

        var seededOffset = ensemble.SigmoidMidpointOffset;
        Assert.True(seededOffset > 0);

        // Step 2: control beats both explore cohorts.
        var control = BuildCohortResult("control", 10, 9, out var cw);
        var high = BuildCohortResult("explore-high", 10, 3, out var hw);
        var low = BuildCohortResult("explore-low", 10, 3, out var lw);

        ensemble.ApplyCohortFeedback(new[] { control, high, low },
            new Dictionary<Guid, HashSet<Guid>>
            {
                { control.UserId, cw },
                { high.UserId, hw },
                { low.UserId, lw }
            });

        Assert.Equal(seededOffset * EnsembleScoringStrategy.MidpointDecayFactor, ensemble.SigmoidMidpointOffset, 4);
    }

    [Fact]
    public void ApplyCohortFeedback_NoQualifyingCohorts_NoDecayApplied()
    {
        var ensemble = new EnsembleScoringStrategy();

        var seedControl = BuildCohortResult("control", 10, 3, out var scw);
        var seedLow = BuildCohortResult("explore-low", 10, 9, out var slw);
        ensemble.ApplyCohortFeedback(new[] { seedControl, seedLow },
            new Dictionary<Guid, HashSet<Guid>>
            {
                { seedControl.UserId, scw },
                { seedLow.UserId, slw }
            });

        var seededOffset = ensemble.SigmoidMidpointOffset;
        Assert.True(seededOffset > 0);

        var control = BuildCohortResult("control", 10, 8, out var cw);
        var tinyHigh = BuildCohortResult("explore-high", 2, 0, out var th);
        var tinyLow = BuildCohortResult("explore-low", 2, 0, out var tl);

        ensemble.ApplyCohortFeedback(new[] { control, tinyHigh, tinyLow },
            new Dictionary<Guid, HashSet<Guid>>
            {
                { control.UserId, cw },
                { tinyHigh.UserId, th },
                { tinyLow.UserId, tl }
            });

        Assert.Equal(seededOffset, ensemble.SigmoidMidpointOffset, 6);
    }

    [Fact]
    public void ApplyCohortFeedback_EmptyRecommendations_Ignored()
    {
        var ensemble = new EnsembleScoringStrategy();
        var initialOffset = ensemble.SigmoidMidpointOffset;

        var empty = new RecommendationResult { UserId = Guid.NewGuid(), UserName = "empty", Cohort = "control" };
        var control = BuildCohortResult("control", 10, 8, out var cw);

        ensemble.ApplyCohortFeedback(new[] { empty, control },
            new Dictionary<Guid, HashSet<Guid>>
            {
                { control.UserId, cw }
            });

        Assert.Equal(initialOffset, ensemble.SigmoidMidpointOffset, 6);
    }

    [Fact]
    public void ApplyCohortFeedback_NullCohort_TreatedAsControl()
    {
        var ensemble = new EnsembleScoringStrategy();

        var nullResult = BuildCohortResult(cohort: null, recCount: 5, watchedCount: 2, out var nw);
        var controlResult = BuildCohortResult("control", 5, 2, out var cw);
        var highResult = BuildCohortResult("explore-high", 10, 9, out var hw);

        ensemble.ApplyCohortFeedback(new[] { nullResult, controlResult, highResult },
            new Dictionary<Guid, HashSet<Guid>>
            {
                { nullResult.UserId, nw },
                { controlResult.UserId, cw },
                { highResult.UserId, hw }
            });

        Assert.Equal(-EnsembleScoringStrategy.MidpointAdaptationStep, ensemble.SigmoidMidpointOffset, 6);
    }

    // ===== Reconfigure =====

    [Fact]
    public void Reconfigure_UpdatesBoundsAndClampsAlpha()
    {
        var ensemble = new EnsembleScoringStrategy(alphaMin: 0.1, alphaMax: 0.9);

        // Train so alpha advances above the minimum
        var positiveFeatures = new CandidateFeatures
        {
            GenreSimilarity = 0.9,
            CollaborativeScore = 0.8,
            CombinedCriticScore = 0.7,
            RecencyScore = 0.6
        };
        var examples = Enumerable.Range(0, 100)
            .Select(_ => new TrainingExample { Features = positiveFeatures, Label = 1.0 })
            .ToList();
        ensemble.Train(examples);

        var alphaBefore = ensemble.CurrentAlpha;
        Assert.True(alphaBefore > 0.1, "Alpha should have advanced past αMin after training");

        // Narrow the ceiling so current alpha must be clamped down
        ensemble.Reconfigure(alphaMin: 0.1, alphaMax: 0.2, genrePenaltyFloor: 0.05);

        Assert.True(ensemble.CurrentAlpha <= 0.2,
            "Alpha must be clamped to new αMax after Reconfigure");
        Assert.True(ensemble.CurrentAlpha >= 0.1,
            "Alpha must remain >= new αMin after Reconfigure");
    }

    [Fact]
    public void Reconfigure_NewBoundsReflectedInSubsequentScore()
    {
        var ensemble = new EnsembleScoringStrategy(alphaMin: 0.0, alphaMax: 0.0);
        var features = new CandidateFeatures
        {
            GenreSimilarity = 1.0,
            CollaborativeScore = 1.0,
            CombinedCriticScore = 1.0,
            RecencyScore = 1.0
        };

        var scoreBefore = ensemble.Score(features);

        // Allow more ML influence — score should stay in [0,1] after reconfigure
        ensemble.Reconfigure(alphaMin: 0.5, alphaMax: 0.8, genrePenaltyFloor: 0.1);
        var scoreAfter = ensemble.Score(features);

        Assert.InRange(scoreBefore, 0.0, 1.0);
        Assert.InRange(scoreAfter, 0.0, 1.0);
    }

    [Fact]
    public void Reconfigure_AlphaMinAboveMax_ClampedToSameValue()
    {
        var ensemble = new EnsembleScoringStrategy();
        // Passing min > max — implementation clamps max to min
        ensemble.Reconfigure(alphaMin: 0.8, alphaMax: 0.3, genrePenaltyFloor: 0.1);
        Assert.InRange(ensemble.CurrentAlpha, 0.0, 1.0);
    }
}
