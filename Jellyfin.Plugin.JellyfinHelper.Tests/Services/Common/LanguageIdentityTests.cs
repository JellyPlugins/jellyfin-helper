using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Common;

/// <summary>
///     Tests for the shared language identity kernel behind statistics facets
///     and recommendation language profiles.
/// </summary>
public class LanguageIdentityTests
{
    [Theory]
    [InlineData("ger", "de")]
    [InlineData("deu", "de")]
    [InlineData("de", "de")]
    [InlineData("eng", "en")]
    [InlineData("fra", "fr")]
    [InlineData("swe", "sv")]
    [InlineData("nob", "no")]
    [InlineData("nno", "no")]
    [InlineData("srp", "sr")]
    [InlineData("per", "fa")]
    [InlineData("hrv", "hr")]
    public void GetIso6391Code_KnownCodes_ResolveToCanonical(string tag, string expected)
        => Assert.Equal(expected, LanguageIdentity.GetIso6391Code(tag));

    [Theory]
    [InlineData("deutsch", "de")]
    [InlineData("svenska", "sv")]
    [InlineData("français", "fr")]
    [InlineData("allemand", "de")]
    [InlineData("nb", "no")]
    [InlineData("iw", "he")]
    public void GetIso6391Code_NamesAndVariants_ResolveToCanonical(string tag, string expected)
        => Assert.Equal(expected, LanguageIdentity.GetIso6391Code(tag));

    [Theory]
    [InlineData("GERMAN Forced SRT by TSCC", "de")]
    [InlineData("en-US", "en")]
    [InlineData("German (Forced)", "de")]
    public void GetIso6391Code_MessyTags_ResolveFirstToken(string tag, string expected)
        => Assert.Equal(expected, LanguageIdentity.GetIso6391Code(tag));

    [Theory]
    [InlineData("Simplified, Mandarin Chinese", "zh")]
    [InlineData("Traditional, Mandarin Chinese", "zh")]
    [InlineData("Traditional, Yue Chinese", "zh")]
    [InlineData("Malay (macrolanguage)", "ms")]
    [InlineData("Modern Greek (1453-)", "el")]
    [InlineData("United states - English - Subrip", "en")]
    public void GetIso6391Code_CompoundTags_ResolveSegments(string tag, string expected)
        => Assert.Equal(expected, LanguageIdentity.GetIso6391Code(tag));

    [Theory]
    [InlineData("Forced by")]
    [InlineData("Dubbed in")]
    [InlineData("Brazilian Portuguese")]
    public void GetIso6391Code_NoiseWithoutLanguage_ReturnsNull(string tag)
        => Assert.Null(LanguageIdentity.GetIso6391Code(tag));

    [Fact]
    public void TrackFlagWords_AllFlagged()
    {
        Assert.NotEmpty(LanguageIdentity.TrackFlagWords);
        foreach (var word in LanguageIdentity.TrackFlagWords)
        {
            Assert.True(LanguageIdentity.IsFlagOnlyTag(word), word);
        }
    }

    [Theory]
    [InlineData("German")]
    [InlineData("de")]
    [InlineData("subtitles by TSCC")]
    public void IsFlagOnlyTag_LanguageContent_NotFlagged(string basis)
        => Assert.False(LanguageIdentity.IsFlagOnlyTag(basis));

    [Theory]
    [InlineData("Full SDH")]
    [InlineData("SDH Hörgeschädigt")]
    [InlineData("sous titres")]
    [InlineData("hearing impaired")]
    [InlineData("forced commentary")]
    public void IsFlagOnlyTag_FlagCompounds_Flagged(string basis)
        => Assert.True(LanguageIdentity.IsFlagOnlyTag(basis));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xyz")]
    [InlineData("Forced")]
    public void GetIso6391Code_Unresolvable_ReturnsNull(string? tag)
        => Assert.Null(LanguageIdentity.GetIso6391Code(tag));

    [Theory]
    [InlineData("de", "German")]
    [InlineData("en", "English")]
    [InlineData("sr", "Serbian")]
    [InlineData("hr", "Croatian")]
    [InlineData("no", "Norwegian")]
    public void DisplayNameForCode_KnownCodes_ReturnDisplay(string code, string expected)
        => Assert.Equal(expected, LanguageIdentity.DisplayNameForCode(code));

    [Fact]
    public void DisplayNameForCode_UnknownCode_PassesThroughUppercased()
        => Assert.Equal("XX", LanguageIdentity.DisplayNameForCode("xx"));

    [Theory]
    [InlineData("  ger  ", "ger")]
    [InlineData("German (Forced)", "German")]
    [InlineData("en-US", "en")]
    [InlineData("United states - English - Subrip", "United states - English - Subrip")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void CleanLanguageTag_TrimsQualifiersAndRegions(string? tag, string? expected)
        => Assert.Equal(expected, LanguageIdentity.CleanLanguageTag(tag));
}
