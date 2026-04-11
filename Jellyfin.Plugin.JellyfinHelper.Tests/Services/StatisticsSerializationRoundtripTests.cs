using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
/// Roundtrip serialization tests to ensure statistics data survives JSON
/// serialize → deserialize cycles. These guard against regressions where
/// property renames or missing deserialization attributes silently drop data.
/// Uses the exact same <see cref="JsonSerializerOptions"/> as
/// <see cref="StatisticsHistoryService"/>.
/// </summary>
public class StatisticsSerializationRoundtripTests
{
    /// <summary>
    /// The JSON options that exactly match StatisticsHistoryService.JsonOptions.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private static T Roundtrip<T>(T original)
    {
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<T>(json, JsonOptions);
        return Assert.IsType<T>(deserialized);
    }

    // ======================== MediaStatisticsResult ========================

    [Fact]
    public void MediaStatisticsResult_Roundtrip_PreservesCollections()
    {
        var original = CreateSampleResult();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MediaStatisticsResult>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Libraries.Count, deserialized!.Libraries.Count);
        Assert.Equal(original.Movies.Count, deserialized.Movies.Count);
        Assert.Equal(original.TvShows.Count, deserialized.TvShows.Count);
        Assert.Equal(original.Music.Count, deserialized.Music.Count);
        Assert.Equal(original.Other.Count, deserialized.Other.Count);
    }

    [Fact]
    public void MediaStatisticsResult_Roundtrip_PreservesComputedSizeTotals()
    {
        var original = CreateSampleResult();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MediaStatisticsResult>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.TotalMovieVideoSize, deserialized!.TotalMovieVideoSize);
        Assert.Equal(original.TotalTvShowVideoSize, deserialized.TotalTvShowVideoSize);
        Assert.Equal(original.TotalMusicAudioSize, deserialized.TotalMusicAudioSize);
        Assert.Equal(original.TotalTrickplaySize, deserialized.TotalTrickplaySize);
        Assert.Equal(original.TotalSubtitleSize, deserialized.TotalSubtitleSize);
        Assert.Equal(original.TotalImageSize, deserialized.TotalImageSize);
        Assert.Equal(original.TotalNfoSize, deserialized.TotalNfoSize);
    }

    [Fact]
    public void MediaStatisticsResult_Roundtrip_PreservesFileCounts()
    {
        var original = CreateSampleResult();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MediaStatisticsResult>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.TotalVideoFileCount, deserialized!.TotalVideoFileCount);
        Assert.Equal(original.TotalAudioFileCount, deserialized.TotalAudioFileCount);
    }

    [Fact]
    public void MediaStatisticsResult_Roundtrip_PreservesHealthChecks()
    {
        var original = CreateSampleResult();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MediaStatisticsResult>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.TotalVideosWithoutSubtitles, deserialized!.TotalVideosWithoutSubtitles);
        Assert.Equal(original.TotalVideosWithoutImages, deserialized.TotalVideosWithoutImages);
        Assert.Equal(original.TotalVideosWithoutNfo, deserialized.TotalVideosWithoutNfo);
        Assert.Equal(original.TotalOrphanedMetadataDirectories, deserialized.TotalOrphanedMetadataDirectories);
    }

    [Fact]
    public void MediaStatisticsResult_Roundtrip_PreservesTimestamp()
    {
        var original = CreateSampleResult();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MediaStatisticsResult>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.ScanTimestamp, deserialized!.ScanTimestamp);
    }

    [Fact]
    public void MediaStatisticsResult_Roundtrip_NonZeroValues()
    {
        // This is the key regression test: after deserialization, computed
        // totals must NOT be zero when the original had non-zero data.
        var original = CreateSampleResult();
        Assert.True(original.TotalMovieVideoSize > 0, "Test data must have non-zero movie size");
        Assert.True(original.TotalVideoFileCount > 0, "Test data must have non-zero video count");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MediaStatisticsResult>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.TotalMovieVideoSize > 0, "Deserialized TotalMovieVideoSize must not be zero");
        Assert.True(deserialized.TotalTvShowVideoSize > 0, "Deserialized TotalTvShowVideoSize must not be zero");
        Assert.True(deserialized.TotalVideoFileCount > 0, "Deserialized TotalVideoFileCount must not be zero");
    }

    // ======================== LibraryStatistics ========================

    [Fact]
    public void LibraryStatistics_Roundtrip_PreservesScalarProperties()
    {
        var original = CreateSampleLibrary("Movies", "movies");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<LibraryStatistics>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.LibraryName, deserialized!.LibraryName);
        Assert.Equal(original.CollectionType, deserialized.CollectionType);
        Assert.Equal(original.VideoSize, deserialized.VideoSize);
        Assert.Equal(original.VideoFileCount, deserialized.VideoFileCount);
        Assert.Equal(original.SubtitleSize, deserialized.SubtitleSize);
        Assert.Equal(original.SubtitleFileCount, deserialized.SubtitleFileCount);
        Assert.Equal(original.ImageSize, deserialized.ImageSize);
        Assert.Equal(original.ImageFileCount, deserialized.ImageFileCount);
        Assert.Equal(original.NfoSize, deserialized.NfoSize);
        Assert.Equal(original.NfoFileCount, deserialized.NfoFileCount);
        Assert.Equal(original.AudioSize, deserialized.AudioSize);
        Assert.Equal(original.AudioFileCount, deserialized.AudioFileCount);
        Assert.Equal(original.TrickplaySize, deserialized.TrickplaySize);
        Assert.Equal(original.TrickplayFolderCount, deserialized.TrickplayFolderCount);
        Assert.Equal(original.OtherSize, deserialized.OtherSize);
        Assert.Equal(original.OtherFileCount, deserialized.OtherFileCount);
    }

    [Fact]
    public void LibraryStatistics_Roundtrip_PreservesDictionaries()
    {
        var original = CreateSampleLibrary("Movies", "movies");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<LibraryStatistics>(json, JsonOptions);

        Assert.NotNull(deserialized);

        AssertDictionaryEqual(original.ContainerFormats, deserialized!.ContainerFormats);
        AssertDictionaryEqual(original.Resolutions, deserialized.Resolutions);
        AssertDictionaryEqual(original.VideoCodecs, deserialized.VideoCodecs);
        AssertDictionaryEqual(original.VideoAudioCodecs, deserialized.VideoAudioCodecs);
        AssertDictionaryEqual(original.MusicAudioCodecs, deserialized.MusicAudioCodecs);

        AssertLongDictionaryEqual(original.ContainerSizes, deserialized.ContainerSizes);
        AssertLongDictionaryEqual(original.ResolutionSizes, deserialized.ResolutionSizes);
        AssertLongDictionaryEqual(original.VideoCodecSizes, deserialized.VideoCodecSizes);
        AssertLongDictionaryEqual(original.VideoAudioCodecSizes, deserialized.VideoAudioCodecSizes);
        AssertLongDictionaryEqual(original.MusicAudioCodecSizes, deserialized.MusicAudioCodecSizes);
    }

    [Fact]
    public void LibraryStatistics_Roundtrip_PreservesHealthChecks()
    {
        var original = CreateSampleLibrary("Movies", "movies");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<LibraryStatistics>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.VideosWithoutSubtitles, deserialized!.VideosWithoutSubtitles);
        Assert.Equal(original.VideosWithoutImages, deserialized.VideosWithoutImages);
        Assert.Equal(original.VideosWithoutNfo, deserialized.VideosWithoutNfo);
        Assert.Equal(original.OrphanedMetadataDirectories, deserialized.OrphanedMetadataDirectories);
    }

    [Fact]
    public void LibraryStatistics_Roundtrip_TotalSizeComputed()
    {
        var original = CreateSampleLibrary("Movies", "movies");
        Assert.True(original.TotalSize > 0, "Test data must have non-zero TotalSize");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<LibraryStatistics>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.TotalSize, deserialized!.TotalSize);
    }

    // ======================== StatisticsSnapshot ========================

    [Fact]
    public void StatisticsSnapshot_Roundtrip_PreservesAllProperties()
    {
        var original = new StatisticsSnapshot
        {
            Timestamp = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
            TotalVideoFileCount = 500,
            TotalAudioFileCount = 200,
            TotalMovieVideoSize = 1_000_000_000_000L,
            TotalTvShowVideoSize = 800_000_000_000L,
            TotalMusicAudioSize = 50_000_000_000L,
            TotalTrickplaySize = 10_000_000_000L,
            TotalSubtitleSize = 500_000_000L,
            TotalImageSize = 2_000_000_000L,
            TotalNfoSize = 100_000_000L,
            TotalSize = 1_862_600_000_000L,
        };
        original.LibrarySizes["Movies"] = 1_000_000_000_000L;
        original.LibrarySizes["TV Shows"] = 800_000_000_000L;
        original.LibrarySizes["Music"] = 50_000_000_000L;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<StatisticsSnapshot>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Timestamp, deserialized!.Timestamp);
        Assert.Equal(original.TotalVideoFileCount, deserialized.TotalVideoFileCount);
        Assert.Equal(original.TotalAudioFileCount, deserialized.TotalAudioFileCount);
        Assert.Equal(original.TotalMovieVideoSize, deserialized.TotalMovieVideoSize);
        Assert.Equal(original.TotalTvShowVideoSize, deserialized.TotalTvShowVideoSize);
        Assert.Equal(original.TotalMusicAudioSize, deserialized.TotalMusicAudioSize);
        Assert.Equal(original.TotalTrickplaySize, deserialized.TotalTrickplaySize);
        Assert.Equal(original.TotalSubtitleSize, deserialized.TotalSubtitleSize);
        Assert.Equal(original.TotalImageSize, deserialized.TotalImageSize);
        Assert.Equal(original.TotalNfoSize, deserialized.TotalNfoSize);
        Assert.Equal(original.TotalSize, deserialized.TotalSize);
    }

    [Fact]
    public void StatisticsSnapshot_Roundtrip_PreservesLibrarySizes()
    {
        var original = new StatisticsSnapshot
        {
            Timestamp = new DateTime(2025, 7, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        original.LibrarySizes["Movies"] = 500_000_000_000L;
        original.LibrarySizes["TV Shows"] = 300_000_000_000L;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<StatisticsSnapshot>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.LibrarySizes.Count);
        Assert.Equal(500_000_000_000L, deserialized.LibrarySizes["Movies"]);
        Assert.Equal(300_000_000_000L, deserialized.LibrarySizes["TV Shows"]);
    }

    [Fact]
    public void StatisticsSnapshotList_Roundtrip_PreservesHistory()
    {
        // The history file is a List<StatisticsSnapshot>, not a single snapshot
        var original = new List<StatisticsSnapshot>
        {
            new StatisticsSnapshot
            {
                Timestamp = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                TotalMovieVideoSize = 900_000_000_000L,
                TotalSize = 1_500_000_000_000L,
            },
            new StatisticsSnapshot
            {
                Timestamp = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                TotalMovieVideoSize = 1_000_000_000_000L,
                TotalSize = 1_700_000_000_000L,
            },
        };
        original[0].LibrarySizes["Movies"] = 900_000_000_000L;
        original[1].LibrarySizes["Movies"] = 1_000_000_000_000L;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<List<StatisticsSnapshot>>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.Count);
        Assert.Equal(original[0].TotalMovieVideoSize, deserialized[0].TotalMovieVideoSize);
        Assert.Equal(original[1].TotalMovieVideoSize, deserialized[1].TotalMovieVideoSize);
        Assert.Single(deserialized[0].LibrarySizes);
        Assert.Single(deserialized[1].LibrarySizes);
    }

    // ======================== Aggregated Codec/Quality ========================

    [Fact]
    public void MediaStatisticsResult_Roundtrip_PreservesAggregatedCodecs()
    {
        var original = CreateSampleResult();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MediaStatisticsResult>(json, JsonOptions);

        Assert.NotNull(deserialized);

        // Aggregated dictionaries are computed from library data, so they
        // must be non-empty if the library data survived deserialization.
        Assert.NotEmpty(deserialized!.TotalContainerFormats);
        Assert.NotEmpty(deserialized.TotalResolutions);
        Assert.NotEmpty(deserialized.TotalVideoCodecs);
        Assert.NotEmpty(deserialized.TotalVideoAudioCodecs);

        // Verify specific values
        Assert.Equal(original.TotalContainerFormats["MKV"], deserialized.TotalContainerFormats["MKV"]);
        Assert.Equal(original.TotalResolutions["1080p"], deserialized.TotalResolutions["1080p"]);
        Assert.Equal(original.TotalVideoCodecs["HEVC"], deserialized.TotalVideoCodecs["HEVC"]);
    }

    // ======================== Helpers ========================

    private static MediaStatisticsResult CreateSampleResult()
    {
        var result = new MediaStatisticsResult
        {
            ScanTimestamp = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
        };

        var movieLib = CreateSampleLibrary("Movies", "movies");
        result.Libraries.Add(movieLib);
        result.Movies.Add(movieLib);

        var tvLib = CreateSampleLibrary("TV Shows", "tvshows");
        tvLib.VideoSize = 800_000_000_000L;
        tvLib.VideoFileCount = 200;
        result.Libraries.Add(tvLib);
        result.TvShows.Add(tvLib);

        var musicLib = CreateSampleLibrary("Music", "music");
        musicLib.VideoSize = 0;
        musicLib.VideoFileCount = 0;
        musicLib.AudioSize = 50_000_000_000L;
        musicLib.AudioFileCount = 1000;
        musicLib.MusicAudioCodecs["FLAC"] = 600;
        musicLib.MusicAudioCodecs["MP3"] = 400;
        musicLib.MusicAudioCodecSizes["FLAC"] = 40_000_000_000L;
        musicLib.MusicAudioCodecSizes["MP3"] = 10_000_000_000L;
        result.Libraries.Add(musicLib);
        result.Music.Add(musicLib);

        return result;
    }

    private static LibraryStatistics CreateSampleLibrary(string name, string collectionType)
    {
        var lib = new LibraryStatistics
        {
            LibraryName = name,
            CollectionType = collectionType,
            VideoSize = 1_000_000_000_000L,
            VideoFileCount = 300,
            SubtitleSize = 500_000_000L,
            SubtitleFileCount = 250,
            ImageSize = 2_000_000_000L,
            ImageFileCount = 600,
            NfoSize = 100_000_000L,
            NfoFileCount = 300,
            AudioSize = 0,
            AudioFileCount = 0,
            TrickplaySize = 10_000_000_000L,
            TrickplayFolderCount = 280,
            OtherSize = 50_000_000L,
            OtherFileCount = 10,
            VideosWithoutSubtitles = 50,
            VideosWithoutImages = 20,
            VideosWithoutNfo = 10,
            OrphanedMetadataDirectories = 5,
        };

        lib.ContainerFormats["MKV"] = 200;
        lib.ContainerFormats["MP4"] = 100;

        lib.Resolutions["4K"] = 50;
        lib.Resolutions["1080p"] = 200;
        lib.Resolutions["720p"] = 50;

        lib.VideoCodecs["HEVC"] = 150;
        lib.VideoCodecs["H.264"] = 150;

        lib.VideoAudioCodecs["DTS"] = 100;
        lib.VideoAudioCodecs["AAC"] = 200;

        lib.ContainerSizes["MKV"] = 700_000_000_000L;
        lib.ContainerSizes["MP4"] = 300_000_000_000L;

        lib.ResolutionSizes["4K"] = 400_000_000_000L;
        lib.ResolutionSizes["1080p"] = 500_000_000_000L;
        lib.ResolutionSizes["720p"] = 100_000_000_000L;

        lib.VideoCodecSizes["HEVC"] = 600_000_000_000L;
        lib.VideoCodecSizes["H.264"] = 400_000_000_000L;

        lib.VideoAudioCodecSizes["DTS"] = 400_000_000_000L;
        lib.VideoAudioCodecSizes["AAC"] = 600_000_000_000L;

        return lib;
    }

    private static void AssertDictionaryEqual(Dictionary<string, int> expected, Dictionary<string, int> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var kvp in expected)
        {
            Assert.True(actual.ContainsKey(kvp.Key), $"Missing key '{kvp.Key}' in deserialized dictionary");
            Assert.Equal(kvp.Value, actual[kvp.Key]);
        }
    }

    private static void AssertLongDictionaryEqual(Dictionary<string, long> expected, Dictionary<string, long> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var kvp in expected)
        {
            Assert.True(actual.ContainsKey(kvp.Key), $"Missing key '{kvp.Key}' in deserialized dictionary");
            Assert.Equal(kvp.Value, actual[kvp.Key]);
        }
    }
}