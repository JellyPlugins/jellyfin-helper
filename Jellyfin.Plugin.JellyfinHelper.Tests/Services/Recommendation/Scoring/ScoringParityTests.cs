using System;
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
    public static TheoryData<CandidateFeatures> ParityBatteries()
    {
        var data = new TheoryData<CandidateFeatures>
        {
            new CandidateFeatures(),
            AllOnesFeatures()
        };

        var rng = new Random(12345);
        for (var i = 0; i < 25; i++)
        {
            data.Add(RandomFeatures(rng));
        }

        return data;
    }

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

    [Theory]
    [MemberData(nameof(ParityBatteries))]
    public void Heuristic_Score_MatchesExplanationFinalScore(CandidateFeatures features)
    {
        var heuristic = CreateHeuristic();

        var score = heuristic.Score(features);
        var explanation = heuristic.ScoreWithExplanation(features);

        Assert.Equal(score, explanation.FinalScore, 10);
    }

    [Theory]
    [MemberData(nameof(ParityBatteries))]
    public void LearnedUntrained_Score_MatchesExplanationFinalScore(CandidateFeatures features)
    {
        var learned = new LearnedScoringStrategy();

        var score = learned.Score(features);
        var explanation = learned.ScoreWithExplanation(features);

        Assert.Equal(score, explanation.FinalScore, 10);
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

    private static CandidateFeatures RandomFeatures(Random rng)
    {
        double Next() => rng.NextDouble();
        bool NextBool() => rng.NextDouble() > 0.5;

        return new CandidateFeatures
        {
            GenreSimilarity = Next(),
            CollaborativeScore = Next(),
            CombinedCriticScore = Next(),
            RecencyScore = Next(),
            YearProximityScore = Next(),
            GenreCount = rng.Next(0, 9),
            IsSeries = NextBool(),
            UserRatingScore = Next(),
            CompletionRatio = Next(),
            IsAbandoned = Next(),
            HasUserInteraction = NextBool(),
            PeopleSimilarity = Next(),
            StudioMatch = NextBool(),
            SeriesAffinity = Next(),
            SeriesProgressionBoost = Next(),
            PopularityScore = Next(),
            DayOfWeekAffinity = Next(),
            HourOfDayAffinity = Next(),
            IsWeekend = NextBool(),
            TagSimilarity = Next(),
            GenreUnderexposure = Next(),
            GenreDominanceRatio = Next(),
            GenreAffinityGap = Next(),
            LibraryAddedRecency = Next(),
            ContentNearestNeighborScore = Next(),
            LanguageAffinity = Next(),
            CollectionProgressionBoost = Next(),
            SubtitleLanguageAffinity = Next(),
            FranchiseAffinity = Next(),
            ProductionLocationAffinity = Next(),
            InheritedTagSimilarity = Next(),
            SeriesCompletability = Next(),
            WriterAffinity = Next(),
            BillingWeightedPeople = Next(),
            GenreStudioIdfPrior = Next()
        };
    }
}
