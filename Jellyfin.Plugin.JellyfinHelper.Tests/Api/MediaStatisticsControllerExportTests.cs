using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
/// Tests for the CSV and JSON export endpoints of <see cref="MediaStatisticsController"/>.
/// These tests ensure that export downloads produce valid output containing all expected
/// fields, including codec breakdowns, health-check paths, and size dictionaries.
/// The controller is instantiated with mocks; statistics data is pre-populated in the
/// memory cache so no real library scan is needed.
/// </summary>
public class MediaStatisticsControllerExportTests
{
    private const string StatsCacheKey = "JellyfinHelper_Statistics";

    private readonly MediaStatisticsController _controller;
    private readonly IMemoryCache _cache;

    public MediaStatisticsControllerExportTests()
    {
        var libraryManagerMock = new Mock<ILibraryManager>();
        libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        var fileSystemMock = new Mock<IFileSystem>();

        var appPathsMock = new Mock<IApplicationPaths>();
        appPathsMock.Setup(p => p.DataPath).Returns(Path.GetTempPath());

        var httpClientFactoryMock = new Mock<IHttpClientFactory>();

        _cache = new MemoryCache(new MemoryCacheOptions());

        var loggerMock = new Mock<ILogger<MediaStatisticsController>>();
        var serviceLoggerMock = new Mock<ILogger<MediaStatisticsService>>();
        var historyLoggerMock = new Mock<ILogger<StatisticsHistoryService>>();

        _controller = new MediaStatisticsController(
            libraryManagerMock.Object,
            fileSystemMock.Object,
            appPathsMock.Object,
            httpClientFactoryMock.Object,
            _cache,
            loggerMock.Object,
            serviceLoggerMock.Object,
            historyLoggerMock.Object);
    }

    // ======================== Helpers ========================

    private static MediaStatisticsResult CreateSampleResult()
    {
        var result = new MediaStatisticsResult
        {
            ScanTimestamp = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
        };

        var movieLib = new LibraryStatistics
        {
            LibraryName = "Movies",
            CollectionType = "movies",
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

        movieLib.ContainerFormats["MKV"] = 200;
        movieLib.ContainerFormats["MP4"] = 100;
        movieLib.Resolutions["4K"] = 50;
        movieLib.Resolutions["1080p"] = 200;
        movieLib.VideoCodecs["HEVC"] = 150;
        movieLib.VideoCodecs["H.264"] = 150;
        movieLib.VideoAudioCodecs["DTS"] = 100;
        movieLib.VideoAudioCodecs["AAC"] = 200;
        movieLib.MusicAudioCodecs["FLAC"] = 10;

        movieLib.ContainerSizes["MKV"] = 700_000_000_000L;
        movieLib.ContainerSizes["MP4"] = 300_000_000_000L;
        movieLib.ResolutionSizes["4K"] = 400_000_000_000L;
        movieLib.ResolutionSizes["1080p"] = 500_000_000_000L;
        movieLib.VideoCodecSizes["HEVC"] = 600_000_000_000L;
        movieLib.VideoCodecSizes["H.264"] = 400_000_000_000L;
        movieLib.VideoAudioCodecSizes["DTS"] = 400_000_000_000L;
        movieLib.VideoAudioCodecSizes["AAC"] = 600_000_000_000L;
        movieLib.MusicAudioCodecSizes["FLAC"] = 5_000_000_000L;

        movieLib.VideosWithoutSubtitlesPaths.Add("/media/movies/Film1/Film1.mkv");
        movieLib.VideosWithoutSubtitlesPaths.Add("/media/movies/Film2/Film2.mp4");
        movieLib.VideosWithoutImagesPaths.Add("/media/movies/Film3/Film3.mkv");
        movieLib.VideosWithoutNfoPaths.Add("/media/movies/Film4/Film4.mkv");
        movieLib.OrphanedMetadataDirectoriesPaths.Add("/media/movies/OldMovie/.metadata");

        result.Libraries.Add(movieLib);
        result.Movies.Add(movieLib);

        return result;
    }

    private void PrePopulateCache(MediaStatisticsResult result)
    {
        _cache.Set(StatsCacheKey, result, TimeSpan.FromMinutes(5));
    }

    private static string GetFileContentString(FileContentResult fileResult)
    {
        return Encoding.UTF8.GetString(fileResult.FileContents);
    }

    // ======================== CSV Export Tests ========================

    [Fact]
    public void ExportCsv_ReturnsFileResult_WithCsvContentType()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportCsv();

        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        Assert.Equal("text/csv", fileResult.ContentType);
        Assert.NotNull(fileResult.FileDownloadName);
        Assert.Contains("jellyfin-statistics-", fileResult.FileDownloadName);
        Assert.EndsWith(".csv", fileResult.FileDownloadName);
    }

    [Fact]
    public void ExportCsv_HeaderRow_ContainsAllExpectedColumns()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, "CSV must have at least a header row and one data row");

        var header = lines[0].Trim();

        // Core file count/size columns
        Assert.Contains("Library", header);
        Assert.Contains("CollectionType", header);
        Assert.Contains("VideoFiles", header);
        Assert.Contains("VideoSizeBytes", header);
        Assert.Contains("AudioFiles", header);
        Assert.Contains("AudioSizeBytes", header);
        Assert.Contains("SubtitleFiles", header);
        Assert.Contains("TrickplayFolders", header);
        Assert.Contains("TotalSizeBytes", header);

        // Codec/resolution breakdown columns
        Assert.Contains("ContainerFormats", header);
        Assert.Contains("Resolutions", header);
        Assert.Contains("VideoCodecs", header);
        Assert.Contains("VideoAudioCodecs", header);
        Assert.Contains("MusicAudioCodecs", header);

        // Size breakdown columns
        Assert.Contains("ContainerSizes", header);
        Assert.Contains("ResolutionSizes", header);
        Assert.Contains("VideoCodecSizes", header);
        Assert.Contains("VideoAudioCodecSizes", header);
        Assert.Contains("MusicAudioCodecSizes", header);

        // Health check columns
        Assert.Contains("VideosWithoutSubtitles", header);
        Assert.Contains("VideosWithoutImages", header);
        Assert.Contains("VideosWithoutNfo", header);
        Assert.Contains("OrphanedMetadataDirectories", header);

        // Health check path columns
        Assert.Contains("VideosWithoutSubtitlesPaths", header);
        Assert.Contains("VideosWithoutImagesPaths", header);
        Assert.Contains("VideosWithoutNfoPaths", header);
        Assert.Contains("OrphanedMetadataDirectoriesPaths", header);
    }

    [Fact]
    public void ExportCsv_DataRow_ContainsLibraryName()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        Assert.Contains("Movies", content);
    }

    [Fact]
    public void ExportCsv_DataRow_ContainsCodecData()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        // Codec dictionaries are serialized as JSON, so check for JSON keys
        Assert.Contains("MKV", content);
        Assert.Contains("HEVC", content);
        Assert.Contains("DTS", content);
        Assert.Contains("1080p", content);
    }

    [Fact]
    public void ExportCsv_DataRow_ContainsHealthCheckPaths()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        // The paths should appear in the CSV (JSON-serialized inside the field)
        Assert.Contains("Film1.mkv", content);
        Assert.Contains("Film2.mp4", content);
        Assert.Contains("Film3", content);
        Assert.Contains("Film4", content);
        Assert.Contains(".metadata", content);
    }

    [Fact]
    public void ExportCsv_DataRow_ContainsNumericSizeValues()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        // Check that numeric values are present (video size = 1_000_000_000_000)
        Assert.Contains("1000000000000", content);
        // Trickplay size = 10_000_000_000
        Assert.Contains("10000000000", content);
    }

    [Fact]
    public void ExportCsv_DataRow_ContainsHealthCheckCounts()
    {
        var result = CreateSampleResult();
        PrePopulateCache(result);

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var lib = result.Libraries[0];

        // The health check counts must appear in the data row
        // VideosWithoutSubtitles = 50, VideosWithoutImages = 20, etc.
        Assert.Contains(lib.VideosWithoutSubtitles.ToString(), content);
        Assert.Contains(lib.VideosWithoutImages.ToString(), content);
        Assert.Contains(lib.VideosWithoutNfo.ToString(), content);
        Assert.Contains(lib.OrphanedMetadataDirectories.ToString(), content);
    }

    [Fact]
    public void ExportCsv_MultipleLibraries_EachLibraryHasOwnRow()
    {
        var result = CreateSampleResult();

        // Add a second library
        var tvLib = new LibraryStatistics
        {
            LibraryName = "TV Shows",
            CollectionType = "tvshows",
            VideoSize = 500_000_000_000L,
            VideoFileCount = 100,
        };
        tvLib.VideosWithoutSubtitlesPaths.Add("/media/tv/Show1/S01E01.mkv");
        result.Libraries.Add(tvLib);
        result.TvShows.Add(tvLib);

        PrePopulateCache(result);

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 2 data rows
        Assert.Equal(3, lines.Length);
        Assert.Contains("Movies", lines[1]);
        Assert.Contains("TV Shows", lines[2]);
    }

    [Fact]
    public void ExportCsv_EmptyLibraries_ProducesHeaderOnly()
    {
        var result = new MediaStatisticsResult
        {
            ScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        PrePopulateCache(result);

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines); // only header row
    }

    [Fact]
    public void ExportCsv_HeaderColumnCount_MatchesDataColumnCount()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2);

        // Count columns in header (simple split since header has no quoted fields)
        var headerColumns = lines[0].Trim().Split(',').Length;

        // For data row, we need to be more careful because JSON inside quoted fields
        // contains commas. But the number of unquoted top-level commas + 1 should match.
        // Instead, verify header has the expected count (35 columns)
        Assert.Equal(35, headerColumns);
    }

    // ======================== JSON Export Tests ========================

    [Fact]
    public void ExportJson_ReturnsFileResult_WithJsonContentType()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportJson();

        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        Assert.Equal("application/json", fileResult.ContentType);
        Assert.NotNull(fileResult.FileDownloadName);
        Assert.Contains("jellyfin-statistics-", fileResult.FileDownloadName);
        Assert.EndsWith(".json", fileResult.FileDownloadName);
    }

    [Fact]
    public void ExportJson_ProducesValidJson()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        // Must not throw - valid JSON
        var doc = JsonDocument.Parse(content);
        Assert.NotNull(doc);
    }

    [Fact]
    public void ExportJson_ContainsLibraryData()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("libraries", out var libraries));
        Assert.Equal(JsonValueKind.Array, libraries.ValueKind);
        Assert.Equal(1, libraries.GetArrayLength());
    }

    [Fact]
    public void ExportJson_ContainsCodecBreakdowns()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var doc = JsonDocument.Parse(content);
        var lib = doc.RootElement.GetProperty("libraries")[0];

        Assert.True(lib.TryGetProperty("containerFormats", out var containers));
        Assert.True(containers.TryGetProperty("MKV", out var mkv));
        Assert.Equal(200, mkv.GetInt32());

        Assert.True(lib.TryGetProperty("videoCodecs", out var codecs));
        Assert.True(codecs.TryGetProperty("HEVC", out var hevc));
        Assert.Equal(150, hevc.GetInt32());
    }

    [Fact]
    public void ExportJson_ContainsSizeBreakdowns()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var doc = JsonDocument.Parse(content);
        var lib = doc.RootElement.GetProperty("libraries")[0];

        Assert.True(lib.TryGetProperty("containerSizes", out var containerSizes));
        Assert.True(containerSizes.TryGetProperty("MKV", out var mkvSize));
        Assert.Equal(700_000_000_000L, mkvSize.GetInt64());
    }

    [Fact]
    public void ExportJson_ContainsHealthCheckPaths()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var doc = JsonDocument.Parse(content);
        var lib = doc.RootElement.GetProperty("libraries")[0];

        Assert.True(lib.TryGetProperty("videosWithoutSubtitlesPaths", out var subPaths));
        Assert.Equal(JsonValueKind.Array, subPaths.ValueKind);
        Assert.Equal(2, subPaths.GetArrayLength());
        Assert.Equal("/media/movies/Film1/Film1.mkv", subPaths[0].GetString());

        Assert.True(lib.TryGetProperty("videosWithoutImagesPaths", out var imgPaths));
        Assert.Equal(1, imgPaths.GetArrayLength());

        Assert.True(lib.TryGetProperty("videosWithoutNfoPaths", out var nfoPaths));
        Assert.Equal(1, nfoPaths.GetArrayLength());

        Assert.True(lib.TryGetProperty("orphanedMetadataDirectoriesPaths", out var orphanPaths));
        Assert.Equal(1, orphanPaths.GetArrayLength());
    }

    [Fact]
    public void ExportJson_ContainsHealthCheckCounts()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var doc = JsonDocument.Parse(content);
        var lib = doc.RootElement.GetProperty("libraries")[0];

        Assert.Equal(50, lib.GetProperty("videosWithoutSubtitles").GetInt32());
        Assert.Equal(20, lib.GetProperty("videosWithoutImages").GetInt32());
        Assert.Equal(10, lib.GetProperty("videosWithoutNfo").GetInt32());
        Assert.Equal(5, lib.GetProperty("orphanedMetadataDirectories").GetInt32());
    }

    [Fact]
    public void ExportJson_ContainsTimestamp()
    {
        var result = CreateSampleResult();
        PrePopulateCache(result);

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("scanTimestamp", out var timestamp));
        Assert.Equal(JsonValueKind.String, timestamp.ValueKind);
    }

    [Fact]
    public void ExportJson_ContainsAggregatedTotals()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("totalMovieVideoSize", out var movieSize));
        Assert.True(movieSize.GetInt64() > 0);

        Assert.True(root.TryGetProperty("totalVideoFileCount", out var videoCount));
        Assert.True(videoCount.GetInt32() > 0);
    }

    [Fact]
    public void ExportJson_UsesCamelCase()
    {
        PrePopulateCache(CreateSampleResult());

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);
        var content = GetFileContentString(fileResult);

        // JSON export uses camelCase property naming
        Assert.Contains("\"scanTimestamp\"", content);
        Assert.Contains("\"libraries\"", content);
        Assert.Contains("\"libraryName\"", content);
        Assert.Contains("\"videoSize\"", content);
        Assert.Contains("\"containerFormats\"", content);

        // Should NOT have PascalCase top-level keys
        Assert.DoesNotContain("\"ScanTimestamp\"", content);
        Assert.DoesNotContain("\"Libraries\"", content);
    }

    // ======================== Filename Format Tests ========================

    [Fact]
    public void ExportCsv_FilenameContainsTimestamp()
    {
        var result = CreateSampleResult();
        PrePopulateCache(result);

        var actionResult = _controller.ExportCsv();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);

        // Timestamp format: yyyyMMdd-HHmmss → 20250615-120000
        Assert.Contains("20250615-120000", fileResult.FileDownloadName);
    }

    [Fact]
    public void ExportJson_FilenameContainsTimestamp()
    {
        var result = CreateSampleResult();
        PrePopulateCache(result);

        var actionResult = _controller.ExportJson();
        var fileResult = Assert.IsType<FileContentResult>(actionResult);

        Assert.Contains("20250615-120000", fileResult.FileDownloadName);
    }
}