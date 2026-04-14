using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

public class CleanEmptyMediaFoldersTaskTests : CleanupTaskTestBase
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILogger<CleanEmptyMediaFoldersTask>> _loggerMock;
    private readonly CleanEmptyMediaFoldersTask _task;

    public CleanEmptyMediaFoldersTaskTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        _loggerMock = TestMockFactory.CreateLogger<CleanEmptyMediaFoldersTask>();
        _task = new CleanEmptyMediaFoldersTask(_libraryManagerMock.Object, _fileSystemMock.Object, new Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.PluginLogService(), _loggerMock.Object);

        // Default: DryRun ON — most tests check dry-run log messages
        // (Config from base class already has DryRun defaults)
    }

    private void VerifyLogContains(string messagePart, LogLevel level)
        => VerifyLogContains(_loggerMock, messagePart, level);

    private void VerifyLogNeverContains(string messagePart, LogLevel level)
        => VerifyLogNeverContains(_loggerMock, messagePart, level);

    [Fact]
    public async Task ExecuteInternalAsync_TopLevelFolderWithSubtitlesOnly_DeletesFolder()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration { EmptyMediaFolderTaskMode = TaskMode.Activate };

        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Old Movie (2020)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Old Movie (2020)", movieDir));

        // Subtitles are non-metadata files → folder is orphaned and should be deleted
        SetupFiles(movieDir, "movie.nfo", "poster.jpg", "movie.srt");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_TopLevelFolderWithOnlyMetadata_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Upcoming Movie (2026)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Upcoming Movie (2026)", movieDir));

        // Only metadata/artwork files ? likely a wanted-list placeholder ? skip
        SetupFiles(movieDir, "movie.nfo", "poster.jpg");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_TopLevelFolderWithVideoFile_IsKept()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Good Movie (2021)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Good Movie (2021)", movieDir));

        SetupFiles(movieDir, "movie.mkv", "movie.nfo", "poster.jpg");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CompletelyEmptyFolder_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Upcoming Movie (2025)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Upcoming Movie (2025)", movieDir));

        SetupFiles(movieDir);
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_TrickplayFolder_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string trickplayDir = "/media/movies/Movie.trickplay";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Movie.trickplay", trickplayDir));

        SetupFiles(trickplayDir, "index.json", "00001.jpg");
        SetupSubDirs(trickplayDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_LogsWouldDeleteWithoutDeleting()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Old Movie (2020)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Old Movie (2020)", movieDir));

        // Include a subtitle so the folder qualifies as orphaned (has non-metadata files)
        SetupFiles(movieDir, "movie.nfo", "movie.srt");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_MetadataOnlyFolder_IsNotReportedForDeletion()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Wanted Movie (2026)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Wanted Movie (2026)", movieDir));

        // Only NFO and poster ? metadata-only placeholder ? should NOT be reported for deletion
        SetupFiles(movieDir, "movie.nfo", "poster.jpg");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
        VerifyLogContains("Would have deleted 0 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithVideoInSeason_EntireFolderIsKept()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Quantum Donuts (2018)";
        const string season1Dir = "/media/tv/Quantum Donuts (2018)/Season 01";
        const string season2Dir = "/media/tv/Quantum Donuts (2018)/Season 02";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Quantum Donuts (2018)", showDir));

        SetupFiles(showDir, "tvshow.nfo");
        SetupSubDirs(showDir,
            ("Season 01", season1Dir),
            ("Season 02", season2Dir));

        SetupFiles(season1Dir, "S01E01.mkv", "season.nfo");
        SetupSubDirs(season1Dir);

        SetupFiles(season2Dir, "season.nfo");
        SetupSubDirs(season2Dir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithNoVideoButSubtitles_IsDeleted()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Cancelled Show (2019)";
        const string season1Dir = "/media/tv/Cancelled Show (2019)/Season 01";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Cancelled Show (2019)", showDir));

        SetupFiles(showDir, "tvshow.nfo", "poster.jpg");
        SetupSubDirs(showDir, ("Season 01", season1Dir));

        // Season folder has a subtitle but no video ? orphaned
        SetupFiles(season1Dir, "season.nfo", "S01E01.srt");
        SetupSubDirs(season1Dir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithOnlyMetadataNoVideo_IsSkipped()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Cancelled Show (2019)";
        const string season1Dir = "/media/tv/Cancelled Show (2019)/Season 01";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Cancelled Show (2019)", showDir));

        // Only metadata/artwork ? placeholder ? skip
        SetupFiles(showDir, "tvshow.nfo", "poster.jpg");
        SetupSubDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir, "season.nfo");
        SetupSubDirs(season1Dir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithDeeplyNestedVideo_IsKept()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Deep Show (2020)";
        const string season1Dir = "/media/tv/Deep Show (2020)/Season 01";
        const string extrasDir = "/media/tv/Deep Show (2020)/Season 01/Extras";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Deep Show (2020)", showDir));

        SetupFiles(showDir, "tvshow.nfo");
        SetupSubDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir, "season.nfo");
        SetupSubDirs(season1Dir, ("Extras", extrasDir));

        SetupFiles(extrasDir, "behind-the-scenes.mkv");
        SetupSubDirs(extrasDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MultipleOrphanedFolders_DeletesAllAndReportsCount()
    {
        const string libraryPath = "/media/movies";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath,
            ("Old Movie 1 (2018)", "/media/movies/Old Movie 1 (2018)"),
            ("Old Movie 2 (2019)", "/media/movies/Old Movie 2 (2019)"));

        // Both have subtitles (non-metadata) ? orphaned
        SetupFiles("/media/movies/Old Movie 1 (2018)", "movie.nfo", "movie.srt");
        SetupSubDirs("/media/movies/Old Movie 1 (2018)");

        SetupFiles("/media/movies/Old Movie 2 (2019)", "movie.nfo", "poster.jpg", "movie.ass");
        SetupSubDirs("/media/movies/Old Movie 2 (2019)");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Would have deleted 2 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NoLibraryFolders_CompletesWithoutError()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration { EmptyMediaFolderTaskMode = TaskMode.Activate };

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleted 0 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CancellationRequested_StopsProcessing()
    {
        const string libraryPath1 = "/media/movies1";
        const string libraryPath2 = "/media/movies2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        SetupTopLevelDirs(libraryPath1, ("Movie", "/media/movies1/Movie"));
        SetupFiles("/media/movies1/Movie", "movie.nfo", "movie.srt");
        SetupSubDirs("/media/movies1/Movie");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await _task.ExecuteAsync(new Progress<double>(), cts.Token);

        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath2, false), Times.Never);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DirectoryScanError_LogsErrorAndContinues()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration { EmptyMediaFolderTaskMode = TaskMode.Activate };

        const string libraryPath1 = "/media/movies1";
        const string libraryPath2 = "/media/movies2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1, false)).Throws(new IOException("Access denied"));

        SetupTopLevelDirs(libraryPath2, ("Old Movie", "/media/movies2/Old Movie"));
        // Include subtitle to make it orphaned
        SetupFiles("/media/movies2/Old Movie", "movie.nfo", "movie.srt");
        SetupSubDirs("/media/movies2/Old Movie");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Error scanning directory", LogLevel.Error);
        VerifyLogContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ProgressIsReported()
    {
        const string libraryPath1 = "/media/movies1";
        const string libraryPath2 = "/media/movies2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        SetupTopLevelDirs(libraryPath1);
        SetupTopLevelDirs(libraryPath2);

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(v => reportedValues.Add(v));

        await _task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(2, reportedValues.Count);
        Assert.Equal(50, reportedValues[0]);
        Assert.Equal(100, reportedValues[1]);
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".mp4")]
    [InlineData(".avi")]
    [InlineData(".m4v")]
    [InlineData(".ts")]
    [InlineData(".iso")]
    [InlineData(".MKV")]
    [InlineData(".Mp4")]
    public async Task ExecuteInternalAsync_VariousVideoExtensions_FolderIsKept(string extension)
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/SomeMovie";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("SomeMovie", movieDir));

        SetupFilesWithFullNames(movieDir, "/media/movies/SomeMovie/video" + extension);
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DuplicateLibraryPaths_ScansOnlyOnce()
    {
        const string libraryPath = "/media/movies";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        SetupTopLevelDirs(libraryPath);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath, false), Times.Once);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithEmptySubdirsOnly_IsSkipped()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Future Show (2026)";
        const string season1Dir = "/media/tv/Future Show (2026)/Season 01";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Future Show (2026)", showDir));

        SetupFiles(showDir);
        SetupSubDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir);
        SetupSubDirs(season1Dir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MusicLibrary_IsCompletelySkipped()
    {
        const string musicPath = "/media/music";

        var musicFolder = new VirtualFolderInfo
        {
            Name = "Music",
            Locations = [musicPath],
            CollectionType = CollectionTypeOptions.music
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([musicFolder]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Music library should never be scanned at all
        _fileSystemMock.Verify(f => f.GetDirectories(musicPath, false), Times.Never);
    }

    [Fact]
    public async Task ExecuteInternalAsync_BoxsetLibrary_IsCompletelySkipped()
    {
        const string collectionsPath = "/config/data/collections";

        var boxsetFolder = new VirtualFolderInfo
        {
            Name = "Collections",
            Locations = [collectionsPath],
            CollectionType = CollectionTypeOptions.boxsets
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([boxsetFolder]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Boxset/Collections library should never be scanned at all
        _fileSystemMock.Verify(f => f.GetDirectories(collectionsPath, false), Times.Never);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MusicAndMoviesLibrary_OnlyMoviesAreScanned()
    {
        const string musicPath = "/media/music";
        const string moviesPath = "/media/movies";

        var musicFolder = new VirtualFolderInfo
        {
            Name = "Music",
            Locations = [musicPath],
            CollectionType = CollectionTypeOptions.music
        };
        var moviesFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            Locations = [moviesPath],
            CollectionType = CollectionTypeOptions.movies
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([musicFolder, moviesFolder]);

        SetupTopLevelDirs(moviesPath, ("Old Movie (2020)", "/media/movies/Old Movie (2020)"));
        // Include subtitle to make it orphaned
        SetupFiles("/media/movies/Old Movie (2020)", "movie.nfo", "movie.srt");
        SetupSubDirs("/media/movies/Old Movie (2020)");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Music should not be scanned
        _fileSystemMock.Verify(f => f.GetDirectories(musicPath, false), Times.Never);
        // Movies should be scanned and orphan detected
        _fileSystemMock.Verify(f => f.GetDirectories(moviesPath, false), Times.Once);
        VerifyLogContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MixedFolders_OnlyOrphanedOnesAreDeleted()
    {
        const string libraryPath = "/media/movies";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath,
            ("Good Movie (2021)", "/media/movies/Good Movie (2021)"),
            ("Orphaned Movie (2019)", "/media/movies/Orphaned Movie (2019)"),
            ("Another Good (2020)", "/media/movies/Another Good (2020)"),
            ("Wanted Movie (2026)", "/media/movies/Wanted Movie (2026)"));

        // Good movie with video ? keep
        SetupFiles("/media/movies/Good Movie (2021)", "movie.mkv", "movie.nfo");
        SetupSubDirs("/media/movies/Good Movie (2021)");

        // Orphaned with subtitle ? delete
        SetupFiles("/media/movies/Orphaned Movie (2019)", "movie.nfo", "poster.jpg", "movie.srt");
        SetupSubDirs("/media/movies/Orphaned Movie (2019)");

        // Another good movie with video ? keep
        SetupFiles("/media/movies/Another Good (2020)", "film.mp4");
        SetupSubDirs("/media/movies/Another Good (2020)");

        // Wanted movie with only metadata ? skip (placeholder)
        SetupFiles("/media/movies/Wanted Movie (2026)", "movie.nfo", "poster.jpg");
        SetupSubDirs("/media/movies/Wanted Movie (2026)");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder: /media/movies/Orphaned Movie (2019)", LogLevel.Information);
        VerifyLogContains("Would have deleted 1 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_FolderWithAudioFiles_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string musicDir = "/media/movies/SomeArtist";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("SomeArtist", musicDir));

        SetupFilesWithFullNames(musicDir, "/media/movies/SomeArtist/track01.mp3", "/media/movies/SomeArtist/track02.flac");
        SetupSubDirs(musicDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_FolderWithNestedAudioFiles_IsSkipped()
    {
        const string libraryPath = "/media/music";
        const string artistDir = "/media/music/Drake";
        const string albumDir = "/media/music/Drake/Album1";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Drake", artistDir));

        SetupFiles(artistDir, "artist.nfo");
        SetupSubDirs(artistDir, ("Album1", albumDir));

        SetupFilesWithFullNames(albumDir, "/media/music/Drake/Album1/song.mp3");
        SetupSubDirs(albumDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_BoxsetFolder_IsSkipped()
    {
        const string libraryPath = "/config/data/collections";
        const string boxsetDir = "/config/data/collections/Star Wars Filmreihe [boxset]";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Star Wars Filmreihe [boxset]", boxsetDir));

        SetupFiles(boxsetDir);
        SetupSubDirs(boxsetDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CollectionFolder_IsSkipped()
    {
        const string libraryPath = "/some/path";
        const string collectionDir = "/some/path/My Favorites [collection]";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("My Favorites [collection]", collectionDir));

        SetupFiles(collectionDir, "collection.xml");
        SetupSubDirs(collectionDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".wav")]
    [InlineData(".aac")]
    [InlineData(".m4a")]
    [InlineData(".opus")]
    [InlineData(".wma")]
    [InlineData(".ape")]
    [InlineData(".MP3")]
    [InlineData(".FLAC")]
    public async Task ExecuteInternalAsync_VariousAudioExtensions_FolderIsSkipped(string extension)
    {
        const string libraryPath = "/media/music";
        const string artistDir = "/media/music/Artist";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Artist", artistDir));

        SetupFilesWithFullNames(artistDir, "/media/music/Artist/track" + extension);
        SetupSubDirs(artistDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CollectionsPathLibrary_IsFilteredOutByLocation()
    {
        const string collectionsPath = "/config/data/collections";

        // Library with null CollectionType but location contains "collections"
        var folder = new VirtualFolderInfo
        {
            Name = "My Collections",
            Locations = [collectionsPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([folder]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Should not be scanned due to path-based filter
        _fileSystemMock.Verify(f => f.GetDirectories(collectionsPath, false), Times.Never);
    }

    // ========== New metadata-only / placeholder tests ==========

    [Fact]
    public async Task ExecuteInternalAsync_FolderWithOnlyNfo_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Wanted (2026)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Wanted (2026)", movieDir));

        // Only NFO ? metadata-only ? skip
        SetupFiles(movieDir, "movie.nfo");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_FolderWithOnlyImages_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Wanted (2026)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Wanted (2026)", movieDir));

        // Only images ? metadata-only ? skip
        SetupFiles(movieDir, "poster.jpg", "fanart.png", "banner.webp");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_FolderWithNfoAndImages_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Wanted (2026)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Wanted (2026)", movieDir));

        // NFO + images ? metadata-only ? skip
        SetupFiles(movieDir, "movie.nfo", "poster.jpg", "fanart.png");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_FolderWithSubtitleAndNfo_IsDeleted()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Deleted Movie (2020)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Deleted Movie (2020)", movieDir));

        // NFO + subtitle ? has non-metadata file ? orphaned ? delete
        SetupFiles(movieDir, "movie.nfo", "movie.srt");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_FolderWithUnknownFileExtension_IsDeleted()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Strange Movie (2020)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Strange Movie (2020)", movieDir));

        // NFO + unknown file ? has non-metadata ? orphaned ? delete
        SetupFiles(movieDir, "movie.nfo", "poster.jpg", "readme.txt");
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithNestedMetadataOnly_IsSkipped()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Wanted Show (2026)";
        const string season1Dir = "/media/tv/Wanted Show (2026)/Season 01";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Wanted Show (2026)", showDir));

        // Show folder with NFO, Season folder with NFO ? all metadata-only ? skip
        SetupFiles(showDir, "tvshow.nfo", "poster.jpg");
        SetupSubDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir, "season.nfo");
        SetupSubDirs(season1Dir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithNestedSubtitleNoVideo_IsDeleted()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Old Show (2019)";
        const string season1Dir = "/media/tv/Old Show (2019)/Season 01";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Old Show (2019)", showDir));

        // Show has NFO, but Season has a subtitle ? non-metadata found deep in tree ? orphaned
        SetupFiles(showDir, "tvshow.nfo");
        SetupSubDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir, "season.nfo", "S01E01.srt");
        SetupSubDirs(season1Dir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Theory]
    [InlineData(".srt")]
    [InlineData(".ass")]
    [InlineData(".ssa")]
    [InlineData(".sub")]
    [InlineData(".idx")]
    [InlineData(".vtt")]
    public async Task ExecuteInternalAsync_VariousSubtitleExtensions_FolderIsDeleted(string extension)
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/OrphanedMovie";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("OrphanedMovie", movieDir));

        SetupFilesWithFullNames(movieDir,
            "/media/movies/OrphanedMovie/movie.nfo",
            "/media/movies/OrphanedMovie/subtitle" + extension);
        SetupSubDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    // ========== Helper methods ==========

    private void SetupLibrary(string libraryPath)
    {
        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);
    }

    private void SetupTopLevelDirs(string parentPath, params (string Name, string FullName)[] dirs)
    {
        var dirMetadata = dirs.Select(d => new FileSystemMetadata
        {
            FullName = d.FullName,
            Name = d.Name,
            IsDirectory = true
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetDirectories(parentPath, false)).Returns(dirMetadata);
    }

    private void SetupSubDirs(string parentPath, params (string Name, string FullName)[] dirs)
    {
        var dirMetadata = dirs.Select(d => new FileSystemMetadata
        {
            FullName = d.FullName,
            Name = d.Name,
            IsDirectory = true
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetDirectories(parentPath, false)).Returns(dirMetadata);
    }

    private void SetupFiles(string dirPath, params string[] fileNames)
    {
        var files = fileNames.Select(name => new FileSystemMetadata
        {
            FullName = dirPath + "/" + name,
            IsDirectory = false
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetFiles(dirPath, false)).Returns(files);
    }

    private void SetupFilesWithFullNames(string dirPath, params string[] fullNames)
    {
        var files = fullNames.Select(name => new FileSystemMetadata
        {
            FullName = name,
            IsDirectory = false
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetFiles(dirPath, false)).Returns(files);
    }

}
