using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

public class ParentalRatingHelperTests
{
    [Fact]
    public void GetCertificationQueryParam_NullRating_ReturnsNull()
    {
        var result = ParentalRatingHelper.GetCertificationQueryParam(null);
        Assert.Null(result);
    }

    [Fact]
    public void GetCertificationQueryParam_HighRating_ReturnsNull()
    {
        // 160+ means unrestricted (FSK 18 / no filter)
        var result = ParentalRatingHelper.GetCertificationQueryParam(160);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0, "FSK%200")]
    [InlineData(50, "FSK%206")]
    [InlineData(60, "FSK%206")]
    [InlineData(80, "FSK%2012")]
    [InlineData(100, "FSK%2012")]
    [InlineData(120, "FSK%2016")]
    [InlineData(140, "FSK%2016")]
    public void GetCertificationQueryParam_ValidRating_ReturnsExpectedCertification(int rating, string expectedCert)
    {
        var result = ParentalRatingHelper.GetCertificationQueryParam(rating);
        Assert.NotNull(result);
        Assert.Contains("certification_country=DE", result);
        Assert.Contains($"certification.lte={expectedCert}", result);
    }

    [Fact]
    public void ShouldExclude_NullMaxRating_ReturnsFalse()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = true, GenreIds = [27] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, null));
    }

    [Fact]
    public void ShouldExclude_AdultContent_WithRestriction_ReturnsTrue()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = true, GenreIds = [35] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 100));
    }

    [Fact]
    public void ShouldExclude_AdultContent_HighRestriction_ReturnsTrue()
    {
        // Even with high (but not null) MaxParentalRating, adult content is excluded
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = true, GenreIds = [35] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 140));
    }

    [Fact]
    public void ShouldExclude_HorrorGenre_ChildAccount_ReturnsTrue()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [27] }; // Horror
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 80));
    }

    [Fact]
    public void ShouldExclude_CrimeGenre_ChildAccount_ReturnsTrue()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [80] }; // Crime
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_ThrillerGenre_ChildAccount_ReturnsTrue()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [53] }; // Thriller
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 100));
    }

    [Fact]
    public void ShouldExclude_WarGenre_ChildAccount_ReturnsTrue()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [10752] }; // War
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 80));
    }

    [Fact]
    public void ShouldExclude_HorrorGenre_TeenAccount_ReturnsFalse()
    {
        // MaxParentalRating > 100 means genre blacklist is NOT applied
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [27] }; // Horror
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 120));
    }

    [Fact]
    public void ShouldExclude_SafeContent_ChildAccount_ReturnsFalse()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [16, 10751] }; // Animation, Family
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_MixedGenres_OneRestricted_ChildAccount_ReturnsTrue()
    {
        // If any genre is restricted, the item is excluded
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [28, 53] }; // Action + Thriller
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 80));
    }

    [Fact]
    public void ShouldExclude_NonAdultContent_HighRating_ReturnsFalse()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [28, 12] }; // Action, Adventure
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 140));
    }
}