using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>
///     Central generator for test data objects used across the test suite. Provides factory methods for VirtualFolderInfo, FileSystemMetadata, LibraryStatistics, MediaStatisticsResult, and other commonly needed test entities.
/// </summary>
public static class TestDataGenerator
{
    /// <summary>
    /// Builds an OS-appropriate absolute path from segments.
    /// E.g. TestPath("media", "movies") => "/media/movies" or "\media\movies".
    /// </summary>
    /// <param name="segments">The path segments to combine.</param>
    /// <returns>An OS-appropriate absolute path string.</returns>
    public static string TestPath(params string[] segments)
        => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    /// <summary>Creates a <see cref="VirtualFolderInfo"/> for a movie library.</summary>
    /// <param name="name">The name of the library (default is "Movies").</param>
    /// <param name="locations">The folder locations for the library.</param>
    /// <returns>A new <see cref="VirtualFolderInfo"/> instance configured for movies.</returns>
    public static VirtualFolderInfo CreateMovieLibrary(string name = "Movies", params string[] locations)
        => CreateLibrary(name, CollectionTypeOptions.movies, locations.Length > 0 ? locations : [TestPath("media", "movies")]);

    /// <summary>Creates a <see cref="VirtualFolderInfo"/> for a TV show library.</summary>
    /// <param name="name">The name of the library (default is "TV Shows").</param>
    /// <param name="locations">The folder locations for the library.</param>
    /// <returns>A new <see cref="VirtualFolderInfo"/> instance configured for TV shows.</returns>
    public static VirtualFolderInfo CreateTvShowLibrary(string name = "TV Shows", params string[] locations)
        => CreateLibrary(name, CollectionTypeOptions.tvshows, locations.Length > 0 ? locations : [TestPath("media", "tv")]);

    /// <summary>Creates a <see cref="VirtualFolderInfo"/> for a music library.</summary>
    /// <param name="name">The name of the library (default is "Music").</param>
    /// <param name="locations">The folder locations for the library.</param>
    /// <returns>A new <see cref="VirtualFolderInfo"/> instance configured for music.</returns>
    public static VirtualFolderInfo CreateMusicLibrary(string name = "Music", params string[] locations)
        => CreateLibrary(name, CollectionTypeOptions.music, locations.Length > 0 ? locations : [TestPath("media", "music")]);

    /// <summary>Creates a <see cref="VirtualFolderInfo"/> with custom settings.</summary>
    /// <param name="name">The name of the library.</param>
    /// <param name="collectionType">The collection type options, or null.</param>
    /// <param name="locations">The folder locations for the library.</param>
    /// <returns>A new custom <see cref="VirtualFolderInfo"/> instance.</returns>
    public static VirtualFolderInfo CreateLibrary(string name, CollectionTypeOptions? collectionType, params string[] locations)
        => new()
        {
            Name = name,
            CollectionType = collectionType,
            Locations = locations,
        };

    /// <summary>Creates a <see cref="FileSystemMetadata"/> representing a file.</summary>
    /// <param name="fullName">The full path of the file.</param>
    /// <param name="length">The length/size of the file in bytes (default is 0).</param>
    /// <returns>A new <see cref="FileSystemMetadata"/> instance representing a file.</returns>
    public static FileSystemMetadata CreateFile(string fullName, long length = 0)
        => new()
        {
            FullName = fullName,
            Name = Path.GetFileName(fullName),
            Length = length,
            IsDirectory = false,
        };

    /// <summary>Creates a <see cref="FileSystemMetadata"/> representing a directory.</summary>
    /// <param name="fullName">The full path of the directory.</param>
    /// <returns>A new <see cref="FileSystemMetadata"/> instance representing a directory.</returns>
    public static FileSystemMetadata CreateDirectory(string fullName)
        => new()
        {
            FullName = fullName,
            Name = Path.GetFileName(fullName),
            IsDirectory = true,
        };

    /// <summary>Safely combines a directory with a file name, stripping any path from fileName.</summary>
    /// <param name="directory">The target directory path.</param>
    /// <param name="fileName">The file name to append.</param>
    /// <returns>The combined file path string.</returns>
    private static string CombineWithFileName(string directory, string fileName)
        => Path.Join(directory, Path.GetFileName(fileName));

    /// <summary>Creates a video file metadata with common video extension (.mkv).</summary>
    /// <param name="directory">The directory containing the file.</param>
    /// <param name="fileName">The name of the video file (default is "movie.mkv").</param>
    /// <param name="length">The file size in bytes (default is 1.5 GB).</param>
    /// <returns>A new <see cref="FileSystemMetadata"/> instance for a video file.</returns>
    public static FileSystemMetadata CreateVideoFile(string directory, string fileName = "movie.mkv", long length = 1_500_000_000)
        => CreateFile(CombineWithFileName(directory, fileName), length);

    /// <summary>Creates a subtitle file metadata.</summary>
    /// <param name="directory">The directory containing the file.</param>
    /// <param name="fileName">The name of the subtitle file (default is "movie.en.srt").</param>
    /// <param name="length">The file size in bytes (default is 50 KB).</param>
    /// <returns>A new <see cref="FileSystemMetadata"/> instance for a subtitle file.</returns>
    public static FileSystemMetadata CreateSubtitleFile(string directory, string fileName = "movie.en.srt", long length = 50_000)
        => CreateFile(CombineWithFileName(directory, fileName), length);

    /// <summary>Creates an image file metadata.</summary>
    /// <param name="directory">The directory containing the file.</param>
    /// <param name="fileName">The name of the image file (default is "poster.jpg").</param>
    /// <param name="length">The file size in bytes (default is 200 KB).</param>
    /// <returns>A new <see cref="FileSystemMetadata"/> instance for an image file.</returns>
    public static FileSystemMetadata CreateImageFile(string directory, string fileName = "poster.jpg", long length = 200_000)
        => CreateFile(CombineWithFileName(directory, fileName), length);

    /// <summary>Creates an NFO file metadata.</summary>
    /// <param name="directory">The directory containing the file.</param>
    /// <param name="fileName">The name of the NFO file (default is "movie.nfo").</param>
    /// <param name="length">The file size in bytes (default is 10 KB).</param>
    /// <returns>A new <see cref="FileSystemMetadata"/> instance for an NFO file.</returns>
    public static FileSystemMetadata CreateNfoFile(string directory, string fileName = "movie.nfo", long length = 10_000)
        => CreateFile(CombineWithFileName(directory, fileName), length);

    /// <summary>Creates an audio file metadata.</summary>
    /// <param name="directory">The directory containing the file.</param>
    /// <param name="fileName">The name of the audio file (default is "track.flac").</param>
    /// <param name="length">The file size in bytes (default is 30 MB).</param>
    /// <returns>A new <see cref="FileSystemMetadata"/> instance for an audio file.</returns>
    public static FileSystemMetadata CreateAudioFile(string directory, string fileName = "track.flac", long length = 30_000_000)
        => CreateFile(CombineWithFileName(directory, fileName), length);

    /// <summary>
    /// Creates a fully populated <see cref="LibraryStatistics"/> with realistic sample data.
    /// Useful for serialization roundtrip tests and export tests.
    /// </summary>
    /// <param name="name">The name of the library (default is "Movies").</param>
    /// <param name="collectionType">The collection type string (default is "movies").</param>
    /// <returns>A fully populated <see cref="LibraryStatistics"/> instance.</returns>
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
        lib.VideoBitrateTiers["8–16 Mbps"] = 200;
        lib.VideoBitrateTiers["16–32 Mbps"] = 100;

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
        lib.VideoBitrateTierSizes["8–16 Mbps"] = 500_000_000_000L;
        lib.VideoBitrateTierSizes["16–32 Mbps"] = 500_000_000_000L;
        lib.DynamicRanges["SDR"] = 250;
        lib.DynamicRanges["HDR10"] = 50;
        lib.DynamicRangeSizes["SDR"] = 800_000_000_000L;
        lib.DynamicRangeSizes["HDR10"] = 200_000_000_000L;
        lib.AudioLanguages["English"] = 250;
        lib.AudioLanguages["German"] = 80;
        lib.AudioLanguageSizes["English"] = 800_000_000_000L;
        lib.AudioLanguageSizes["German"] = 200_000_000_000L;
        lib.SubtitleLanguages["English"] = 200;
        lib.SubtitleLanguages["German"] = 60;
        lib.SubtitleLanguageSizes["English"] = 700_000_000_000L;
        lib.SubtitleLanguageSizes["German"] = 150_000_000_000L;
        lib.WatchedTiers["Watched"] = 180;
        lib.WatchedTiers["Never watched"] = 120;
        lib.WatchedTierSizes["Watched"] = 600_000_000_000L;
        lib.WatchedTierSizes["Never watched"] = 400_000_000_000L;

        // Per file maps so serialization roundtrips cover them, not just empty dicts.
        var samplePath = TestPath("media", "movies", "Sample.mkv");
        lib.FileSizes[samplePath] = 8_000_000_000L;
        lib.VideoBitrates[samplePath] = 12.5;
        lib.ResolutionDimensions[samplePath] = "1920x800";
        lib.VideoCodecPaths["H.264"] = new Collection<string> { samplePath };
        lib.VideoAudioCodecPaths["AAC"] = new Collection<string> { samplePath };
        lib.ContainerFormatPaths["MKV"] = new Collection<string> { samplePath };
        lib.ResolutionPaths["1080p"] = new Collection<string> { samplePath };
        lib.DynamicRangePaths["SDR"] = new Collection<string> { samplePath };
        lib.VideoBitrateTierPaths["8–16 Mbps"] = new Collection<string> { samplePath };
        lib.AudioLanguagePaths["English"] = new Collection<string> { samplePath };
        lib.SubtitleLanguagePaths["English"] = new Collection<string> { samplePath };
        lib.WatchedTierPaths["Watched"] = new Collection<string> { samplePath };
        lib.AudioTrackLabels[samplePath] = new Collection<string> { "English", "German (Forced)" };
        lib.SubtitleTrackLabels[samplePath] = new Collection<string> { "English (SRT)" };
        lib.WatchedByUserPaths["Alice"] = new Collection<string> { samplePath };
        lib.WatchedByUserSizes["Alice"] = 8_000_000_000L;
        lib.WatchedDetails[samplePath] = new Collection<WatchedUserDetail> { new() { Username = "Alice", PlayCount = 3 } };

        // Health check paths
        lib.VideosWithoutSubtitlesPaths.Add(TestPath("media", "movies", "NoSub1.mkv"));
        lib.VideosWithoutSubtitlesPaths.Add(TestPath("media", "movies", "NoSub2.mkv"));
        lib.VideosWithoutImagesPaths.Add(TestPath("media", "movies", "NoImg1.mkv"));
        lib.VideosWithoutNfoPaths.Add(TestPath("media", "movies", "NoNfo1.mkv"));
        lib.OrphanedMetadataDirectoriesPaths.Add(TestPath("media", "movies", "OrphanedDir"));

        return lib;
    }

    /// <summary>
    /// Creates a <see cref="MediaStatisticsResult"/> with a single movie library.
    /// Used by export tests and serialization roundtrip tests.
    /// </summary>
    /// <returns>A sample <see cref="MediaStatisticsResult"/> instance containing a movie library.</returns>
    public static MediaStatisticsResult CreateSampleStatisticsResult()
    {
        var result = new MediaStatisticsResult
        {
            ScanTimestamp = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
        };

        var movieLib = CreateSampleLibraryStatistics("Movies", "movies");
        // Add specific paths for export tests
        movieLib.VideosWithoutSubtitlesPaths.Clear();
        movieLib.VideosWithoutSubtitlesPaths.Add(TestPath("media", "movies", "Film1", "Film1.mkv"));
        movieLib.VideosWithoutSubtitlesPaths.Add(TestPath("media", "movies", "Film2", "Film2.mp4"));
        movieLib.VideosWithoutImagesPaths.Clear();
        movieLib.VideosWithoutImagesPaths.Add(TestPath("media", "movies", "Film3", "Film3.mkv"));
        movieLib.VideosWithoutNfoPaths.Clear();
        movieLib.VideosWithoutNfoPaths.Add(TestPath("media", "movies", "Film4", "Film4.mkv"));
        movieLib.OrphanedMetadataDirectoriesPaths.Clear();
        movieLib.OrphanedMetadataDirectoriesPaths.Add(TestPath("media", "movies", "OldMovie", ".metadata"));
        result.Libraries.Add(movieLib);
        result.Movies.Add(movieLib);

        return result;
    }

    /// <summary>
    /// Creates a <see cref="MediaStatisticsResult"/> with movie, TV show, and music libraries.
    /// Used by serialization roundtrip tests.
    /// </summary>
    /// <returns>A fully populated <see cref="MediaStatisticsResult"/> instance with multiple library types.</returns>
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
        musicLib.SubtitleSize = 0;
        musicLib.SubtitleFileCount = 0;
        musicLib.ImageSize = 0;
        musicLib.ImageFileCount = 0;
        musicLib.NfoSize = 0;
        musicLib.NfoFileCount = 0;
        musicLib.TrickplaySize = 0;
        musicLib.TrickplayFolderCount = 0;
        musicLib.OtherSize = 0;
        musicLib.OtherFileCount = 0;
        musicLib.ContainerFormats.Clear();
        musicLib.Resolutions.Clear();
        musicLib.VideoCodecs.Clear();
        musicLib.VideoAudioCodecs.Clear();
        musicLib.ContainerSizes.Clear();
        musicLib.ResolutionSizes.Clear();
        musicLib.VideoCodecSizes.Clear();
        musicLib.VideoAudioCodecSizes.Clear();
        musicLib.VideoBitrateTiers.Clear();
        musicLib.VideoBitrateTierSizes.Clear();
        musicLib.VideoBitrateTierPaths.Clear();
        musicLib.DynamicRanges.Clear();
        musicLib.DynamicRangeSizes.Clear();
        musicLib.DynamicRangePaths.Clear();
        musicLib.VideoCodecPaths.Clear();
        musicLib.VideoAudioCodecPaths.Clear();
        musicLib.ContainerFormatPaths.Clear();
        musicLib.ResolutionPaths.Clear();
        musicLib.ResolutionDimensions.Clear();
        musicLib.VideoBitrates.Clear();
        musicLib.AudioLanguages.Clear();
        musicLib.AudioLanguageSizes.Clear();
        musicLib.AudioLanguagePaths.Clear();
        musicLib.SubtitleLanguages.Clear();
        musicLib.SubtitleLanguageSizes.Clear();
        musicLib.SubtitleLanguagePaths.Clear();
        musicLib.AudioTrackLabels.Clear();
        musicLib.SubtitleTrackLabels.Clear();
        musicLib.WatchedTiers.Clear();
        musicLib.WatchedTierSizes.Clear();
        musicLib.WatchedTierPaths.Clear();
        musicLib.WatchedByUserPaths.Clear();
        musicLib.WatchedByUserSizes.Clear();
        musicLib.WatchedDetails.Clear();
        musicLib.VideosWithoutSubtitles = 0;
        musicLib.VideosWithoutImages = 0;
        musicLib.VideosWithoutNfo = 0;
        musicLib.OrphanedMetadataDirectories = 0;
        musicLib.VideosWithoutSubtitlesPaths.Clear();
        musicLib.VideosWithoutImagesPaths.Clear();
        musicLib.VideosWithoutNfoPaths.Clear();
        musicLib.OrphanedMetadataDirectoriesPaths.Clear();
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

    /// <summary>
    /// Creates a uniquely named temporary directory and returns its path.
    /// Caller is responsible for deleting it (use with try/finally or IDisposable).
    /// </summary>
    /// <param name="prefix">The prefix string to use for the temporary directory name (default is "jh-test").</param>
    /// <returns>The absolute path to the newly created temporary directory.</returns>
    public static string CreateTempDirectory(string prefix = "jh-test")
    {
        var safePrefix = Path.GetFileName(prefix);
        if (string.IsNullOrWhiteSpace(safePrefix))
        {
            safePrefix = "jh-test";
        }

        var directoryName = $"{safePrefix}-{Guid.NewGuid():N}";
        if (Path.IsPathRooted(directoryName))
        {
            throw new InvalidOperationException("Temporary directory name must be a relative path segment.");
        }

        var tempDir = Path.Join(Path.GetTempPath(), directoryName);
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }
}