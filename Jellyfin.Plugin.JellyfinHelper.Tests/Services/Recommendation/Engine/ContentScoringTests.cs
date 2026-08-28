using System.Diagnostics;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     xUnit collection marker for tests that touch process-global state related to ContentScoring - the static ParallelArrayMismatchCount counter and the Listeners chain used by Assert(bool).
/// </summary>
[CollectionDefinition(Name)]
public sealed class ContentScoringGlobalStateCollection
{
    /// <summary>The named-collection identifier used by <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "ContentScoring global state";
}

/// <summary>
///     Tests for ContentScoring static helper methods.
/// </summary>
[Collection(ContentScoringGlobalStateCollection.Name)]
public sealed class ContentScoringTests
{
    // ComputePopularityScore Tests

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
        // collaborativeScore * 0.8 > 1.0 when collaborativeScore > 1.25 Since collaborative scores are normalized to [0,1], this shouldn't happen in practice, but the Clamp guarantees contract compliance.
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
        // Negative collaborative score -> falls through to critic fallback (since !(neg > 0))
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

    // ComputeContentNearestNeighborScore parallel-array mismatch guard

    [Fact]
    public void ComputeContentNearestNeighborScore_ParallelArrayMismatch_DegradesGracefully()
    {
        // Silent-degradation guard: when the parallel arrays disagree in length (always a bug), the method must NOT throw AND must still produce a score that reflects at least the genre dimension (the primary 50% signal).
        var candidateGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action", "SciFi" };
        var watchedGenres = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "Action" },
            new(StringComparer.OrdinalIgnoreCase) { "SciFi", "Drama" }
        };
        // Deliberate mismatch: people list has fewer entries than genre list.
        var watchedPeople = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "Actor A" }
        };
        // Deliberate mismatch: studio list is empty.
        var watchedStudios = new List<HashSet<string>>();

        var listeners = Trace.Listeners;
        var savedListeners = new TraceListener[listeners.Count];
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

            // Genre-only path: candidate {Action, SciFi} vs first watched {Action} -> Jaccard 1/2 (Action shared, SciFi only in candidate).
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
        // Positive-path check: when all three parallel arrays have equal length the counter must stay flat. Guards against a stray increment path (e.g.
        var candidateGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" };
        var watchedGenres = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "Action" }
        };
        var watchedPeople = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "Actor A" }
        };
        var watchedStudios = new List<HashSet<string>>
        {
            new(StringComparer.OrdinalIgnoreCase) { "Studio X" }
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

    [Fact]
    public void ComputeContentNearestNeighborScore_NoWatchedItems_ReturnsZero()
    {
        // Cold-start user with no history: the empty-set guard must short-circuit to 0.0 BEFORE the parallel-array length check runs.
        var candidateGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" };
        var watchedGenres = new List<HashSet<string>>();
        var watchedPeople = new List<HashSet<string>>();
        var watchedStudios = new List<HashSet<string>>();

        var before = ContentScoring.ParallelArrayMismatchCount;

        var score = ContentScoring.ComputeContentNearestNeighborScore(
            candidateGenres,
            candidatePeople: null,
            candidateStudios: null,
            watchedGenres,
            watchedPeople,
            watchedStudios);

        Assert.Equal(0.0, score);
        Assert.Equal(before, ContentScoring.ParallelArrayMismatchCount);
    }

    // NormalizeCriticRating Tests

    [Fact]
    public void NormalizeCriticRating_ValidPercentage_NormalizesToZeroOne()
    {
        // A finite, in-range Tomatometer takes the normal path: divide by 100, not the 0.5 fallback.
        var result = ContentScoring.NormalizeCriticRating(80f);
        Assert.Equal(0.80, result, 10);
    }

    [Fact]
    public void NormalizeCriticRating_AboveHundred_ClampsToOne()
    {
        // Values above 100 must saturate at 1.0 per the Math.Clamp upper bound.
        var result = ContentScoring.NormalizeCriticRating(150f);
        Assert.Equal(1.0, result, 10);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NormalizeCriticRating_MissingOrNegativeOrNonFinite_ReturnsNeutralHalf(float? criticRating)
    {
        // Every guarded input returns the documented neutral 0.5 - a broken guard that fell
        // through (e.g. NaN/100) would produce NaN or a negative value and fail here.
        var result = ContentScoring.NormalizeCriticRating(criticRating);
        Assert.Equal(0.5, result, 10);
    }

    // ComputeCombinedCriticScore Tests

    [Fact]
    public void ComputeCombinedCriticScore_BothSources_Blends55Tmdb45Tomatometer()
    {
        // Both present: locks the exact documented blend 0.55*tmdb + 0.45*tomatometer.
        var result = ContentScoring.ComputeCombinedCriticScore(8f, 60f);
        Assert.Equal((0.55 * 0.8) + (0.45 * 0.6), result, 10); // 0.71
    }

    [Fact]
    public void ComputeCombinedCriticScore_OnlyCritic_UsesTomatometerExclusively()
    {
        // Community rating absent but critic present: must use Tomatometer/100 only,
        // not the neutral 0.5 fallback nor the blended path.
        var result = ContentScoring.ComputeCombinedCriticScore((float?)null, 90f);
        Assert.Equal(0.90, result, 10);
    }
}
