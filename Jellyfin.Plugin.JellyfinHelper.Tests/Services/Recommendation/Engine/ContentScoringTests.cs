using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="ContentScoring"/> static helper methods.
///     <para>
///         Roadmap v3 (C3): the previously-tested
///         <c>TrainingDataBuilder.ComputeCollectionProgressionBoostFromCache</c> legacy method
///         was removed because it was dead code — only reflection-based tests referenced it and
///         its 0.0/0.3/0.5 flat heuristic had already been superseded by the diminishing-returns
///         <c>ComputeCollectionProgressionBoostWithCounts</c> used in both Phase 1 and Phase 3
///         of <c>TrainingDataBuilder</c>. The remaining formula is covered end-to-end via the
///         Phase 1 / Phase 3 training paths, which exercise the same math with real BoxSet inputs.
///     </para>
/// </summary>
public sealed class ContentScoringTests
{
    // ============================================================
    // ComputePopularityScore Tests
    // ============================================================

    [Fact]
    public void ComputePopularityScore_CollaborativePositive_ScalesBy08()
    {
        var result = ContentScoring.ComputePopularityScore(0.5, 0.8);
        Assert.Equal(0.4, result, 10); // 0.5 * 0.8 = 0.4
    }

    [Fact]
    public void ComputePopularityScore_CollaborativeZero_UsesCriticFallback()
    {
        var result = ContentScoring.ComputePopularityScore(0.0, 0.8);
        Assert.Equal(0.24, result, 10); // 0.8 * 0.3 = 0.24
    }

    [Fact]
    public void ComputePopularityScore_CollaborativeHigh_ClampsToOne()
    {
        // collaborativeScore * 0.8 > 1.0 when collaborativeScore > 1.25
        // Since collaborative scores are normalized to [0,1], this shouldn't happen in practice,
        // but the Clamp guarantees contract compliance.
        var result = ContentScoring.ComputePopularityScore(1.5, 0.0);
        Assert.Equal(1.0, result, 10); // Clamped
    }

    [Fact]
    public void ComputePopularityScore_FallbackPath_ClampsToOne()
    {
        // Edge case: combinedCriticScore * 0.3 should be clamped
        // Even though combinedCriticScore is normally [0,1], defensive clamping is verified.
        var result = ContentScoring.ComputePopularityScore(0.0, 4.0);
        Assert.Equal(1.0, result, 10); // 4.0 * 0.3 = 1.2, clamped to 1.0
    }

    [Fact]
    public void ComputePopularityScore_FallbackPath_ClampsToZero()
    {
        // Negative input (defensive)
        var result = ContentScoring.ComputePopularityScore(0.0, -1.0);
        Assert.Equal(0.0, result, 10); // -1.0 * 0.3 = -0.3, clamped to 0.0
    }

    [Fact]
    public void ComputePopularityScore_BothZero_ReturnsZero()
    {
        var result = ContentScoring.ComputePopularityScore(0.0, 0.0);
        Assert.Equal(0.0, result, 10);
    }

    [Fact]
    public void ComputePopularityScore_CollaborativeNegative_UsesFallback()
    {
        // Negative collaborative score → falls through to critic fallback (since !(neg > 0))
        var result = ContentScoring.ComputePopularityScore(-0.1, 0.5);
        Assert.Equal(0.15, result, 10); // 0.5 * 0.3 = 0.15
    }

    [Fact]
    public void ComputePopularityScore_ResultAlwaysInZeroOneRange()
    {
        // Exhaustive boundary check
        var testCases = new (double collab, double critic)[]
        {
            (0.0, 0.0), (1.0, 1.0), (0.0, 1.0), (1.0, 0.0),
            (0.5, 0.5), (0.01, 0.99), (0.99, 0.01)
        };

        foreach (var (collab, critic) in testCases)
        {
            var result = ContentScoring.ComputePopularityScore(collab, critic);
            Assert.InRange(result, 0.0, 1.0);
        }
    }

    // ============================================================
    // ComputeContentNearestNeighborScore parallel-array mismatch guard
    // ============================================================

    [Fact]
    public void ComputeContentNearestNeighborScore_ParallelArrayMismatch_DegradesGracefully()
    {
        // Silent-degradation guard: when the parallel arrays disagree in length (always a bug),
        // the method must NOT throw AND must still produce a score that reflects at least the
        // genre dimension (the primary 50% signal). It must also record the mismatch on the
        // process-lifetime counter so operators / diagnostics can observe the degraded state
        // even in Release builds where Debug.Assert is a no-op.
        //
        // Debug.Assert in Debug builds would abort the test run via the default trace listener,
        // so we scope a listener swap that swallows the assertion while the method runs. The
        // Trace.TraceWarning emitted on the first mismatch is orthogonal to this — we do not
        // assert on its exact wording (that would be brittle), only on the counter delta.
        var candidateGenres = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "Action", "SciFi" };
        var watchedGenres = new List<HashSet<string>>
        {
            new(System.StringComparer.OrdinalIgnoreCase) { "Action" },
            new(System.StringComparer.OrdinalIgnoreCase) { "SciFi", "Drama" }
        };
        // Deliberate mismatch: people list has fewer entries than genre list.
        var watchedPeople = new List<HashSet<string>>
        {
            new(System.StringComparer.OrdinalIgnoreCase) { "Actor A" }
        };
        // Deliberate mismatch: studio list is empty.
        var watchedStudios = new List<HashSet<string>>();

        var listeners = System.Diagnostics.Trace.Listeners;
        var savedListeners = new System.Diagnostics.TraceListener[listeners.Count];
        listeners.CopyTo(savedListeners, 0);
        listeners.Clear();
        try
        {
            var before = ContentScoring.ParallelArrayMismatchCount;

            var score = ContentScoring.ComputeContentNearestNeighborScore(
                candidateGenres,
                candidatePeople: null,
                candidateStudios: null,
                watchedGenres,
                watchedPeople,
                watchedStudios);

            // Genre-only path: candidate {Action, SciFi} vs first watched {Action} → Jaccard 1/2
            // (Action shared, SciFi only in candidate). Second watched {SciFi, Drama} vs candidate
            // gives 1/3 (SciFi shared). Max composite is 0.5 × 0.5 = 0.25 from the first row —
            // the people/studio contributions are 0 due to the mismatch guard degrading them.
            Assert.InRange(score, 0.0, 1.0);
            Assert.True(score > 0.0, $"Score must reflect the surviving genre signal, got {score}");

            var after = ContentScoring.ParallelArrayMismatchCount;
            Assert.True(after > before,
                $"Mismatch counter must increment on parallel-array length disagreement (before={before}, after={after})");
        }
        finally
        {
            foreach (var listener in savedListeners)
            {
                listeners.Add(listener);
            }
        }
    }

    [Fact]
    public void ComputeContentNearestNeighborScore_MatchedArrays_DoNotIncrementMismatchCounter()
    {
        // Positive-path check: when all three parallel arrays have equal length the counter
        // must stay flat. Guards against a stray increment path (e.g. off-by-one) that would
        // otherwise silently poison the counter for the whole process.
        var candidateGenres = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "Action" };
        var watchedGenres = new List<HashSet<string>>
        {
            new(System.StringComparer.OrdinalIgnoreCase) { "Action" }
        };
        var watchedPeople = new List<HashSet<string>>
        {
            new(System.StringComparer.OrdinalIgnoreCase) { "Actor A" }
        };
        var watchedStudios = new List<HashSet<string>>
        {
            new(System.StringComparer.OrdinalIgnoreCase) { "Studio X" }
        };

        var before = ContentScoring.ParallelArrayMismatchCount;

        var score = ContentScoring.ComputeContentNearestNeighborScore(
            candidateGenres,
            candidatePeople: null,
            candidateStudios: null,
            watchedGenres,
            watchedPeople,
            watchedStudios);

        var after = ContentScoring.ParallelArrayMismatchCount;
        Assert.Equal(before, after);
        Assert.InRange(score, 0.0, 1.0);
    }
}
