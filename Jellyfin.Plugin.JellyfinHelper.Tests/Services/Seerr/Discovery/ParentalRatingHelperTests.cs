using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>External parental limits are individual Jellyfin-scale score checks, never inferred from genre or a legacy score band.</summary>
public sealed class ParentalRatingHelperTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(1000, null, false)]
    [InlineData(null, int.MaxValue, false)]
    [InlineData(null, 0, true)]
    [InlineData(null, 6, true)]
    [InlineData(null, 12, true)]
    [InlineData(null, 18, true)]
    [InlineData(null, 141, true)]
    [InlineData(0, 0, false)]
    [InlineData(6, 6, false)]
    [InlineData(7, 6, true)]
    [InlineData(12, 12, false)]
    [InlineData(18, 12, true)]
    [InlineData(1000, 18, true)]
    [InlineData(-1, 6, true)]
    [Trait("Category", "Security")]
    public void ShouldExclude_VerifiedScoreRespectsExactAccountLimit(int? certification, int? maximum, bool expected)
    {
        var candidate = new TmdbDiscoverItem { Id = 1, CertificationScore = certification };
        Assert.Equal(expected, ParentalRatingHelper.ShouldExclude(candidate, maximum));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(10751)]
    [InlineData(10762)]
    [InlineData(10402)]
    [InlineData(80)]
    [InlineData(27)]
    [Trait("Category", "Security")]
    public void GenreCannotOverrideVerifiedAgeOrProveAnUnknownAge(int genre)
    {
        var candidate = new TmdbDiscoverItem { GenreIds = [genre], CertificationScore = 6 };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 6));
        candidate.CertificationScore = 18;
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 6));
        candidate.CertificationScore = null;
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 6));
    }
}
