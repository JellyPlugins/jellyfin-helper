using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

public class MediaStatisticsServiceTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILogger<MediaStatisticsService>> _loggerMock;
    private readonly MediaStatisticsService _service;

    public MediaStatisticsServiceTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _loggerMock = new Mock<ILogger<MediaStatisticsService>>();
        _service = new MediaStatisticsService(_libraryManagerMock.Object, _fileSystemMock.Object, _loggerMock.Object);
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
}