using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
/// Central generator for test data objects used across the test suite.
/// Provides factory methods for VirtualFolderInfo, FileSystemMetadata,
/// LibraryStatistics, MediaStatisticsResult, and other commonly needed test entities.
/// </summary>
public static class TestDataGenerator
{
    // ===== Paths =====

    /// <summary>
    /// Builds an OS-appropriate absolute path from segments.
    /// E.g. TestPath("media", "movies") => "/media/movies" or "\media\movies".
    /// </summary>
    public static string TestPath(params string[] segments)
        => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    // ===== VirtualFolderInfo =====

    /// <summary>Creates a <see cref="VirtualFolderInfo"/> for a movie library.</summary>
    public static VirtualFolderInfo CreateMovieLibrary(string name = "Movies", params string[] locations)
        => CreateLibrary(name, CollectionTypeOptions.movies, locations.Length > 0 ? locations : [TestPath("media", "movies")]);

    /// <summary>Creates a <see cref="VirtualFolderInfo"/> for a TV show library.</summary>
    public static VirtualFolderInfo CreateTvShowLibrary(string name = "TV Shows", params string[] locations)
        => CreateLibrary(name, CollectionTypeOptions.tvshows, locations.Length > 0 ? locations : [TestPath("media", "tv")]);

    /// <summary>Creates a <see cref="VirtualFolderInfo"/> for a music library.</summary>
    public static VirtualFolderInfo CreateMusicLibrary(string name = "Music", params string[] locations)
        => CreateLibrary(name, CollectionTypeOptions.music, locations.Length > 0 ? locations : [TestPath("media", "music")]);

    /// <summary>Creates a <see cref="VirtualFolderInfo"/> with custom settings.</summary>
    public static VirtualFolderInfo CreateLibrary(string name, CollectionTypeOptions? collectionType, params string[] locations)
        => new()
        {
            Name = name,
            CollectionType = collectionType,
            Locations = locations,
        };

    // ===== FileSystemMetadata =====

    /// <summary>Creates a <see cref="FileSystemMetadata"/> representing a file.</summary>
    public static FileSystemMetadata CreateFile(string fullName, long length = 0)
        => new()
        {
            FullName = fullName,
            Name = Path.GetFileName(fullName),
            Length = length,
            IsDirectory = false,
        };

    /// <summary>Creates a <see cref="FileSystemMetadata"/> representing a directory.</summary>
    public static FileSystemMetadata CreateDirectory(string fullName)
        => new()
        {
            FullName = fullName,
            Name = Path.GetFileName(fullName),
            IsDirectory = true,
        };

    /// <summary>Creates a video file metadata with common video extension (.mkv).</summary>
    public static FileSystemMetadata CreateVideoFile(string directory, string fileName = "movie.mkv", long length = 1_500_000_000)
        => CreateFile(Path.Combine(directory, Path.GetFileName(fileName)), length);

    /// <summary>Creates a subtitle file metadata.</summary>
    public static FileSystemMetadata CreateSubtitleFile(string directory, string fileName = "movie.en.srt", long length = 50_000)
        => CreateFile(Path.Combine(directory, Path.GetFileName(fileName)), length);

    /// <summary>Creates an image file metadata.</summary>
    public static FileSystemMetadata CreateImageFile(string directory, string fileName = "poster.jpg", long length = 200_000)
        => CreateFile(Path.Combine(directory, Path.GetFileName(fileName)), length);

    /// <summary>Creates an NFO file metadata.</summary>
    public static FileSystemMetadata CreateNfoFile(string directory, string fileName = "movie.nfo", long length = 10_000)
        => CreateFile(Path.Combine(directory, Path.GetFileName(fileName)), length);

    /// <summary>Creates an audio file metadata.</summary>
    public static FileSystemMetadata CreateAudioFile(string directory, string fileName = "track.flac", long length = 30_000_000)
        => CreateFile(Path.Combine(directory, Path.GetFileName(fileName)), length);

    // ===== LibraryStatistics =====

    /// <summary>
    /// Creates a fully populated <see cref="LibraryStatistics"/> with realistic sample data.
    /// Useful for serialization roundtrip tests and export tests.
    /// </summary>
    public static LibraryStatistics CreateSampleLibraryStatistics(string name = "Movies", string collectionType = "movies")
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

        // Codec counts
        lib.ContainerFormats["MKV"] = 200;
        lib.ContainerFormats["MP4"] = 100;
        lib.Resolutions["4K"] = 50;
        lib.Resolutions["1080p"] = 200;
        lib.Resolutions["720p"] = 50;
        lib.VideoCodecs["HEVC"] = 150;
        lib.VideoCodecs["H.264"] = 150;
        lib.VideoAudioCodecs["DTS"] = 100;
        lib.VideoAudioCodecs["AAC"] = 200;
        lib.MusicAudioCodecs["FLAC"] = 10;

        // Codec sizes
        lib.ContainerSizes["MKV"] = 700_000_000_000L;
        lib.ContainerSizes["MP4"] = 300_000_000_000L;
        lib.ResolutionSizes["4K"] = 400_000_000_000L;
        lib.ResolutionSizes["1080p"] = 500_000_000_000L;
        lib.ResolutionSizes["720p"] = 100_000_000_000L;
        lib.VideoCodecSizes["HEVC"] = 600_000_000_000L;
        lib.VideoCodecSizes["H.264"] = 400_000_000_000L;
        lib.VideoAudioCodecSizes["DTS"] = 400_000_000_000L;
        lib.VideoAudioCodecSizes["AAC"] = 600_000_000_000L;
        lib.MusicAudioCodecSizes["FLAC"] = 5_000_000_000L;

        // Health check paths
        lib.VideosWithoutSubtitlesPaths.Add("/media/movies/NoSub1.mkv");
        lib.VideosWithoutSubtitlesPaths.Add("/media/movies/NoSub2.mkv");
        lib.VideosWithoutImagesPaths.Add("/media/movies/NoImg1.mkv");
        lib.VideosWithoutNfoPaths.Add("/media/movies/NoNfo1.mkv");
        lib.OrphanedMetadataDirectoriesPaths.Add("/media/movies/OrphanedDir");

        return lib;
    }

    // ===== MediaStatisticsResult =====

    /// <summary>
    /// Creates a complete <see cref="MediaStatisticsResult"/> with movie, TV, and music libraries.
    /// Used by export tests and serialization roundtrip tests.
    /// </summary>
    public static MediaStatisticsResult CreateSampleStatisticsResult()
    {
        var result = new MediaStatisticsResult
        {
            ScanTimestamp = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
        };

        var movieLib = CreateSampleLibraryStatistics("Movies", "movies");
        // Add specific paths for export tests
        movieLib.VideosWithoutSubtitlesPaths.Clear();
        movieLib.VideosWithoutSubtitlesPaths.Add("/media/movies/Film1/Film1.mkv");
        movieLib.VideosWithoutSubtitlesPaths.Add("/media/movies/Film2/Film2.mp4");
        movieLib.VideosWithoutImagesPaths.Clear();
        movieLib.VideosWithoutImagesPaths.Add("/media/movies/Film3/Film3.mkv");
        movieLib.VideosWithoutNfoPaths.Clear();
        movieLib.VideosWithoutNfoPaths.Add("/media/movies/Film4/Film4.mkv");
        movieLib.OrphanedMetadataDirectoriesPaths.Clear();
        movieLib.OrphanedMetadataDirectoriesPaths.Add("/media/movies/OldMovie/.metadata");
        result.Libraries.Add(movieLib);
        result.Movies.Add(movieLib);

        return result;
    }

    /// <summary>
    /// Creates a <see cref="MediaStatisticsResult"/> with movie, TV show, and music libraries.
    /// Used by serialization roundtrip tests.
    /// </summary>
    public static MediaStatisticsResult CreateFullSampleStatisticsResult()
    {
        var result = new MediaStatisticsResult
        {
            ScanTimestamp = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
        };

        var movieLib = CreateSampleLibraryStatistics("Movies", "movies");
        result.Libraries.Add(movieLib);
        result.Movies.Add(movieLib);

        var tvLib = CreateSampleLibraryStatistics("TV Shows", "tvshows");
        tvLib.VideoSize = 800_000_000_000L;
        tvLib.VideoFileCount = 200;
        result.Libraries.Add(tvLib);
        result.TvShows.Add(tvLib);

        var musicLib = CreateSampleLibraryStatistics("Music", "music");
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

    // ===== Temporary Directory Helper =====

    /// <summary>
    /// Creates a uniquely named temporary directory and returns its path.
    /// Caller is responsible for deleting it (use with try/finally or IDisposable).
    /// </summary>
    public static string CreateTempDirectory(string prefix = "jh-test")
    {
        var safePrefix = Path.GetFileName(prefix);
        if (string.IsNullOrWhiteSpace(safePrefix))
        {
            safePrefix = "jh-test";
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"{safePrefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }
}