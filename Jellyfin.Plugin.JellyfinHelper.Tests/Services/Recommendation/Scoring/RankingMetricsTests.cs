using System;
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

    public static TheoryData<double[], double[], int, double> PrecisionAtKValueCases() => new()
    {
        { new[] { 0.9, 0.8, 0.7, 0.6, 0.1, 0.05 }, new[] { 1.0, 0.8, 0.7, 0.6, 0.0, 0.0 }, 4, 1.0 },
        { new[] { 0.9, 0.8, 0.1, 0.05 }, new[] { 0.0, 0.0, 1.0, 0.8 }, 2, 0.0 },
        { new[] { 0.95, 0.90, 0.85, 0.80, 0.75, 0.10, 0.05 }, new[] { 1.0, 0.0, 0.8, 0.0, 0.7, 1.0, 0.9 }, 5, 3.0 / 5.0 },
        { new[] { 0.9, 0.8, 0.7 }, new[] { 1.0, 0.8, 0.6 }, 10, 1.0 },
        { new[] { 0.9, 0.8, 0.7 }, new[] { 0.0, 0.1, 0.2 }, 3, 0.0 },
        { new[] { 0.9, 0.8, 0.7, 0.6 }, new[] { 1.0, 0.9, 0.8, 0.7 }, 4, 1.0 },
        { new[] { 0.9 }, new[] { 1.0 }, 1, 1.0 },
        { new[] { 0.9 }, new[] { 0.1 }, 1, 0.0 },
    };

    [Theory]
    [MemberData(nameof(PrecisionAtKValueCases))]
    public void PrecisionAtK_Value(double[] pred, double[] lbl, int k, double expected) =>
        Assert.Equal(expected, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: k), 10);

    [Fact]
    public void PrecisionAtK_Empty_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputePrecisionAtK([], [], k: 5), 10);

    [Fact]
    public void PrecisionAtK_CustomThreshold()
    {
        var pred = new[] { 0.9, 0.8, 0.7 };
        var lbl = new[] { 0.8, 0.6, 0.5 };
        Assert.Equal(2.0 / 3.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 3, relevanceThreshold: 0.5), 10);
        Assert.Equal(1.0 / 3.0, RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 3, relevanceThreshold: 0.7), 10);
    }

    public static TheoryData<double[], double[], int, double> RecallAtKValueCases() => new()
    {
        { new[] { 0.9, 0.8, 0.7, 0.1, 0.05 }, new[] { 1.0, 0.8, 0.6, 0.0, 0.0 }, 3, 1.0 },
        { new[] { 0.9, 0.8, 0.7, 0.6, 0.5 }, new[] { 1.0, 0.0, 0.8, 0.7, 0.6 }, 3, 2.0 / 4.0 },
        { new[] { 0.9, 0.8 }, new[] { 0.0, 0.1 }, 2, 0.0 },
        { new[] { 0.9, 0.8 }, new[] { 1.0, 0.8 }, 100, 1.0 },
        { new[] { 0.9, 0.8, 0.7, 0.6, 0.5 }, new[] { 0.0, 0.0, 1.0, 0.0, 0.0 }, 3, 1.0 },
        { new[] { 0.9, 0.8, 0.7, 0.6, 0.5 }, new[] { 0.0, 0.0, 0.0, 0.0, 1.0 }, 2, 0.0 },
        { new[] { 0.9, 0.8, 0.7, 0.6 }, new[] { 1.0, 0.9, 0.8, 0.7 }, 4, 1.0 },
        { new[] { 0.9 }, new[] { 1.0 }, 1, 1.0 },
    };

    [Theory]
    [MemberData(nameof(RecallAtKValueCases))]
    public void RecallAtK_Value(double[] pred, double[] lbl, int k, double expected) =>
        Assert.Equal(expected, RankingMetrics.ComputeRecallAtK(pred, lbl, k: k), 10);

    [Fact]
    public void RecallAtK_Empty_ReturnsZero() =>
        Assert.Equal(0.0, RankingMetrics.ComputeRecallAtK([], [], k: 5), 10);

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PrecisionAtK_NonPositiveK_ReturnsZero(int k) =>
        Assert.Equal(0.0, RankingMetrics.ComputePrecisionAtK(new[] { 0.9 }, new[] { 1.0 }, k: k), 10);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecallAtK_NonPositiveK_ReturnsZero(int k) =>
        Assert.Equal(0.0, RankingMetrics.ComputeRecallAtK(new[] { 0.9 }, new[] { 1.0 }, k: k), 10);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NdcgAtK_NonPositiveK_ReturnsZero(int k) =>
        Assert.Equal(0.0, RankingMetrics.ComputeNdcgAtK(new[] { 0.9 }, new[] { 1.0 }, k: k), 10);

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

    [Fact]
    public void RecallAtK_CustomThreshold()
    {
        var pred = new[] { 0.9, 0.8, 0.7, 0.6 };
        var lbl = new[] { 0.9, 0.7, 0.4, 0.2 };

        Assert.Equal(1.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 2, relevanceThreshold: 0.5), 10);
        Assert.Equal(1.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 1, relevanceThreshold: 0.8), 10);
        Assert.Equal(2.0 / 3.0, RankingMetrics.ComputeRecallAtK(pred, lbl, k: 2, relevanceThreshold: 0.3), 10);
    }

    [Fact]
    public void NdcgAtK_SwappedPair_LowerThanPerfect()
    {
        var perfect = RankingMetrics.ComputeNdcgAtK(new[] { 0.9, 0.1 }, new[] { 1.0, 0.2 }, k: 2);
        var swapped = RankingMetrics.ComputeNdcgAtK(new[] { 0.9, 0.1 }, new[] { 0.2, 1.0 }, k: 2);

        Assert.Equal(1.0, perfect, 6);
        Assert.True(swapped < perfect, $"Swapped ({swapped:F4}) should be < perfect ({perfect:F4})");
    }

    // Predictions and labels are paired per example; a length mismatch is caller error.
    // The guard must reject it up front rather than index into the shorter array.

    [Fact]
    public void PrecisionAtK_MismatchedArrayLengths_Throws()
    {
        var pred = new[] { 0.9, 0.8, 0.7 };
        var lbl = new[] { 1.0, 0.0 };

        var ex = Assert.Throws<ArgumentException>(() => RankingMetrics.ComputePrecisionAtK(pred, lbl, k: 3));
        Assert.Equal("labels", ex.ParamName);
    }

    [Fact]
    public void RecallAtK_MismatchedArrayLengths_Throws()
    {
        var pred = new[] { 0.9, 0.8, 0.7 };
        var lbl = new[] { 1.0, 0.0, 0.8, 0.6 };

        var ex = Assert.Throws<ArgumentException>(() => RankingMetrics.ComputeRecallAtK(pred, lbl, k: 3));
        Assert.Equal("labels", ex.ParamName);
    }

    [Fact]
    public void NdcgAtK_MismatchedArrayLengths_Throws()
    {
        var pred = new[] { 0.9, 0.8 };
        var lbl = new[] { 1.0, 0.0, 0.8 };

        var ex = Assert.Throws<ArgumentException>(() => RankingMetrics.ComputeNdcgAtK(pred, lbl, k: 2));
        Assert.Equal("labels", ex.ParamName);
    }
}
