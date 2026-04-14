using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
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
        _service = new MediaStatisticsService(_libraryManagerMock.Object, _fileSystemMock.Object, loggerMock.Object);
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

    // ===== Video Codec Parsing Tests =====

    [Theory]
    [InlineData("Movie.x265.mkv", "HEVC")]
    [InlineData("Movie.HEVC.mkv", "HEVC")]
    [InlineData("Movie.H.265.mkv", "HEVC")]
    [InlineData("Movie.h265.mkv", "HEVC")]
    [InlineData("Movie.x264.mkv", "H.264")]
    [InlineData("Movie.H.264.mkv", "H.264")]
    [InlineData("Movie.AVC.mkv", "H.264")]
    [InlineData("Movie.AV1.mkv", "AV1")]
    [InlineData("Movie.VP9.webm", "VP9")]
    [InlineData("Movie.XviD.avi", "XviD")]
    [InlineData("Movie.DivX.avi", "DivX")]
    [InlineData("Movie.MPEG2.mpg", "MPEG")]
    [InlineData("Movie.mkv", "Unknown")]
    public void ParseVideoCodec_DetectsCorrectCodec(string fileName, string expected)
    {
        var result = MediaStatisticsService.ParseVideoCodec(fileName);
        Assert.Equal(expected, result);
    }

    // ===== Resolution Parsing Tests =====

    [Theory]
    [InlineData("Movie.2160p.mkv", "4K")]
    [InlineData("Movie.4K.mkv", "4K")]
    [InlineData("Movie.UHD.mkv", "4K")]
    [InlineData("Movie.1080p.mkv", "1080p")]
    [InlineData("Movie.1080i.mkv", "1080p")]
    [InlineData("Movie.720p.mkv", "720p")]
    [InlineData("Movie.480p.mkv", "480p")]
    [InlineData("Movie.SD.mkv", "480p")]
    [InlineData("Movie.576p.mkv", "576p")]
    [InlineData("Movie.mkv", "Unknown")]
    public void ParseResolution_DetectsCorrectResolution(string fileName, string expected)
    {
        var result = MediaStatisticsService.ParseResolution(fileName);
        Assert.Equal(expected, result);
    }

    // ===== Audio Codec Parsing Tests =====

    [Theory]
    [InlineData("Song.FLAC.mp3", ".mp3", "FLAC")]
    [InlineData("Song.AAC.m4a", ".m4a", "AAC")]
    [InlineData("Song.Opus.ogg", ".ogg", "Opus")]
    [InlineData("Song.DTS.mkv", ".mkv", "DTS")]
    [InlineData("Song.AC3.mkv", ".mkv", "AC3")]
    [InlineData("Song.EAC3.mkv", ".mkv", "EAC3")]
    [InlineData("Song.TrueHD.mkv", ".mkv", "TrueHD")]
    [InlineData("Song.Vorbis.ogg", ".ogg", "Vorbis")]
    [InlineData("Song.ALAC.m4a", ".m4a", "ALAC")]
    [InlineData("Song.PCM.wav", ".wav", "PCM")]
    public void ParseAudioCodec_FromFilenameTag_DetectsCorrectCodec(string fileName, string ext, string expected)
    {
        var result = MediaStatisticsService.ParseAudioCodec(fileName, ext);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Song.flac", ".flac", "FLAC")]
    [InlineData("Track.mp3", ".mp3", "MP3")]
    [InlineData("Music.ogg", ".ogg", "Vorbis")]
    [InlineData("Sound.opus", ".opus", "Opus")]
    [InlineData("Audio.wav", ".wav", "WAV")]
    [InlineData("Music.wma", ".wma", "WMA")]
    [InlineData("Song.m4a", ".m4a", "AAC")]
    [InlineData("Music.aac", ".aac", "AAC")]
    [InlineData("Lossless.ape", ".ape", "APE")]
    [InlineData("Music.wv", ".wv", "WavPack")]
    [InlineData("HiRes.dsf", ".dsf", "DSD")]
    [InlineData("HiRes.dff", ".dff", "DSD")]
    public void ParseAudioCodec_FromExtension_DetectsCorrectCodec(string fileName, string ext, string expected)
    {
        var result = MediaStatisticsService.ParseAudioCodec(fileName, ext);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseAudioCodec_UnknownExtension_ReturnsUnknown()
    {
        var result = MediaStatisticsService.ParseAudioCodec("file.xyz", ".xyz");
        Assert.Equal("Unknown", result);
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

        Assert.Equal(2, stats.VideoAudioCodecs["DTS"]);
        Assert.Equal(1, stats.VideoAudioCodecs["AC3"]);
        Assert.Equal(7_000_000_000, stats.VideoAudioCodecSizes["DTS"]);
        Assert.Equal(1_500_000_000, stats.VideoAudioCodecSizes["AC3"]);
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

        Assert.Equal(1, stats.VideoCodecs["HEVC"]);
        Assert.Equal(1, stats.VideoCodecs["H.264"]);
        Assert.Equal(1, stats.Resolutions["1080p"]);
        Assert.Equal(1, stats.Resolutions["720p"]);
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

        // Video files should populate VideoCodecs and VideoAudioCodecs, not MusicAudioCodecs
        Assert.NotEmpty(movieStats.VideoCodecs);
        Assert.Contains("HEVC", movieStats.VideoCodecs.Keys);
        Assert.NotEmpty(movieStats.VideoAudioCodecs);
        Assert.Contains("DTS", movieStats.VideoAudioCodecs.Keys);
        Assert.Empty(movieStats.MusicAudioCodecs);
    }

    [Fact]
    public void CalculateStatistics_MixedLibraries_CodecsSeparatedByType()
    {
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

        // Movie library should have video-related codecs only
        var movieStats = result.Movies[0];
        Assert.Contains("H.264", movieStats.VideoCodecs.Keys);
        Assert.Contains("1080p", movieStats.Resolutions.Keys);
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

        var m4vFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "musicvideos", "Artist - Song.m4v"),
            Name = "Artist - Song.m4v",
            Length = 100_000_000,
            IsDirectory = false
        };

        _fileSystemMock.Setup(f => f.GetFiles(mvPath, false)).Returns([m4vFile]);
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
    private readonly Mock<ILogger<MediaStatisticsService>> _loggerMock;
    private readonly TestableMediaStatisticsService _service;

    public EmbeddedSubtitleDetectionTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _loggerMock = new Mock<ILogger<MediaStatisticsService>>();
        _service = new TestableMediaStatisticsService(
            _libraryManagerMock.Object, _fileSystemMock.Object, _loggerMock.Object);
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
    /// to allow controlling embedded subtitle detection without Jellyfin's runtime infrastructure.
    /// </summary>
    private sealed class TestableMediaStatisticsService : MediaStatisticsService
    {
        private readonly Dictionary<string, bool> _embeddedSubtitles = new(StringComparer.OrdinalIgnoreCase);

        public TestableMediaStatisticsService(
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            ILogger<MediaStatisticsService> logger)
            : base(libraryManager, fileSystem, logger)
        {
        }

        public void SetHasEmbeddedSubtitles(string filePath, bool value)
            => _embeddedSubtitles[filePath] = value;

        internal override bool HasEmbeddedSubtitles(string filePath)
            => _embeddedSubtitles.TryGetValue(filePath, out var result) && result;
    }
}
