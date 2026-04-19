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
    public void IsDryRun_DeactivateMode_ReturnsTrue()
    {
        Assert.True(CleanupConfigHelper.IsDryRun(TaskMode.Deactivate));
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
        var cfg = new PluginConfiguration { TrashFolderPath = "" };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath("/media/movies");
        Assert.Equal(Path.Join("/media/movies", ".jellyfin-trash"), result);
    }

    [Fact]
    public void GetTrashPath_DefaultsToJellyfinTrash_WhenWhitespace()
    {
        var cfg = new PluginConfiguration { TrashFolderPath = "   " };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath("/media/movies");
        Assert.Equal(Path.Join("/media/movies", ".jellyfin-trash"), result);
    }

    [Fact]
    public void GetTrashPath_RelativePath_JoinsWithLibraryRoot()
    {
        var cfg = new PluginConfiguration { TrashFolderPath = ".trash" };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath("/media/movies");
        Assert.Equal(Path.Join("/media/movies", ".trash"), result);
    }

    [Fact]
    public void GetTrashPath_AbsolutePath_ReturnsAsIs()
    {
        var absolutePath = Path.GetFullPath("/tmp/trash");
        var cfg = new PluginConfiguration { TrashFolderPath = absolutePath };
        var helper = CreateHelper(cfg);
        var result = helper.GetTrashPath("/media/movies");
        Assert.Equal(absolutePath, result);
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
    public void GetFilteredLibraryLocations_AppliesIncludeFilter()
    {
        var cfg = new PluginConfiguration { IncludedLibraries = "Movies" };
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
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
}