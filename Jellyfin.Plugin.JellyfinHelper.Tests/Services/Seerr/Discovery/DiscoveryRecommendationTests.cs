using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests the setter guards on <see cref="DiscoveryRecommendation"/>.
///     The class stores three <c>double</c> fields - <see cref="DiscoveryRecommendation.Score"/>,
///     <see cref="DiscoveryRecommendation.TmdbRating"/>, and <see cref="DiscoveryRecommendation.Popularity"/> -
///     each protected by an inline setter that must:
///     <list type="bullet">
///         <item>Reject non-finite input (<c>NaN</c>, ±<c>Infinity</c>) by coercing to <c>0</c>.</item>
///         <item>Clamp <c>Score</c> to [0.0, 1.0] and <c>TmdbRating</c> to [0.0, 10.0].</item>
///         <item>Coerce negative or zero <c>Popularity</c> values to <c>0</c>.</item>
///     </list>
///     <para>
///         BUG SURFACES: without these guards a poisoned candidate (e.g. TMDb returning
///         <c>NaN</c> for a broken poll) would either crash <c>JsonSerializer</c> at cache
///         persistence time (System.Text.Json rejects NaN by default) or silently propagate
///         a non-finite value into the training pipeline, corrupting the neural weights on
///         the next training pass. These tests pin every failure mode individually so a
///         regression cannot re-introduce them one at a time.
///     </para>
/// </summary>
public sealed class DiscoveryRecommendationTests
{
    // ---------------- Score ----------------

    [Fact]
    public void Score_NaN_CoercedToZero()
    {
        var sut = new DiscoveryRecommendation { Score = double.NaN };
        Assert.Equal(0.0, sut.Score);
    }

    [Fact]
    public void Score_PositiveInfinity_CoercedToZero()
    {
        // Note: an "obvious" implementation would clamp Infinity to 1.0. This one deliberately
        // treats non-finite as "unknown/broken input" and returns 0, forcing the item to sort
        // to the bottom rather than the top. An earlier build clamped Infinity
        // to 1.0 which meant a single JSON parse bug on the TMDb side made every affected
        // candidate #1 in the ranking.
        var sut = new DiscoveryRecommendation { Score = double.PositiveInfinity };
        Assert.Equal(0.0, sut.Score);
    }

    [Fact]
    public void Score_NegativeInfinity_CoercedToZero()
    {
        var sut = new DiscoveryRecommendation { Score = double.NegativeInfinity };
        Assert.Equal(0.0, sut.Score);
    }

    [Fact]
    public void Score_AboveOne_ClampedToOne()
    {
        var sut = new DiscoveryRecommendation { Score = 5.7 };
        Assert.Equal(1.0, sut.Score);
    }

    [Fact]
    public void Score_BelowZero_ClampedToZero()
    {
        var sut = new DiscoveryRecommendation { Score = -0.42 };
        Assert.Equal(0.0, sut.Score);
    }

    [Fact]
    public void Score_InRange_PreservedExactly()
    {
        var sut = new DiscoveryRecommendation { Score = 0.7345 };
        Assert.Equal(0.7345, sut.Score);
    }

    [Fact]
    public void Score_ExactBoundaries_Preserved()
    {
        var atZero = new DiscoveryRecommendation { Score = 0.0 };
        var atOne = new DiscoveryRecommendation { Score = 1.0 };
        Assert.Equal(0.0, atZero.Score);
        Assert.Equal(1.0, atOne.Score);
    }

    // ---------------- TmdbRating ----------------

    [Fact]
    public void TmdbRating_NaN_CoercedToZero()
    {
        var sut = new DiscoveryRecommendation { TmdbRating = double.NaN };
        Assert.Equal(0.0, sut.TmdbRating);
    }

    [Fact]
    public void TmdbRating_PositiveInfinity_CoercedToZero()
    {
        var sut = new DiscoveryRecommendation { TmdbRating = double.PositiveInfinity };
        Assert.Equal(0.0, sut.TmdbRating);
    }

    [Fact]
    public void TmdbRating_NegativeInfinity_CoercedToZero()
    {
        var sut = new DiscoveryRecommendation { TmdbRating = double.NegativeInfinity };
        Assert.Equal(0.0, sut.TmdbRating);
    }

    [Fact]
    public void TmdbRating_AboveTen_ClampedToTen()
    {
        var sut = new DiscoveryRecommendation { TmdbRating = 12.5 };
        Assert.Equal(10.0, sut.TmdbRating);
    }

    [Fact]
    public void TmdbRating_BelowZero_ClampedToZero()
    {
        var sut = new DiscoveryRecommendation { TmdbRating = -1.5 };
        Assert.Equal(0.0, sut.TmdbRating);
    }

    [Fact]
    public void TmdbRating_InRange_PreservedExactly()
    {
        // 7.4 is a plausible TMDb rating and must round-trip unchanged.
        var sut = new DiscoveryRecommendation { TmdbRating = 7.4 };
        Assert.Equal(7.4, sut.TmdbRating);
    }

    // ---------------- Popularity ----------------

    [Fact]
    public void Popularity_NaN_CoercedToZero()
    {
        var sut = new DiscoveryRecommendation { Popularity = double.NaN };
        Assert.Equal(0.0, sut.Popularity);
    }

    [Fact]
    public void Popularity_PositiveInfinity_CoercedToZero()
    {
        // Unlike Score/TmdbRating this field has NO upper clamp - Infinity would be
        // an arbitrarily-large valid value if we treated it "as-is". The guard therefore
        // has to reject non-finite explicitly, which this test pins.
        var sut = new DiscoveryRecommendation { Popularity = double.PositiveInfinity };
        Assert.Equal(0.0, sut.Popularity);
    }

    [Fact]
    public void Popularity_NegativeValue_CoercedToZero()
    {
        // Popularity has no meaningful negative interpretation (TMDb never emits <0).
        // A negative here indicates upstream corruption; the guard replaces it with 0
        // so ExternalCandidateFeatureBuilder.NormalizePopularity doesn't emit a
        // negative feature that shifts the neural model's inputs off-distribution.
        var sut = new DiscoveryRecommendation { Popularity = -3.14 };
        Assert.Equal(0.0, sut.Popularity);
    }

    [Fact]
    public void Popularity_Zero_StoredAsZero()
    {
        // The guard uses `> 0` - exactly 0 falls through to the else-branch and lands
        // at 0.0. This is behaviourally equivalent to the "positive value" branch for
        // the boundary but exercises the alternative code path.
        var sut = new DiscoveryRecommendation { Popularity = 0.0 };
        Assert.Equal(0.0, sut.Popularity);
    }

    [Fact]
    public void Popularity_PositiveValue_PreservedExactly()
    {
        var sut = new DiscoveryRecommendation { Popularity = 42.7 };
        Assert.Equal(42.7, sut.Popularity);
    }

    // ---------------- Default values ----------------

    [Fact]
    public void Defaults_MatchDocumentedContract()
    {
        // A freshly-constructed recommendation must have sane defaults so callers that
        // forget to set optional fields don't end up serialising `null`/undefined into
        // the discovery cache.
        var sut = new DiscoveryRecommendation();
        Assert.Equal(0, sut.TmdbId);
        Assert.Equal("movie", sut.MediaType);
        Assert.Equal(string.Empty, sut.Title);
        Assert.Null(sut.Year);
        Assert.Equal(0.0, sut.Score);
        Assert.Equal(string.Empty, sut.Reason);
        Assert.Equal(string.Empty, sut.ReasonKey);
        Assert.Null(sut.RelatedInfo);
        Assert.Empty(sut.Genres);
        Assert.Equal(0.0, sut.TmdbRating);
        Assert.Null(sut.PosterPath);
        Assert.Null(sut.Overview);
        Assert.False(sut.AlreadyRequested);
        Assert.Null(sut.KnownPeople);
        Assert.Equal(0.0, sut.Popularity);
    }
}