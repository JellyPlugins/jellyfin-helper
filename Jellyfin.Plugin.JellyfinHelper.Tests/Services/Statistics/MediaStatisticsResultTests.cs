using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class MediaStatisticsResultTests
{
    [Fact]
    public void TotalMovieVideoSize_SumsAllMovieLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Movies.Add(new LibraryStatistics { VideoSize = 1000 });
        result.Movies.Add(new LibraryStatistics { VideoSize = 2000 });
        Assert.Equal(3000, result.TotalMovieVideoSize);
    }

    [Fact]
    public void TotalTvShowVideoSize_SumsAllTvShowLibraries()
    {
        var result = new MediaStatisticsResult();
        result.TvShows.Add(new LibraryStatistics { VideoSize = 500 });
        result.TvShows.Add(new LibraryStatistics { VideoSize = 700 });
        Assert.Equal(1200, result.TotalTvShowVideoSize);
    }

    [Fact]
    public void TotalMusicAudioSize_SumsAllMusicLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Music.Add(new LibraryStatistics { AudioSize = 300 });
        result.Music.Add(new LibraryStatistics { AudioSize = 400 });
        Assert.Equal(700, result.TotalMusicAudioSize);
    }

    [Fact]
    public void TotalTrickplaySize_SumsAcrossAllLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { TrickplaySize = 100 });
        result.Libraries.Add(new LibraryStatistics { TrickplaySize = 200 });
        Assert.Equal(300, result.TotalTrickplaySize);
    }

    [Fact]
    public void TotalSubtitleSize_SumsAcrossAllLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { SubtitleSize = 50 });
        result.Libraries.Add(new LibraryStatistics { SubtitleSize = 75 });
        Assert.Equal(125, result.TotalSubtitleSize);
    }

    [Fact]
    public void TotalImageSize_SumsAcrossAllLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { ImageSize = 10 });
        result.Libraries.Add(new LibraryStatistics { ImageSize = 20 });
        Assert.Equal(30, result.TotalImageSize);
    }

    [Fact]
    public void TotalNfoSize_SumsAcrossAllLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { NfoSize = 5 });
        result.Libraries.Add(new LibraryStatistics { NfoSize = 15 });
        Assert.Equal(20, result.TotalNfoSize);
    }

    [Fact]
    public void TotalVideoFileCount_SumsAcrossAllLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { VideoFileCount = 10 });
        result.Libraries.Add(new LibraryStatistics { VideoFileCount = 20 });
        Assert.Equal(30, result.TotalVideoFileCount);
    }

    [Fact]
    public void TotalAudioFileCount_SumsAcrossAllLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { AudioFileCount = 100 });
        result.Libraries.Add(new LibraryStatistics { AudioFileCount = 200 });
        Assert.Equal(300, result.TotalAudioFileCount);
    }

    [Fact]
    public void TotalContainerFormats_AggregatesDictionaries()
    {
        var result = new MediaStatisticsResult();
        var lib1 = new LibraryStatistics();
        lib1.ContainerFormats["mkv"] = 5;
        lib1.ContainerFormats["mp4"] = 3;
        var lib2 = new LibraryStatistics();
        lib2.ContainerFormats["mkv"] = 2;
        lib2.ContainerFormats["avi"] = 1;
        result.Libraries.Add(lib1);
        result.Libraries.Add(lib2);

        var totals = result.TotalContainerFormats;
        Assert.Equal(7, totals["mkv"]);
        Assert.Equal(3, totals["mp4"]);
        Assert.Equal(1, totals["avi"]);
    }

    [Fact]
    public void TotalResolutions_AggregatesDictionaries()
    {
        var result = new MediaStatisticsResult();
        var lib1 = new LibraryStatistics();
        lib1.Resolutions["1080p"] = 10;
        var lib2 = new LibraryStatistics();
        lib2.Resolutions["1080p"] = 5;
        lib2.Resolutions["4K"] = 3;
        result.Libraries.Add(lib1);
        result.Libraries.Add(lib2);

        var totals = result.TotalResolutions;
        Assert.Equal(15, totals["1080p"]);
        Assert.Equal(3, totals["4K"]);
    }

    [Fact]
    public void TotalVideoCodecs_AggregatesDictionaries()
    {
        var result = new MediaStatisticsResult();
        var lib1 = new LibraryStatistics();
        lib1.VideoCodecs["h264"] = 10;
        var lib2 = new LibraryStatistics();
        lib2.VideoCodecs["h265"] = 5;
        result.Libraries.Add(lib1);
        result.Libraries.Add(lib2);

        var totals = result.TotalVideoCodecs;
        Assert.Equal(10, totals["h264"]);
        Assert.Equal(5, totals["h265"]);
    }

    [Fact]
    public void TotalVideoAudioCodecs_AggregatesDictionaries()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideoAudioCodecs["aac"] = 7;
        result.Libraries.Add(lib);

        Assert.Equal(7, result.TotalVideoAudioCodecs["aac"]);
    }

    [Fact]
    public void TotalMusicAudioCodecs_AggregatesMusicOnly()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.MusicAudioCodecs["flac"] = 12;
        result.Music.Add(lib);

        Assert.Equal(12, result.TotalMusicAudioCodecs["flac"]);
    }

    [Fact]
    public void TotalContainerSizes_AggregatesLongDictionaries()
    {
        var result = new MediaStatisticsResult();
        var lib1 = new LibraryStatistics();
        lib1.ContainerSizes["mkv"] = 1000000L;
        var lib2 = new LibraryStatistics();
        lib2.ContainerSizes["mkv"] = 2000000L;
        result.Libraries.Add(lib1);
        result.Libraries.Add(lib2);

        Assert.Equal(3000000L, result.TotalContainerSizes["mkv"]);
    }

    [Fact]
    public void TotalResolutionSizes_AggregatesLongDictionaries()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.ResolutionSizes["1080p"] = 5000000L;
        result.Libraries.Add(lib);

        Assert.Equal(5000000L, result.TotalResolutionSizes["1080p"]);
    }

    [Fact]
    public void TotalVideoCodecSizes_AggregatesLongDictionaries()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideoCodecSizes["h264"] = 999L;
        result.Libraries.Add(lib);

        Assert.Equal(999L, result.TotalVideoCodecSizes["h264"]);
    }

    [Fact]
    public void TotalVideoAudioCodecSizes_AggregatesLongDictionaries()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideoAudioCodecSizes["aac"] = 500L;
        result.Libraries.Add(lib);

        Assert.Equal(500L, result.TotalVideoAudioCodecSizes["aac"]);
    }

    [Fact]
    public void TotalMusicAudioCodecSizes_AggregatesMusicOnly()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.MusicAudioCodecSizes["flac"] = 8000L;
        result.Music.Add(lib);

        Assert.Equal(8000L, result.TotalMusicAudioCodecSizes["flac"]);
    }

    // ===== Health Checks =====

    [Fact]
    public void TotalVideosWithoutSubtitles_SumsAcrossLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { VideosWithoutSubtitles = 3 });
        result.Libraries.Add(new LibraryStatistics { VideosWithoutSubtitles = 5 });
        Assert.Equal(8, result.TotalVideosWithoutSubtitles);
    }

    [Fact]
    public void TotalVideosWithoutImages_SumsAcrossLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { VideosWithoutImages = 2 });
        result.Libraries.Add(new LibraryStatistics { VideosWithoutImages = 4 });
        Assert.Equal(6, result.TotalVideosWithoutImages);
    }

    [Fact]
    public void TotalVideosWithoutNfo_SumsAcrossLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { VideosWithoutNfo = 1 });
        result.Libraries.Add(new LibraryStatistics { VideosWithoutNfo = 2 });
        Assert.Equal(3, result.TotalVideosWithoutNfo);
    }

    [Fact]
    public void TotalOrphanedMetadataDirectories_SumsAcrossLibraries()
    {
        var result = new MediaStatisticsResult();
        result.Libraries.Add(new LibraryStatistics { OrphanedMetadataDirectories = 10 });
        result.Libraries.Add(new LibraryStatistics { OrphanedMetadataDirectories = 20 });
        Assert.Equal(30, result.TotalOrphanedMetadataDirectories);
    }

    // ===== Detail Paths =====

    [Fact]
    public void TotalVideosWithoutSubtitlesPaths_AggregatesPaths()
    {
        var result = new MediaStatisticsResult();
        var lib1 = new LibraryStatistics();
        lib1.VideosWithoutSubtitlesPaths.Add("/a.mkv");
        var lib2 = new LibraryStatistics();
        lib2.VideosWithoutSubtitlesPaths.Add("/b.mkv");
        result.Libraries.Add(lib1);
        result.Libraries.Add(lib2);

        Assert.Equal(2, result.TotalVideosWithoutSubtitlesPaths.Count);
        Assert.Contains("/a.mkv", result.TotalVideosWithoutSubtitlesPaths);
        Assert.Contains("/b.mkv", result.TotalVideosWithoutSubtitlesPaths);
    }

    [Fact]
    public void TotalVideosWithoutImagesPaths_AggregatesPaths()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideosWithoutImagesPaths.Add("/c.mkv");
        result.Libraries.Add(lib);
        Assert.Single(result.TotalVideosWithoutImagesPaths);
    }

    [Fact]
    public void TotalVideosWithoutNfoPaths_AggregatesPaths()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideosWithoutNfoPaths.Add("/d.mkv");
        result.Libraries.Add(lib);
        Assert.Single(result.TotalVideosWithoutNfoPaths);
    }

    [Fact]
    public void TotalOrphanedMetadataDirectoriesPaths_AggregatesPaths()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.OrphanedMetadataDirectoriesPaths.Add("/orphaned");
        result.Libraries.Add(lib);
        Assert.Single(result.TotalOrphanedMetadataDirectoriesPaths);
    }

    // ===== Root Paths =====

    [Fact]
    public void MovieRootPaths_AggregatesFromMovies()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.RootPaths.Add("/media/movies");
        result.Movies.Add(lib);
        Assert.Contains("/media/movies", result.MovieRootPaths);
    }

    [Fact]
    public void TvShowRootPaths_AggregatesFromTvShows()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.RootPaths.Add("/media/tv");
        result.TvShows.Add(lib);
        Assert.Contains("/media/tv", result.TvShowRootPaths);
    }

    [Fact]
    public void MusicRootPaths_AggregatesFromMusic()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.RootPaths.Add("/media/music");
        result.Music.Add(lib);
        Assert.Contains("/media/music", result.MusicRootPaths);
    }

    [Fact]
    public void OtherRootPaths_AggregatesFromOther()
    {
        var result = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.RootPaths.Add("/media/other");
        result.Other.Add(lib);
        Assert.Contains("/media/other", result.OtherRootPaths);
    }

    // ===== Empty Collections =====

    [Fact]
    public void AllTotals_ReturnZero_WhenEmpty()
    {
        var result = new MediaStatisticsResult();
        Assert.Equal(0, result.TotalMovieVideoSize);
        Assert.Equal(0, result.TotalTvShowVideoSize);
        Assert.Equal(0, result.TotalMusicAudioSize);
        Assert.Equal(0, result.TotalTrickplaySize);
        Assert.Equal(0, result.TotalSubtitleSize);
        Assert.Equal(0, result.TotalImageSize);
        Assert.Equal(0, result.TotalNfoSize);
        Assert.Equal(0, result.TotalVideoFileCount);
        Assert.Equal(0, result.TotalAudioFileCount);
        Assert.Equal(0, result.TotalVideosWithoutSubtitles);
        Assert.Equal(0, result.TotalVideosWithoutImages);
        Assert.Equal(0, result.TotalVideosWithoutNfo);
        Assert.Equal(0, result.TotalOrphanedMetadataDirectories);
        Assert.Empty(result.TotalContainerFormats);
        Assert.Empty(result.TotalResolutions);
        Assert.Empty(result.TotalVideoCodecs);
        Assert.Empty(result.TotalVideoAudioCodecs);
        Assert.Empty(result.TotalMusicAudioCodecs);
        Assert.Empty(result.TotalContainerSizes);
        Assert.Empty(result.TotalResolutionSizes);
        Assert.Empty(result.TotalVideoCodecSizes);
        Assert.Empty(result.TotalVideoAudioCodecSizes);
        Assert.Empty(result.TotalMusicAudioCodecSizes);
    }
}