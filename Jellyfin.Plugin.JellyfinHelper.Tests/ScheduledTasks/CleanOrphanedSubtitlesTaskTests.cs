using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
/// Tests for <see cref="CleanOrphanedSubtitlesTask"/> subtitle name parsing logic.
/// </summary>
public class CleanOrphanedSubtitlesTaskTests
{
    // === GetSubtitleBaseName: simple language codes and flags ===

    [Theory]
    [InlineData("/movies/Movie Name (2021).en.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).en.forced.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).de.hi.ass", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).eng.sdh.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie.Name.2021.en.srt", "Movie.Name.2021")]
    [InlineData("/movies/Movie.srt", "Movie")]
    public void GetSubtitleBaseName_StripsLanguageAndFlagSuffixes(string filePath, string expected)
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath, new HashSet<string> { expected });
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/movies/Movie.DTS.srt", "Movie.DTS")]
    [InlineData("/movies/Movie.HDR.srt", "Movie.HDR")]
    [InlineData("/movies/Movie.x265.srt", "Movie.x265")]
    [InlineData("/movies/Movie.REMUX.srt", "Movie.REMUX")]
    [InlineData("/movies/Movie.2160p.srt", "Movie.2160p")]
    public void GetSubtitleBaseName_DoesNotStripNonLanguageSuffixes(string filePath, string expected)
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath, new HashSet<string> { expected });
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetSubtitleBaseName_HandlesFileWithNoDots()
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName("/movies/MovieName.srt", new HashSet<string> { "MovieName" });
        Assert.Equal("MovieName", result);
    }

    [Fact]
    public void GetSubtitleBaseName_HandlesMultipleLanguageSuffixes()
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName("/movies/Movie.en.forced.default.srt", new HashSet<string> { "Movie" });
        Assert.Equal("Movie", result);
    }

    [Fact]
    public void GetSubtitleBaseName_HandlesThreeLetterLanguageCode()
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName("/movies/Movie.ger.srt", new HashSet<string> { "Movie" });
        Assert.Equal("Movie", result);
    }

    [Fact]
    public void GetSubtitleBaseName_PreservesYearInParentheses()
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName("/movies/The Movie (2023).fr.srt", new HashSet<string> { "The Movie (2023)" });
        Assert.Equal("The Movie (2023)", result);
    }

    // === False-positive regression tests ===

    [Theory]
    [InlineData("/movies/Movie.S01E01.srt", "Movie.S01E01")]
    [InlineData("/movies/Movie.720p.srt", "Movie.720p")]
    [InlineData("/movies/Movie.BluRay.srt", "Movie.BluRay")]
    [InlineData("/movies/Movie.FLAC.srt", "Movie.FLAC")]
    public void GetSubtitleBaseName_DoesNotStripEncodingOrQualityTokens(string filePath, string expected)
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath, new HashSet<string> { expected });
        Assert.Equal(expected, result);
    }

    // === GetSubtitleBaseName: BCP-47 regional tags ===

    [Theory]
    [InlineData("/movies/Movie Name (2021).es-MX.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).pt-BR.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).zh-TW.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).zh-CN.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).en-US.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).en-GB.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).fr-CA.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).es-AR.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).de-AT.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).de-CH.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).no-NO.srt", "Movie Name (2021)")]
    public void GetSubtitleBaseName_StripsBcp47RegionalTags(string filePath, string expected)
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath, new HashSet<string> { expected });
        Assert.Equal(expected, result);
    }

    // === GetSubtitleBaseName: BCP-47 script subtags ===

    [Theory]
    [InlineData("/movies/Movie Name (2021).zh-Hans.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).zh-Hant.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).sr-Latn.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).sr-Cyrl.srt", "Movie Name (2021)")]
    public void GetSubtitleBaseName_StripsBcp47ScriptTags(string filePath, string expected)
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath, new HashSet<string> { expected });
        Assert.Equal(expected, result);
    }

    // === GetSubtitleBaseName: BCP-47 tags combined with flags ===

    [Theory]
    [InlineData("/movies/Movie Name (2021).es-MX.forced.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).pt-BR.sdh.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).zh-Hans.default.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).en-US.hi.srt", "Movie Name (2021)")]
    [InlineData("/movies/Movie Name (2021).fr-CA.cc.srt", "Movie Name (2021)")]
    public void GetSubtitleBaseName_StripsBcp47TagsWithFlags(string filePath, string expected)
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath, new HashSet<string> { expected });
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/movies/Movie.DTS-HD.srt", "Movie.DTS-HD")]
    [InlineData("/movies/Movie.DTS-X.srt", "Movie.DTS-X")]
    [InlineData("/movies/Movie.h264-GROUP.srt", "Movie.h264-GROUP")]
    [InlineData("/movies/Movie.x264-YIFY.srt", "Movie.x264-YIFY")]
    [InlineData("/movies/Movie.x265-RARBG.srt", "Movie.x265-RARBG")]
    [InlineData("/movies/Movie.DD5-1.srt", "Movie.DD5-1")]
    [InlineData("/movies/Movie.AAC2-0.srt", "Movie.AAC2-0")]
    public void GetSubtitleBaseName_DoesNotStripHyphenatedEncodingTokens(string filePath, string expected)
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath, new HashSet<string> { expected });
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetSubtitleBaseName_RealWorldBugReport_SpanishLatino()
    {
        // Subtitle: Rocky (1976) [...][DTS-HD MA 5.1][...]-seleZen.es-MX.srt
        // Video:    Rocky (1976) [...][DTS-HD MA 5.1][...]-seleZen.mkv
        // The dot in "5.1" splits the filename but both files split identically.
        // After stripping "es-MX" the subtitle base must equal the video base.
        const string subtitlePath =
            "/data/media/movies/Rocky (1976)/Rocky (1976) [tmdbid-1366] - [Remux-2160p][DTS-HD MA 5.1][DV HDR10][h265]-seleZen.es-MX.srt";

        const string videoBaseName =
            "Rocky (1976) [tmdbid-1366] - [Remux-2160p][DTS-HD MA 5.1][DV HDR10][h265]-seleZen";

        var subtitleBase = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(subtitlePath, new HashSet<string> { videoBaseName });
        Assert.Equal(videoBaseName, subtitleBase);
    }

    // === IsSubtitleSuffix: direct unit tests ===

    [Theory]
    [InlineData("en", true)]
    [InlineData("de", true)]
    [InlineData("eng", true)]
    [InlineData("deu", true)]
    [InlineData("ger", true)]
    [InlineData("spa", true)]
    [InlineData("forced", true)]
    [InlineData("sdh", true)]
    [InlineData("hi", true)]
    [InlineData("cc", true)]
    [InlineData("default", true)]
    public void IsSubtitleSuffix_RecognizesSimpleCodes(string segment, bool expected)
    {
        var result = CleanOrphanedSubtitlesTask.IsSubtitleSuffix(segment);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("es-MX", true)]
    [InlineData("pt-BR", true)]
    [InlineData("zh-TW", true)]
    [InlineData("zh-CN", true)]
    [InlineData("en-US", true)]
    [InlineData("en-GB", true)]
    [InlineData("fr-CA", true)]
    [InlineData("de-AT", true)]
    [InlineData("de-CH", true)]
    [InlineData("no-NO", true)]
    [InlineData("es-AR", true)]
    [InlineData("ja-JP", true)]
    [InlineData("ko-KR", true)]
    public void IsSubtitleSuffix_RecognizesBcp47RegionalTags(string segment, bool expected)
    {
        var result = CleanOrphanedSubtitlesTask.IsSubtitleSuffix(segment);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("zh-Hans", true)]
    [InlineData("zh-Hant", true)]
    [InlineData("sr-Latn", true)]
    [InlineData("sr-Cyrl", true)]
    [InlineData("az-Latn", true)]
    [InlineData("uz-Cyrl", true)]
    public void IsSubtitleSuffix_RecognizesBcp47ScriptTags(string segment, bool expected)
    {
        var result = CleanOrphanedSubtitlesTask.IsSubtitleSuffix(segment);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("DTS-HD", false)]
    [InlineData("DTS-X", false)]
    [InlineData("h264-GROUP", false)]
    [InlineData("x264-YIFY", false)]
    [InlineData("x265-RARBG", false)]
    [InlineData("DD5-1", false)]
    [InlineData("AAC2-0", false)]
    [InlineData("DDP5-1", false)]
    [InlineData("HDR10", false)]
    [InlineData("REMUX", false)]
    [InlineData("2160p", false)]
    [InlineData("BluRay", false)]
    [InlineData("S01E01", false)]
    [InlineData("", false)]
    public void IsSubtitleSuffix_RejectsNonLanguageTokens(string segment, bool expected)
    {
        var result = CleanOrphanedSubtitlesTask.IsSubtitleSuffix(segment);
        Assert.Equal(expected, result);
    }

    // === Edge case: 3-letter language code with region ===

    [Theory]
    [InlineData("spa-MX", true)]
    [InlineData("por-BR", true)]
    [InlineData("zho-TW", true)]
    [InlineData("eng-US", true)]
    [InlineData("deu-AT", true)]
    [InlineData("fra-CA", true)]
    public void IsSubtitleSuffix_RecognizesThreeLetterCodeWithRegion(string segment, bool expected)
    {
        var result = CleanOrphanedSubtitlesTask.IsSubtitleSuffix(segment);
        Assert.Equal(expected, result);
    }

    // === Three-subtag {lang}-{script}-{region} tags ===

    [Theory]
    [InlineData("zh-Hans-TW")]
    [InlineData("zh-Hant-HK")]
    [InlineData("sr-Latn-RS")]
    [InlineData("zh-Hans-419")]
    public void IsSubtitleSuffix_RecognizesLangScriptRegionTags(string segment)
    {
        // Full BCP-47 lang-script-region tags are valid subtitle suffixes and must be stripped.
        Assert.True(CleanOrphanedSubtitlesTask.IsSubtitleSuffix(segment));
    }

    [Theory]
    [InlineData("zh-XX-TW")]  // middle subtag is not a 4-letter script
    [InlineData("zh-Hans-XYZ")] // trailing region is neither 2-alpha nor 3-digit
    [InlineData("zh-Hans-1")]   // trailing region too short to be a numeric region
    public void IsSubtitleSuffix_RejectsInvalidThreeSubtagTags(string segment)
    {
        // The script&&region rule must reject malformed three-subtag inputs so a title like
        // "Foo.zh-XX-TW" is not wrongly treated as language-tagged and stripped.
        Assert.False(CleanOrphanedSubtitlesTask.IsSubtitleSuffix(segment));
    }
}
