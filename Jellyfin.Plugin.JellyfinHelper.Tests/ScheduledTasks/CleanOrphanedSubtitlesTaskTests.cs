using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
/// Tests for <see cref="CleanOrphanedSubtitlesTask"/> subtitle name parsing logic.
/// </summary>
public class CleanOrphanedSubtitlesTaskTests
{
    // === GetSubtitleBaseName ===

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
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath);
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
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetSubtitleBaseName_HandlesFileWithNoDots()
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName("/movies/MovieName.srt");
        Assert.Equal("MovieName", result);
    }

    [Fact]
    public void GetSubtitleBaseName_HandlesMultipleLanguageSuffixes()
    {
        // "Movie.en.forced.default.srt" → "Movie"
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName("/movies/Movie.en.forced.default.srt");
        Assert.Equal("Movie", result);
    }

    [Fact]
    public void GetSubtitleBaseName_HandlesThreeLetterLanguageCode()
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName("/movies/Movie.ger.srt");
        Assert.Equal("Movie", result);
    }

    [Fact]
    public void GetSubtitleBaseName_PreservesYearInParentheses()
    {
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName("/movies/The Movie (2023).fr.srt");
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
        var result = CleanOrphanedSubtitlesTask.GetSubtitleBaseName(filePath);
        Assert.Equal(expected, result);
    }
}
