using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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

    // === FIX-3 · Deterministic exploration seed ===

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

    // === FIX-2 · Exploration reaches beyond the top count·5 MMR window ===

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
