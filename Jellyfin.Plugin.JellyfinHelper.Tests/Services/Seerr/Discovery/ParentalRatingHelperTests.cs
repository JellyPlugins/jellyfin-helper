using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

public class ParentalRatingHelperTests
{
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
    public void ShouldExclude_HorrorGenre_TeenAccount_ReturnsTrue()
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
    public void ShouldExclude_ThrillerGenre_TeenAccount_ReturnsTrue()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [53] }; // Thriller
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 100));
    }

    [Fact]
    public void ShouldExclude_WarGenre_TeenAccount_ReturnsTrue()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [10752] }; // War
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 80));
    }

    [Fact]
    public void ShouldExclude_HorrorGenre_OlderTeenAccount_ReturnsFalse()
    {
        // MaxParentalRating > 100 means genre blacklist is NOT applied
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [27] }; // Horror
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 120));
    }

    [Fact]
    public void ShouldExclude_AnimationOnly_ChildAccount_ReturnsTrue()
    {
        // Animation (16) alone WITHOUT Family/Kids genre = Adult animation (Family Guy, American Dad)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [16] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_AnimationComedy_ChildAccount_ReturnsTrue()
    {
        // Animation + Comedy but NO Family/Kids genre = Adult animation
        // This is exactly what American Dad and Family Guy look like on TMDb!
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [16, 35] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_AnimationWithFamily_ChildAccount_ReturnsFalse()
    {
        // Animation + Family = genuinely child-safe (e.g. Frozen, Moana)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [16, 10751] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_AnimationWithKids_ChildAccount_ReturnsFalse()
    {
        // Animation + Kids (TV) = genuinely child-safe (e.g. Peppa Pig, Bluey)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [16, 10762] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_AnimationCrime_ChildAccount_ReturnsTrue()
    {
        // Animation + Crime = Adult animation (Archer)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [16, 80] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_ComedyOnly_ChildAccount_ReturnsTrue()
    {
        // Comedy alone without Family = adult comedy
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [35] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_FamilyComedy_ChildAccount_ReturnsFalse()
    {
        // Family + Comedy = child-safe
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [10751, 35] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_FamilyOnly_ChildAccount_ReturnsFalse()
    {
        // Family genre alone = child-safe
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [10751] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_KidsOnly_ChildAccount_ReturnsFalse()
    {
        // Kids (TV) genre alone = child-safe
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [10762] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_MusicGenre_ChildAccount_ReturnsFalse()
    {
        // Music genre = child-safe (primary allowed genre)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [10402] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_AdventureOnly_ChildAccount_ReturnsTrue()
    {
        // Adventure alone (without Family/Kids) = not safe for strict child
        // (e.g. Indiana Jones, Mission Impossible)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [12] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_FamilyAdventure_ChildAccount_ReturnsFalse()
    {
        // Family + Adventure = child-safe (e.g. Finding Nemo)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [10751, 12] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_AnimationFamilyAdventure_ChildAccount_ReturnsFalse()
    {
        // Animation + Family + Adventure = genuinely child-safe (e.g. Moana)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [16, 10751, 12] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_AnimationFamilyThriller_ChildAccount_ReturnsTrue()
    {
        // Even with Family, Thriller genre makes it excluded
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [16, 10751, 53] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_DramaOnly_ChildAccount_ReturnsTrue()
    {
        // Drama (18) is not on any child-friendly list
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [18] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 50));
    }

    [Fact]
    public void ShouldExclude_ActionDrama_TeenAccount_ReturnsFalse()
    {
        // For FSK-12 (61-100), Action + Drama are not on the blacklist
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [28, 18] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 80));
    }

    [Fact]
    public void ShouldExclude_MixedGenres_OneRestricted_TeenAccount_ReturnsTrue()
    {
        // For FSK-12 (61-100), blacklist is used: Thriller is restricted
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [28, 53] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 80));
    }

    [Fact]
    public void ShouldExclude_NonAdultContent_HighRating_ReturnsFalse()
    {
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [28, 12] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 140));
    }

    [Fact]
    public void ShouldExclude_AdultContent_ExactBoundary141_ReturnsFalse()
    {
        // MaxParentalRating >= 141 is treated as unrestricted (adult account)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = true, GenreIds = [27, 53] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 141));
    }

    [Fact]
    public void ShouldExclude_ActionOnly_StrictChildAccount_ReturnsTrue()
    {
        // Pure Action (28) without any child-friendly genre = excluded for FSK-6
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [28] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_FantasyOnly_ChildAccount_ReturnsTrue()
    {
        // Fantasy alone (without Family/Kids) = conditional, needs primary child genre
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [14] };
        Assert.True(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }

    [Fact]
    public void ShouldExclude_FamilyFantasy_ChildAccount_ReturnsFalse()
    {
        // Family + Fantasy = child-safe (e.g. Harry Potter for young kids... well actually borderline)
        var candidate = new TmdbDiscoverItem { Id = 1, Adult = false, GenreIds = [10751, 14] };
        Assert.False(ParentalRatingHelper.ShouldExclude(candidate, 60));
    }
}