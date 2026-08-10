using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

public class CleanupConfigHelperTests
{
    private static CleanupConfigHelper CreateHelper(PluginConfiguration? config = null)
    {
        var cfg = config ?? new PluginConfiguration();
        var configServiceMock = new Mock<IPluginConfigurationService>();
        configServiceMock.Setup(s => s.IsInitialized).Returns(true);
        configServiceMock.Setup(s => s.GetConfiguration()).Returns(cfg);
        return new CleanupConfigHelper(configServiceMock.Object);
    }

    // ===== GetConfig =====

    [Fact]
    public void GetConfig_ReturnsDefaultConfig_WhenPluginNotInitialized()
    {
        var configServiceMock = new Mock<IPluginConfigurationService>();
        configServiceMock.Setup(s => s.IsInitialized).Returns(false);
        configServiceMock.Setup(s => s.GetConfiguration()).Returns(new PluginConfiguration());

        var helper = new CleanupConfigHelper(configServiceMock.Object);
        var config = helper.GetConfig();
        Assert.NotNull(config);
    }

    [Fact]
    public void GetConfig_ReturnsConfiguredValues()
    {
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 7, UseTrash = true };
        var helper = CreateHelper(cfg);
        var result = helper.GetConfig();
        Assert.Equal(7, result.OrphanMinAgeDays);
        Assert.True(result.UseTrash);
    }

    // ===== TaskMode Getters =====

    [Theory]
    [InlineData(TaskMode.Activate)]
    [InlineData(TaskMode.DryRun)]
    [InlineData(TaskMode.Deactivate)]
    public void GetTrickplayTaskMode_ReturnsConfiguredValue(TaskMode mode)
    {
        var cfg = new PluginConfiguration { TrickplayTaskMode = mode };
        var helper = CreateHelper(cfg);
        Assert.Equal(mode, helper.GetTrickplayTaskMode());
    }

    [Theory]
    [InlineData(TaskMode.Activate)]
    [InlineData(TaskMode.DryRun)]
    [InlineData(TaskMode.Deactivate)]
    public void GetEmptyMediaFolderTaskMode_ReturnsConfiguredValue(TaskMode mode)
    {
        var cfg = new PluginConfiguration { EmptyMediaFolderTaskMode = mode };
        var helper = CreateHelper(cfg);
        Assert.Equal(mode, helper.GetEmptyMediaFolderTaskMode());
    }

    [Theory]
    [InlineData(TaskMode.Activate)]
    [InlineData(TaskMode.DryRun)]
    [InlineData(TaskMode.Deactivate)]
    public void GetOrphanedSubtitleTaskMode_ReturnsConfiguredValue(TaskMode mode)
    {
        var cfg = new PluginConfiguration { OrphanedSubtitleTaskMode = mode };
        var helper = CreateHelper(cfg);
        Assert.Equal(mode, helper.GetOrphanedSubtitleTaskMode());
    }

    [Theory]
    [InlineData(TaskMode.Activate)]
    [InlineData(TaskMode.DryRun)]
    [InlineData(TaskMode.Deactivate)]
    public void GetLinkRepairTaskMode_ReturnsConfiguredValue(TaskMode mode)
    {
        var cfg = new PluginConfiguration { LinkRepairTaskMode = mode };
        var helper = CreateHelper(cfg);
        Assert.Equal(mode, helper.GetLinkRepairTaskMode());
    }

    // ===== IsDryRun Instance Methods =====

    [Fact]
    public void IsDryRunTrickplay_ReturnsTrue_WhenDryRun()
    {
        var cfg = new PluginConfiguration { TrickplayTaskMode = TaskMode.DryRun };
        Assert.True(CreateHelper(cfg).IsDryRunTrickplay());
    }

    [Fact]
    public void IsDryRunTrickplay_ReturnsFalse_WhenActivate()
    {
        var cfg = new PluginConfiguration { TrickplayTaskMode = TaskMode.Activate };
        Assert.False(CreateHelper(cfg).IsDryRunTrickplay());
    }

    [Fact]
    public void IsDryRunTrickplay_ReturnsFalse_WhenDeactivate()
    {
        // Deactivate means the task is skipped entirely (early-exit in the base class);
        // IsDryRun is never consulted in that path and correctly returns false for Deactivate.
        var cfg = new PluginConfiguration { TrickplayTaskMode = TaskMode.Deactivate };
        Assert.False(CreateHelper(cfg).IsDryRunTrickplay());
    }

    [Fact]
    public void IsDryRunEmptyMediaFolders_ReturnsTrue_WhenDryRun()
    {
        var cfg = new PluginConfiguration { EmptyMediaFolderTaskMode = TaskMode.DryRun };
        Assert.True(CreateHelper(cfg).IsDryRunEmptyMediaFolders());
    }

    [Fact]
    public void IsDryRunEmptyMediaFolders_ReturnsFalse_WhenActivate()
    {
        var cfg = new PluginConfiguration { EmptyMediaFolderTaskMode = TaskMode.Activate };
        Assert.False(CreateHelper(cfg).IsDryRunEmptyMediaFolders());
    }

    [Fact]
    public void IsDryRunEmptyMediaFolders_ReturnsFalse_WhenDeactivate()
    {
        // Deactivate means the task is skipped entirely; IsDryRun correctly returns false.
        var cfg = new PluginConfiguration { EmptyMediaFolderTaskMode = TaskMode.Deactivate };
        Assert.False(CreateHelper(cfg).IsDryRunEmptyMediaFolders());
    }

    [Fact]
    public void IsDryRunOrphanedSubtitles_ReturnsTrue_WhenDryRun()
    {
        var cfg = new PluginConfiguration { OrphanedSubtitleTaskMode = TaskMode.DryRun };
        Assert.True(CreateHelper(cfg).IsDryRunOrphanedSubtitles());
    }

    [Fact]
    public void IsDryRunOrphanedSubtitles_ReturnsFalse_WhenActivate()
    {
        var cfg = new PluginConfiguration { OrphanedSubtitleTaskMode = TaskMode.Activate };
        Assert.False(CreateHelper(cfg).IsDryRunOrphanedSubtitles());
    }

    [Fact]
    public void IsDryRunOrphanedSubtitles_ReturnsFalse_WhenDeactivate()
    {
        // Deactivate means the task is skipped entirely; IsDryRun correctly returns false.
        var cfg = new PluginConfiguration { OrphanedSubtitleTaskMode = TaskMode.Deactivate };
        Assert.False(CreateHelper(cfg).IsDryRunOrphanedSubtitles());
    }

    [Fact]
    public void IsDryRunLinkRepair_ReturnsTrue_WhenDryRun()
    {
        var cfg = new PluginConfiguration { LinkRepairTaskMode = TaskMode.DryRun };
        Assert.True(CreateHelper(cfg).IsDryRunLinkRepair());
    }

    [Fact]
    public void IsDryRunLinkRepair_ReturnsFalse_WhenActivate()
    {
        var cfg = new PluginConfiguration { LinkRepairTaskMode = TaskMode.Activate };
        Assert.False(CreateHelper(cfg).IsDryRunLinkRepair());
    }

    [Fact]
    public void IsDryRunLinkRepair_ReturnsFalse_WhenDeactivate()
    {
        // Deactivate means the task is skipped entirely; IsDryRun correctly returns false.
        var cfg = new PluginConfiguration { LinkRepairTaskMode = TaskMode.Deactivate };
        Assert.False(CreateHelper(cfg).IsDryRunLinkRepair());
    }

    // ===== Static IsDryRun =====

    [Fact]
    public void IsDryRun_ActivateMode_ReturnsFalse()
    {
        Assert.False(CleanupConfigHelper.IsDryRun(TaskMode.Activate));
    }

    [Fact]
    public void IsDryRun_DryRunMode_ReturnsTrue()
    {
        Assert.True(CleanupConfigHelper.IsDryRun(TaskMode.DryRun));
    }

    [Fact]
    public void IsDryRun_DeactivateMode_ReturnsFalse()
    {
        // Deactivate triggers an early-exit in the base task before IsDryRun is consulted.
        // IsDryRun correctly returns false - only DryRun mode returns true.
        Assert.False(CleanupConfigHelper.IsDryRun(TaskMode.Deactivate));
    }

    // ===== ParseCommaSeparated =====

    [Fact]
    public void ParseCommaSeparated_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(CleanupConfigHelper.ParseCommaSeparated(null));
        Assert.Empty(CleanupConfigHelper.ParseCommaSeparated(""));
        Assert.Empty(CleanupConfigHelper.ParseCommaSeparated("   "));
    }

    [Fact]
    public void ParseCommaSeparated_ValidInput_ReturnsParsedValues()
    {
        var result = CleanupConfigHelper.ParseCommaSeparated("Movies, TV Shows , Music");
        Assert.Equal(3, result.Count);
        Assert.Contains("Movies", result);
        Assert.Contains("TV Shows", result);
        Assert.Contains("Music", result);
    }

    [Fact]
    public void ParseCommaSeparated_CaseInsensitive()
    {
        var result = CleanupConfigHelper.ParseCommaSeparated("movies, MOVIES");
        Assert.Single(result);
    }

    [Fact]
    public void ParseCommaSeparated_TrimsWhitespace()
    {
        var result = CleanupConfigHelper.ParseCommaSeparated("  a , b , c  ");
        Assert.Equal(3, result.Count);
        Assert.Contains("a", result);
        Assert.Contains("b", result);
        Assert.Contains("c", result);
    }

    // ===== GetTrashPath =====

    [Fact]
    public void GetTrashPath_DefaultsToJellyfinTrash_WhenEmpty()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var cfg = new PluginConfiguration { TrashFolderPath = "" };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        Assert.Equal(Path.GetFullPath(Path.Join(root, ".jellyfin-trash")), result);
    }

    [Fact]
    public void GetTrashPath_DefaultsToJellyfinTrash_WhenWhitespace()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var cfg = new PluginConfiguration { TrashFolderPath = "   " };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        Assert.Equal(Path.GetFullPath(Path.Join(root, ".jellyfin-trash")), result);
    }

    [Fact]
    public void GetTrashPath_RelativePath_JoinsWithLibraryRoot()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var cfg = new PluginConfiguration { TrashFolderPath = ".trash" };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        Assert.Equal(Path.GetFullPath(Path.Join(root, ".trash")), result);
    }

    [Fact]
    public void GetTrashPath_AbsolutePath_ReturnsAsIs()
    {
        var absolutePath = Path.GetFullPath(Path.Join(Path.GetTempPath(), "my-trash"));
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var cfg = new PluginConfiguration { TrashFolderPath = absolutePath };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        Assert.Equal(absolutePath, result);
    }

    [Fact]
    public void GetTrashPath_AbsolutePath_EqualToLibraryRoot_FallsBackToDefault()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var cfg = new PluginConfiguration { TrashFolderPath = root };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        var expected = Path.GetFullPath(Path.Join(root, ".jellyfin-trash"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetTrashPath_RelativePathTraversal_FallsBackToDefault()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var cfg = new PluginConfiguration { TrashFolderPath = "../../sensitive" };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        // Path traversal must not escape the library root - must fall back to safe default.
        var expected = Path.GetFullPath(Path.Join(root, ".jellyfin-trash"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetTrashPath_DotPath_FallsBackToDefault()
    {
        // TrashFolderPath = "." resolves to the library root itself - must not be allowed.
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var cfg = new PluginConfiguration { TrashFolderPath = "." };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        var expected = Path.GetFullPath(Path.Join(root, ".jellyfin-trash"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetTrashPath_FilesystemRoot_ResolvesCorrectly()
    {
        // When the library itself is at the filesystem root,
        // the root-normalization logic must still produce a valid child path.
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var cfg = new PluginConfiguration { TrashFolderPath = ".jellyfin-trash" };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        var expected = Path.GetFullPath(Path.Join(root, ".jellyfin-trash"));
        Assert.Equal(expected, result);
        // Must be a child of root, not root itself
        Assert.NotEqual(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            result.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    // ===== GetFilteredLibraryLocations =====

    [Fact]
    public void GetFilteredLibraryLocations_ThrowsOnNull()
    {
        var helper = CreateHelper();
        Assert.Throws<System.ArgumentNullException>(() => helper.GetFilteredLibraryLocations(null!));
    }

    [Fact]
    public void GetFilteredLibraryLocations_ReturnsEmpty_WhenNoFolders()
    {
        var helper = CreateHelper();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo>());
        var result = helper.GetFilteredLibraryLocations(libraryManager.Object);
        Assert.Empty(result);
    }

    [Fact]
    public void GetFilteredLibraryLocations_ExcludesMusicLibraries()
    {
        var helper = CreateHelper();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo>
            {
                new()
                {
                    Name = "Music",
                    CollectionType = CollectionTypeOptions.music,
                    Locations = ["/media/music"]
                },
                new()
                {
                    Name = "Movies",
                    CollectionType = CollectionTypeOptions.movies,
                    Locations = ["/media/movies"]
                }
            });
        var result = helper.GetFilteredLibraryLocations(libraryManager.Object);
        Assert.Single(result);
        Assert.Equal("/media/movies", result[0]);
    }

    [Fact]
    public void GetFilteredLibraryLocations_ExcludesBoxsets()
    {
        var helper = CreateHelper();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo>
            {
                new()
                {
                    Name = "Collections",
                    CollectionType = CollectionTypeOptions.boxsets,
                    Locations = ["/media/collections"]
                },
                new()
                {
                    Name = "TV Shows",
                    CollectionType = CollectionTypeOptions.tvshows,
                    Locations = ["/media/tvshows"]
                }
            });
        var result = helper.GetFilteredLibraryLocations(libraryManager.Object);
        Assert.Single(result);
        Assert.Equal("/media/tvshows", result[0]);
    }

    [Fact]
    public void GetFilteredLibraryLocations_ExcludesCollectionsByName()
    {
        var helper = CreateHelper();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo>
            {
                new()
                {
                    Name = "My Collection",
                    CollectionType = CollectionTypeOptions.movies,
                    Locations = ["/media/collection"]
                },
                new()
                {
                    Name = "My Boxset",
                    CollectionType = CollectionTypeOptions.movies,
                    Locations = ["/media/boxset"]
                },
                new()
                {
                    Name = "Movies",
                    CollectionType = CollectionTypeOptions.movies,
                    Locations = ["/media/movies"]
                }
            });
        var result = helper.GetFilteredLibraryLocations(libraryManager.Object);
        Assert.Single(result);
        Assert.Equal("/media/movies", result[0]);
    }

    [Fact]
    public void GetFilteredLibraryLocations_NoExclude_ReturnsAllVideoLibraries()
    {
        var cfg = new PluginConfiguration { ExcludedLibraries = "" };
        var helper = CreateHelper(cfg);
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo>
            {
                new()
                {
                    Name = "Movies",
                    CollectionType = CollectionTypeOptions.movies,
                    Locations = ["/media/movies"]
                },
                new()
                {
                    Name = "TV Shows",
                    CollectionType = CollectionTypeOptions.tvshows,
                    Locations = ["/media/tvshows"]
                }
            });
        var result = helper.GetFilteredLibraryLocations(libraryManager.Object);
        Assert.Equal(2, result.Count);
        Assert.Contains("/media/movies", result);
        Assert.Contains("/media/tvshows", result);
    }

    [Fact]
    public void GetFilteredLibraryLocations_AppliesExcludeFilter()
    {
        var cfg = new PluginConfiguration { ExcludedLibraries = "TV Shows" };
        var helper = CreateHelper(cfg);
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo>
            {
                new()
                {
                    Name = "Movies",
                    CollectionType = CollectionTypeOptions.movies,
                    Locations = ["/media/movies"]
                },
                new()
                {
                    Name = "TV Shows",
                    CollectionType = CollectionTypeOptions.tvshows,
                    Locations = ["/media/tvshows"]
                }
            });
        var result = helper.GetFilteredLibraryLocations(libraryManager.Object);
        Assert.Single(result);
        Assert.Equal("/media/movies", result[0]);
    }

    [Fact]
    public void GetFilteredLibraryLocations_ExcludesCollectionsPath()
    {
        var helper = CreateHelper();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo>
            {
                new()
                {
                    Name = "Movies",
                    CollectionType = CollectionTypeOptions.movies,
                    Locations = ["/config/data/collections", "/media/movies"]
                }
            });
        var result = helper.GetFilteredLibraryLocations(libraryManager.Object);
        Assert.Single(result);
        Assert.Equal("/media/movies", result[0]);
    }

    [Fact]
    public void GetFilteredLibraryLocations_DeduplicatesLocations()
    {
        var helper = CreateHelper();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(lm => lm.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo>
            {
                new()
                {
                    Name = "Movies",
                    CollectionType = CollectionTypeOptions.movies,
                    Locations = ["/media/movies"]
                },
                new()
                {
                    Name = "More Movies",
                    CollectionType = CollectionTypeOptions.movies,
                    Locations = ["/media/movies"]
                }
            });
        var result = helper.GetFilteredLibraryLocations(libraryManager.Object);
        Assert.Single(result);
    }

    // ===== IsOldEnoughForDeletion =====

    [Fact]
    public void IsOldEnoughForDeletion_ZeroDays_AlwaysTrue()
    {
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 0 };
        var helper = CreateHelper(cfg);
        Assert.True(helper.IsOldEnoughForDeletion("/nonexistent"));
    }

    [Fact]
    public void IsOldEnoughForDeletion_NonExistentDir_ReturnsFalse()
    {
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 1 };
        var helper = CreateHelper(cfg);
        Assert.False(helper.IsOldEnoughForDeletion("/this/path/does/not/exist/at/all"));
    }

    [Fact]
    public void IsOldEnoughForDeletion_RecentDir_ReturnsFalse()
    {
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 365 };
        var helper = CreateHelper(cfg);
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.False(helper.IsOldEnoughForDeletion(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public void IsOldEnoughForDeletion_UsesEarlierOf_CreationTime_And_LastWriteTime()
    {
        // The guard picks min(CreationTime, LastWriteTime) so that a directory whose
        // LastWriteTime was bumped recently is still considered old if it was created
        // long ago - and vice versa.  We can only control LastWriteTime reliably in a
        // test, so we verify the LastWriteTime branch: a directory created just now but
        // whose LastWriteTime is back-dated to > MinAgeDays ago must return true.
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 30 };
        var helper = CreateHelper(cfg);
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Back-date LastWriteTime far into the past so min(Created, LastWrite) is old.
            var oldDate = DateTime.UtcNow.AddDays(-60);
            Directory.SetLastWriteTimeUtc(tempDir, oldDate);
            Directory.SetCreationTimeUtc(tempDir, oldDate);

            Assert.True(helper.IsOldEnoughForDeletion(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public void IsOldEnoughForDeletion_Pre1980Timestamp_ReturnsFalse()
    {
        // Timestamps before 1980 are treated as corrupted (FAT filesystem epoch artefacts,
        // clock-drift on embedded hardware, etc.).  The guard rejects them to prevent a
        // directory with a bogus creation time from being considered arbitrarily old and
        // therefore eligible for immediate deletion.
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 1 };
        var helper = CreateHelper(cfg);
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            Directory.SetCreationTimeUtc(tempDir, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(tempDir, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.False(helper.IsOldEnoughForDeletion(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    // ===== IsFileOldEnoughForDeletion =====

    [Fact]
    public void IsFileOldEnoughForDeletion_ZeroDays_AlwaysTrue()
    {
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 0 };
        var helper = CreateHelper(cfg);
        Assert.True(helper.IsFileOldEnoughForDeletion("/nonexistent"));
    }

    [Fact]
    public void IsFileOldEnoughForDeletion_NonExistentFile_ReturnsFalse()
    {
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 1 };
        var helper = CreateHelper(cfg);
        Assert.False(helper.IsFileOldEnoughForDeletion("/this/path/does/not/exist.txt"));
    }

    [Fact]
    public void IsFileOldEnoughForDeletion_RecentFile_ReturnsFalse()
    {
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 365 };
        var helper = CreateHelper(cfg);
        var tempFile = Path.GetTempFileName();
        try
        {
            Assert.False(helper.IsFileOldEnoughForDeletion(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ===== GetExistingTrashFoldersForPath =====

    [Fact]
    public void GetExistingTrashFoldersForPath_ThrowsOnNullLibraryManager()
    {
        var helper = CreateHelper();
        Assert.Throws<System.ArgumentNullException>(() =>
            helper.GetExistingTrashFoldersForPath(null!, "/tmp"));
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_EmptyQuery_ReturnsEmpty()
    {
        var helper = CreateHelper();
        var lm = new Mock<ILibraryManager>();
        Assert.Empty(helper.GetExistingTrashFoldersForPath(lm.Object, string.Empty));
        Assert.Empty(helper.GetExistingTrashFoldersForPath(lm.Object, "   "));
        Assert.Empty(helper.GetExistingTrashFoldersForPath(lm.Object, null!));
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_AbsolutePath_NonExistent_ReturnsEmpty()
    {
        var helper = CreateHelper();
        var lm = new Mock<ILibraryManager>();
        lm.Setup(m => m.GetVirtualFolders()).Returns([]);

        var query = Path.Join(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(helper.GetExistingTrashFoldersForPath(lm.Object, query));
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_AbsolutePath_Exists_ReturnsIt()
    {
        var helper = CreateHelper();
        var lm = new Mock<ILibraryManager>();
        lm.Setup(m => m.GetVirtualFolders()).Returns([]);

        var trash = Path.Join(Path.GetTempPath(), "jfh-trash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(trash);
        try
        {
            var result = helper.GetExistingTrashFoldersForPath(lm.Object, trash);
            Assert.Single(result);
            Assert.Equal(Path.GetFullPath(trash), result[0]);
        }
        finally
        {
            Directory.Delete(trash, recursive: true);
        }
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_AbsolutePath_MatchesLibraryRoot_ReturnsEmpty()
    {
        // Safety guard: if the trash query resolves to a real library root, the method must
        // report NO match so that downstream relocate/delete flows cannot wipe out the library.
        var helper = CreateHelper();
        var libraryRoot = Path.Join(Path.GetTempPath(), "jfh-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryRoot);
        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "Movies", Locations = [libraryRoot] }
            ]);

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, libraryRoot);
            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_RelativePath_ResolvesPerLibrary()
    {
        var helper = CreateHelper();
        var lib1 = Path.Join(Path.GetTempPath(), "jfh-l1-" + Guid.NewGuid().ToString("N"));
        var lib2 = Path.Join(Path.GetTempPath(), "jfh-l2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(lib1, ".trash")); // only lib1 has a real trash
        Directory.CreateDirectory(lib2);
        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "L1", Locations = [lib1] },
                new VirtualFolderInfo { Name = "L2", Locations = [lib2] }
            ]);

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, ".trash");
            Assert.Single(result);
            Assert.Equal(Path.GetFullPath(Path.Join(lib1, ".trash")), result[0]);
        }
        finally
        {
            if (Directory.Exists(lib1)) Directory.Delete(lib1, recursive: true);
            if (Directory.Exists(lib2)) Directory.Delete(lib2, recursive: true);
        }
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_RelativePathEscape_IsRejected()
    {
        // "../../etc" tries to escape the library root - must never be reported as valid trash.
        //
        // To make the test meaningful we ALSO materialise the target of the escape (a
        // sibling directory next to the library root that actually exists). Without this
        // extra step the test could pass simply because the resolved path happens not to
        // exist on the CI runner, which would let a regression that dropped the containment
        // check ship silently. With the sibling in place, only the containment guard can
        // keep this test green.
        var helper = CreateHelper();
        var parent = Path.Join(Path.GetTempPath(), "jfh-esc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var lib = Path.Join(parent, "library");
        var escapedSibling = Path.Join(parent, "outside-lib");
        Directory.CreateDirectory(lib);
        Directory.CreateDirectory(escapedSibling);

        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "L", Locations = [lib] }
            ]);

            // Craft a relative path that ACTUALLY resolves to escapedSibling from lib.
            // e.g. "../outside-lib" resolves out of the library root - the guard must
            // still refuse to report it as a trash candidate even though the destination
            // path physically exists.
            var relativeEscape = ".." + Path.DirectorySeparatorChar + "outside-lib";

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, relativeEscape);
            Assert.Empty(result);

            // Sanity: prove the target directory really is reachable via that relative path.
            // If Path.GetRelativePath ever changes semantics, this Assert catches it before
            // the containment test degenerates into a vacuous "path doesn't exist" pass.
            var resolved = Path.GetFullPath(Path.Combine(lib, relativeEscape));
            Assert.Equal(Path.GetFullPath(escapedSibling), resolved);
            Assert.True(Directory.Exists(resolved), "escaped sibling must exist so the test isn't vacuous");
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("/media/film_collections/Movies")]
    [InlineData("/collections_archive/TV")]
    [InlineData("/my-collections-backup/data")]
    public void GetFilteredLibraryLocations_PathWithCollectionsSubstring_NotExcluded(string location)
    {
        var helper = CreateHelper();
        var lm = new Mock<ILibraryManager>();
        lm.Setup(m => m.GetVirtualFolders()).Returns([
            new VirtualFolderInfo
            {
                Name = "Movies",
                CollectionType = CollectionTypeOptions.movies,
                Locations = [location]
            }
        ]);

        var result = helper.GetFilteredLibraryLocations(lm.Object);

        Assert.Contains(location, result);
    }

    [Theory]
    [InlineData("/config/data/collections")]
    [InlineData("/config/data/collections/")]
    [InlineData("/jellyfin/data/collections/metadata")]
    public void GetFilteredLibraryLocations_PathWithExactCollectionsSegment_IsExcluded(string location)
    {
        var helper = CreateHelper();
        var lm = new Mock<ILibraryManager>();
        lm.Setup(m => m.GetVirtualFolders()).Returns([
            new VirtualFolderInfo
            {
                Name = "Movies",
                CollectionType = CollectionTypeOptions.movies,
                Locations = [location]
            }
        ]);

        var result = helper.GetFilteredLibraryLocations(lm.Object);

        Assert.DoesNotContain(location, result);
    }

    // ===== IsCollectionsPath =====

    [Theory]
    [InlineData("/config/data/collections", true)]
    [InlineData("/config/data/collections/", true)]
    [InlineData("/jellyfin/data/collections/metadata", true)]
    [InlineData("/media/Collections/movies", true)]         // case-insensitive
    [InlineData("/media/COLLECTIONS", true)]                // case-insensitive uppercase
    [InlineData(@"C:\jellyfin\data\collections", true)]     // Windows backslash separator
    [InlineData(@"C:\jellyfin\data\collections\artwork", true)]
    [InlineData("/media/movies", false)]
    [InlineData("/media/mycollectionsabc", false)]          // not a segment-exact match
    [InlineData("/media/collections-extra", false)]         // hyphenated word is not the same segment
    [InlineData("", false)]
    public void IsCollectionsPath_SegmentExactMatch_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, CleanupConfigHelper.IsCollectionsPath(path));
    }

    // ===== IsFileOldEnoughForDeletion pre-1980 guard =====

    [Fact]
    public void IsFileOldEnoughForDeletion_Pre1980Timestamp_ReturnsFalse()
    {
        // A file whose timestamps predate 1980 is treated as corrupted (FAT epoch / clock drift).
        // Mirrors the directory pre-1980 guard: a bogus old timestamp must not make the file
        // look arbitrarily old and thus immediately eligible for deletion.
        var cfg = new PluginConfiguration { OrphanMinAgeDays = 1 };
        var helper = CreateHelper(cfg);
        var tempFile = Path.GetTempFileName();
        try
        {
            File.SetCreationTimeUtc(tempFile, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(tempFile, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.False(helper.IsFileOldEnoughForDeletion(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ===== GetTrashPath unresolvable-path fallbacks =====

    [Fact]
    public void GetTrashPath_AbsolutePathThatCannotBeResolved_FallsBackToDefault()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        // Build a genuinely fully-qualified path off the real filesystem root ("C:\" on Windows,
        // "/" on Linux) and embed a NUL. IsPathFullyQualified stays true on both OSes, but the NUL
        // makes GetFullPath throw ArgumentException inside the absolute branch on both platforms.
        // ("C:\<40000 chars>" is only fully-qualified-and-throwing on Windows; on Linux it is a
        // valid relative name, so it would take the relative branch and never hit the fallback.)
        var fsRoot = Path.GetPathRoot(Path.GetFullPath(root))!;
        var cfg = new PluginConfiguration { TrashFolderPath = Path.Join(fsRoot, "bad\0dir") };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        Assert.Equal(Path.GetFullPath(Path.Join(root, ".jellyfin-trash")), result);
    }

    [Fact]
    public void GetTrashPath_RelativePathThatCannotBeResolved_FallsBackToDefault()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        // A NUL char keeps the path relative (IsPathFullyQualified false) but makes
        // GetFullPath throw ArgumentException in the relative branch - must fall back safely.
        var cfg = new PluginConfiguration { TrashFolderPath = "bad\0dir" };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath(root);
        Assert.Equal(Path.GetFullPath(Path.Join(root, ".jellyfin-trash")), result);
    }

    // ===== GetExistingTrashFoldersForPath invalid-path handling =====

    [Fact]
    public void GetExistingTrashFoldersForPath_UnresolvableLibraryRoot_IsSkipped_AndValidResultReturned()
    {
        // One library root is a NUL-containing string that fails root normalization; it must be
        // skipped without aborting the scan, so a valid absolute trash query still resolves.
        var helper = CreateHelper();
        var trash = Path.Join(Path.GetTempPath(), "jfh-trash-" + Guid.NewGuid().ToString("N"));
        var realLib = Path.Join(Path.GetTempPath(), "jfh-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(trash);
        Directory.CreateDirectory(realLib);
        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "Broken", Locations = ["bad\0root"] },
                new VirtualFolderInfo { Name = "Movies", Locations = [realLib] }
            ]);

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, trash);
            Assert.Single(result);
            Assert.Equal(Path.GetFullPath(trash), result[0]);
        }
        finally
        {
            Directory.Delete(trash, recursive: true);
            Directory.Delete(realLib, recursive: true);
        }
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_AbsoluteQueryThatCannotBeResolved_ReturnsEmpty()
    {
        // Fully-qualified over-long query throws PathTooLongException while resolving the query;
        // the invalid path is skipped silently and yields no candidates.
        var helper = CreateHelper();
        var realLib = Path.Join(Path.GetTempPath(), "jfh-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(realLib);
        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "Movies", Locations = [realLib] }
            ]);

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, "C:\\" + new string('a', 40000));
            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(realLib, recursive: true);
        }
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_RelativeQuery_ExcludesMusicAndBoxsetLibraries()
    {
        // Relative trash resolution must skip non-video library types entirely, even when they
        // physically contain the trash subfolder - only the movies library should be scanned.
        var helper = CreateHelper();
        var music = Path.Join(Path.GetTempPath(), "jfh-music-" + Guid.NewGuid().ToString("N"));
        var boxset = Path.Join(Path.GetTempPath(), "jfh-box-" + Guid.NewGuid().ToString("N"));
        var movies = Path.Join(Path.GetTempPath(), "jfh-mov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(music, ".trash"));
        Directory.CreateDirectory(Path.Join(boxset, ".trash"));
        Directory.CreateDirectory(Path.Join(movies, ".trash"));
        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "Music", CollectionType = CollectionTypeOptions.music, Locations = [music] },
                new VirtualFolderInfo { Name = "Box", CollectionType = CollectionTypeOptions.boxsets, Locations = [boxset] },
                new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies, Locations = [movies] }
            ]);

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, ".trash");
            Assert.Single(result);
            Assert.Equal(Path.GetFullPath(Path.Join(movies, ".trash")), result[0]);
        }
        finally
        {
            Directory.Delete(music, recursive: true);
            Directory.Delete(boxset, recursive: true);
            Directory.Delete(movies, recursive: true);
        }
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_RelativeQuery_ExcludesLibrariesNamedLikeCollections()
    {
        // Name-pattern fallback: a video-typed library whose name contains "collection" is filtered
        // out of relative trash resolution even though its CollectionType is not music/boxset.
        var helper = CreateHelper();
        var named = Path.Join(Path.GetTempPath(), "jfh-named-" + Guid.NewGuid().ToString("N"));
        var movies = Path.Join(Path.GetTempPath(), "jfh-mov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(named, ".trash"));
        Directory.CreateDirectory(Path.Join(movies, ".trash"));
        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "My Collection", CollectionType = CollectionTypeOptions.movies, Locations = [named] },
                new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies, Locations = [movies] }
            ]);

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, ".trash");
            Assert.Single(result);
            Assert.Equal(Path.GetFullPath(Path.Join(movies, ".trash")), result[0]);
        }
        finally
        {
            Directory.Delete(named, recursive: true);
            Directory.Delete(movies, recursive: true);
        }
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_RelativeQuery_ExcludesUserExcludedLibraries()
    {
        // The configured exclude list must apply to relative trash resolution too.
        var cfg = new PluginConfiguration { ExcludedLibraries = "TV Shows" };
        var helper = CreateHelper(cfg);
        var excluded = Path.Join(Path.GetTempPath(), "jfh-excl-" + Guid.NewGuid().ToString("N"));
        var movies = Path.Join(Path.GetTempPath(), "jfh-mov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(excluded, ".trash"));
        Directory.CreateDirectory(Path.Join(movies, ".trash"));
        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "TV Shows", CollectionType = CollectionTypeOptions.tvshows, Locations = [excluded] },
                new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies, Locations = [movies] }
            ]);

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, ".trash");
            Assert.Single(result);
            Assert.Equal(Path.GetFullPath(Path.Join(movies, ".trash")), result[0]);
        }
        finally
        {
            Directory.Delete(excluded, recursive: true);
            Directory.Delete(movies, recursive: true);
        }
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_RelativeResolvesToAnotherLibraryRoot_IsSkipped()
    {
        // A relative trash path that stays within library A but coincides with a SECOND library's
        // root must never be reported - otherwise relocate/delete could target that whole library.
        var helper = CreateHelper();
        var parent = Path.Join(Path.GetTempPath(), "jfh-par-" + Guid.NewGuid().ToString("N"));
        var libA = Path.Join(parent, "A");
        var libTrash = Path.Join(libA, "trash"); // a real library registered here
        Directory.CreateDirectory(libTrash);
        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "A", CollectionType = CollectionTypeOptions.movies, Locations = [libA] },
                new VirtualFolderInfo { Name = "Trash", CollectionType = CollectionTypeOptions.movies, Locations = [libTrash] }
            ]);

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, "trash");
            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void GetExistingTrashFoldersForPath_RelativeQueryThatCannotBeResolved_IsSkipped()
    {
        // A NUL-containing relative query makes per-library GetFullPath throw ArgumentException;
        // the library is skipped silently rather than throwing out of the scan.
        var helper = CreateHelper();
        var lib = Path.Join(Path.GetTempPath(), "jfh-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lib);
        try
        {
            var lm = new Mock<ILibraryManager>();
            lm.Setup(m => m.GetVirtualFolders()).Returns([
                new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies, Locations = [lib] }
            ]);

            var result = helper.GetExistingTrashFoldersForPath(lm.Object, "bad\0trash");
            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(lib, recursive: true);
        }
    }
}
