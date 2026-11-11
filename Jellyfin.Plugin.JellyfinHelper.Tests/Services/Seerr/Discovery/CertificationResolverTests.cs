using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>Actual country-specific movie/TV certifications are mapped on Jellyfin's scale and unknown data fails closed.</summary>
public sealed class CertificationResolverTests
{
    [Fact]
    public void MovieReleases_UseJellyfinsCountryMapperAndMostRestrictiveRecognizedRelease()
    {
        var localization = Mapper();
        var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>("""
            {"id":1,"releases":{"results":[
              {"iso_3166_1":"DE","release_dates":[{"certification":"6"},{"certification":"18"}]},
              {"iso_3166_1":"US","release_dates":[{"certification":"PG-13"}]}]}}
            """)!;

        Assert.Equal(18, CertificationResolver.Resolve(detail, localization.Object));
        localization.Verify(mapper => mapper.GetRatingScore("6", "DE"), Times.Once);
        localization.Verify(mapper => mapper.GetRatingScore("18", "DE"), Times.Once);
        localization.Verify(mapper => mapper.GetRatingScore("PG-13", "US"), Times.Once);
    }

    [Fact]
    public void TvContentRatings_AreNotMistakenForMovieReleaseDates()
    {
        var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>("""{"id":1,"contentRatings":{"results":[{"iso_3166_1":"US","rating":"TV-MA"}]}}""")!;
        Assert.Equal(17, CertificationResolver.Resolve(detail, Mapper().Object));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"releases\":null}")]
    [InlineData("{\"releases\":{\"results\":{}}}")]
    [InlineData("{\"releases\":{\"results\":[null,{}, {\"iso_3166_1\":\"DE\",\"release_dates\":[null,{\"certification\":7},{\"certification\":\"\"}]}]}}")]
    [InlineData("{\"contentRatings\":{\"results\":[{\"iso_3166_1\":\"bad-country\",\"rating\":\"TV-MA\"}]}}")]
    public void UnknownOrMalformedCertifications_DoNotBecomeChildSafe(string json)
    {
        var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>(json)!;
        Assert.Null(CertificationResolver.Resolve(detail, Mapper().Object));
    }

    [Fact]
    public void AdultFlag_CannotBeClearedByAnOtherwiseLowCertification()
    {
        var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>("""{"releases":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"6"}]}]}}""")!;
        Assert.Equal(1000, CertificationResolver.Resolve(detail, Mapper().Object, adultFlag: true));
        Assert.Null(CertificationResolver.Resolve(detail, localization: null));
    }

    [Theory]
    [InlineData(6, 6, false)]
    [InlineData(7, 6, true)]
    [InlineData(12, 12, false)]
    [InlineData(18, 12, true)]
    [InlineData(null, 6, true)]
    [InlineData(null, null, false)]
    public void ParentalPolicy_UsesTheIndividualAccountLimitNotGenreLabels(int? certificate, int? maximum, bool excluded)
    {
        var candidate = new TmdbDiscoverItem { GenreIds = [10751, 10402], CertificationScore = certificate };
        Assert.Equal(excluded, ParentalRatingHelper.ShouldExclude(candidate, maximum));
    }

    [Fact]
    public void CertifiedSafeContent_DoesNotRequireAnUnrelatedFamilyGenre()
    {
        var candidate = new TmdbDiscoverItem { GenreIds = [80], CertificationScore = 6 };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 6));
    }

    private static Mock<ILocalizationManager> Mapper()
    {
        var mapper = new Mock<ILocalizationManager>();
        mapper.Setup(m => m.GetRatingScore("6", "DE")).Returns(new ParentalRatingScore(6, null));
        mapper.Setup(m => m.GetRatingScore("18", "DE")).Returns(new ParentalRatingScore(18, null));
        mapper.Setup(m => m.GetRatingScore("PG-13", "US")).Returns(new ParentalRatingScore(13, null));
        mapper.Setup(m => m.GetRatingScore("TV-MA", "US")).Returns(new ParentalRatingScore(17, null));
        mapper.Setup(m => m.GetRatingScore("XXX", "US")).Returns(new ParentalRatingScore(1000, null));
        return mapper;
    }
}
