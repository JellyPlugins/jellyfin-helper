using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="RankingMetrics"/>: Precision@K, Recall@K, NDCG@K.
/// </summary>
public sealed class RankingMetricsTests
{
    [Fact]
    public void DefaultK_Is10() => Assert.Equal(10, RankingMetrics.DefaultK);

    [Fact]
    public void DefaultRelevanceThreshold_Is05() => Assert.Equal(0.5, RankingMetrics.DefaultRelevanceThreshold);

    // === Precision@K ===

    [Fact]
    public void PrecisionAtK_PerfectRanking_ReturnsOne()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.1, 0.05 };
        var lbl = new[] { 1.0, 0.8, 0.7, 0.6, 0.0, 0.0 };
        Assert.Equal(1.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 4), 10);
    }

    [Fact]
    public void PrecisionAtK_WorstRanking_ReturnsZero()
    {
        var pred = new[] { 0.9, 0.8, 0.1, 0.05 };
        var lbl = new[] { 0.0, 0.0, 1.0, 0.8 };
        Assert.Equal(0.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 2), 10);
    }

    [Fact]
    public void PrecisionAtK_MixedRanking()
    {
        var pred = new[] { 0.95, 0.90, 0.85, 0.80, 0.75, 0.10, 0.05 };
        var lbl = new[] { 1.0, 0.0, 0.8, 0.0, 0.7, 1.0, 0.9 };
        Assert.Equal(3.0 / 5.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 5), 10);
    }

    [Fact]
    public void PrecisionAtK_Empty_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputePrecisionAtK([], [], k: 5), 10);

    [Fact]
    public void PrecisionAtK_KZero_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputePrecisionAtK(new[] { 0.9 }, new[] { 1.0 }, k: 0), 10);

    [Fact]
    public void PrecisionAtK_KLargerThanN()
    {
        var pred = new[] { 0.9, 0.8, 0.7 };
        var lbl = new[] { 1.0, 0.8, 0.6 };
        Assert.Equal(1.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 10), 10);
    }

    [Fact]
    public void PrecisionAtK_NoRelevant_ReturnsZero()
    {
        var pred = new[] { 0.9, 0.8, 0.7 };
        var lbl = new[] { 0.0, 0.1, 0.2 };
        Assert.Equal(0.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 3), 10);
    }

    [Fact]
    public void PrecisionAtK_CustomThreshold()
    {
        var pred = new[] { 0.9, 0.8, 0.7 };
        var lbl = new[] { 0.8, 0.6, 0.5 };
        Assert.Equal(2.0 / 3.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 3, relevanceThreshold: 0.5), 10);
        Assert.Equal(1.0 / 3.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 3, relevanceThreshold: 0.7), 10);
    }

    // === Recall@K ===

    [Fact]
    public void RecallAtK_AllRelevantInTopK_ReturnsOne()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.1, 0.05 };
        var lbl = new[] { 1.0, 0.8, 0.6, 0.0, 0.0 };
        Assert.Equal(1.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 3), 10);
    }

    [Fact]
    public void RecallAtK_PartialRecovery()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        var lbl = new[] { 1.0, 0.0, 0.8, 0.7, 0.6 };
        Assert.Equal(2.0 / 4.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 3), 10);
    }

    [Fact]
    public void RecallAtK_NoRelevant_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputeRecallAtK(new[] { 0.9, 0.8 }, new[] { 0.0, 0.1 }, k: 2), 10);

    [Fact]
    public void RecallAtK_Empty_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputeRecallAtK([], [], k: 5), 10);

    [Fact]
    public void RecallAtK_KZero_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputeRecallAtK(new[] { 0.9 }, new[] { 1.0 }, k: 0), 10);

    [Fact]
    public void RecallAtK_KLargerThanN_AllRelevant() =>
        Assert.Equal(1.0, RankingMetrics.ComputeRecallAtK(new[] { 0.9, 0.8 }, new[] { 1.0, 0.8 }, k: 100), 10);

    [Fact]
    public void RecallAtK_SingleRelevant_Found()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        var lbl = new[] { 0.0, 0.0, 1.0, 0.0, 0.0 };
        Assert.Equal(1.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 3), 10);
    }

    [Fact]
    public void RecallAtK_SingleRelevant_NotFound()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        var lbl = new[] { 0.0, 0.0, 0.0, 0.0, 1.0 };
        Assert.Equal(0.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 2), 10);
    }

    // === NDCG@K ===

    [Fact]
    public void NdcgAtK_PerfectRanking_ReturnsOne()
    {
        var pred = new[] { 0.9, 0.7, 0.5, 0.3, 0.1 };
        var lbl = new[] { 1.0, 0.8, 0.5, 0.2, 0.0 };
        Assert.Equal(1.0, RankingMetrics.ComputeNdcgAtK(pred, lbl, k: 5), 6);
    }

    [Fact]
    public void NdcgAtK_ReversedRanking_IsLow()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        var lbl = new[] { 0.0, 0.0, 0.0, 1.0, 1.0 };
        var ndcg = RankingMetrics.ComputeNdcgAtK(pred, lbl, k: 5);
        Assert.True(ndcg < 0.8, $"Reversed ranking NDCG should be low: {ndcg:F4}");
        Assert.True(ndcg > 0.0);
    }

    [Fact]
    public void NdcgAtK_AllZeroLabels_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputeNdcgAtK(new[] { 0.9, 0.8 }, new[] { 0.0, 0.0 }, k: 2), 10);

    [Fact]
    public void NdcgAtK_Empty_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputeNdcgAtK([], [], k: 5), 10);

    [Fact]
    public void NdcgAtK_KZero_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputeNdcgAtK(new[] { 0.9 }, new[] { 1.0 }, k: 0), 10);

    [Fact]
    public void NdcgAtK_SingleItem_ReturnsOne() =>
        Assert.Equal(1.0, RankingMetrics.ComputeNdcgAtK(new[] { 0.9 }, new[] { 1.0 }, k: 1), 10);

    [Fact]
    public void NdcgAtK_HigherLabelsAtTop_BetterThanBottom()
    {
        var pred = new[] { 0.9, 0.8, 0.1 };
        var good = RankingMetrics.ComputeNdcgAtK(pred, new[] { 1.0, 0.0, 0.0 }, k: 3);
        var bad = RankingMetrics.ComputeNdcgAtK(pred, new[] { 0.0, 0.0, 1.0 }, k: 3);
        Assert.True(good > bad, $"Top-heavy labels should have higher NDCG: {good:F4} vs {bad:F4}");
    }

    // === ComputeAllFromArrays ===

    [Fact]
    public void ComputeAllFromArrays_ReturnsConsistentResults()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        var lbl = new[] { 1.0, 0.0, 0.8, 0.0, 0.7 };

        var (p, r, n) = RankingMetrics.ComputeAllFromArrays(pred, lbl, k: 3);

        Assert.Equal(RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 3), p, 10);
        Assert.Equal(RankingMetrics.ComputeRecallAtK(pred, lbl, k: 3), r, 10);
        Assert.Equal(RankingMetrics.ComputeNdcgAtK(pred, lbl, k: 3), n, 10);
    }

    [Fact]
    public void ComputeAllFromArrays_Empty_ReturnsZeros()
    {
        var (p, r, n) = RankingMetrics.ComputeAllFromArrays([], [], k: 5);
        Assert.Equal(0.0, p, 10);
        Assert.Equal(0.0, r, 10);
        Assert.Equal(0.0, n, 10);
    }

    // === ComputeAll with strategy ===

    [Fact]
    public void ComputeAll_WithStrategy_ProducesValidMetrics()
    {
        var strategy = new LearnedScoringStrategy();
        var examples = new List<TrainingExample>();
        var rng = new Random(42);

        for (var i = 0; i < 20; i++)
        {
            var genreSim = rng.NextDouble();
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = genreSim,
                    CombinedCriticScore = rng.NextDouble(),
                    CollaborativeScore = rng.NextDouble()
                },
                Label = genreSim > 0.5 ? 0.85 : 0.1
            });
        }

        var (p, r, n) = RankingMetrics.ComputeAll(examples, strategy, k: 5);

        Assert.InRange(p, 0.0, 1.0);
        Assert.InRange(r, 0.0, 1.0);
        Assert.InRange(n, 0.0, 1.0);
    }

    [Fact]
    public void ComputeAll_EmptyExamples_ReturnsZeros()
    {
        var strategy = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var (p, r, n) = RankingMetrics.ComputeAll([], strategy);
        Assert.Equal(0.0, p, 10);
        Assert.Equal(0.0, r, 10);
        Assert.Equal(0.0, n, 10);
    }

    // === Consistency: P@K and R@K relationship ===

    [Fact]
    public void PrecisionAndRecall_AreConsistent()
    {
        // When K equals total items and all are relevant, both should be 1.0
        var pred = new[] { 0.9, 0.8, 0.7 };
        var lbl = new[] { 1.0, 0.8, 0.6 };

        var p = RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 3);
        var r = RankingMetrics.ComputeRecallAtK(pred, lbl, k: 3);

        Assert.Equal(1.0, p, 10);
        Assert.Equal(1.0, r, 10);
    }

    [Fact]
    public void IncreasingK_IncreasesRecall()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        var lbl = new[] { 0.0, 1.0, 0.0, 1.0, 0.0 };

        var r1 = RankingMetrics.ComputeRecallAtK(pred, lbl, k: 1);
        var r2 = RankingMetrics.ComputeRecallAtK(pred, lbl, k: 2);
        var r4 = RankingMetrics.ComputeRecallAtK(pred, lbl, k: 4);

        Assert.True(r2 >= r1, $"Recall@2 ({r2:F4}) should be >= Recall@1 ({r1:F4})");
        Assert.True(r4 >= r2, $"Recall@4 ({r4:F4}) should be >= Recall@2 ({r2:F4})");
    }

    [Fact]
    public void IncreasingK_DecreasesPrecision_WhenIrrelevantItemsAdded()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        var lbl = new[] { 1.0, 1.0, 0.0, 0.0, 0.0 };

        var p2 = RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 2);
        var p5 = RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 5);

        Assert.Equal(1.0, p2, 10);
        Assert.True(p5 < p2, $"P@5 ({p5:F4}) should be < P@2 ({p2:F4})");
    }

    // === NDCG monotonicity ===

    [Fact]
    public void NdcgAtK_IsOneForAllK_WhenPerfectRanking()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        var lbl = new[] { 1.0, 0.8, 0.6, 0.3, 0.0 };

        for (var k = 1; k <= 5; k++)
        {
            Assert.Equal(1.0, RankingMetrics.ComputeNdcgAtK(pred, lbl, k: k), 6);
        }
    }

    [Fact]
    public void NdcgAtK_KLargerThanN_SameAsFullList()
    {
        var pred = new[] { 0.9, 0.8, 0.7 };
        var lbl = new[] { 1.0, 0.5, 0.0 };

        var ndcgFull = RankingMetrics.ComputeNdcgAtK(pred, lbl, k: 3);
        var ndcgLargeK = RankingMetrics.ComputeNdcgAtK(pred, lbl, k: 100);

        Assert.Equal(ndcgFull, ndcgLargeK, 10);
    }

    // === ComputeAllFromArrays with custom threshold ===

    [Fact]
    public void ComputeAllFromArrays_CustomThreshold()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        var lbl = new[] { 0.9, 0.6, 0.4, 0.3, 0.1 };

        var (pLow, rLow, _) = RankingMetrics.ComputeAllFromArrays(pred, lbl, k: 3, relevanceThreshold: 0.5);
        var (pHigh, rHigh, _) = RankingMetrics.ComputeAllFromArrays(pred, lbl, k: 3, relevanceThreshold: 0.8);

        Assert.True(pLow >= pHigh, $"Lower threshold precision: {pLow:F4} vs {pHigh:F4}");
        Assert.Equal(1.0, rLow, 10);
        Assert.Equal(1.0, rHigh, 10);
    }

    // === ComputeAll with HeuristicScoringStrategy ===

    [Fact]
    public void ComputeAll_WithHeuristicStrategy_ProducesValidMetrics()
    {
        var strategy = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        var examples = new List<TrainingExample>();
        var rng = new Random(123);

        for (var i = 0; i < 15; i++)
        {
            var genreSim = rng.NextDouble();
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = genreSim,
                    CombinedCriticScore = rng.NextDouble(),
                    CollaborativeScore = rng.NextDouble()
                },
                Label = genreSim > 0.5 ? 0.9 : 0.05
            });
        }

        var (p, r, n) = RankingMetrics.ComputeAll(examples, strategy, k: 5);

        Assert.InRange(p, 0.0, 1.0);
        Assert.InRange(r, 0.0, 1.0);
        Assert.InRange(n, 0.0, 1.0);
    }

    // === Edge cases: all items relevant ===

    [Fact]
    public void PrecisionAtK_AllRelevant_ReturnsOne()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6 };
        var lbl = new[] { 1.0, 0.9, 0.8, 0.7 };
        Assert.Equal(1.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 4), 10);
    }

    [Fact]
    public void RecallAtK_AllRelevant_ReturnsOne()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6 };
        var lbl = new[] { 1.0, 0.9, 0.8, 0.7 };
        Assert.Equal(1.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 4), 10);
    }

    // === Edge case: tied scores ===

    [Fact]
    public void PrecisionAtK_TiedScores_StillComputes()
    {
        var pred = new[] { 0.5, 0.5, 0.5, 0.5, 0.5 };
        var lbl = new[] { 1.0, 0.0, 1.0, 0.0, 1.0 };

        var p = RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 3);
        Assert.InRange(p, 0.0, 1.0);
    }

    [Fact]
    public void NdcgAtK_TiedScores_StillComputes()
    {
        var pred = new[] { 0.5, 0.5, 0.5 };
        var lbl = new[] { 1.0, 0.5, 0.0 };

        var ndcg = RankingMetrics.ComputeNdcgAtK(pred, lbl, k: 3);
        Assert.InRange(ndcg, 0.0, 1.0);
    }

    // === Edge case: negative K ===

    [Fact]
    public void PrecisionAtK_NegativeK_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputePrecisionAtK(new[] { 0.9 }, new[] { 1.0 }, k: -1), 10);

    [Fact]
    public void RecallAtK_NegativeK_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputeRecallAtK(new[] { 0.9 }, new[] { 1.0 }, k: -1), 10);

    [Fact]
    public void NdcgAtK_NegativeK_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputeNdcgAtK(new[] { 0.9 }, new[] { 1.0 }, k: -1), 10);

    // === Edge case: single item ===

    [Fact]
    public void PrecisionAtK_SingleRelevantItem_ReturnsOne() =>
        Assert.Equal(1.0, RankingMetrics.ComputePrecisionAtK(new[] { 0.9 }, new[] { 1.0 }, k: 1), 10);

    [Fact]
    public void PrecisionAtK_SingleIrrelevantItem_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputePrecisionAtK(new[] { 0.9 }, new[] { 0.1 }, k: 1), 10);

    [Fact]
    public void RecallAtK_SingleRelevantItem_K1_ReturnsOne() =>
        Assert.Equal(1.0, RankingMetrics.ComputeRecallAtK(new[] { 0.9 }, new[] { 1.0 }, k: 1), 10);

    // === Default parameters ===

    [Fact]
    public void ComputeAll_UsesDefaultKAndThreshold()
    {
        var strategy = new LearnedScoringStrategy();
        var examples = new List<TrainingExample>();
        var rng = new Random(77);

        for (var i = 0; i < 30; i++)
        {
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = rng.NextDouble(),
                    CombinedCriticScore = rng.NextDouble(),
                    CollaborativeScore = rng.NextDouble()
                },
                Label = rng.NextDouble()
            });
        }

        var (p, r, n) = RankingMetrics.ComputeAll(examples, strategy);

        Assert.InRange(p, 0.0, 1.0);
        Assert.InRange(r, 0.0, 1.0);
        Assert.InRange(n, 0.0, 1.0);
    }

    [Fact]
    public void ComputeAllFromArrays_UsesDefaultKAndThreshold()
    {
        var pred = new double[15];
        var lbl = new double[15];
        var rng = new Random(99);

        for (var i = 0; i < 15; i++)
        {
            pred[i] = rng.NextDouble();
            lbl[i] = rng.NextDouble();
        }

        var (p, r, n) = RankingMetrics.ComputeAllFromArrays(pred, lbl);

        Assert.InRange(p, 0.0, 1.0);
        Assert.InRange(r, 0.0, 1.0);
        Assert.InRange(n, 0.0, 1.0);
    }

    // === Recall with custom threshold ===

    [Fact]
    public void RecallAtK_CustomThreshold()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6 };
        var lbl = new[] { 0.9, 0.7, 0.4, 0.2 };

        Assert.Equal(1.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 2, relevanceThreshold: 0.5), 10);
        Assert.Equal(1.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 1, relevanceThreshold: 0.8), 10);
        Assert.Equal(2.0 / 3.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 2, relevanceThreshold: 0.3), 10);
    }

    // === NDCG swapped pair ===

    [Fact]
    public void NdcgAtK_SwappedPair_LowerThanPerfect()
    {
        var perfect = RankingMetrics.ComputeNdcgAtK(new[] { 0.9, 0.1 }, new[] { 1.0, 0.2 }, k: 2);
        var swapped = RankingMetrics.ComputeNdcgAtK(new[] { 0.9, 0.1 }, new[] { 0.2, 1.0 }, k: 2);

        Assert.Equal(1.0, perfect, 6);
        Assert.True(swapped < perfect, $"Swapped ({swapped:F4}) should be < perfect ({perfect:F4})");
    }
}
