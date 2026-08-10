using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class MediaStatisticsServiceTvShowTests
{
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly MediaStatisticsService _service;

    public MediaStatisticsServiceTvShowTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        var loggerMock = TestMockFactory.CreateLogger<MediaStatisticsService>();
        var configHelperMock = TestMockFactory.CreateCleanupConfigHelper();
        _service = new MediaStatisticsService(_libraryManagerMock.Object, _fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(), loggerMock.Object, configHelperMock.Object);
    }

    private static string TestPath(params string[] segments)
    {
        return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
    }

    [Fact]
    public void CalculateStatistics_TvShowStructure_Series1AndSpecials_ShouldNotBeOrphaned()
    {
        // root
        //  +- Series 1 (metadata here: folder.jpg, series.nfo)
        //      +- Season 01
        //          +- S01E01.mkv
        //      +- Specials
        //          +- S00E01.mkv

        var libraryPath = TestPath("media", "tv");
        var seriesPath = TestPath("media", "tv", "Series 1");
        var season01Path = TestPath("media", "tv", "Series 1", "Season 01");
        var specialsPath = TestPath("media", "tv", "Series 1", "Specials");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "TV Shows",
            CollectionType = CollectionTypeOptions.tvshows,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        // Root
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([
            new FileSystemMetadata { FullName = seriesPath, Name = "Series 1", IsDirectory = true }
        ]);

        // Series 1 - Contains metadata but no direct video files
        _fileSystemMock.Setup(f => f.GetFiles(seriesPath)).Returns([
            new FileSystemMetadata
            {
                FullName = TestPath(seriesPath, "folder.jpg"), Name = "folder.jpg", Length = 100_000,
                IsDirectory = false
            },
            new FileSystemMetadata
            {
                FullName = TestPath(seriesPath, "tvshow.nfo"), Name = "tvshow.nfo", Length = 5_000, IsDirectory = false
            }
        ]);
        _fileSystemMock.Setup(f => f.GetDirectories(seriesPath)).Returns([
            new FileSystemMetadata { FullName = season01Path, Name = "Season 01", IsDirectory = true },
            new FileSystemMetadata { FullName = specialsPath, Name = "Specials", IsDirectory = true }
        ]);

        // Season 01 - Contains video
        _fileSystemMock.Setup(f => f.GetFiles(season01Path)).Returns([
            new FileSystemMetadata
            {
                FullName = TestPath(season01Path, "S01E01.mkv"), Name = "S01E01.mkv", Length = 1_000_000_000,
                IsDirectory = false
            }
        ]);
        _fileSystemMock.Setup(f => f.GetDirectories(season01Path)).Returns([]);

        // Specials - Contains video
        _fileSystemMock.Setup(f => f.GetFiles(specialsPath)).Returns([
            new FileSystemMetadata
            {
                FullName = TestPath(specialsPath, "S00E01.mkv"), Name = "S00E01.mkv", Length = 500_000_000,
                IsDirectory = false
            }
        ]);
        _fileSystemMock.Setup(f => f.GetDirectories(specialsPath)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.DoesNotContain(seriesPath, stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(0, stats.OrphanedMetadataDirectories);
    }

    [Fact]
    public void CalculateStatistics_TrulyOrphanedDir_ShouldStillBeDetected()
    {
        var libraryPath = TestPath("media", "tv");
        var orphanPath = TestPath("media", "tv", "OrphanedFolder");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "TV Shows",
            CollectionType = CollectionTypeOptions.tvshows,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([
            new FileSystemMetadata { FullName = orphanPath, Name = "OrphanedFolder", IsDirectory = true }
        ]);

        // OrphanedFolder - metadata but no videos AND no subdirectories containing videos
        _fileSystemMock.Setup(f => f.GetFiles(orphanPath)).Returns([
            new FileSystemMetadata
            {
                FullName = TestPath(orphanPath, "random.srt"), Name = "random.srt", Length = 50_000, IsDirectory = false
            }
        ]);
        _fileSystemMock.Setup(f => f.GetDirectories(orphanPath)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Contains(orphanPath, stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(1, stats.OrphanedMetadataDirectories);
    }

    [Fact]
    public void CalculateStatistics_EmptyFolder_ShouldNotBeOrphaned()
    {
        var libraryPath = TestPath("media", "tv");
        var emptyPath = TestPath("media", "tv", "EmptyFolder");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "TV Shows",
            CollectionType = CollectionTypeOptions.tvshows,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([
            new FileSystemMetadata { FullName = emptyPath, Name = "EmptyFolder", IsDirectory = true }
        ]);

        // EmptyFolder - no files, no subdirs
        _fileSystemMock.Setup(f => f.GetFiles(emptyPath)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(emptyPath)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(0, stats.OrphanedMetadataDirectories);
    }

    [Fact]
    public void CalculateStatistics_SpecialsWithoutEpisodes_ShouldNotBeOrphanedIfMetadataExists()
    {
        // Specials folder often contains its own metadata (images) even if no episodes are currently there
        // Actually, if it has metadata but no episodes, it might be seen as orphaned by current logic.
        // But if it's named "Specials" or "Season 00", it should probably be ignored in TV libraries.

        var libraryPath = TestPath("media", "tv");
        var seriesPath = TestPath("media", "tv", "Series 1");
        var specialsPath = TestPath("media", "tv", "Series 1", "Specials");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "TV Shows",
            CollectionType = CollectionTypeOptions.tvshows,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([
            new FileSystemMetadata { FullName = seriesPath, Name = "Series 1", IsDirectory = true }
        ]);

        _fileSystemMock.Setup(f => f.GetFiles(seriesPath)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(seriesPath)).Returns([
            new FileSystemMetadata { FullName = specialsPath, Name = "Specials", IsDirectory = true }
        ]);

        // Specials folder with only an image
        _fileSystemMock.Setup(f => f.GetFiles(specialsPath)).Returns([
            new FileSystemMetadata
            {
                FullName = TestPath(specialsPath, "season-specials-poster.jpg"), Name = "season-specials-poster.jpg",
                Length = 50_000, IsDirectory = false
            }
        ]);
        _fileSystemMock.Setup(f => f.GetDirectories(specialsPath)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        // Currently, 'Specials' would be reported as orphaned.
        Assert.DoesNotContain(specialsPath, stats.OrphanedMetadataDirectoriesPaths);
    }

    [Fact]
    public void CalculateStatistics_MovieLibrary_StillDetectsOrphanedDirs()
    {
        var libraryPath = TestPath("media", "movies");
        var orphanPath = TestPath("media", "movies", "OrphanedMovieDir");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([
            new FileSystemMetadata { FullName = orphanPath, Name = "OrphanedMovieDir", IsDirectory = true }
        ]);

        _fileSystemMock.Setup(f => f.GetFiles(orphanPath)).Returns([
            new FileSystemMetadata
            {
                FullName = TestPath(orphanPath, "poster.jpg"), Name = "poster.jpg", Length = 50_000, IsDirectory = false
            }
        ]);
        _fileSystemMock.Setup(f => f.GetDirectories(orphanPath)).Returns([]);

        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];

        Assert.Contains(orphanPath, stats.OrphanedMetadataDirectoriesPaths);
    }
}