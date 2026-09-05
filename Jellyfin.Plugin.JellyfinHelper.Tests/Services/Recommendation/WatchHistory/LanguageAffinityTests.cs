using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.WatchHistory;

/// <summary>
///     Tests for the audio language affinity feature: LanguageProfileEntry, language-related properties on UserWatchProfile, NormalizeLanguage, and ComputeLanguageAffinity.
/// </summary>
public sealed class LanguageAffinityTests
{
    // LanguageProfileEntry Tests

    [Theory]
    [InlineData(10, 0, 10.0)]
    [InlineData(0, 8, 2.0)]
    [InlineData(5, 4, 6.0)]
    [InlineData(0, 0, 0.0)]
    public void WeightedScore_Value(int chosen, int forced, double expected)
    {
        var entry = new LanguageProfileEntry { ChosenCount = chosen, ForcedCount = forced };
        Assert.Equal(expected, entry.WeightedScore, 10);
    }

    // UserWatchProfile Language Property Tests

    [Fact]
    public void PrimaryLanguage_EmptyProfile_ReturnsNull()
    {
        var profile = new UserWatchProfile();
        Assert.Null(profile.PrimaryLanguage);
    }

    [Fact]
    public void PrimaryLanguage_SingleEntry_ReturnsThatLanguage()
    {
        var profile = new UserWatchProfile();
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 5 };
        Assert.Equal("de", profile.PrimaryLanguage);
    }

    [Fact]
    public void PrimaryLanguage_MultipleEntries_ReturnsHighestWeighted()
    {
        var profile = new UserWatchProfile();
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 10, ForcedCount = 0 }; // score = 10.0
        profile.LanguageProfile["en"] = new LanguageProfileEntry { ChosenCount = 2, ForcedCount = 20 }; // score = 2 + 5 = 7.0
        profile.LanguageProfile["ja"] = new LanguageProfileEntry { ChosenCount = 1, ForcedCount = 0 }; // score = 1.0

        Assert.Equal("de", profile.PrimaryLanguage);
    }

    [Fact]
    public void PrimaryLanguage_ForcedDominates_WhenNoChosenExists()
    {
        var profile = new UserWatchProfile();
        profile.LanguageProfile["en"] = new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 100 }; // score = 25.0
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 4 }; // score = 1.0

        Assert.Equal("en", profile.PrimaryLanguage);
    }

    [Fact]
    public void PreferredLanguages_EmptyProfile_ReturnsEmpty()
    {
        var profile = new UserWatchProfile();
        Assert.Empty(profile.PreferredLanguages);
    }

    [Fact]
    public void PreferredLanguages_OnlyForced_ReturnsEmpty()
    {
        var profile = new UserWatchProfile();
        profile.LanguageProfile["en"] = new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 10 };
        Assert.Empty(profile.PreferredLanguages);
    }

    [Fact]
    public void PreferredLanguages_MixedChosenAndForced_ReturnsOnlyChosen()
    {
        var profile = new UserWatchProfile();
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 5, ForcedCount = 2 };
        profile.LanguageProfile["en"] = new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 10 };
        profile.LanguageProfile["ja"] = new LanguageProfileEntry { ChosenCount = 1, ForcedCount = 0 };

        var preferred = profile.PreferredLanguages;
        Assert.Contains("de", preferred);
        Assert.Contains("ja", preferred);
        Assert.DoesNotContain("en", preferred);
        Assert.Equal(2, preferred.Count);
    }

    [Fact]
    public void ToleratedLanguages_EmptyProfile_ReturnsEmpty()
    {
        var profile = new UserWatchProfile();
        Assert.Empty(profile.ToleratedLanguages);
    }

    [Fact]
    public void ToleratedLanguages_OnlyChosen_ReturnsEmpty()
    {
        var profile = new UserWatchProfile();
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 5, ForcedCount = 0 };
        Assert.Empty(profile.ToleratedLanguages);
    }

    [Fact]
    public void ToleratedLanguages_ForcedWithoutChosen_ReturnsThem()
    {
        var profile = new UserWatchProfile();
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 5, ForcedCount = 3 }; // not tolerated (has chosen)
        profile.LanguageProfile["en"] = new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 10 }; // tolerated
        profile.LanguageProfile["fr"] = new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 2 }; // tolerated

        var tolerated = profile.ToleratedLanguages;
        Assert.DoesNotContain("de", tolerated);
        Assert.Contains("en", tolerated);
        Assert.Contains("fr", tolerated);
        Assert.Equal(2, tolerated.Count);
    }

    [Fact]
    public void ToleratedLanguages_MixedChosenAndForced_ExcludesBothChosen()
    {
        var profile = new UserWatchProfile();
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 1, ForcedCount = 5 };

        // Has both chosen and forced - NOT tolerated (user actively chose it too)
        Assert.Empty(profile.ToleratedLanguages);
    }

    // NormalizeLanguage Tests

    [Fact]
    public void NormalizeLanguage_Null_ReturnsNull()
    {
        Assert.Null(WatchHistoryService.NormalizeLanguage(null));
    }

    [Fact]
    public void NormalizeLanguage_Empty_ReturnsNull()
    {
        Assert.Null(WatchHistoryService.NormalizeLanguage(""));
        Assert.Null(WatchHistoryService.NormalizeLanguage("   "));
    }

    [Theory]
    [InlineData("ger", "de")]
    [InlineData("deu", "de")]
    [InlineData("eng", "en")]
    [InlineData("jpn", "ja")]
    [InlineData("fre", "fr")]
    [InlineData("fra", "fr")]
    [InlineData("spa", "es")]
    [InlineData("ita", "it")]
    [InlineData("por", "pt")]
    [InlineData("rus", "ru")]
    [InlineData("chi", "zh")]
    [InlineData("zho", "zh")]
    [InlineData("kor", "ko")]
    [InlineData("dut", "nl")]
    [InlineData("nld", "nl")]
    [InlineData("pol", "pl")]
    [InlineData("tur", "tr")]
    [InlineData("ara", "ar")]
    [InlineData("hin", "hi")]
    [InlineData("swe", "sv")]
    [InlineData("dan", "da")]
    [InlineData("nor", "no")]
    [InlineData("fin", "fi")]
    [InlineData("hun", "hu")]
    [InlineData("ces", "cs")]
    [InlineData("cze", "cs")]
    [InlineData("ron", "ro")]
    [InlineData("rum", "ro")]
    [InlineData("tha", "th")]
    [InlineData("vie", "vi")]
    [InlineData("ukr", "uk")]
    [InlineData("heb", "he")]
    [InlineData("ell", "el")]
    [InlineData("gre", "el")]
    [InlineData("ind", "id")]
    [InlineData("msa", "ms")]
    [InlineData("may", "ms")]
    [InlineData("hrv", "hr")]
    [InlineData("srp", "sr")]
    [InlineData("slk", "sk")]
    [InlineData("slo", "sk")]
    [InlineData("slv", "sl")]
    [InlineData("bul", "bg")]
    [InlineData("cat", "ca")]
    [InlineData("est", "et")]
    [InlineData("lav", "lv")]
    [InlineData("lit", "lt")]
    [InlineData("fas", "fa")]
    [InlineData("per", "fa")]
    [InlineData("urd", "ur")]
    public void NormalizeLanguage_ThreeLetterToTwoLetter(string input, string expected)
    {
        Assert.Equal(expected, WatchHistoryService.NormalizeLanguage(input));
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    [InlineData("fr", "fr")]
    public void NormalizeLanguage_AlreadyTwoLetter_ReturnsSame(string input, string expected)
    {
        Assert.Equal(expected, WatchHistoryService.NormalizeLanguage(input));
    }

    [Fact]
    public void NormalizeLanguage_CaseInsensitive()
    {
        Assert.Equal("de", WatchHistoryService.NormalizeLanguage("GER"));
        Assert.Equal("en", WatchHistoryService.NormalizeLanguage("ENG"));
        Assert.Equal("ja", WatchHistoryService.NormalizeLanguage("Jpn"));
    }

    [Fact]
    public void NormalizeLanguage_TrimsWhitespace()
    {
        Assert.Equal("de", WatchHistoryService.NormalizeLanguage("  ger  "));
        Assert.Equal("en", WatchHistoryService.NormalizeLanguage(" eng "));
    }

    [Fact]
    public void NormalizeLanguage_UnknownThreeLetter_ReturnsAsIs()
    {
        // Unknown 3-letter codes are returned lowercase as-is
        Assert.Equal("xyz", WatchHistoryService.NormalizeLanguage("xyz"));
    }

    // ComputeLanguageAffinity Tests (via Engine static method) Note: ComputeLanguageAffinity requires BaseItem.GetMediaStreams() which needs Jellyfin infrastructure.
}