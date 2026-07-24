using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="DiversityReranker"/>: contract of <c>ApplyDiversityReranking</c>
///     on edge cases (empty input, zero count).
///     <para>
///         Full MMR behaviour with the multi-dimensional similarity blend
///         (genre 50% + studio 30% + era 20%) is exercised through integration tests
///         that construct real <see cref="BaseItem"/> derivatives; here we only lock down
///         the public contract for degenerate inputs and the new seed/pool guarantees.
///     </para>
/// </summary>
public class DiversityRerankerTests
{
    [Fact]
    public void ApplyDiversityReranking_EmptyList_ReturnsEmpty()
    {
        var result = DiversityReranker.ApplyDiversityReranking(
            new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>(),
            5);
        Assert.Empty(result);
    }

    [Fact]
    public void ApplyDiversityReranking_ZeroCount_ReturnsEmpty()
    {
        // Use a non-empty candidate list so we actually exercise the count <= 0 guard.
        // With an empty list the method would return empty regardless of the guard, which
        // means removing the guard could regress silently. Access to (BaseItem)null! is
        // safe here because the guard returns before any Item field is dereferenced.
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (null!, 1.0, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.ApplyDiversityReranking(candidates, 0);

        Assert.Empty(result);
    }

    [Fact]
    public void ApplyDiversityReranking_SameSeed_ProducesIdenticalSelection()
    {
        // Build a large candidate list so exploration slots are actually filled from the
        // widened pool. With 400 candidates and count=20 the exploration band spans
        // ranks 100..400, which is more than enough for the seeded RNG to matter.
        var candidates = BuildLinearlyDecreasingCandidates(400);
        const int count = 20;
        const int seed = 12345;

        var runA = DiversityReranker.ApplyDiversityReranking(candidates, count, seed);
        var runB = DiversityReranker.ApplyDiversityReranking(candidates, count, seed);

        Assert.Equal(runA.Count, runB.Count);
        for (var i = 0; i < runA.Count; i++)
        {
            Assert.Equal(runA[i].Item.Id, runB[i].Item.Id);
        }
    }

    [Fact]
    public void ApplyDiversityReranking_DifferentSeeds_ProduceDifferentTails()
    {
        // Two distinct seeds should sample different exploration picks even though the MMR
        // head is deterministic. We only assert the tail differs (last EngineConstants.ExplorationSlotCount
        // slots) because MMR itself is deterministic.
        var candidates = BuildLinearlyDecreasingCandidates(400);
        const int count = 20;

        var runA = DiversityReranker.ApplyDiversityReranking(candidates, count, seed: 1);
        var runB = DiversityReranker.ApplyDiversityReranking(candidates, count, seed: 2);

        var tailA = runA.TakeLast(EngineConstants.ExplorationSlotCount).Select(x => x.Item.Id).ToList();
        var tailB = runB.TakeLast(EngineConstants.ExplorationSlotCount).Select(x => x.Item.Id).ToList();

        // Highly unlikely to collide on both seeds; a single mismatch is enough for divergence.
        Assert.NotEqual(tailA, tailB);
    }

    [Fact]
    public void ApplyDiversityReranking_ExplorationCanSelectFromWidenedBand()
    {
        // Build 400 candidates; MMR pool = top 100 (count·5), exploration band = ranks 100..400.
        // Run enough deterministic seeds to statistically hit the widened band.
        // The regression this locks down: previously exploration only ever picked from ranks 0..100,
        // so a rank ≥ 100 pick was impossible.
        var candidates = BuildLinearlyDecreasingCandidates(400);
        const int count = 20;
        // Reference the shared constant so any future retune touches a single line.
        var mmrPoolSize = count * DiversityReranker.MmrPoolFactor;

        var reachedWidenedBand = false;
        for (var seed = 0; seed < 40 && !reachedWidenedBand; seed++)
        {
            var result = DiversityReranker.ApplyDiversityReranking(candidates, count, seed);
            foreach (var (item, _, _, _, _) in result)
            {
                var rank = candidates.FindIndex(c => c.Item.Id == item.Id);
                if (rank >= mmrPoolSize)
                {
                    reachedWidenedBand = true;
                    break;
                }
            }
        }

        Assert.True(reachedWidenedBand,
            "Exploration must be able to reach picks from the widened band (rank ≥ count·5).");
    }

    [Fact]
    public void ApplyDiversityReranking_NullSeed_UsesRandomSharedFallback()
    {
        // Locks in the documented "opt-in to non-deterministic exploration" fallback: when the
        // caller passes seed=null (the default), the method must still return a valid, complete
        // recommendation list rather than throwing. Two runs are almost certain to differ in the
        // tail because Random.Shared is not deterministic across invocations. We only assert the
        // fallback is reachable and produces valid output — the "does the shape differ" check
        // is intentionally loose because process-wide entropy could theoretically produce a
        // collision (extremely unlikely with 320-element pools).
        var candidates = BuildLinearlyDecreasingCandidates(400);
        const int count = 20;

        var runA = DiversityReranker.ApplyDiversityReranking(candidates, count);
        var runB = DiversityReranker.ApplyDiversityReranking(candidates, count);

        Assert.Equal(count, runA.Count);
        Assert.Equal(count, runB.Count);
        Assert.Equal(runA.Count, runA.Select(x => x.Item.Id).Distinct().Count());
        Assert.Equal(runB.Count, runB.Select(x => x.Item.Id).Distinct().Count());
    }

    [Fact]
    public void ApplyDiversityReranking_ExplorationPoolExcludesMmrPicks()
    {
        // Distinct-Item invariant: no BaseItem may appear twice in the result. Previously the MMR
        // leftover fallback could theoretically re-pick an already selected item; the guard uses
        // an explicit mmrSelectedIds set.
        var candidates = BuildLinearlyDecreasingCandidates(400);
        const int count = 20;

        for (var seed = 0; seed < 5; seed++)
        {
            var result = DiversityReranker.ApplyDiversityReranking(candidates, count, seed);
            var distinct = result.Select(r => r.Item.Id).Distinct().Count();
            Assert.Equal(result.Count, distinct);
        }
    }

    // === Contract: fewer candidates than count returns fully sorted list ===

    [Fact]
    public void ApplyDiversityReranking_CandidatesLessThanOrEqualToCount_ReturnsAllSortedByScore()
    {
        // When there are fewer candidates than requested, the method must skip MMR entirely
        // and just return everything sorted by score DESC. An early implementation
        // reused the MMR path with count=candidates.Count which could still reshuffle by MMR
        // penalties, changing the returned order in unpredictable ways.
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (new Movie { Id = Guid.NewGuid(), Name = "A" }, 0.3, string.Empty, string.Empty, null),
            (new Movie { Id = Guid.NewGuid(), Name = "B" }, 0.9, string.Empty, string.Empty, null),
            (new Movie { Id = Guid.NewGuid(), Name = "C" }, 0.6, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.ApplyDiversityReranking(candidates, 10);

        Assert.Equal(3, result.Count);
        Assert.Equal(0.9, result[0].Score);
        Assert.Equal(0.6, result[1].Score);
        Assert.Equal(0.3, result[2].Score);
    }

    [Fact]
    public void ApplyDiversityReranking_CandidatesExactlyMatchCount_ReturnsSortedByScore()
    {
        // Boundary: candidates.Count == count triggers the same short-circuit.
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (new Movie { Id = Guid.NewGuid() }, 0.1, string.Empty, string.Empty, null),
            (new Movie { Id = Guid.NewGuid() }, 0.5, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.ApplyDiversityReranking(candidates, 2);

        Assert.Equal(2, result.Count);
        Assert.Equal(0.5, result[0].Score);
        Assert.Equal(0.1, result[1].Score);
    }

    // === Contract: negative count returns empty ===

    [Fact]
    public void ApplyDiversityReranking_NegativeCount_ReturnsEmpty()
    {
        var candidates = BuildLinearlyDecreasingCandidates(10);
        Assert.Empty(DiversityReranker.ApplyDiversityReranking(candidates, -3));
        Assert.Empty(DiversityReranker.ApplyDiversityReranking(candidates, int.MinValue));
    }

    // === DeduplicateSeries ===

    [Fact]
    public void DeduplicateSeries_EmptyInput_ReturnsEmpty()
    {
        var result = DiversityReranker.DeduplicateSeries(
            new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>());
        Assert.Empty(result);
    }

    [Fact]
    public void DeduplicateSeries_MoviesArePassThrough()
    {
        // Movies (and non-series items) must never be deduplicated.
        var m1 = new Movie { Id = Guid.NewGuid(), Name = "M1" };
        var m2 = new Movie { Id = Guid.NewGuid(), Name = "M2" };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (m1, 0.9, string.Empty, string.Empty, null),
            (m2, 0.8, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Item.Id == m1.Id);
        Assert.Contains(result, r => r.Item.Id == m2.Id);
    }

    [Fact]
    public void DeduplicateSeries_KeepsHighestScoredEpisodePerSeries()
    {
        // Regression guard: if two episodes of the same series appear, only the highest-scored
        // episode may survive. Order-of-appearance MUST NOT matter (in-place replacement bug).
        var seriesId = Guid.NewGuid();
        var lowEpisode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var highEpisode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (lowEpisode, 0.3, string.Empty, string.Empty, null),
            (highEpisode, 0.9, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Single(result);
        Assert.Equal(highEpisode.Id, result[0].Item.Id);
        Assert.Equal(0.9, result[0].Score);
    }

    [Fact]
    public void DeduplicateSeries_KeepsHighestEvenWhenFirstIsHighest()
    {
        // Mirror of the previous test, but with the highest-scored episode listed first.
        // The dedup logic must be commutative with respect to input order.
        var seriesId = Guid.NewGuid();
        var high = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var low = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (high, 0.9, string.Empty, string.Empty, null),
            (low, 0.3, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Single(result);
        Assert.Equal(high.Id, result[0].Item.Id);
    }

    [Fact]
    public void DeduplicateSeries_TieOnScore_KeepsFirstOccurrence()
    {
        // On tie, the "strictly greater" comparison must not overwrite the first
        // occurrence. This locks the tie-break to be "first wins", which is what the
        // implementation currently does (uses `>` not `>=`).
        var seriesId = Guid.NewGuid();
        var first = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var second = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (first, 0.5, string.Empty, string.Empty, null),
            (second, 0.5, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Single(result);
        Assert.Equal(first.Id, result[0].Item.Id);
    }

    [Fact]
    public void DeduplicateSeries_DifferentSeriesIdsAreKeptSeparately()
    {
        // Two episodes from different series must NOT be deduped against each other.
        var ep1 = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid() };
        var ep2 = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid() };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (ep1, 0.5, string.Empty, string.Empty, null),
            (ep2, 0.4, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DeduplicateSeries_EpisodeWithEmptySeriesIdIsTreatedAsUngrouped()
    {
        // An Episode.SeriesId == Guid.Empty may not be treated as "this series"
        // otherwise every orphan episode would collapse into a single result entry.
        var orphan1 = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.Empty };
        var orphan2 = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.Empty };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (orphan1, 0.5, string.Empty, string.Empty, null),
            (orphan2, 0.4, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DeduplicateSeries_SeasonUsesSeriesIdForGrouping()
    {
        // Regression: a Season and an Episode both belonging to the same series must
        // collapse to a single entry (the higher-scored one wins).
        var seriesId = Guid.NewGuid();
        var season = new Season { Id = Guid.NewGuid(), SeriesId = seriesId };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (season, 0.4, string.Empty, string.Empty, null),
            (episode, 0.7, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Single(result);
        Assert.Equal(episode.Id, result[0].Item.Id);
    }

    [Fact]
    public void DeduplicateSeries_SeriesUsesOwnIdForGrouping()
    {
        // A Series itself must group by its own Id, not require SeriesId (which is unset).
        var seriesId = Guid.NewGuid();
        var series = new Series { Id = seriesId };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (series, 0.9, string.Empty, string.Empty, null),
            (episode, 0.4, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Single(result);
        Assert.Equal(series.Id, result[0].Item.Id);
    }

    [Fact]
    public void DeduplicateSeries_SeriesWithEmptyIdIsTreatedAsUngrouped()
    {
        // Series.Id == Guid.Empty must not collapse all orphan series entries into one.
        var s1 = new Series { Id = Guid.Empty };
        var s2 = new Series { Id = Guid.Empty };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (s1, 0.5, string.Empty, string.Empty, null),
            (s2, 0.4, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DeduplicateSeries_MixedMoviesAndSeries_MoviesUntouchedSeriesDeduped()
    {
        var seriesId = Guid.NewGuid();
        var movie1 = new Movie { Id = Guid.NewGuid() };
        var movie2 = new Movie { Id = Guid.NewGuid() };
        var ep1 = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var ep2 = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };

        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (movie1, 0.5, string.Empty, string.Empty, null),
            (ep1, 0.6, string.Empty, string.Empty, null),
            (movie2, 0.4, string.Empty, string.Empty, null),
            (ep2, 0.8, string.Empty, string.Empty, null)
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Equal(3, result.Count); // 2 movies + 1 winning episode
        Assert.Contains(result, r => r.Item.Id == movie1.Id);
        Assert.Contains(result, r => r.Item.Id == movie2.Id);
        Assert.Contains(result, r => r.Item.Id == ep2.Id);
        Assert.DoesNotContain(result, r => r.Item.Id == ep1.Id);
    }

    [Fact]
    public void DeduplicateSeries_PreservesReasonAndRelatedItemOfWinner()
    {
        // Regression: when replacing the loser with the winner, ALL tuple fields must be
        // carried over (Reason, ReasonKey, RelatedItem) — not just the Score. Otherwise the
        // UI could show the loser's reason attached to the winner's score.
        var seriesId = Guid.NewGuid();
        var lowEp = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var highEp = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var candidates = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        {
            (lowEp, 0.3, "loserReason", "loser.key", "loserRelated"),
            (highEp, 0.9, "winnerReason", "winner.key", "winnerRelated")
        };

        var result = DiversityReranker.DeduplicateSeries(candidates);

        Assert.Single(result);
        Assert.Equal("winnerReason", result[0].Reason);
        Assert.Equal("winner.key", result[0].ReasonKey);
        Assert.Equal("winnerRelated", result[0].RelatedItem);
    }

    /// <summary>
    ///     Builds a linearly decreasing candidate list of the requested length. Each item is a
    ///     <see cref="Movie"/> with a unique <see cref="Guid"/> and a distinct decreasing score,
    ///     which keeps the MMR ordering deterministic and stable across test runs.
    /// </summary>
    private static List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        BuildLinearlyDecreasingCandidates(int size)
    {
        var list = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>(size);
        for (var i = 0; i < size; i++)
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = $"Movie {i}"
            };
            list.Add((movie, 1.0 - (i / (double)size), string.Empty, string.Empty, null));
        }

        return list;
    }
}
