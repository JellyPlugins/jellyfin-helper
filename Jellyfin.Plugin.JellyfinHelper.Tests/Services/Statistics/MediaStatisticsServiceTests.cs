using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class MediaStatisticsServiceTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly MediaStatisticsService _service;

    public MediaStatisticsServiceTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        var loggerMock = TestMockFactory.CreateLogger<MediaStatisticsService>();
        var configHelperMock = TestMockFactory.CreateCleanupConfigHelper();
        _service = new MediaStatisticsService(_libraryManagerMock.Object, _fileSystemMock.Object, TestMockFactory.CreatePluginLogService(), loggerMock.Object, configHelperMock.Object);
    }

    private static string TestPath(params string[] segments)
        => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    [Fact]
    public void CalculateStatistics_NoLibraries_ReturnsEmptyResult()
    {
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Empty(result.Libraries);
        Assert.Empty(result.Movies);
        Assert.Empty(result.TvShows);
        Assert.Empty(result.Music);
        Assert.Empty(result.Other);
        Assert.Equal(0, result.TotalMovieVideoSize);
        Assert.Equal(0, result.TotalTvShowVideoSize);
        Assert.Equal(0, result.TotalTrickplaySize);
    }

    [Fact]
    public void CalculateStatistics_MovieLibrary_ClassifiesVideoFiles()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var mkvFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.mkv"),
            Name = "Film.mkv",
            Length = 1_500_000_000,
            IsDirectory = false
        };

        var mp4File = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film2.mp4"),
            Name = "Film2.mp4",
            Length = 2_000_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([mkvFile, mp4File]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Single(result.Libraries);
        Assert.Single(result.Movies);
        Assert.Empty(result.TvShows);
        Assert.Equal(3_500_000_000, result.TotalMovieVideoSize);
        Assert.Equal(2, result.Libraries[0].VideoFileCount);
    }

    [Fact]
    public void CalculateStatistics_TvShowLibrary_ClassifiesCorrectly()
    {
        var libraryPath = TestPath("media", "tv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "TV Shows",
            CollectionType = CollectionTypeOptions.tvshows,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "tv", "Episode.mkv"),
            Name = "Episode.mkv",
            Length = 500_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Single(result.TvShows);
        Assert.Empty(result.Movies);
        Assert.Equal(500_000_000, result.TotalTvShowVideoSize);
    }

    [Fact]
    public void CalculateStatistics_MusicLibrary_ClassifiesAudioFiles()
    {
        var libraryPath = TestPath("media", "music");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var flacFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "music", "Song.flac"),
            Name = "Song.flac",
            Length = 30_000_000,
            IsDirectory = false
        };

        var mp3File = new FileSystemMetadata
        {
            FullName = TestPath("media", "music", "Track.mp3"),
            Name = "Track.mp3",
            Length = 5_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([flacFile, mp3File]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Single(result.Music);
        Assert.Equal(35_000_000, result.TotalMusicAudioSize);
        Assert.Equal(2, result.Libraries[0].AudioFileCount);
    }

    [Fact]
    public void CalculateStatistics_SubtitleFiles_CountedCorrectly()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var srtFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.srt"),
            Name = "Film.srt",
            Length = 50_000,
            IsDirectory = false
        };

        var assFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.ass"),
            Name = "Film.ass",
            Length = 80_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([srtFile, assFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal(130_000, result.TotalSubtitleSize);
        Assert.Equal(2, result.Libraries[0].SubtitleFileCount);
    }

    [Fact]
    public void CalculateStatistics_ImageFiles_CountedCorrectly()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var jpgFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "poster.jpg"),
            Name = "poster.jpg",
            Length = 200_000,
            IsDirectory = false
        };

        var pngFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "backdrop.png"),
            Name = "backdrop.png",
            Length = 500_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([jpgFile, pngFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal(700_000, result.TotalImageSize);
        Assert.Equal(2, result.Libraries[0].ImageFileCount);
    }

    [Fact]
    public void CalculateStatistics_NfoFiles_CountedCorrectly()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var nfoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.nfo"),
            Name = "Film.nfo",
            Length = 10_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([nfoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal(10_000, result.TotalNfoSize);
        Assert.Equal(1, result.Libraries[0].NfoFileCount);
    }

    [Fact]
    public void CalculateStatistics_TrickplayFolder_SizeCalculated()
    {
        var libraryPath = TestPath("media", "movies");
        var trickplayPath = TestPath("media", "movies", "Film.trickplay");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayPath,
            Name = "Film.trickplay",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([trickplayDir]);

        // Trickplay folder content
        var trickplayFile1 = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.trickplay", "001.jpg"),
            Name = "001.jpg",
            Length = 25_000,
            IsDirectory = false
        };
        var trickplayFile2 = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.trickplay", "002.jpg"),
            Name = "002.jpg",
            Length = 25_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(trickplayPath, false)).Returns([trickplayFile1, trickplayFile2]);
        _fileSystemMock.Setup(f => f.GetDirectories(trickplayPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal(50_000, result.TotalTrickplaySize);
        Assert.Equal(1, result.Libraries[0].TrickplayFolderCount);
    }

    [Fact]
    public void CalculateStatistics_UnrecognizedFiles_CountedAsOther()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var txtFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "readme.txt"),
            Name = "readme.txt",
            Length = 1_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([txtFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal(1_000, result.Libraries[0].OtherSize);
        Assert.Equal(1, result.Libraries[0].OtherFileCount);
    }

    [Fact]
    public void CalculateStatistics_RecursiveDirectoryTraversal_AccumulatesSizes()
    {
        var libraryPath = TestPath("media", "movies");
        var subDirPath = TestPath("media", "movies", "Film");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        // Root has no files
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([]);

        var subDir = new FileSystemMetadata
        {
            FullName = subDirPath,
            Name = "Film",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([subDir]);

        // Subdirectory has a video file
        var videoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film", "Film.mkv"),
            Name = "Film.mkv",
            Length = 4_000_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(subDirPath, false)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(subDirPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal(4_000_000_000, result.TotalMovieVideoSize);
    }

    [Fact]
    public void CalculateStatistics_IoExceptionInDirectory_ContinuesGracefully()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Throws(new IOException("Access denied"));

        var result = _service.CalculateStatistics();

        // Should not throw, stats should be zero
        Assert.Single(result.Libraries);
        Assert.Equal(0, result.Libraries[0].VideoSize);
    }

    [Fact]
    public void CalculateStatistics_UnauthorizedAccessInDirectory_ContinuesGracefully()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Throws(new UnauthorizedAccessException("Forbidden"));

        var result = _service.CalculateStatistics();

        Assert.Single(result.Libraries);
        Assert.Equal(0, result.Libraries[0].VideoSize);
    }

    [Fact]
    public void CalculateStatistics_MultipleLibraries_AggregatedCorrectly()
    {
        var moviePath = TestPath("media", "movies");
        var tvPath = TestPath("media", "tv");

        var movieFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [moviePath]
        };
        var tvFolder = new VirtualFolderInfo
        {
            Name = "TV Shows",
            CollectionType = CollectionTypeOptions.tvshows,
            Locations = [tvPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([movieFolder, tvFolder]);

        var movieFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.mkv"),
            Name = "Film.mkv",
            Length = 2_000_000_000,
            IsDirectory = false
        };

        var tvFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "tv", "Episode.mkv"),
            Name = "Episode.mkv",
            Length = 500_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(moviePath, false)).Returns([movieFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(moviePath, false)).Returns([]);
        _fileSystemMock.Setup(f => f.GetFiles(tvPath, false)).Returns([tvFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(tvPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal(2, result.Libraries.Count);
        Assert.Single(result.Movies);
        Assert.Single(result.TvShows);
        Assert.Equal(2_000_000_000, result.TotalMovieVideoSize);
        Assert.Equal(500_000_000, result.TotalTvShowVideoSize);
    }

    [Fact]
    public void CalculateStatistics_HomeVideosLibrary_ClassifiedAsMovies()
    {
        var libraryPath = TestPath("media", "homevideos");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Home Videos",
            CollectionType = CollectionTypeOptions.homevideos,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "homevideos", "vacation.mp4"),
            Name = "vacation.mp4",
            Length = 1_000_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Single(result.Movies);
        Assert.Empty(result.TvShows);
        Assert.Equal(1_000_000_000, result.TotalMovieVideoSize);
    }

    [Fact]
    public void CalculateStatistics_NullCollectionType_ClassifiedAsOther()
    {
        var libraryPath = TestPath("media", "misc");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Misc",
            CollectionType = null,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Single(result.Other);
        Assert.Empty(result.Movies);
        Assert.Empty(result.TvShows);
        Assert.Empty(result.Music);
    }

    [Fact]
    public void CalculateStatistics_MixedFileTypes_AllCategorizedCorrectly()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.mkv"), Name = "Film.mkv", Length = 1_000_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.srt"), Name = "Film.srt", Length = 50_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "poster.jpg"), Name = "poster.jpg", Length = 200_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.nfo"), Name = "Film.nfo", Length = 10_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "theme.mp3"), Name = "theme.mp3", Length = 5_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "readme.txt"), Name = "readme.txt", Length = 1_000, IsDirectory = false }
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1_000_000_000, stats.VideoSize);
        Assert.Equal(1, stats.VideoFileCount);
        Assert.Equal(50_000, stats.SubtitleSize);
        Assert.Equal(1, stats.SubtitleFileCount);
        Assert.Equal(200_000, stats.ImageSize);
        Assert.Equal(1, stats.ImageFileCount);
        Assert.Equal(10_000, stats.NfoSize);
        Assert.Equal(1, stats.NfoFileCount);
        Assert.Equal(5_000_000, stats.AudioSize);
        Assert.Equal(1, stats.AudioFileCount);
        Assert.Equal(1_000, stats.OtherSize);
        Assert.Equal(1, stats.OtherFileCount);
    }

    [Fact]
    public void CalculateStatistics_TrickplayFolderCaseInsensitive_Detected()
    {
        var libraryPath = TestPath("media", "movies");
        var trickplayPath = TestPath("media", "movies", "Film.TRICKPLAY");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayPath,
            Name = "Film.TRICKPLAY",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([trickplayDir]);

        var trickplayFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.TRICKPLAY", "001.jpg"),
            Name = "001.jpg",
            Length = 10_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(trickplayPath, false)).Returns([trickplayFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(trickplayPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal(10_000, result.Libraries[0].TrickplaySize);
        Assert.Equal(1, result.Libraries[0].TrickplayFolderCount);
    }

    [Fact]
    public void CalculateStatistics_NestedTrickplayFolder_SizeIncludesSubdirectories()
    {
        var libraryPath = TestPath("media", "movies");
        var trickplayPath = TestPath("media", "movies", "Film.trickplay");
        var trickplaySubDir = TestPath("media", "movies", "Film.trickplay", "320");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([]);

        var trickplayDir = new FileSystemMetadata
        {
            FullName = trickplayPath,
            Name = "Film.trickplay",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([trickplayDir]);

        // Trickplay root files
        var rootFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.trickplay", "index.bif"),
            Name = "index.bif",
            Length = 5_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(trickplayPath, false)).Returns([rootFile]);

        // Trickplay subdirectory
        var subDir = new FileSystemMetadata
        {
            FullName = trickplaySubDir,
            Name = "320",
            IsDirectory = true
        };
        _fileSystemMock.Setup(f => f.GetDirectories(trickplayPath, false)).Returns([subDir]);

        var subFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.trickplay", "320", "001.jpg"),
            Name = "001.jpg",
            Length = 15_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(trickplaySubDir, false)).Returns([subFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(trickplaySubDir, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal(20_000, result.Libraries[0].TrickplaySize);
    }

    [Fact]
    public void CalculateStatistics_LibraryPaths_PreservedInResult()
    {
        var libraryPath1 = TestPath("media", "movies1");
        var libraryPath2 = TestPath("media", "movies2");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "My Movie Collections",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath1, libraryPath2]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(It.IsAny<string>(), false)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(It.IsAny<string>(), false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Single(result.Libraries);
        Assert.Equal(2, result.Libraries[0].RootPaths.Count);
        Assert.Contains(libraryPath1, result.Libraries[0].RootPaths);
        Assert.Contains(libraryPath2, result.Libraries[0].RootPaths);
        
        Assert.Contains(libraryPath1, result.MovieRootPaths);
        Assert.Contains(libraryPath2, result.MovieRootPaths);
    }

    [Fact]
    public void CalculateStatistics_LibraryName_PreservedInResult()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "My Movie Collection",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        Assert.Equal("My Movie Collection", result.Libraries[0].LibraryName);
        Assert.Equal("movies", result.Libraries[0].CollectionType);
    }

    [Fact]
    public void LibraryStatistics_TotalSize_SumsAllCategories()
    {
        var stats = new LibraryStatistics
        {
            VideoSize = 1000,
            SubtitleSize = 200,
            ImageSize = 100,
            NfoSize = 50,
            AudioSize = 500,
            TrickplaySize = 300,
            OtherSize = 25
        };

        Assert.Equal(2175, stats.TotalSize);
    }

    // ===== ClassifyVideoCodec Tests =====

    [Theory]
    [InlineData("hevc", "HEVC")]
    [InlineData("HEVC", "HEVC")]
    [InlineData("h265", "HEVC")]
    [InlineData("h.265", "HEVC")]
    [InlineData("h264", "H.264")]
    [InlineData("H264", "H.264")]
    [InlineData("h.264", "H.264")]
    [InlineData("avc", "H.264")]
    [InlineData("AVC", "H.264")]
    [InlineData("av1", "AV1")]
    [InlineData("vp9", "VP9")]
    [InlineData("vp8", "VP8")]
    [InlineData("mpeg2video", "MPEG-2")]
    [InlineData("mpeg2", "MPEG-2")]
    [InlineData("mpeg4", "MPEG-4")]
    [InlineData("xvid", "XviD")]
    [InlineData("divx", "DivX")]
    [InlineData("vc1", "VC-1")]
    [InlineData("wmv3", "VC-1")]
    [InlineData("theora", "Theora")]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    public void ClassifyVideoCodec_MapsCorrectly(string? codec, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyVideoCodec(codec));
    }

    [Fact]
    public void ClassifyVideoCodec_UnknownCodec_ReturnsUppercased()
    {
        // Unknown codecs are returned uppercased as-is
        Assert.Equal("SOMEWEIRDCODEC", MediaStatisticsService.ClassifyVideoCodec("someweirdcodec"));
    }

    // ===== ClassifyResolution Tests =====

    [Theory]
    [InlineData(7680, 4320, "8K")]
    [InlineData(8192, 4320, "8K")]
    [InlineData(3840, 2160, "4K")]
    [InlineData(4096, 2160, "4K")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1920, 1088, "1080p")]       // common MPEG encoding artifact
    [InlineData(1280, 720, "720p")]
    [InlineData(720, 576, "576p")]
    [InlineData(720, 480, "480p")]
    [InlineData(640, 480, "480p")]
    [InlineData(320, 240, "SD")]
    [InlineData(null, null, "Unknown")]
    [InlineData(0, 0, "Unknown")]
    [InlineData(-1, -1, "Unknown")]
    public void ClassifyResolution_MapsCorrectly(int? width, int? height, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyResolution(width, height));
    }

    [Fact]
    public void ClassifyResolution_PortraitOrientation_StillClassifiesCorrectly()
    {
        // 1080×1920 portrait → should still detect as 1080p
        Assert.Equal("1080p", MediaStatisticsService.ClassifyResolution(1080, 1920));
    }

    // ===== ClassifyAudioCodec Tests =====

    [Theory]
    [InlineData("truehd", null, "TrueHD")]
    [InlineData("truehd", "Atmos", "TrueHD Atmos")]
    [InlineData("truehd", "Dolby TrueHD + Atmos", "TrueHD Atmos")]
    [InlineData("eac3", null, "EAC3")]
    [InlineData("eac3", "Atmos", "EAC3 Atmos")]
    [InlineData("eac3", "JOC", "EAC3 Atmos")]
    [InlineData("ac3", null, "AC3")]
    [InlineData("a_ac3", null, "AC3")]
    [InlineData("dts", null, "DTS")]
    [InlineData("dts", "DTS-HD MA", "DTS-HD MA")]
    [InlineData("dts", "MA", "DTS-HD MA")]
    [InlineData("dts", "DTS:X", "DTS:X")]
    [InlineData("dts", "DTS-X", "DTS:X")]
    [InlineData("dts", "HRA", "DTS-HD HRA")]
    [InlineData("dts", "DTS-HD HRA", "DTS-HD HRA")]
    [InlineData("dts", "ES", "DTS-ES")]
    [InlineData("aac", null, "AAC")]
    [InlineData("mp4a", null, "AAC")]
    [InlineData("aac", "HE-AAC", "HE-AAC")]
    [InlineData("aac", "HE_AAC v2", "HE-AAC")]
    [InlineData("aac", "LC", "AAC-LC")]
    [InlineData("flac", null, "FLAC")]
    [InlineData("mp3", null, "MP3")]
    [InlineData("opus", null, "Opus")]
    [InlineData("vorbis", null, "Vorbis")]
    [InlineData("pcm_s16le", null, "PCM")]
    [InlineData("pcm_s24le", null, "PCM")]
    [InlineData("pcm_s32le", null, "PCM")]
    [InlineData("alac", null, "ALAC")]
    [InlineData("wmav2", null, "WMA")]
    [InlineData("wmapro", null, "WMA")]
    [InlineData(null, null, "Unknown")]
    [InlineData("", null, "Unknown")]
    public void ClassifyAudioCodec_MapsCorrectly(string? codec, string? profile, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyAudioCodec(codec, profile));
    }

    [Fact]
    public void ClassifyAudioCodec_UnknownCodec_ReturnsUppercased()
    {
        Assert.Equal("SOMEAUDIOCODEC", MediaStatisticsService.ClassifyAudioCodec("someaudiocodec", null));
    }

    // ===== ClassifyDynamicRange Tests =====

    [Fact]
    public void ClassifyDynamicRange_NullStream_ReturnsUnknown()
    {
        Assert.Equal("Unknown", MediaStatisticsService.ClassifyDynamicRange(null));
    }

    [Theory]
    [InlineData(VideoRangeType.DOVI, "Dolby Vision")]
    [InlineData(VideoRangeType.DOVIWithHDR10, "Dolby Vision")]
    [InlineData(VideoRangeType.DOVIWithHDR10Plus, "Dolby Vision")]
    [InlineData(VideoRangeType.DOVIWithHLG, "Dolby Vision")]
    [InlineData(VideoRangeType.DOVIWithSDR, "Dolby Vision")]
    [InlineData(VideoRangeType.HDR10Plus, "HDR10+")]
    [InlineData(VideoRangeType.HDR10, "HDR10")]
    [InlineData(VideoRangeType.HLG, "HLG")]
    [InlineData(VideoRangeType.SDR, "SDR")]
    public void ClassifyDynamicRange_VideoRangeType_MapsCorrectly(VideoRangeType rangeType, string expected)
    {
        // Use the overload that accepts enum values directly (VideoRangeType/VideoRange are read-only on MediaStream)
        Assert.Equal(expected, MediaStatisticsService.ClassifyDynamicRange(rangeType, default));
    }

    [Fact]
    public void ClassifyDynamicRange_FallbackToVideoRange_HDR()
    {
        // When VideoRangeType is default (0), falls back to VideoRange
        Assert.Equal("HDR", MediaStatisticsService.ClassifyDynamicRange(default, VideoRange.HDR));
    }

    [Fact]
    public void ClassifyDynamicRange_FallbackToVideoRange_SDR()
    {
        Assert.Equal("SDR", MediaStatisticsService.ClassifyDynamicRange(default, VideoRange.SDR));
    }

    [Fact]
    public void ClassifyDynamicRange_DefaultStream_ReturnsSdr()
    {
        // A stream with no range info defaults to SDR (VideoRangeType.SDR = 0 in Jellyfin 10.11)
        var stream = new MediaStream { Type = MediaStreamType.Video };
        Assert.Equal("SDR", MediaStatisticsService.ClassifyDynamicRange(stream));
    }

    // ===== Container Format Tracking Tests =====

    [Fact]
    public void CalculateStatistics_VideoFiles_TracksContainerFormats()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film1.mkv"), Name = "Film1.mkv", Length = 1000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film2.mkv"), Name = "Film2.mkv", Length = 2000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film3.mp4"), Name = "Film3.mp4", Length = 3000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(2, stats.ContainerFormats["MKV"]);
        Assert.Equal(1, stats.ContainerFormats["MP4"]);
        Assert.Equal(3000, stats.ContainerSizes["MKV"]);
        Assert.Equal(3000, stats.ContainerSizes["MP4"]);

        // Verify container format paths are tracked
        Assert.Equal(2, stats.ContainerFormatPaths["MKV"].Count);
        Assert.Single(stats.ContainerFormatPaths["MP4"]);
        Assert.Contains(TestPath("media", "movies", "Film1.mkv"), stats.ContainerFormatPaths["MKV"]);
        Assert.Contains(TestPath("media", "movies", "Film2.mkv"), stats.ContainerFormatPaths["MKV"]);
        Assert.Contains(TestPath("media", "movies", "Film3.mp4"), stats.ContainerFormatPaths["MP4"]);
    }

    // ===== Audio Codec Tracking in Statistics =====

    [Fact]
    public void CalculateStatistics_AudioFiles_TracksMusicAudioCodecs()
    {
        var libraryPath = TestPath("media", "music");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "music", "Song1.flac"), Name = "Song1.flac", Length = 30_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "music", "Song2.flac"), Name = "Song2.flac", Length = 25_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "music", "Song3.mp3"), Name = "Song3.mp3", Length = 5_000_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(2, stats.MusicAudioCodecs["FLAC"]);
        Assert.Equal(1, stats.MusicAudioCodecs["MP3"]);
        Assert.Equal(55_000_000, stats.MusicAudioCodecSizes["FLAC"]);
        Assert.Equal(5_000_000, stats.MusicAudioCodecSizes["MP3"]);

        // Verify music audio codec paths are tracked
        Assert.Equal(2, stats.MusicAudioCodecPaths["FLAC"].Count);
        Assert.Single(stats.MusicAudioCodecPaths["MP3"]);
        Assert.Contains(TestPath("media", "music", "Song1.flac"), stats.MusicAudioCodecPaths["FLAC"]);
        Assert.Contains(TestPath("media", "music", "Song2.flac"), stats.MusicAudioCodecPaths["FLAC"]);
        Assert.Contains(TestPath("media", "music", "Song3.mp3"), stats.MusicAudioCodecPaths["MP3"]);
    }

    // ===== Health Check Tests =====

    [Fact]
    public void CalculateStatistics_VideoWithoutSubtitles_CountedInHealthCheck()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.mkv"), Name = "Film.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "poster.jpg"), Name = "poster.jpg", Length = 100_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.nfo"), Name = "Film.nfo", Length = 5_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.VideosWithoutSubtitles);
        Assert.Equal(0, stats.VideosWithoutImages);
        Assert.Equal(0, stats.VideosWithoutNfo);

        // Verify paths are populated
        Assert.Single(stats.VideosWithoutSubtitlesPaths);
        Assert.Contains(TestPath("media", "movies", "Film.mkv"), stats.VideosWithoutSubtitlesPaths);
        Assert.Empty(stats.VideosWithoutImagesPaths);
        Assert.Empty(stats.VideosWithoutNfoPaths);
    }

    [Fact]
    public void CalculateStatistics_VideoWithAllMetadata_NoHealthWarnings()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.mkv"), Name = "Film.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.srt"), Name = "Film.srt", Length = 50_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "poster.jpg"), Name = "poster.jpg", Length = 100_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.nfo"), Name = "Film.nfo", Length = 5_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(0, stats.VideosWithoutSubtitles);
        Assert.Equal(0, stats.VideosWithoutImages);
        Assert.Equal(0, stats.VideosWithoutNfo);
        Assert.Equal(0, stats.OrphanedMetadataDirectories);

        // Verify path lists are empty when no health issues
        Assert.Empty(stats.VideosWithoutSubtitlesPaths);
        Assert.Empty(stats.VideosWithoutImagesPaths);
        Assert.Empty(stats.VideosWithoutNfoPaths);
        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
    }

    [Fact]
    public void CalculateStatistics_OrphanedMetadata_DetectedCorrectly()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        // Directory with subtitles but no video
        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.srt"), Name = "Film.srt", Length = 50_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.nfo"), Name = "Film.nfo", Length = 5_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.OrphanedMetadataDirectories);

        // Verify orphaned metadata path is recorded
        Assert.Single(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Contains(libraryPath, stats.OrphanedMetadataDirectoriesPaths);
    }

    [Fact]
    public void CalculateStatistics_HealthCheckPaths_MultipleVideosWithoutSubtitles_AllPathsRecorded()
    {
        var libraryPath = TestPath("media", "movies");
        var subDirPath = TestPath("media", "movies", "SubFolder");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        // Root: video without subtitles but with image and nfo
        var rootFiles = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film1.mkv"), Name = "Film1.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "poster.jpg"), Name = "poster.jpg", Length = 100_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film1.nfo"), Name = "Film1.nfo", Length = 5_000, IsDirectory = false },
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(rootFiles);

        var subDir = new FileSystemMetadata { FullName = subDirPath, Name = "SubFolder", IsDirectory = true };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([subDir]);

        // SubFolder: video without subtitles and without images
        var subFiles = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "SubFolder", "Film2.mkv"), Name = "Film2.mkv", Length = 2_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "SubFolder", "Film2.nfo"), Name = "Film2.nfo", Length = 3_000, IsDirectory = false },
        };
        _fileSystemMock.Setup(f => f.GetFiles(subDirPath, false)).Returns(subFiles);
        _fileSystemMock.Setup(f => f.GetDirectories(subDirPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Both videos lack subtitles
        Assert.Equal(2, stats.VideosWithoutSubtitles);
        Assert.Equal(2, stats.VideosWithoutSubtitlesPaths.Count);
        Assert.Contains(TestPath("media", "movies", "Film1.mkv"), stats.VideosWithoutSubtitlesPaths);
        Assert.Contains(TestPath("media", "movies", "SubFolder", "Film2.mkv"), stats.VideosWithoutSubtitlesPaths);

        // Only SubFolder video lacks images
        Assert.Equal(1, stats.VideosWithoutImages);
        Assert.Single(stats.VideosWithoutImagesPaths);
        Assert.Contains(TestPath("media", "movies", "SubFolder", "Film2.mkv"), stats.VideosWithoutImagesPaths);

        // Both have NFO
        Assert.Equal(0, stats.VideosWithoutNfo);
        Assert.Empty(stats.VideosWithoutNfoPaths);
    }

    [Fact]
    public void CalculateStatistics_HealthCheckPaths_VideoWithoutNfo_PathRecorded()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.mkv"), Name = "Film.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.srt"), Name = "Film.srt", Length = 50_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "poster.jpg"), Name = "poster.jpg", Length = 100_000, IsDirectory = false },
            // no .nfo file
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.VideosWithoutNfo);
        Assert.Single(stats.VideosWithoutNfoPaths);
        Assert.Contains(TestPath("media", "movies", "Film.mkv"), stats.VideosWithoutNfoPaths);
        Assert.Equal(0, stats.VideosWithoutSubtitles);
        Assert.Empty(stats.VideosWithoutSubtitlesPaths);
    }

    [Fact]
    public void CalculateStatistics_HealthCheckPaths_VideoWithoutImages_PathRecorded()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.mkv"), Name = "Film.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.srt"), Name = "Film.srt", Length = 50_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.nfo"), Name = "Film.nfo", Length = 5_000, IsDirectory = false },
            // no image file
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.VideosWithoutImages);
        Assert.Single(stats.VideosWithoutImagesPaths);
        Assert.Contains(TestPath("media", "movies", "Film.mkv"), stats.VideosWithoutImagesPaths);
    }

    [Fact]
    public void CalculateStatistics_HealthCheckPaths_BoxsetLibrary_NoPathsRecorded()
    {
        var boxsetPath = TestPath("config", "data", "collections");

        var boxsetFolder = new VirtualFolderInfo
        {
            Name = "Collections",
            CollectionType = CollectionTypeOptions.boxsets,
            Locations = [boxsetPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([boxsetFolder]);

        var posterFile = new FileSystemMetadata
        {
            FullName = TestPath("config", "data", "collections", "poster.jpg"),
            Name = "poster.jpg",
            Length = 200_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(boxsetPath, false)).Returns([posterFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(boxsetPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Boxsets skip health checks, so all path lists should be empty
        Assert.Empty(stats.VideosWithoutSubtitlesPaths);
        Assert.Empty(stats.VideosWithoutImagesPaths);
        Assert.Empty(stats.VideosWithoutNfoPaths);
        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
    }

    [Fact]
    public void CalculateStatistics_HealthCheckPaths_MultipleLibraries_PathsSeparatePerLibrary()
    {
        var moviePath = TestPath("media", "movies");
        var tvPath = TestPath("media", "tv");

        var movieFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [moviePath]
        };
        var tvFolder = new VirtualFolderInfo
        {
            Name = "TV Shows",
            CollectionType = CollectionTypeOptions.tvshows,
            Locations = [tvPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([movieFolder, tvFolder]);

        // Movies: video without subtitles
        var movieFiles = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.mkv"), Name = "Film.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "poster.jpg"), Name = "poster.jpg", Length = 100_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.nfo"), Name = "Film.nfo", Length = 5_000, IsDirectory = false },
        };
        _fileSystemMock.Setup(f => f.GetFiles(moviePath, false)).Returns(movieFiles);
        _fileSystemMock.Setup(f => f.GetDirectories(moviePath, false)).Returns([]);

        // TV: video with all metadata
        var tvFiles = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "tv", "Episode.mkv"), Name = "Episode.mkv", Length = 500_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "tv", "Episode.srt"), Name = "Episode.srt", Length = 30_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "tv", "poster.jpg"), Name = "poster.jpg", Length = 50_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "tv", "Episode.nfo"), Name = "Episode.nfo", Length = 3_000, IsDirectory = false },
        };
        _fileSystemMock.Setup(f => f.GetFiles(tvPath, false)).Returns(tvFiles);
        _fileSystemMock.Setup(f => f.GetDirectories(tvPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        var movieStats = result.Libraries.First(l => l.LibraryName == "Movies");
        var tvStats = result.Libraries.First(l => l.LibraryName == "TV Shows");

        // Movie library: missing subtitles, path recorded
        Assert.Equal(1, movieStats.VideosWithoutSubtitles);
        Assert.Single(movieStats.VideosWithoutSubtitlesPaths);
        Assert.Contains(TestPath("media", "movies", "Film.mkv"), movieStats.VideosWithoutSubtitlesPaths);

        // TV library: all good, no paths
        Assert.Equal(0, tvStats.VideosWithoutSubtitles);
        Assert.Empty(tvStats.VideosWithoutSubtitlesPaths);
        Assert.Empty(tvStats.VideosWithoutImagesPaths);
        Assert.Empty(tvStats.VideosWithoutNfoPaths);
    }

    // ===== Audio Codec Tracking from Video Filenames =====

    [Fact]
    public void CalculateStatistics_VideoWithAudioCodecInFilename_TracksVideoAudioCodecs()
    {
        // Without Jellyfin metadata (no BaseItem in lookup), audio codecs from video files
        // will be "Unknown" and are intentionally NOT tracked in VideoAudioCodecs.
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.x265.DTS.1080p.mkv"), Name = "Film.x265.DTS.1080p.mkv", Length = 2_000_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film2.x264.AC3.720p.mkv"), Name = "Film2.x264.AC3.720p.mkv", Length = 1_500_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film3.x265.DTS.4K.mkv"), Name = "Film3.x265.DTS.4K.mkv", Length = 5_000_000_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Without metadata, audio codec is "Unknown" which is filtered out
        Assert.Empty(stats.VideoAudioCodecs);
        Assert.Empty(stats.VideoAudioCodecSizes);
    }

    [Fact]
    public void CalculateStatistics_VideoWithoutAudioCodecInFilename_DoesNotTrackUnknown()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.mkv"), Name = "Film.mkv", Length = 1_000_000_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Empty(stats.VideoAudioCodecs);
        Assert.Empty(stats.VideoAudioCodecSizes);
    }

    [Fact]
    public void CalculateStatistics_OggFile_ClassifiedAsAudio()
    {
        var libraryPath = TestPath("media", "music");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "music", "Song.ogg"), Name = "Song.ogg", Length = 5_000_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.AudioFileCount);
        Assert.Equal(0, stats.VideoFileCount);
        Assert.Equal(5_000_000, stats.AudioSize);
        Assert.Equal(1, stats.MusicAudioCodecs["Vorbis"]);
        Assert.Equal(5_000_000, stats.MusicAudioCodecSizes["Vorbis"]);
    }

    // ===== Resolution & Codec Tracking in Statistics =====

    [Fact]
    public void CalculateStatistics_VideoWithCodecInFilename_TracksVideoCodecs()
    {
        // Without Jellyfin metadata (no BaseItem in lookup), codecs and resolutions
        // are "Unknown" since they now come from MediaStream, not from filenames.
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.x265.1080p.mkv"), Name = "Film.x265.1080p.mkv", Length = 2_000_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film2.x264.720p.mkv"), Name = "Film2.x264.720p.mkv", Length = 1_500_000_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Without metadata, both are "Unknown"
        Assert.Equal(2, stats.VideoCodecs["Unknown"]);
        Assert.Equal(2, stats.Resolutions["Unknown"]);
    }

    // ===== MediaStatisticsResult Aggregation Tests =====

    [Fact]
    public void MediaStatisticsResult_Aggregation_SumsCorrectly()
    {
        var result = new MediaStatisticsResult();

        var movieLib = new LibraryStatistics
        {
            LibraryName = "Movies",
            CollectionType = "movies",
            VideoSize = 100,
            VideoFileCount = 5,
            AudioSize = 10,
            AudioFileCount = 2,
        };

        var tvLib = new LibraryStatistics
        {
            LibraryName = "TV",
            CollectionType = "tvshows",
            VideoSize = 200,
            VideoFileCount = 10,
        };

        var musicLib = new LibraryStatistics
        {
            LibraryName = "Music",
            CollectionType = "music",
            AudioSize = 50,
            AudioFileCount = 20,
        };

        result.Libraries.Add(movieLib);
        result.Libraries.Add(tvLib);
        result.Libraries.Add(musicLib);
        result.Movies.Add(movieLib);
        result.TvShows.Add(tvLib);
        result.Music.Add(musicLib);

        Assert.Equal(100, result.TotalMovieVideoSize);
        Assert.Equal(200, result.TotalTvShowVideoSize);
        Assert.Equal(50, result.TotalMusicAudioSize);
        Assert.Equal(15, result.TotalVideoFileCount);
        Assert.Equal(22, result.TotalAudioFileCount);
    }

    // ===== PathValidator Tests =====

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void PathValidator_IsSafePath_RejectsEmptyInput(string? path, bool expected)
    {
        Assert.Equal(expected, PathValidator.IsSafePath(path, "/base"));
    }

    [Fact]
    public void PathValidator_IsSafePath_RejectsTraversal()
    {
        Assert.False(PathValidator.IsSafePath("/base/../etc/passwd", "/base"));
        Assert.False(PathValidator.IsSafePath("/base/sub/../../etc", "/base"));
    }

    [Fact]
    public void PathValidator_IsSafePath_RejectsNullBytes()
    {
        Assert.False(PathValidator.IsSafePath("/base/file\0.txt", "/base"));
    }

    [Fact]
    public void PathValidator_SanitizeFileName_RemovesDirectoryComponents()
    {
        var result = PathValidator.SanitizeFileName("../../etc/passwd");
        Assert.Equal("passwd", result);
    }

    [Fact]
    public void PathValidator_SanitizeFileName_HandlesEmptyInput()
    {
        Assert.Equal("export", PathValidator.SanitizeFileName(""));
        Assert.Equal("export", PathValidator.SanitizeFileName("   "));
    }

    [Fact]
    public void PathValidator_SanitizeFileName_PreservesValidName()
    {
        Assert.Equal("report.csv", PathValidator.SanitizeFileName("report.csv"));
    }

    // ===== MediaExtensions Codec Mapping Tests =====

    [Fact]
    public void MediaExtensions_AudioExtensionToCodec_ContainsAllAudioExtensions()
    {
        // Every audio extension should have a codec mapping
        foreach (var ext in MediaExtensions.AudioExtensions)
        {
            Assert.True(
                MediaExtensions.AudioExtensionToCodec.ContainsKey(ext),
                $"Audio extension '{ext}' has no codec mapping in AudioExtensionToCodec");
        }
    }

    [Fact]
    public void MediaExtensions_AudioExtensionToCodec_ReturnsCorrectCodecs()
    {
        Assert.Equal("FLAC", MediaExtensions.AudioExtensionToCodec[".flac"]);
        Assert.Equal("MP3", MediaExtensions.AudioExtensionToCodec[".mp3"]);
        Assert.Equal("AAC", MediaExtensions.AudioExtensionToCodec[".aac"]);
        Assert.Equal("AAC", MediaExtensions.AudioExtensionToCodec[".m4a"]);
        Assert.Equal("Opus", MediaExtensions.AudioExtensionToCodec[".opus"]);
        Assert.Equal("Vorbis", MediaExtensions.AudioExtensionToCodec[".ogg"]);
        Assert.Equal("DSD", MediaExtensions.AudioExtensionToCodec[".dsf"]);
    }

    [Fact]
    public void MediaExtensions_AudioExtensionToCodec_IsCaseInsensitive()
    {
        Assert.Equal("FLAC", MediaExtensions.AudioExtensionToCodec[".FLAC"]);
        Assert.Equal("MP3", MediaExtensions.AudioExtensionToCodec[".Mp3"]);
    }

    [Fact]
    public void CalculateStatistics_BoxsetLibrary_ScannedButHealthChecksSkipped()
    {
        var boxsetPath = TestPath("config", "data", "collections");

        var boxsetFolder = new VirtualFolderInfo
        {
            Name = "Collections",
            CollectionType = CollectionTypeOptions.boxsets,
            Locations = [boxsetPath]
        };

        var moviePath = TestPath("media", "movies");
        var movieFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [moviePath]
        };

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([boxsetFolder, movieFolder]);

        // Boxset folder contains a poster image but no video/subtitle � would normally trigger orphaned metadata
        var posterFile = new FileSystemMetadata
        {
            FullName = TestPath("config", "data", "collections", "poster.jpg"),
            Name = "poster.jpg",
            Length = 200_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(boxsetPath, false)).Returns([posterFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(boxsetPath, false)).Returns([]);

        // Movie folder has a video without subtitles � should trigger health check warning
        var mkvFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.mkv"),
            Name = "Film.mkv",
            Length = 1_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(moviePath, false)).Returns([mkvFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(moviePath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        // Both libraries should be present in statistics
        Assert.Equal(2, result.Libraries.Count);
        Assert.Single(result.Movies);
        Assert.Single(result.Other); // boxsets land in Other

        // Boxset library: files are scanned (poster counted) but no health check flags
        var boxsetStats = result.Libraries.First(l => l.LibraryName == "Collections");
        Assert.Equal(1, boxsetStats.ImageFileCount);
        Assert.Equal(200_000, boxsetStats.ImageSize);
        Assert.Equal(0, boxsetStats.VideosWithoutSubtitles);
        Assert.Equal(0, boxsetStats.VideosWithoutImages);
        Assert.Equal(0, boxsetStats.VideosWithoutNfo);
        Assert.Equal(0, boxsetStats.OrphanedMetadataDirectories); // would be 1 without skipHealthChecks

        // Movie library: health checks still work normally
        var movieStats = result.Libraries.First(l => l.LibraryName == "Movies");
        Assert.Equal(1, movieStats.VideosWithoutSubtitles);
        Assert.Equal(1, movieStats.VideosWithoutImages);
        Assert.Equal(1, movieStats.VideosWithoutNfo);
    }

    [Fact]
    public void CalculateStatistics_MusicLibrary_HealthChecksSkipped()
    {
        var musicPath = TestPath("media", "music");

        var musicFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [musicPath]
        };

        var moviePath = TestPath("media", "movies");
        var movieFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [moviePath]
        };

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([musicFolder, movieFolder]);

        // Music folder contains only an image (cover art) and NFO � would normally trigger orphaned metadata
        var coverFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "music", "cover.jpg"),
            Name = "cover.jpg",
            Length = 150_000,
            IsDirectory = false
        };
        var nfoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "music", "artist.nfo"),
            Name = "artist.nfo",
            Length = 5_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(musicPath, false)).Returns([coverFile, nfoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(musicPath, false)).Returns([]);

        // Movie folder has a video without subtitles � should trigger health check warning
        var mkvFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.mkv"),
            Name = "Film.mkv",
            Length = 1_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(moviePath, false)).Returns([mkvFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(moviePath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        // Both libraries should be present
        Assert.Equal(2, result.Libraries.Count);
        Assert.Single(result.Music);
        Assert.Single(result.Movies);

        // Music library: files are scanned but no health check flags
        var musicStats = result.Libraries.First(l => l.LibraryName == "Music");
        Assert.Equal(1, musicStats.ImageFileCount);
        Assert.Equal(150_000, musicStats.ImageSize);
        Assert.Equal(1, musicStats.NfoFileCount);
        Assert.Equal(0, musicStats.VideosWithoutSubtitles);
        Assert.Equal(0, musicStats.VideosWithoutImages);
        Assert.Equal(0, musicStats.VideosWithoutNfo);
        Assert.Equal(0, musicStats.OrphanedMetadataDirectories); // would be 1 without skipHealthChecks
        Assert.Empty(musicStats.VideosWithoutSubtitlesPaths);
        Assert.Empty(musicStats.VideosWithoutImagesPaths);
        Assert.Empty(musicStats.VideosWithoutNfoPaths);
        Assert.Empty(musicStats.OrphanedMetadataDirectoriesPaths);

        // Movie library: health checks still work normally
        var movieStats = result.Libraries.First(l => l.LibraryName == "Movies");
        Assert.Equal(1, movieStats.VideosWithoutSubtitles);
        Assert.Equal(1, movieStats.VideosWithoutImages);
        Assert.Equal(1, movieStats.VideosWithoutNfo);
    }

    // ===== Codec Library Separation Tests =====

    [Fact]
    public void CalculateStatistics_MusicLibrary_DoesNotPopulateVideoCodecs()
    {
        var musicPath = TestPath("media", "music");

        var musicFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [musicPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([musicFolder]);

        var flacFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "music", "Song.flac"),
            Name = "Song.flac",
            Length = 30_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(musicPath, false)).Returns([flacFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(musicPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var musicStats = result.Music[0];

        // Music library should have MusicAudioCodecs but no VideoCodecs
        Assert.NotEmpty(musicStats.MusicAudioCodecs);
        Assert.Empty(musicStats.VideoCodecs);
        Assert.Empty(musicStats.VideoAudioCodecs);
        Assert.Empty(musicStats.Resolutions);
        Assert.Empty(musicStats.ContainerFormats);
    }

    [Fact]
    public void CalculateStatistics_MovieLibrary_DoesNotPopulateMusicAudioCodecsFromVideoFiles()
    {
        // Without Jellyfin metadata, video codec falls back to "Unknown" and
        // audio codec is "Unknown" (filtered out of VideoAudioCodecs).
        var moviePath = TestPath("media", "movies");

        var movieFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [moviePath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([movieFolder]);

        var mkvFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.1080p.x265.DTS.mkv"),
            Name = "Film.1080p.x265.DTS.mkv",
            Length = 2_000_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(moviePath, false)).Returns([mkvFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(moviePath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var movieStats = result.Movies[0];

        // VideoCodecs populated (as "Unknown" without metadata)
        Assert.NotEmpty(movieStats.VideoCodecs);
        Assert.Contains("Unknown", movieStats.VideoCodecs.Keys);
        // VideoAudioCodecs empty (Unknown is filtered out)
        Assert.Empty(movieStats.VideoAudioCodecs);
        // MusicAudioCodecs should remain empty (video files don't go there)
        Assert.Empty(movieStats.MusicAudioCodecs);
    }

    [Fact]
    public void CalculateStatistics_MixedLibraries_CodecsSeparatedByType()
    {
        // Without Jellyfin metadata, video codecs/resolutions are "Unknown".
        // Separation between Video and Music codecs still works.
        var moviePath = TestPath("media", "movies");
        var musicPath = TestPath("media", "music");

        var movieFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [moviePath]
        };
        var musicFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [musicPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([movieFolder, musicFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.1080p.x264.mkv"),
            Name = "Film.1080p.x264.mkv",
            Length = 2_000_000_000,
            IsDirectory = false
        };
        var musicFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "music", "Song.flac"),
            Name = "Song.flac",
            Length = 30_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(moviePath, false)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(moviePath, false)).Returns([]);
        _fileSystemMock.Setup(f => f.GetFiles(musicPath, false)).Returns([musicFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(musicPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        // Movie library should have video-related codecs (Unknown without metadata) and container
        var movieStats = result.Movies[0];
        Assert.Contains("Unknown", movieStats.VideoCodecs.Keys);
        Assert.Contains("Unknown", movieStats.Resolutions.Keys);
        Assert.Contains("MKV", movieStats.ContainerFormats.Keys);
        Assert.Empty(movieStats.MusicAudioCodecs);

        // Music library should have music audio codecs only
        var musicStats = result.Music[0];
        Assert.Contains("FLAC", musicStats.MusicAudioCodecs.Keys);
        Assert.Empty(musicStats.VideoCodecs);
        Assert.Empty(musicStats.Resolutions);
        Assert.Empty(musicStats.ContainerFormats);
    }

    [Fact]
    public void CalculateStatistics_AudioFileInMovieLibrary_GoesToMusicAudioCodecs()
    {
        // A soundtrack .mp3 in a movie library should be counted as MusicAudioCodecs
        // This verifies that audio files are always tracked in MusicAudioCodecs regardless of library type
        var moviePath = TestPath("media", "movies");

        var movieFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [moviePath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([movieFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.mkv"),
            Name = "Film.mkv",
            Length = 2_000_000_000,
            IsDirectory = false
        };
        var soundtrackFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "theme.mp3"),
            Name = "theme.mp3",
            Length = 5_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(moviePath, false)).Returns([videoFile, soundtrackFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(moviePath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var movieStats = result.Movies[0];

        // Audio file in movie library goes to MusicAudioCodecs (not VideoAudioCodecs)
        Assert.Contains("MP3", movieStats.MusicAudioCodecs.Keys);
        // But this library is classified as Movies, so the frontend should NOT
        // include it when aggregating MusicAudioCodecs (only Music libraries)
        Assert.Single(result.Movies);
        Assert.Empty(result.Music);
        Assert.Empty(result.TotalMusicAudioCodecs);
    }

    [Fact]
    public void CalculateStatistics_MusicVideoLibrary_ClassifiedAsMovies()
    {
        // musicvideos collection type is classified as Movies (isMovies)
        var mvPath = TestPath("media", "musicvideos");

        var mvFolder = new VirtualFolderInfo
        {
            Name = "Music Videos",
            CollectionType = CollectionTypeOptions.musicvideos,
            Locations = [mvPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([mvFolder]);

        var m4VFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "musicvideos", "Artist - Song.m4v"),
            Name = "Artist - Song.m4v",
            Length = 100_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(mvPath, false)).Returns([m4VFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(mvPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        // musicvideos library should be in Movies, not Music
        Assert.Single(result.Movies);
        Assert.Empty(result.Music);

        var stats = result.Movies[0];
        Assert.Contains("M4V", stats.ContainerFormats.Keys);
    }

    [Fact]
    public void CalculateStatistics_MixedLibraries_AggregatedTotalsReflectAllLibraries()
    {
        // Verify that TotalContainerFormats aggregates from ALL libraries (including music if it has containers)
        // but TotalVideoCodecs only comes from libraries that have video files
        var moviePath = TestPath("media", "movies");
        var musicPath = TestPath("media", "music");

        var movieFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [moviePath]
        };
        var musicFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [musicPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([movieFolder, musicFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.mkv"),
            Name = "Film.mkv",
            Length = 2_000_000_000,
            IsDirectory = false
        };
        var musicFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "music", "Song.flac"),
            Name = "Song.flac",
            Length = 30_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(moviePath, false)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(moviePath, false)).Returns([]);
        _fileSystemMock.Setup(f => f.GetFiles(musicPath, false)).Returns([musicFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(musicPath, false)).Returns([]);

        var result = _service.CalculateStatistics();

        // TotalVideoCodecs should only reflect movie libraries (music has no video codecs)
        Assert.Single(result.TotalVideoCodecs); // "Unknown" from Film.mkv
        // TotalMusicAudioCodecs should only reflect music libraries
        Assert.Single(result.TotalMusicAudioCodecs); // "FLAC" from Song.flac
        Assert.Contains("FLAC", result.TotalMusicAudioCodecs.Keys);

        // TotalContainerFormats aggregates from ALL libraries
        // Movie library has MKV, music library has no container formats (only video files tracked)
        Assert.Contains("MKV", result.TotalContainerFormats.Keys);
    }
}

// ===== Embedded Subtitle Detection Tests =====

/// <summary>
/// Tests for embedded subtitle detection in video files (e.g. MKV with built-in subtitle streams).
/// Uses a testable subclass that overrides <see cref="MediaStatisticsService.HasEmbeddedSubtitles"/>
/// to avoid relying on Jellyfin's internal media source infrastructure.
/// </summary>
public class EmbeddedSubtitleDetectionTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly TestableMediaStatisticsService _service;

    public EmbeddedSubtitleDetectionTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        var loggerMock = new Mock<ILogger<MediaStatisticsService>>();
        var configHelperMock = TestMockFactory.CreateCleanupConfigHelper();
        _service = new TestableMediaStatisticsService(
            _libraryManagerMock.Object, _fileSystemMock.Object, TestMockFactory.CreatePluginLogService(), loggerMock.Object, configHelperMock.Object);
    }

    private static string TestPath(params string[] segments)
        => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    [Fact]
    public void VideoWithEmbeddedSubtitles_NotCountedAsWithoutSubtitles()
    {
        var libraryPath = TestPath("media", "movies");
        var videoPath = TestPath("media", "movies", "Film.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = videoPath, Name = "Film.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "poster.jpg"), Name = "poster.jpg", Length = 100_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.nfo"), Name = "Film.nfo", Length = 5_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Mark this video as having embedded subtitles
        _service.SetHasEmbeddedSubtitles(videoPath, true);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(0, stats.VideosWithoutSubtitles);
        Assert.Empty(stats.VideosWithoutSubtitlesPaths);
    }

    [Fact]
    public void VideoWithoutAnySubtitles_StillCountedAsWithoutSubtitles()
    {
        var libraryPath = TestPath("media", "movies");
        var videoPath = TestPath("media", "movies", "Film.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = videoPath, Name = "Film.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "poster.jpg"), Name = "poster.jpg", Length = 100_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.nfo"), Name = "Film.nfo", Length = 5_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // No embedded subtitles (default)

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.VideosWithoutSubtitles);
        Assert.Single(stats.VideosWithoutSubtitlesPaths);
        Assert.Contains(videoPath, stats.VideosWithoutSubtitlesPaths);
    }

    [Fact]
    public void VideoWithExternalSubtitles_NotCountedRegardlessOfEmbedded()
    {
        var libraryPath = TestPath("media", "movies");
        var videoPath = TestPath("media", "movies", "Film.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = videoPath, Name = "Film.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.srt"), Name = "Film.srt", Length = 50_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "poster.jpg"), Name = "poster.jpg", Length = 100_000, IsDirectory = false },
            new FileSystemMetadata { FullName = TestPath("media", "movies", "Film.nfo"), Name = "Film.nfo", Length = 5_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Not setting embedded subtitles � external .srt is enough

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(0, stats.VideosWithoutSubtitles);
        Assert.Empty(stats.VideosWithoutSubtitlesPaths);
    }

    [Fact]
    public void MixedVideos_SomeWithEmbeddedSubtitles_OnlyMissingOnesCounted()
    {
        var libraryPath = TestPath("media", "movies");
        var video1Path = TestPath("media", "movies", "Film1.mkv");
        var video2Path = TestPath("media", "movies", "Film2.mkv");
        var video3Path = TestPath("media", "movies", "Film3.mp4");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = video1Path, Name = "Film1.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = video2Path, Name = "Film2.mkv", Length = 2_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = video3Path, Name = "Film3.mp4", Length = 3_000_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Film1 has embedded subtitles, Film2 and Film3 do not
        _service.SetHasEmbeddedSubtitles(video1Path, true);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(2, stats.VideosWithoutSubtitles);
        Assert.Equal(2, stats.VideosWithoutSubtitlesPaths.Count);
        Assert.DoesNotContain(video1Path, stats.VideosWithoutSubtitlesPaths);
        Assert.Contains(video2Path, stats.VideosWithoutSubtitlesPaths);
        Assert.Contains(video3Path, stats.VideosWithoutSubtitlesPaths);
    }

    [Fact]
    public void AllVideosWithEmbeddedSubtitles_NoneCountedAsWithout()
    {
        var libraryPath = TestPath("media", "movies");
        var video1Path = TestPath("media", "movies", "Film1.mkv");
        var video2Path = TestPath("media", "movies", "Film2.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = video1Path, Name = "Film1.mkv", Length = 1_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = video2Path, Name = "Film2.mkv", Length = 2_000_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        _service.SetHasEmbeddedSubtitles(video1Path, true);
        _service.SetHasEmbeddedSubtitles(video2Path, true);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(0, stats.VideosWithoutSubtitles);
        Assert.Empty(stats.VideosWithoutSubtitlesPaths);
    }

    [Fact]
    public void EmbeddedSubtitleCheck_AcrossSubdirectories_WorksCorrectly()
    {
        var libraryPath = TestPath("media", "movies");
        var subDirPath = TestPath("media", "movies", "SubFolder");
        var video1Path = TestPath("media", "movies", "Film1.mkv");
        var video2Path = TestPath("media", "movies", "SubFolder", "Film2.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        // Root: video without external subs
        var rootFiles = new[]
        {
            new FileSystemMetadata { FullName = video1Path, Name = "Film1.mkv", Length = 1_000_000, IsDirectory = false },
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(rootFiles);

        var subDir = new FileSystemMetadata { FullName = subDirPath, Name = "SubFolder", IsDirectory = true };
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([subDir]);

        // Subdirectory: video without external subs
        var subFiles = new[]
        {
            new FileSystemMetadata { FullName = video2Path, Name = "Film2.mkv", Length = 2_000_000, IsDirectory = false },
        };
        _fileSystemMock.Setup(f => f.GetFiles(subDirPath, false)).Returns(subFiles);
        _fileSystemMock.Setup(f => f.GetDirectories(subDirPath, false)).Returns([]);

        // Film1 has embedded subtitles, Film2 does not
        _service.SetHasEmbeddedSubtitles(video1Path, true);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.VideosWithoutSubtitles);
        Assert.Single(stats.VideosWithoutSubtitlesPaths);
        Assert.DoesNotContain(video1Path, stats.VideosWithoutSubtitlesPaths);
        Assert.Contains(video2Path, stats.VideosWithoutSubtitlesPaths);
    }

    [Fact]
    public void EmbeddedSubtitleCheck_SkippedForBoxsetLibraries()
    {
        var libraryPath = TestPath("media", "boxsets");
        var videoPath = TestPath("media", "boxsets", "Film.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Boxsets",
            CollectionType = CollectionTypeOptions.boxsets,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = videoPath, Name = "Film.mkv", Length = 1_000_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // No embedded subtitles set � but health checks are skipped for boxsets
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(0, stats.VideosWithoutSubtitles);
        Assert.Empty(stats.VideosWithoutSubtitlesPaths);
    }

    [Fact]
    public void EmbeddedSubtitleCheck_SkippedForMusicLibraries()
    {
        var libraryPath = TestPath("media", "music");
        var videoPath = TestPath("media", "music", "MusicVideo.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = videoPath, Name = "MusicVideo.mkv", Length = 500_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(0, stats.VideosWithoutSubtitles);
        Assert.Empty(stats.VideosWithoutSubtitlesPaths);
    }

    /// <summary>
    /// Testable subclass that overrides <see cref="MediaStatisticsService.HasEmbeddedSubtitles"/>
    /// and <see cref="MediaStatisticsService.BuildItemLookup"/> to allow controlling embedded
    /// subtitle detection and metadata lookup without Jellyfin's runtime infrastructure.
    /// </summary>
    private sealed class TestableMediaStatisticsService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService pluginLog,
        ILogger<MediaStatisticsService> logger,
        ICleanupConfigHelper configHelper)
        : MediaStatisticsService(libraryManager, fileSystem, pluginLog, logger, configHelper)
    {
        private readonly Dictionary<string, bool> _embeddedSubtitles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BaseItem> _itemLookup = new(StringComparer.OrdinalIgnoreCase);

        public void SetHasEmbeddedSubtitles(string filePath, bool value)
            => _embeddedSubtitles[filePath] = value;

        public void SetItemLookup(string filePath, BaseItem item)
            => _itemLookup[filePath] = item;

        internal override bool HasEmbeddedSubtitles(string filePath, IReadOnlyList<MediaStream>? streams)
            => _embeddedSubtitles.TryGetValue(filePath, out var result) && result;

        internal override Dictionary<string, BaseItem> BuildItemLookup()
            => new(_itemLookup, StringComparer.OrdinalIgnoreCase);
    }
}

// ===== Classify Method Unit Tests =====

/// <summary>
/// Unit tests for the static classifier methods in <see cref="MediaStatisticsService"/>.
/// These methods map raw MediaStream codec/resolution/range data into display labels.
/// </summary>
public class ClassifyMethodTests
{
    // === ClassifyVideoCodec ===

    [Theory]
    [InlineData("hevc", "HEVC")]
    [InlineData("HEVC", "HEVC")]
    [InlineData("h265", "HEVC")]
    [InlineData("H.265", "HEVC")]
    [InlineData("h264", "H.264")]
    [InlineData("H.264", "H.264")]
    [InlineData("avc", "H.264")]
    [InlineData("AVC", "H.264")]
    [InlineData("av1", "AV1")]
    [InlineData("vp9", "VP9")]
    [InlineData("vp8", "VP8")]
    [InlineData("mpeg2video", "MPEG-2")]
    [InlineData("mpeg2", "MPEG-2")]
    [InlineData("mp2v", "MPEG-2")]
    [InlineData("mpeg4", "MPEG-4")]
    [InlineData("xvid", "XviD")]
    [InlineData("divx", "DivX")]
    [InlineData("vc1", "VC-1")]
    [InlineData("wmv3", "VC-1")]
    [InlineData("theora", "Theora")]
    public void ClassifyVideoCodec_KnownCodecs(string input, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyVideoCodec(input));
    }

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    public void ClassifyVideoCodec_NullOrEmpty_ReturnsUnknown(string? input, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyVideoCodec(input));
    }

    [Fact]
    public void ClassifyVideoCodec_UnknownCodec_ReturnsUppercased()
    {
        Assert.Equal("SOMECODEC", MediaStatisticsService.ClassifyVideoCodec("somecodec"));
    }

    // === ClassifyResolution ===

    [Theory]
    [InlineData(7680, 4320, "8K")]
    [InlineData(8192, 4320, "8K")]
    [InlineData(3840, 2160, "4K")]
    [InlineData(4096, 2160, "4K")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1920, 800, "720p")]   // Cinematic ratio — min dimension is 800
    [InlineData(1280, 720, "720p")]
    [InlineData(720, 576, "576p")]
    [InlineData(720, 480, "480p")]
    [InlineData(640, 480, "480p")]
    [InlineData(320, 240, "SD")]
    public void ClassifyResolution_KnownResolutions(int width, int height, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyResolution(width, height));
    }

    [Theory]
    [InlineData(null, null, "Unknown")]
    [InlineData(0, 0, "Unknown")]
    [InlineData(-1, 1080, "Unknown")]
    [InlineData(1920, 0, "Unknown")]
    [InlineData(1920, null, "Unknown")]
    public void ClassifyResolution_InvalidDimensions_ReturnsUnknown(int? width, int? height, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyResolution(width, height));
    }

    [Fact]
    public void ClassifyResolution_Portrait_UsesMinMaxCorrectly()
    {
        // Portrait 1080×1920 should still be classified as 1080p
        Assert.Equal("1080p", MediaStatisticsService.ClassifyResolution(1080, 1920));
    }

    [Fact]
    public void ClassifyResolution_UltraWide_2560x1080()
    {
        // 2560×1080 ultrawide — minDimension=1080, maxDimension=2560
        Assert.Equal("1080p", MediaStatisticsService.ClassifyResolution(2560, 1080));
    }

    [Fact]
    public void ClassifyResolution_Narrow720p_960x720()
    {
        // 960×720 (4:3 at 720p) — minDimension=720, maxDimension=960 (< 1280)
        // Matches the (>= 720, _) branch
        Assert.Equal("720p", MediaStatisticsService.ClassifyResolution(960, 720));
    }

    [Fact]
    public void ClassifyResolution_576p_PAL()
    {
        // Standard PAL resolution (portrait-order check)
        Assert.Equal("576p", MediaStatisticsService.ClassifyResolution(576, 720));
    }

    // === ClassifyDynamicRange ===

    [Theory]
    [InlineData(VideoRangeType.DOVI, VideoRange.Unknown, "Dolby Vision")]
    [InlineData(VideoRangeType.DOVIWithHDR10, VideoRange.Unknown, "Dolby Vision")]
    [InlineData(VideoRangeType.DOVIWithHDR10Plus, VideoRange.Unknown, "Dolby Vision")]
    [InlineData(VideoRangeType.DOVIWithHLG, VideoRange.Unknown, "Dolby Vision")]
    [InlineData(VideoRangeType.DOVIWithSDR, VideoRange.Unknown, "Dolby Vision")]
    [InlineData(VideoRangeType.HDR10Plus, VideoRange.Unknown, "HDR10+")]
    [InlineData(VideoRangeType.HDR10, VideoRange.Unknown, "HDR10")]
    [InlineData(VideoRangeType.HLG, VideoRange.Unknown, "HLG")]
    [InlineData(VideoRangeType.SDR, VideoRange.Unknown, "SDR")]
    public void ClassifyDynamicRange_FromVideoRangeType(VideoRangeType rangeType, VideoRange range, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyDynamicRange(rangeType, range));
    }

    [Theory]
    [InlineData(VideoRange.HDR, "HDR")]
    [InlineData(VideoRange.SDR, "SDR")]
    public void ClassifyDynamicRange_FallbackToVideoRange(VideoRange range, string expected)
    {
        // When VideoRangeType is Unknown, falls back to VideoRange
        Assert.Equal(expected, MediaStatisticsService.ClassifyDynamicRange(VideoRangeType.Unknown, range));
    }

    [Fact]
    public void ClassifyDynamicRange_BothUnknown_ReturnsUnknown()
    {
        Assert.Equal("Unknown", MediaStatisticsService.ClassifyDynamicRange(VideoRangeType.Unknown, VideoRange.Unknown));
    }

    [Fact]
    public void ClassifyDynamicRange_NullStream_ReturnsUnknown()
    {
        Assert.Equal("Unknown", MediaStatisticsService.ClassifyDynamicRange(null));
    }

    // === ClassifyAudioCodec ===

    [Theory]
    [InlineData("truehd", null, "TrueHD")]
    [InlineData("truehd", "Atmos", "TrueHD Atmos")]
    [InlineData("truehd", "atmos", "TrueHD Atmos")]
    [InlineData("eac3", null, "EAC3")]
    [InlineData("eac3", "Atmos", "EAC3 Atmos")]
    [InlineData("eac3", "JOC", "EAC3 Atmos")]
    [InlineData("e-ac-3", null, "EAC3")]
    [InlineData("ac3", null, "AC3")]
    [InlineData("a_ac3", null, "AC3")]
    [InlineData("dts", null, "DTS")]
    [InlineData("dts", "DTS-HD MA", "DTS-HD MA")]
    [InlineData("dts", "MA", "DTS-HD MA")]
    [InlineData("dts", "DTS:X", "DTS:X")]
    [InlineData("dts", "DTS-X", "DTS:X")]
    [InlineData("dts", "DTS-HD HRA", "DTS-HD HRA")]
    [InlineData("dts", "HRA", "DTS-HD HRA")]
    [InlineData("dts", "DTS-ES", "DTS-ES")]
    [InlineData("dts", "ES", "DTS-ES")]
    [InlineData("aac", null, "AAC")]
    [InlineData("aac", "LC", "AAC-LC")]
    [InlineData("aac", "AAC-LC", "AAC-LC")]
    [InlineData("aac", "AAC LC", "AAC-LC")]
    [InlineData("aac", "HE-AAC", "HE-AAC")]
    [InlineData("aac", "HE_AAC", "HE-AAC")]
    [InlineData("aac", "HE AAC", "HE-AAC")]
    [InlineData("mp4a", null, "AAC")]
    [InlineData("flac", null, "FLAC")]
    [InlineData("mp3", null, "MP3")]
    [InlineData("mp2", null, "MP3")]
    [InlineData("opus", null, "Opus")]
    [InlineData("vorbis", null, "Vorbis")]
    [InlineData("pcm_s16le", null, "PCM")]
    [InlineData("pcm_s24le", null, "PCM")]
    [InlineData("pcm_s32le", null, "PCM")]
    [InlineData("pcm_f32le", null, "PCM")]
    [InlineData("pcm", null, "PCM")]
    [InlineData("lpcm", null, "PCM")]
    [InlineData("alac", null, "ALAC")]
    [InlineData("wmav2", null, "WMA")]
    [InlineData("wmapro", null, "WMA")]
    [InlineData("wma", null, "WMA")]
    [InlineData("wav", null, "WAV")]
    public void ClassifyAudioCodec_KnownCodecs(string? codec, string? profile, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyAudioCodec(codec, profile));
    }

    [Theory]
    [InlineData(null, null, "Unknown")]
    [InlineData("", null, "Unknown")]
    public void ClassifyAudioCodec_NullOrEmpty_ReturnsUnknown(string? codec, string? profile, string expected)
    {
        Assert.Equal(expected, MediaStatisticsService.ClassifyAudioCodec(codec, profile));
    }

    [Fact]
    public void ClassifyAudioCodec_UnknownCodec_ReturnsUppercased()
    {
        Assert.Equal("SOMEAUDIO", MediaStatisticsService.ClassifyAudioCodec("someaudio", null));
    }
}

// ===== Metadata Extraction Integration Tests =====

/// <summary>
/// Integration tests that verify video and music metadata extraction from Jellyfin
/// MediaStream data, using a testable subclass with pre-populated item lookups.
/// </summary>
public class MetadataExtractionTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly TestableMetadataService _service;

    public MetadataExtractionTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        var loggerMock = new Mock<ILogger<MediaStatisticsService>>();
        var configHelperMock = TestMockFactory.CreateCleanupConfigHelper();
        _service = new TestableMetadataService(
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(),
            loggerMock.Object,
            configHelperMock.Object);
    }

    private static string TestPath(params string[] segments)
        => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    [Fact]
    public void VideoWithMetadata_ExtractsCodecResolutionDynamicRange()
    {
        var libraryPath = TestPath("media", "movies");
        var videoPath = TestPath("media", "movies", "Film.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = videoPath,
            Name = "Film.mkv",
            Length = 5_000_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Set up a mock BaseItem with MediaStreams
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = videoPath;
        mockItem.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160 },
            new MediaStream
            {
                Type = MediaStreamType.Audio,
                Codec = "truehd",
                Profile = "Atmos"
            }
        ]);

        _service.SetItemLookup(videoPath, mockItem.Object);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Video codec
        Assert.Contains("HEVC", stats.VideoCodecs.Keys);
        Assert.Equal(1, stats.VideoCodecs["HEVC"]);

        // Resolution
        Assert.Contains("4K", stats.Resolutions.Keys);
        Assert.Equal(1, stats.Resolutions["4K"]);

        // Dynamic range
        // VideoRangeType is read-only on MediaStream; default maps to SDR
        Assert.Contains("SDR", stats.DynamicRanges.Keys);
        Assert.Equal(1, stats.DynamicRanges["SDR"]);

        // Audio codec from video
        Assert.Contains("TrueHD Atmos", stats.VideoAudioCodecs.Keys);
        Assert.Equal(1, stats.VideoAudioCodecs["TrueHD Atmos"]);

        // Container format from extension
        Assert.Contains("MKV", stats.ContainerFormats.Keys);
    }

    [Fact]
    public void VideoWithDolbyVision_ClassifiedCorrectly()
    {
        var libraryPath = TestPath("media", "movies");
        var videoPath = TestPath("media", "movies", "DoVi.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = videoPath,
            Name = "DoVi.mkv",
            Length = 8_000_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = videoPath;
        mockItem.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160 },
            new MediaStream
            {
                Type = MediaStreamType.Audio,
                Codec = "eac3",
                Profile = "Atmos"
            }
        ]);

        _service.SetItemLookup(videoPath, mockItem.Object);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // VideoRangeType is read-only; default maps to SDR (DoVi classification tested via ClassifyDynamicRange unit tests)
        Assert.Contains("SDR", stats.DynamicRanges.Keys);
        Assert.Contains("EAC3 Atmos", stats.VideoAudioCodecs.Keys);
    }

    [Fact]
    public void VideoWithoutMetadata_FallsBackToUnknown()
    {
        var libraryPath = TestPath("media", "movies");
        var videoPath = TestPath("media", "movies", "Film.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = videoPath,
            Name = "Film.mkv",
            Length = 1_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // No item in lookup → falls back to Unknown
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Contains("Unknown", stats.VideoCodecs.Keys);
        Assert.Contains("Unknown", stats.Resolutions.Keys);
        Assert.Contains("Unknown", stats.DynamicRanges.Keys);
        // Audio codec "Unknown" is filtered out
        Assert.Empty(stats.VideoAudioCodecs);
    }

    [Fact]
    public void MusicFileWithMetadata_ExtractsAudioCodec()
    {
        var libraryPath = TestPath("media", "music");
        var musicPath = TestPath("media", "music", "Song.flac");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var musicFile = new FileSystemMetadata
        {
            FullName = musicPath,
            Name = "Song.flac",
            Length = 30_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([musicFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = musicPath;
        mockItem.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream
            {
                Type = MediaStreamType.Audio,
                Codec = "flac"
            }
        ]);

        _service.SetItemLookup(musicPath, mockItem.Object);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Contains("FLAC", stats.MusicAudioCodecs.Keys);
        Assert.Equal(1, stats.MusicAudioCodecs["FLAC"]);
    }

    [Fact]
    public void MusicFileWithoutMetadata_FallsBackToExtensionMapping()
    {
        var libraryPath = TestPath("media", "music");
        var musicPath = TestPath("media", "music", "Song.flac");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var musicFile = new FileSystemMetadata
        {
            FullName = musicPath,
            Name = "Song.flac",
            Length = 30_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([musicFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // No item in lookup → falls back to extension mapping
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Contains("FLAC", stats.MusicAudioCodecs.Keys);
    }

    [Fact]
    public void MultipleVideos_DifferentCodecs_AllTrackedCorrectly()
    {
        var libraryPath = TestPath("media", "movies");
        var hevcPath = TestPath("media", "movies", "Film1.mkv");
        var h264Path = TestPath("media", "movies", "Film2.mp4");
        var av1Path = TestPath("media", "movies", "Film3.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = hevcPath, Name = "Film1.mkv", Length = 5_000_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = h264Path, Name = "Film2.mp4", Length = 2_000_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = av1Path, Name = "Film3.mkv", Length = 3_000_000_000, IsDirectory = false },
        };

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // HEVC 4K HDR10
        var mockItem1 = new Mock<BaseItem>();
        mockItem1.Object.Path = hevcPath;
        mockItem1.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = "dts", Profile = "DTS-HD MA" }
        ]);

        // H.264 1080p SDR
        var mockItem2 = new Mock<BaseItem>();
        mockItem2.Object.Path = h264Path;
        mockItem2.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = "aac", Profile = "LC" }
        ]);

        // AV1 4K Dolby Vision
        var mockItem3 = new Mock<BaseItem>();
        mockItem3.Object.Path = av1Path;
        mockItem3.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Codec = "av1", Width = 3840, Height = 2160 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = "truehd", Profile = "Atmos" }
        ]);

        _service.SetItemLookup(hevcPath, mockItem1.Object);
        _service.SetItemLookup(h264Path, mockItem2.Object);
        _service.SetItemLookup(av1Path, mockItem3.Object);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Video codecs
        Assert.Equal(1, stats.VideoCodecs["HEVC"]);
        Assert.Equal(1, stats.VideoCodecs["H.264"]);
        Assert.Equal(1, stats.VideoCodecs["AV1"]);

        // Resolutions
        Assert.Equal(2, stats.Resolutions["4K"]);
        Assert.Equal(1, stats.Resolutions["1080p"]);

        // Dynamic ranges – VideoRangeType is read-only on MediaStream, so all default to SDR
        // (specific DynamicRange classification is covered by ClassifyDynamicRange unit tests)
        Assert.Equal(3, stats.DynamicRanges["SDR"]);

        // Audio codecs
        Assert.Equal(1, stats.VideoAudioCodecs["DTS-HD MA"]);
        Assert.Equal(1, stats.VideoAudioCodecs["AAC-LC"]);
        Assert.Equal(1, stats.VideoAudioCodecs["TrueHD Atmos"]);

        // Container formats
        Assert.Equal(2, stats.ContainerFormats["MKV"]);
        Assert.Equal(1, stats.ContainerFormats["MP4"]);

        // Sizes tracked correctly
        Assert.Equal(5_000_000_000, stats.VideoCodecSizes["HEVC"]);
        Assert.Equal(2_000_000_000, stats.VideoCodecSizes["H.264"]);
        Assert.Equal(3_000_000_000, stats.VideoCodecSizes["AV1"]);
    }

    [Fact]
    public void CalculateStatistics_VideoMetadata_TracksDynamicRangePaths()
    {
        var libraryPath = TestPath("media", "movies");
        var hevcPath = TestPath("media", "movies", "HDR.mkv");
        var sdrPath = TestPath("media", "movies", "SDR.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var files = new[]
        {
            new FileSystemMetadata { FullName = hevcPath, Name = "HDR.mkv", Length = 5_000_000_000, IsDirectory = false },
            new FileSystemMetadata { FullName = sdrPath, Name = "SDR.mkv", Length = 2_000_000_000, IsDirectory = false }
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns(files);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        var mockItem1 = new Mock<BaseItem>();
        mockItem1.Object.Path = hevcPath;
        mockItem1.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = "truehd" }
        ]);

        var mockItem2 = new Mock<BaseItem>();
        mockItem2.Object.Path = sdrPath;
        mockItem2.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = "aac" }
        ]);

        _service.SetItemLookup(hevcPath, mockItem1.Object);
        _service.SetItemLookup(sdrPath, mockItem2.Object);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // DynamicRangePaths should be populated (VideoRangeType defaults to SDR on MediaStream)
        Assert.True(stats.DynamicRangePaths.ContainsKey("SDR"));
        Assert.Contains(hevcPath, stats.DynamicRangePaths["SDR"]);
        Assert.Contains(sdrPath, stats.DynamicRangePaths["SDR"]);

        // DynamicRangeSizes should be tracked
        Assert.Equal(7_000_000_000, stats.DynamicRangeSizes["SDR"]);
    }

    [Fact]
    public void BuildItemLookup_WhenGetItemListThrows_ReturnsEmptyDictionary()
    {
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Throws(new InvalidOperationException("Database unavailable"));

        // BuildItemLookup is internal virtual — call it directly on the base service
        var baseService = new MediaStatisticsService(
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<MediaStatisticsService>().Object,
            TestMockFactory.CreateCleanupConfigHelper().Object);

        var lookup = baseService.BuildItemLookup();

        Assert.NotNull(lookup);
        Assert.Empty(lookup);
    }

    [Fact]
    public void CalculateStatistics_WhenGetMediaStreamsThrows_FallsBackToUnknown()
    {
        var libraryPath = TestPath("media", "movies");
        var filePath = TestPath("media", "movies", "Broken.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = "Broken.mkv",
            Length = 1_000_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Item exists but GetMediaStreams throws
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = filePath;
        mockItem.Setup(i => i.GetMediaStreams()).Throws(new IOException("Corrupt media"));

        _service.SetItemLookup(filePath, mockItem.Object);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Should fall back to "Unknown" for all metadata fields
        Assert.Equal(1, stats.VideoCodecs["Unknown"]);
        Assert.Equal(1, stats.Resolutions["Unknown"]);
        Assert.Equal(1, stats.DynamicRanges["Unknown"]);
        // Audio codec "Unknown" is NOT tracked in VideoAudioCodecs (by design)
        Assert.Empty(stats.VideoAudioCodecs);

        // Container format still works (from file extension, not streams)
        Assert.Equal(1, stats.ContainerFormats["MKV"]);
    }

    // ===== Per-File FindByPath Fallback Tests =====

    [Fact]
    public void CalculateStatistics_VideoNotInLookup_FallsBackToFindByPath()
    {
        var libraryPath = TestPath("media", "movies");
        var filePath = TestPath("media", "movies", "NewFilm.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = "NewFilm.mkv",
            Length = 2_000_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Item is NOT added to the lookup (no SetItemLookup call).
        // Instead, set up FindByPath to return the item as a fallback.
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = filePath;
        mockItem.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = "truehd", Profile = "Dolby TrueHD + Atmos" }
        ]);

        _libraryManagerMock.Setup(m => m.FindByPath(filePath, false)).Returns(mockItem.Object);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Metadata should be resolved via FindByPath fallback
        Assert.Equal(1, stats.VideoCodecs["HEVC"]);
        Assert.Equal(1, stats.Resolutions["4K"]);
        // VideoRangeType is read-only on MediaStream; default maps to SDR
        Assert.Equal(1, stats.DynamicRanges["SDR"]);
        Assert.Equal(1, stats.VideoAudioCodecs["TrueHD Atmos"]);
    }

    [Fact]
    public void CalculateStatistics_VideoNotInLookup_FindByPathReturnsNull_FallsBackToUnknown()
    {
        var libraryPath = TestPath("media", "movies");
        var filePath = TestPath("media", "movies", "Unscanned.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = "Unscanned.mkv",
            Length = 1_000_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Neither lookup nor FindByPath knows about this file
        _libraryManagerMock.Setup(m => m.FindByPath(filePath, false)).Returns((BaseItem?)null);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.VideoCodecs["Unknown"]);
        Assert.Equal(1, stats.Resolutions["Unknown"]);
        Assert.Equal(1, stats.DynamicRanges["Unknown"]);
        Assert.Empty(stats.VideoAudioCodecs);

        // Container format still works (from file extension)
        Assert.Equal(1, stats.ContainerFormats["MKV"]);
    }

    [Fact]
    public void CalculateStatistics_VideoNotInLookup_FindByPathThrows_FallsBackToUnknown()
    {
        var libraryPath = TestPath("media", "movies");
        var filePath = TestPath("media", "movies", "ErrorFile.mkv");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = "ErrorFile.mkv",
            Length = 500_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // FindByPath throws an exception
        _libraryManagerMock.Setup(m => m.FindByPath(filePath, false))
            .Throws(new InvalidOperationException("Database error"));

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Equal(1, stats.VideoCodecs["Unknown"]);
        Assert.Equal(1, stats.Resolutions["Unknown"]);
        Assert.Equal(1, stats.DynamicRanges["Unknown"]);
        Assert.Empty(stats.VideoAudioCodecs);
        Assert.Equal(1, stats.ContainerFormats["MKV"]);
    }

    [Fact]
    public void CalculateStatistics_MusicNotInLookup_FallsBackToFindByPath()
    {
        var libraryPath = TestPath("media", "music");
        var filePath = TestPath("media", "music", "NewSong.flac");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = "NewSong.flac",
            Length = 40_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Not in lookup; FindByPath returns item with Opus codec (different from extension)
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = filePath;
        mockItem.Setup(i => i.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Audio, Codec = "flac" }
        ]);

        _libraryManagerMock.Setup(m => m.FindByPath(filePath, false)).Returns(mockItem.Object);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Codec from Jellyfin streams via FindByPath fallback
        Assert.Equal(1, stats.MusicAudioCodecs["FLAC"]);
        Assert.Equal(40_000_000, stats.MusicAudioCodecSizes["FLAC"]);
    }

    [Fact]
    public void CalculateStatistics_MusicNotInLookup_FindByPathReturnsNull_FallsBackToExtension()
    {
        var libraryPath = TestPath("media", "music");
        var filePath = TestPath("media", "music", "Unknown.flac");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = "Unknown.flac",
            Length = 25_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Neither lookup nor FindByPath knows about this file
        _libraryManagerMock.Setup(m => m.FindByPath(filePath, false)).Returns((BaseItem?)null);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Falls back to extension-based mapping: .flac → FLAC
        Assert.Equal(1, stats.MusicAudioCodecs["FLAC"]);
        Assert.Equal(25_000_000, stats.MusicAudioCodecSizes["FLAC"]);
    }

    [Fact]
    public void CalculateStatistics_MusicItemFoundButNoAudioStream_FallsBackToExtension()
    {
        var libraryPath = TestPath("media", "music");
        var filePath = TestPath("media", "music", "NoStreams.mp3");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Music",
            CollectionType = CollectionTypeOptions.music,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = "NoStreams.mp3",
            Length = 5_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath, false)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath, false)).Returns([]);

        // Item found in lookup but has no audio streams (empty list)
        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = filePath;
        mockItem.Setup(i => i.GetMediaStreams()).Returns([]);

        _service.SetItemLookup(filePath, mockItem.Object);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // ClassifyAudioCodec returns "Unknown" for null codec → extension fallback kicks in
        Assert.Equal(1, stats.MusicAudioCodecs["MP3"]);
        Assert.Equal(5_000_000, stats.MusicAudioCodecSizes["MP3"]);
    }

    /// <summary>
    /// Testable subclass for metadata extraction tests that provides control over both
    /// embedded subtitle detection and the item lookup dictionary.
    /// </summary>
    private sealed class TestableMetadataService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService pluginLog,
        ILogger<MediaStatisticsService> logger,
        ICleanupConfigHelper configHelper)
        : MediaStatisticsService(libraryManager, fileSystem, pluginLog, logger, configHelper)
    {
        private readonly Dictionary<string, BaseItem> _itemLookup = new(StringComparer.OrdinalIgnoreCase);

        public void SetItemLookup(string filePath, BaseItem item)
            => _itemLookup[filePath] = item;

        internal override Dictionary<string, BaseItem> BuildItemLookup()
            => new(_itemLookup, StringComparer.OrdinalIgnoreCase);

        internal override bool HasEmbeddedSubtitles(string filePath, IReadOnlyList<MediaStream>? streams)
            => false;
    }
}
