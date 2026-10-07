using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

public class ParentalRatingHelperTests
{
    private static bool Exclude(bool adult, int[] genreIds, int? maxRating) =>
        ParentalRatingHelper.ShouldExclude(new TmdbDiscoverItem { Id = 1, Adult = adult, GenreIds = [.. genreIds] }, maxRating);

    // A null MaxParentalRating means an unrestricted account, and any rating at or above 141 is treated the
    // same way, so even adult content passes. Adult content is excluded for every finite rating below that.
    [Theory]
    [InlineData(true, new[] { 27 }, null, false)]
    [InlineData(true, new[] { 27, 53 }, 141, false)]
    [InlineData(false, new[] { 28, 12 }, 140, false)]
    [InlineData(true, new[] { 35 }, 100, true)]
    [InlineData(true, new[] { 35 }, 140, true)]
    public void ShouldExclude_AdultFlagAndUnrestrictedRatings(bool adult, int[] genreIds, int? maxRating, bool expected)
    {
        Assert.Equal(expected, Exclude(adult, genreIds, maxRating));
    }

    // For a strict child account (FSK-6, rating 60) a title is safe only when it carries a primary child genre
    // such as Family (10751), Kids (10762) or Music (10402). Animation (16) or Comedy (35) on their own read as
    // adult animation (Family Guy, Archer), and Action/Adventure/Fantasy/Crime/Thriller are excluded outright.
    [Theory]
    [InlineData(new[] { 16 }, true)]
    [InlineData(new[] { 16, 35 }, true)]
    [InlineData(new[] { 16, 80 }, true)]
    [InlineData(new[] { 35 }, true)]
    [InlineData(new[] { 12 }, true)]
    [InlineData(new[] { 28 }, true)]
    [InlineData(new[] { 14 }, true)]
    public void ShouldExclude_ChildAccount_WithoutPrimaryChildGenre_Excluded(int[] genreIds, bool expected)
    {
        Assert.Equal(expected, Exclude(adult: false, genreIds, 60));
    }

    [Theory]
    [InlineData(new[] { 16, 10751 }, false)]
    [InlineData(new[] { 16, 10762 }, false)]
    [InlineData(new[] { 10751, 35 }, false)]
    [InlineData(new[] { 10751 }, false)]
    [InlineData(new[] { 10762 }, false)]
    [InlineData(new[] { 10402 }, false)]
    [InlineData(new[] { 10751, 12 }, false)]
    [InlineData(new[] { 16, 10751, 12 }, false)]
    [InlineData(new[] { 10751, 14 }, false)]
    public void ShouldExclude_ChildAccount_WithPrimaryChildGenre_Allowed(int[] genreIds, bool expected)
    {
        Assert.Equal(expected, Exclude(adult: false, genreIds, 60));
    }

    // Even a Family title is excluded once a restricted genre such as Thriller is present.
    [Theory]
    [InlineData(new[] { 16, 10751, 53 }, 60, true)]
    [InlineData(new[] { 18 }, 50, true)]
    [InlineData(new[] { 27 }, 80, true)]
    [InlineData(new[] { 53 }, 100, true)]
    [InlineData(new[] { 10752 }, 80, true)]
    [InlineData(new[] { 28, 53 }, 80, true)]
    [InlineData(new[] { 28, 18 }, 80, false)]
    [InlineData(new[] { 27 }, 120, false)]
    public void ShouldExclude_RatingBandAppliesGenreBlacklist(int[] genreIds, int maxRating, bool expected)
    {
        Assert.Equal(expected, Exclude(adult: false, genreIds, maxRating));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(101)]
    [InlineData(120)]
    [InlineData(140)]
    public void ShouldExclude_IntermediateBand101To140_BlocksAdultFlagOnly(int maxRating)
    {
        // Pinned contract: between the teen blacklist (<=100) and unrestricted (>=141) only the
        // Adult flag filters. Genre blacklists do not apply here; unenriched candidates are
        // dropped upstream by the discovery service, not here.
        Assert.True(Exclude(adult: true, [35], maxRating));
        Assert.False(Exclude(adult: false, [27], maxRating));
        Assert.False(Exclude(adult: false, [53], maxRating));
        Assert.False(Exclude(adult: false, [28], maxRating));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(101)]
    [InlineData(140)]
    public void ShouldExclude_IntermediateBand_EmptyGenres_PassesFilter(int maxRating)
    {
        // Unenriched Trakt candidates carry GenreIds=[] and Adult=false. The parental helper
        // itself passes them; the SeerrDiscoveryService must drop unenriched+restricted items.
        Assert.False(Exclude(adult: false, [], maxRating));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ShouldExclude_UnrestrictedThreshold_Is141()
    {
        Assert.Equal(141, ParentalRatingHelper.UnrestrictedThreshold);
        Assert.False(Exclude(adult: true, [27], 141));
        Assert.False(Exclude(adult: true, [27], null));
        Assert.True(Exclude(adult: true, [27], 140));
    }
}
