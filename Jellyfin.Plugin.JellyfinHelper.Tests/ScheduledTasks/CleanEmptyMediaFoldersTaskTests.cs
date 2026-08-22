using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
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
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<ILogger<CleanEmptyMediaFoldersTask>> _loggerMock;
    private readonly CleanEmptyMediaFoldersTask _task;

    public CleanEmptyMediaFoldersTaskTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        _loggerMock = TestMockFactory.CreateLogger<CleanEmptyMediaFoldersTask>();
        _task = new CleanEmptyMediaFoldersTask(
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(),
            _loggerMock.Object,
            MockConfigHelper.Object,
            MockTrackingService.Object,
            MockTrashService.Object);

        // Default: DryRun ON - most tests check dry-run log messages
        // (Config from base class already has DryRun defaults)
    }

    private void VerifyLogContains(string messagePart, LogLevel level)
    {
        VerifyLogContains(_loggerMock, messagePart, level);
    }

    private void VerifyLogNeverContains(string messagePart, LogLevel level)
    {
        VerifyLogNeverContains(_loggerMock, messagePart, level);
    }

    [Fact]
    public async Task ExecuteInternalAsync_TopLevelFolderWithSubtitlesOnly_DeletesFolder()
    {
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;

        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Old Movie (2020)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Old Movie (2020)", movieDir));

        // Subtitles are non-metadata files → folder is orphaned and should be deleted
        SetupFiles(movieDir, "movie.nfo", "poster.jpg", "movie.srt");
        SetupTopLevelDirs(movieDir);

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

        // Only metadata/artwork files → likely a wanted-list placeholder → skip
        SetupFiles(movieDir, "movie.nfo", "poster.jpg");
        SetupTopLevelDirs(movieDir);

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
        SetupTopLevelDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_TopLevelFolderWithOnlyStrmFile_IsKept()
    {
        // A .strm file is a Jellyfin stream-link pointing at remote/relocated video. It is classified
        // as a video file (MediaExtensions.VideoExtensions), so a folder whose only real content is a
        // .strm must be treated as an active media folder and NEVER deleted — even though the .strm
        // itself is a tiny text file that looks like a non-media file to a naive extension check.
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;

        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Streamed Movie (2022)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Streamed Movie (2022)", movieDir));

        // Only a .strm link file plus metadata/artwork — no local video, no other files.
        SetupFiles(movieDir, "movie.strm", "movie.nfo", "poster.jpg");
        SetupTopLevelDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
        MockTrackingService.Verify(
            t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CompletelyEmptyFolder_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Upcoming Movie (2025)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Upcoming Movie (2025)", movieDir));

        SetupFiles(movieDir);
        SetupTopLevelDirs(movieDir);

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
        SetupTopLevelDirs(trickplayDir);

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
        SetupTopLevelDirs(movieDir);

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

        // Only NFO and poster → metadata-only placeholder → should NOT be reported for deletion
        SetupFiles(movieDir, "movie.nfo", "poster.jpg");
        SetupTopLevelDirs(movieDir);

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
        SetupTopLevelDirs(showDir,
            ("Season 01", season1Dir),
            ("Season 02", season2Dir));

        SetupFiles(season1Dir, "S01E01.mkv", "season.nfo");
        SetupTopLevelDirs(season1Dir);

        SetupFiles(season2Dir, "season.nfo");
        SetupTopLevelDirs(season2Dir);

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
        SetupTopLevelDirs(showDir, ("Season 01", season1Dir));

        // Season folder has a subtitle but no video → orphaned
        SetupFiles(season1Dir, "season.nfo", "S01E01.srt");
        SetupTopLevelDirs(season1Dir);

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

        // Only metadata/artwork → placeholder → skip
        SetupFiles(showDir, "tvshow.nfo", "poster.jpg");
        SetupTopLevelDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir, "season.nfo");
        SetupTopLevelDirs(season1Dir);

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
        SetupTopLevelDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir, "season.nfo");
        SetupTopLevelDirs(season1Dir, ("Extras", extrasDir));

        SetupFiles(extrasDir, "behind-the-scenes.mkv");
        SetupTopLevelDirs(extrasDir);

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

        // Both have subtitles (non-metadata) → orphaned
        SetupFiles("/media/movies/Old Movie 1 (2018)", "movie.nfo", "movie.srt");
        SetupTopLevelDirs("/media/movies/Old Movie 1 (2018)");

        SetupFiles("/media/movies/Old Movie 2 (2019)", "movie.nfo", "poster.jpg", "movie.ass");
        SetupTopLevelDirs("/media/movies/Old Movie 2 (2019)");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Would have deleted 2 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NoLibraryFolders_CompletesWithoutError()
    {
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("No library folders configured", LogLevel.Information);
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
        SetupTopLevelDirs("/media/movies1/Movie");

        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _task.ExecuteAsync(new Progress<double>(), cts.Token));

        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath2), Times.Never);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DirectoryScanError_LogsErrorAndContinues()
    {
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;

        const string libraryPath1 = "/media/movies1";
        const string libraryPath2 = "/media/movies2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1)).Throws(new IOException("Access denied"));

        SetupTopLevelDirs(libraryPath2, ("Old Movie", "/media/movies2/Old Movie"));
        // Include subtitle to make it orphaned
        SetupFiles("/media/movies2/Old Movie", "movie.nfo", "movie.srt");
        SetupTopLevelDirs("/media/movies2/Old Movie");

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
        var progress = new SynchronousProgress<double>(reportedValues.Add);

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
    public async Task ExecuteInternalAsync_VariousVideoExtensions_FolderIsKept(string extension)
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/SomeMovie";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("SomeMovie", movieDir));

        SetupFilesWithFullNames(movieDir, "/media/movies/SomeMovie/video" + extension);
        SetupTopLevelDirs(movieDir);

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

        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath), Times.Once);
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
        SetupTopLevelDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir);
        SetupTopLevelDirs(season1Dir);

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
        _fileSystemMock.Verify(f => f.GetDirectories(musicPath), Times.Never);
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
        _fileSystemMock.Verify(f => f.GetDirectories(collectionsPath), Times.Never);
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
        SetupTopLevelDirs("/media/movies/Old Movie (2020)");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Music should not be scanned
        _fileSystemMock.Verify(f => f.GetDirectories(musicPath), Times.Never);
        // Movies should be scanned and orphan detected
        _fileSystemMock.Verify(f => f.GetDirectories(moviesPath), Times.Once);
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

        // Good movie with video → keep
        SetupFiles("/media/movies/Good Movie (2021)", "movie.mkv", "movie.nfo");
        SetupTopLevelDirs("/media/movies/Good Movie (2021)");

        // Orphaned with subtitle → delete
        SetupFiles("/media/movies/Orphaned Movie (2019)", "movie.nfo", "poster.jpg", "movie.srt");
        SetupTopLevelDirs("/media/movies/Orphaned Movie (2019)");

        // Another good movie with video → keep
        SetupFiles("/media/movies/Another Good (2020)", "film.mp4");
        SetupTopLevelDirs("/media/movies/Another Good (2020)");

        // Wanted movie with only metadata → skip (placeholder)
        SetupFiles("/media/movies/Wanted Movie (2026)", "movie.nfo", "poster.jpg");
        SetupTopLevelDirs("/media/movies/Wanted Movie (2026)");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder: /media/movies/Orphaned Movie (2019)",
            LogLevel.Information);
        VerifyLogContains("Would have deleted 1 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_FolderWithAudioFiles_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string musicDir = "/media/movies/SomeArtist";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("SomeArtist", musicDir));

        SetupFilesWithFullNames(musicDir, "/media/movies/SomeArtist/track01.mp3",
            "/media/movies/SomeArtist/track02.flac");
        SetupTopLevelDirs(musicDir);

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
        SetupTopLevelDirs(artistDir, ("Album1", albumDir));

        SetupFilesWithFullNames(albumDir, "/media/music/Drake/Album1/song.mp3");
        SetupTopLevelDirs(albumDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_BoxsetFolder_IsSkipped()
    {
        // Use a regular library path (not containing "collections") so the library is not
        // filtered out at the base-class level. Include a non-metadata file (.srt) so the
        // folder would otherwise be flagged as orphaned. The [boxset] name guard is the
        // ONLY reason this folder is skipped.
        const string libraryPath = "/media/movies";
        const string boxsetDir = "/media/movies/Star Wars Filmreihe [boxset]";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Star Wars Filmreihe [boxset]", boxsetDir));

        // Non-metadata file ensures this would otherwise be flagged as orphaned
        SetupFiles(boxsetDir, "movie.nfo", "movie.srt");
        SetupTopLevelDirs(boxsetDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CollectionFolder_IsSkipped()
    {
        // Use a regular library path and include a non-metadata file (.srt) so the folder
        // would otherwise be flagged as orphaned. The [collection] name guard is the
        // ONLY reason this folder is skipped.
        const string libraryPath = "/media/movies";
        const string collectionDir = "/media/movies/My Favorites [collection]";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("My Favorites [collection]", collectionDir));

        // Non-metadata file ensures this would otherwise be flagged as orphaned
        SetupFiles(collectionDir, "collection.xml", "movie.srt");
        SetupTopLevelDirs(collectionDir);

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
    public async Task ExecuteInternalAsync_VariousAudioExtensions_FolderIsSkipped(string extension)
    {
        const string libraryPath = "/media/music";
        const string artistDir = "/media/music/Artist";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Artist", artistDir));

        SetupFilesWithFullNames(artistDir, "/media/music/Artist/track" + extension);
        SetupTopLevelDirs(artistDir);

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
        _fileSystemMock.Verify(f => f.GetDirectories(collectionsPath), Times.Never);
    }

    // ========== New metadata-only / placeholder tests ==========

    [Theory]
    [InlineData("movie.nfo")]
    [InlineData("poster.jpg", "fanart.png", "banner.webp")]
    [InlineData("movie.nfo", "poster.jpg", "fanart.png")]
    public async Task ExecuteInternalAsync_MetadataOnlyFiles_FolderIsSkipped(params string[] files)
    {
        // A folder containing ONLY metadata/artwork files (NFO + images, no video/audio/subtitle)
        // is treated as a wanted-list placeholder and must never be reported for deletion.
        // Coverage: single NFO, images-only, and NFO+images combos all hit the same
        // !hasNonMetadataFiles guard in AnalyzeDirectoryRecursive.
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Wanted (2026)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Wanted (2026)", movieDir));
        SetupFiles(movieDir, files);
        SetupTopLevelDirs(movieDir);

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

        // NFO + subtitle → has non-metadata file → orphaned → delete
        SetupFiles(movieDir, "movie.nfo", "movie.srt");
        SetupTopLevelDirs(movieDir);

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

        // NFO + unknown file → has non-metadata → orphaned → delete
        SetupFiles(movieDir, "movie.nfo", "poster.jpg", "readme.txt");
        SetupTopLevelDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithNestedSubtitleNoVideo_IsDeleted()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Old Show (2019)";
        const string season1Dir = "/media/tv/Old Show (2019)/Season 01";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Old Show (2019)", showDir));

        // Show has NFO, but Season has a subtitle → non-metadata found deep in tree → orphaned
        SetupFiles(showDir, "tvshow.nfo");
        SetupTopLevelDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir, "season.nfo", "S01E01.srt");
        SetupTopLevelDirs(season1Dir);

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
        SetupTopLevelDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    // AnalyzeDirectoryRecursive returns (HasVideoFiles=true, TotalBytes=0) on early
    // video-found exit. We verify the observable contract end-to-end:
    //   - Run with TWO top-level folders: one with video (must be kept) and one orphan (must be deleted).
    //   - UseTrash=true so MoveToTrash is called; the mock returns a known byte count for the orphan.
    //   - RecordCleanup must be called exactly once with bytesFreed == orphanBytes (not orphanBytes + videoBytes),
    //     proving the video folder contributed zero bytes to the accounting.
    [Fact]
    public async Task ExecuteInternalAsync_VideoFolderContributesZeroBytesToRecordCleanup()
    {
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
        Config.UseTrash = true;

        const string libraryPath = "/media/movies";
        const string videoDir = "/media/movies/Active Movie (2021)";
        const string orphanDir = "/media/movies/Orphaned Movie (2019)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath,
            ("Active Movie (2021)", videoDir),
            ("Orphaned Movie (2019)", orphanDir));

        // Video folder - has video file → must be skipped entirely
        SetupFiles(videoDir, "movie.mkv", "movie.nfo");
        SetupTopLevelDirs(videoDir);

        // Orphan folder - subtitle only → must be trashed
        SetupFiles(orphanDir, "movie.nfo", "movie.srt");
        SetupTopLevelDirs(orphanDir);

        const long orphanBytes = 12345L;
        MockTrashService
            .Setup(t => t.MoveToTrash(orphanDir, It.IsAny<string>(), It.IsAny<ILogger>()))
            .Returns(orphanBytes);
        MockTrashService
            .Setup(t => t.MoveToTrash(videoDir, It.IsAny<string>(), It.IsAny<ILogger>()))
            .Returns(0L); // should never be called, but safe default

        long? capturedBytes = null;
        MockTrackingService
            .Setup(t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()))
            .Callback<long, int, ILogger>((bytes, _, _) => capturedBytes = bytes);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // RecordCleanup must have fired exactly once (the orphan)
        MockTrackingService.Verify(
            t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
            Times.Once);

        // Bytes must equal exactly the orphan's bytes - video folder contributes nothing
        Assert.Equal(orphanBytes, capturedBytes);

        // MoveToTrash must never have been called on the video folder
        MockTrashService.Verify(
            t => t.MoveToTrash(videoDir, It.IsAny<string>(), It.IsAny<ILogger>()),
            Times.Never);
    }

    // VideoExtensions uses OrdinalIgnoreCase, so uppercase extensions are matched
    // without any normalisation step. This test verifies the full task pipeline with an
    // uppercase extension by pairing the video folder with an orphan folder in the same run,
    // confirming the video folder is kept (RecordCleanup only fires for the orphan).
    [Fact]
    public async Task ExecuteInternalAsync_UppercaseVideoExtension_FolderKeptOrphanStillDeleted()
    {
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
        Config.UseTrash = true;

        const string libraryPath = "/media/movies";
        const string videoDir = "/media/movies/Good Movie (2022)";
        const string orphanDir = "/media/movies/Gone Movie (2018)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath,
            ("Good Movie (2022)", videoDir),
            ("Gone Movie (2018)", orphanDir));

        // Uppercase extension - VideoExtensions.OrdinalIgnoreCase handles this directly
        SetupFiles(videoDir, "movie.MKV");
        SetupTopLevelDirs(videoDir);

        SetupFiles(orphanDir, "movie.nfo", "movie.srt");
        SetupTopLevelDirs(orphanDir);

        MockTrashService
            .Setup(t => t.MoveToTrash(orphanDir, It.IsAny<string>(), It.IsAny<ILogger>()))
            .Returns(1L);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Orphan was trashed → RecordCleanup fires once
        MockTrackingService.Verify(
            t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()),
            Times.Once);
        // Video folder must never be passed to trash
        MockTrashService.Verify(
            t => t.MoveToTrash(videoDir, It.IsAny<string>(), It.IsAny<ILogger>()),
            Times.Never);
    }

    // An orphan that fails the age gate must be left untouched: neither deleted nor reported.
    // The base default IsOldEnoughForDeletion==true, so override to false to reach the age branch.
    [Fact]
    public async Task ExecuteInternalAsync_OrphanTooNew_IsSkippedWithMinAgeLog()
    {
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
        Config.OrphanMinAgeDays = 7;

        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Fresh Orphan (2026)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Fresh Orphan (2026)", movieDir));

        // NFO + subtitle → would be an orphan, but the age gate blocks it.
        SetupFiles(movieDir, "movie.nfo", "movie.srt");
        SetupTopLevelDirs(movieDir);

        MockConfigHelper.Setup(x => x.IsOldEnoughForDeletion(movieDir)).Returns(false);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Skipping too-new orphan", LogLevel.Debug);
        VerifyLogNeverContains("Deleting orphaned media folder", LogLevel.Information);
        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
    }

    // Hard-delete path (non-dry-run, no trash). Directory.Delete is a real static I/O call, so a
    // real temp dir must back the mocked analysis; treeBytes (from the mocked file Length) and the
    // deleted count must both flow into RecordCleanup.
    [Fact]
    public async Task ExecuteInternalAsync_HardDeleteOrphan_RemovesFolderAndRecordsTreeBytes()
    {
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;
        Config.UseTrash = false;

        var libraryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var realDir = Path.Combine(libraryPath, "Orphan");
        Directory.CreateDirectory(realDir);
        File.WriteAllText(Path.Combine(realDir, "movie.srt"), "sub");

        try
        {
            SetupLibrary(libraryPath);
            SetupTopLevelDirs(libraryPath, ("Orphan", realDir));

            // Non-zero Length so treeBytes is accumulated and recorded.
            const long fileBytes = 4096L;
            _fileSystemMock.Setup(f => f.GetFiles(realDir)).Returns([
                new FileSystemMetadata { FullName = realDir + "/movie.srt", IsDirectory = false, Length = fileBytes }
            ]);
            SetupTopLevelDirs(realDir);

            long? capturedBytes = null;
            var capturedCount = 0;
            MockTrackingService
                .Setup(t => t.RecordCleanup(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<ILogger>()))
                .Callback<long, int, ILogger>((bytes, count, _) =>
                {
                    capturedBytes = bytes;
                    capturedCount = count;
                });

            await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            VerifyLogContains("Deleting orphaned media folder", LogLevel.Information);
            Assert.False(Directory.Exists(realDir));
            Assert.Equal(fileBytes, capturedBytes);
            Assert.Equal(1, capturedCount);
        }
        finally
        {
            if (Directory.Exists(libraryPath))
            {
                Directory.Delete(libraryPath, true);
            }
        }
    }

    // A folder whose file listing throws is unreadable: it must never be flagged for deletion.
    [Fact]
    public async Task ExecuteInternalAsync_GetFilesThrows_LogsWarningAndSkipsFolder()
    {
        Config.EmptyMediaFolderTaskMode = TaskMode.Activate;

        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Locked Movie (2020)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Locked Movie (2020)", movieDir));

        _fileSystemMock.Setup(f => f.GetFiles(movieDir)).Throws(new IOException("locked"));
        SetupTopLevelDirs(movieDir);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Could not list files in", LogLevel.Warning);
        VerifyLogNeverContains("Would delete orphaned media folder", LogLevel.Information);
    }

    // Fail-closed: an unreadable subdirectory listing leaves the subtree unanalyzed, so the orphan
    // verdict is unproven — a video could live behind the directory we failed to enumerate. Even
    // though a subtitle was already found at the top, the folder must NOT be flagged for deletion.
    [Fact]
    public async Task ExecuteInternalAsync_GetSubdirectoriesThrows_LogsWarningAndSkipsFolder()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Half Read (2019)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Half Read (2019)", movieDir));

        // Subtitle → non-metadata → looks like an orphan candidate on its own.
        SetupFiles(movieDir, "movie.srt");
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Throws(new UnauthorizedAccessException());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Could not list subdirectories in", LogLevel.Warning);
        VerifyLogContains("unresolved symlinked/unreadable subdirectory", LogLevel.Warning);
        VerifyLogNeverContains("[Dry Run] Would delete orphaned media folder", LogLevel.Information);
    }

    // Deactivate mode short-circuits ExecuteAsync before any scan: it must report 100 once and
    // never touch the library manager or file system. A regression that fell through to RunCleanup
    // would emit "Task started" and enumerate directories; one that skipped the report would leave
    // the scheduler stuck below 100%.
    [Fact]
    public async Task ExecuteInternalAsync_DeactivateMode_ReportsFullProgressAndSkipsScan()
    {
        Config.EmptyMediaFolderTaskMode = TaskMode.Deactivate;

        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Old Movie (2020)";

        // A genuine orphan candidate so any scan would be observable via GetDirectories.
        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Old Movie (2020)", movieDir));
        SetupFiles(movieDir, "movie.nfo", "movie.srt");
        SetupTopLevelDirs(movieDir);

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(reportedValues.Add);

        await _task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Single(reportedValues);
        Assert.Equal(100, reportedValues[0]);
        MockConfigHelper.Verify(x => x.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()), Times.Never);
        _fileSystemMock.Verify(f => f.GetDirectories(It.IsAny<string>()), Times.Never);
        VerifyLogNeverContains("Task started", LogLevel.Information);
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

        _fileSystemMock.Setup(f => f.GetDirectories(parentPath)).Returns(dirMetadata);
    }

    private void SetupFiles(string dirPath, params string[] fileNames)
        => SetupFilesWithFullNames(dirPath, fileNames.Select(name => dirPath + "/" + name).ToArray());

    private void SetupFilesWithFullNames(string dirPath, params string[] fullNames)
    {
        var files = fullNames.Select(name => new FileSystemMetadata
        {
            FullName = name,
            IsDirectory = false
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetFiles(dirPath)).Returns(files);
    }
}