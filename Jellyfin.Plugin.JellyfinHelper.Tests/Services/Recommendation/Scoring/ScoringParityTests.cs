using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Cross-strategy parity pins for the 38-feature contract shared by the heuristic,
///     learned, and neural strategies: FeatureIndex <-> vector slots <-> default weights
///     <-> score explanations must stay in lock-step. If a feature is added to the vector
///     but forgotten in BuildExplanation (or vice versa), Score and FinalScore diverge and
///     these tests fail. Score/explanation equality is asserted at 10 decimals because the
///     two paths sum contributions in a different order (floating-point non-associativity).
/// </summary>
public sealed class ScoringParityTests
{
    private static IEnumerable<CandidateFeatures> ParityBatteries()
    {
        yield return new CandidateFeatures();
        yield return AllOnesFeatures();

        for (var i = 0; i < 25; i++)
        {
            yield return PseudoRandomFeatures(i);
        }
    }

    // Deterministic pseudo-random value in [0, 1) from an integer seed (fractional
    // multiples of the golden ratio). A plain counter sequence is used instead of
    // System.Random: the values only need to look arbitrary for parity probing, and
    // Random triggers security-analyzer noise about insecure RNGs.
    private static double PseudoRandom(int seed) => (seed * 0.618033988749895) % 1.0;

    [Fact]
    public void FeatureIndex_DistinctCount_MatchesFeatureCount()
    {
        // Mirrors the production guard in DefaultWeights.CreateWeightArray at test level so a
        // miscount fails fast here with a clear message instead of deep inside scoring.
        var distinct = Enum.GetValues<FeatureIndex>().Select(v => (int)v).Distinct().Count();

        Assert.Equal(CandidateFeatures.FeatureCount, distinct);
    }

    [Fact]
    public void Heuristic_AllOnesScore_IsInSaneRange()
    {
        // With every signal at maximum the hand-tuned weights must land clearly above neutral
        // but below saturation: guards against order-of-magnitude weight typos.
        var heuristic = CreateHeuristic();

        var score = heuristic.Score(AllOnesFeatures());

        Assert.InRange(score, 0.7, 0.95);
    }

    [Fact]
    public void Heuristic_Score_MatchesExplanationFinalScore()
    {
        var heuristic = CreateHeuristic();

        foreach (var features in ParityBatteries())
        {
            Assert.Equal(heuristic.Score(features), heuristic.ScoreWithExplanation(features).FinalScore, 10);
        }
    }

    [Fact]
    public void LearnedUntrained_Score_MatchesExplanationFinalScore()
    {
        var learned = new LearnedScoringStrategy();

        foreach (var features in ParityBatteries())
        {
            Assert.Equal(learned.Score(features), learned.ScoreWithExplanation(features).FinalScore, 10);
        }
    }

    private static HeuristicScoringStrategy CreateHeuristic() =>
        // Penalty disabled so the battery compares the raw linear core on both paths.
        new(genrePenaltyFloor: 1.0);

    private static CandidateFeatures AllOnesFeatures() =>
        new()
        {
            GenreSimilarity = 1.0,
            CollaborativeScore = 1.0,
            CombinedCriticScore = 1.0,
            RecencyScore = 1.0,
            YearProximityScore = 1.0,
            GenreCount = 5,
            IsSeries = true,
            UserRatingScore = 1.0,
            CompletionRatio = 1.0,
            IsAbandoned = 1.0,
            HasUserInteraction = true,
            PeopleSimilarity = 1.0,
            StudioMatch = true,
            SeriesAffinity = 1.0,
            SeriesProgressionBoost = 1.0,
            PopularityScore = 1.0,
            DayOfWeekAffinity = 1.0,
            HourOfDayAffinity = 1.0,
            IsWeekend = true,
            TagSimilarity = 1.0,
            GenreUnderexposure = 1.0,
            GenreDominanceRatio = 1.0,
            GenreAffinityGap = 1.0,
            LibraryAddedRecency = 1.0,
            ContentNearestNeighborScore = 1.0,
            LanguageAffinity = 1.0,
            CollectionProgressionBoost = 1.0,
            SubtitleLanguageAffinity = 1.0,
            FranchiseAffinity = 1.0,
            ProductionLocationAffinity = 1.0,
            InheritedTagSimilarity = 1.0,
            SeriesCompletability = 1.0,
            WriterAffinity = 1.0,
            BillingWeightedPeople = 1.0,
            GenreStudioIdfPrior = 1.0
        };

    private static CandidateFeatures PseudoRandomFeatures(int index)
    {
        double Next(int salt) => PseudoRandom(index * 64 + salt);
        bool NextBool(int salt) => Next(salt) > 0.5;

        return new CandidateFeatures
        {
            GenreSimilarity = Next(1),
            CollaborativeScore = Next(2),
            CombinedCriticScore = Next(3),
            RecencyScore = Next(4),
            YearProximityScore = Next(5),
            GenreCount = (int)(Next(6) * 9),
            IsSeries = NextBool(7),
            UserRatingScore = Next(8),
            CompletionRatio = Next(9),
            IsAbandoned = Next(10),
            HasUserInteraction = NextBool(11),
            PeopleSimilarity = Next(12),
            StudioMatch = NextBool(13),
            SeriesAffinity = Next(14),
            SeriesProgressionBoost = Next(15),
            PopularityScore = Next(16),
            DayOfWeekAffinity = Next(17),
            HourOfDayAffinity = Next(18),
            IsWeekend = NextBool(19),
            TagSimilarity = Next(20),
            GenreUnderexposure = Next(21),
            GenreDominanceRatio = Next(22),
            GenreAffinityGap = Next(23),
            LibraryAddedRecency = Next(24),
            ContentNearestNeighborScore = Next(25),
            LanguageAffinity = Next(26),
            CollectionProgressionBoost = Next(27),
            SubtitleLanguageAffinity = Next(28),
            FranchiseAffinity = Next(29),
            ProductionLocationAffinity = Next(30),
            InheritedTagSimilarity = Next(31),
            SeriesCompletability = Next(32),
            WriterAffinity = Next(33),
            BillingWeightedPeople = Next(34),
            GenreStudioIdfPrior = Next(35)
        };
    }
}
