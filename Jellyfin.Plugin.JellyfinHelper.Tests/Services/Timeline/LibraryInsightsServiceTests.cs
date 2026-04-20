using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

public class LibraryInsightsServiceTests
{
    // -- DetermineChangeType ----------------------------------------

    [Fact]
    public void DetermineChangeType_SameTimestamps_ReturnsAdded()
    {
        var ts = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("added", LibraryInsightsService.DetermineChangeType(ts, ts));
    }

    [Fact]
    public void DetermineChangeType_DiffLessThanThreshold_ReturnsAdded()
    {
        var created = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var modified = created.AddMinutes(30);
        Assert.Equal("added", LibraryInsightsService.DetermineChangeType(created, modified));
    }

    [Fact]
    public void DetermineChangeType_DiffExactlyAtThreshold_ReturnsChanged()
    {
        var created = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var modified = created.AddHours(LibraryInsightsService.AddedVsChangedThresholdHours);
        Assert.Equal("changed", LibraryInsightsService.DetermineChangeType(created, modified));
    }

    [Fact]
    public void DetermineChangeType_DiffAboveThreshold_ReturnsChanged()
    {
        var created = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var modified = created.AddDays(5);
        Assert.Equal("changed", LibraryInsightsService.DetermineChangeType(created, modified));
    }

    [Fact]
    public void DetermineChangeType_ModifiedBeforeCreated_UsesAbsoluteDiff()
    {
        // Edge case: modified is before created (filesystem quirk)
        var created = new DateTime(2025, 6, 5, 12, 0, 0, DateTimeKind.Utc);
        var modified = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("changed", LibraryInsightsService.DetermineChangeType(created, modified));
    }

    // -- BuildResult: Empty input -----------------------------------

    [Fact]
    public void BuildResult_EmptyList_ReturnsEmptyResult()
    {
        var result = LibraryInsightsService.BuildResult(new List<LibraryInsightEntry>());

        Assert.Empty(result.Largest);
        Assert.Empty(result.Recent);
        Assert.Empty(result.LibrarySizes);
        Assert.Equal(0, result.LargestTotalSize);
        Assert.Equal(0, result.RecentTotalCount);
    }

    // -- BuildResult: Largest sorting & limits ----------------------

    [Fact]
    public void BuildResult_LargestEntriesSortedDescending()
    {
        var entries = new List<LibraryInsightEntry>
        {
            MakeEntry("Small", "movies", 100_000_000),
            MakeEntry("Large", "movies", 900_000_000),
            MakeEntry("Medium", "movies", 500_000_000)
        };

        var result = LibraryInsightsService.BuildResult(entries);

        Assert.Equal("Large", result.Largest[0].Name);
        Assert.Equal("Medium", result.Largest[1].Name);
        Assert.Equal("Small", result.Largest[2].Name);
    }

    [Fact]
    public void BuildResult_LargestTotalSizeIsSum()
    {
        var entries = new List<LibraryInsightEntry>
        {
            MakeEntry("A", "movies", 100),
            MakeEntry("B", "tvshows", 200),
            MakeEntry("C", "movies", 300)
        };

        var result = LibraryInsightsService.BuildResult(entries);

        Assert.Equal(600, result.LargestTotalSize);
    }

    [Fact]
    public void BuildResult_LargestLimitsPerType()
    {
        // Create 15 movie entries and 15 tvshow entries — should be limited to 10 each
        var entries = new List<LibraryInsightEntry>();
        for (int i = 0; i < 15; i++)
        {
            entries.Add(MakeEntry($"Movie{i}", "movies", (i + 1) * 1_000_000L));
            entries.Add(MakeEntry($"Show{i}", "tvshows", (i + 1) * 1_000_000L));
        }

        var result = LibraryInsightsService.BuildResult(entries);

        var movieCount = result.Largest.Count(e =>
            string.Equals(e.CollectionType, "movies", StringComparison.OrdinalIgnoreCase));
        var showCount = result.Largest.Count(e =>
            string.Equals(e.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(LibraryInsightsService.TopLargestPerType, movieCount);
        Assert.Equal(LibraryInsightsService.TopLargestPerType, showCount);
    }

    // -- BuildResult: Recent entries --------------------------------

    [Fact]
    public void BuildResult_RecentIncludesEntriesWithinWindow()
    {
        var now = DateTime.UtcNow;
        var entries = new List<LibraryInsightEntry>
        {
            MakeEntry("Recent", "movies", 100, createdUtc: now.AddDays(-5), changeType: "added"),
            MakeEntry("Old", "movies", 200, createdUtc: now.AddDays(-60), changeType: "added")
        };

        var result = LibraryInsightsService.BuildResult(entries);

        Assert.Single(result.Recent);
        Assert.Equal("Recent", result.Recent[0].Name);
        Assert.Equal(1, result.RecentTotalCount);
    }

    [Fact]
    public void BuildResult_RecentUsesModifiedForChanged()
    {
        var now = DateTime.UtcNow;

        // Entry created 90 days ago, but modified 2 days ago (changed)
        var entries = new List<LibraryInsightEntry>
        {
            new LibraryInsightEntry
            {
                Name = "Edited",
                Size = 500,
                CreatedUtc = now.AddDays(-90),
                ModifiedUtc = now.AddDays(-2),
                LibraryName = "Movies",
                CollectionType = "movies",
                ChangeType = "changed"
            }
        };

        var result = LibraryInsightsService.BuildResult(entries);

        Assert.Single(result.Recent);
        Assert.Equal("Edited", result.Recent[0].Name);
    }

    [Fact]
    public void BuildResult_RecentSortedByDateDescending()
    {
        var now = DateTime.UtcNow;
        var entries = new List<LibraryInsightEntry>
        {
            MakeEntry("Older", "movies", 100, createdUtc: now.AddDays(-10), changeType: "added"),
            MakeEntry("Newest", "movies", 200, createdUtc: now.AddDays(-1), changeType: "added"),
            MakeEntry("Middle", "movies", 300, createdUtc: now.AddDays(-5), changeType: "added")
        };

        var result = LibraryInsightsService.BuildResult(entries);

        Assert.Equal("Newest", result.Recent[0].Name);
        Assert.Equal("Middle", result.Recent[1].Name);
        Assert.Equal("Older", result.Recent[2].Name);
    }

    // -- BuildResult: Library sizes ---------------------------------

    [Fact]
    public void BuildResult_LibrarySizesGroupedCorrectly()
    {
        var entries = new List<LibraryInsightEntry>
        {
            MakeEntry("A", "movies", 100, libraryName: "Films"),
            MakeEntry("B", "movies", 200, libraryName: "Films"),
            MakeEntry("C", "tvshows", 300, libraryName: "TV"),
            MakeEntry("D", "tvshows", 400, libraryName: "TV")
        };

        var result = LibraryInsightsService.BuildResult(entries);

        Assert.Equal(2, result.LibrarySizes.Count);
        Assert.Equal(300, result.LibrarySizes["Films"]);
        Assert.Equal(700, result.LibrarySizes["TV"]);
    }

    [Fact]
    public void BuildResult_LibrarySizesCaseInsensitive()
    {
        var entries = new List<LibraryInsightEntry>
        {
            MakeEntry("A", "movies", 100, libraryName: "Movies"),
            MakeEntry("B", "movies", 200, libraryName: "movies")
        };

        var result = LibraryInsightsService.BuildResult(entries);

        Assert.Single(result.LibrarySizes);
        Assert.Equal(300, result.LibrarySizes.Values.First());
    }

    // -- BuildResult: Mixed collection types ------------------------

    [Fact]
    public void BuildResult_MixedCollectionTypesIncludedInLargest()
    {
        var entries = new List<LibraryInsightEntry>
        {
            MakeEntry("MixedItem", "mixed", 500_000_000)
        };

        var result = LibraryInsightsService.BuildResult(entries);

        Assert.Single(result.Largest);
        Assert.Equal("MixedItem", result.Largest[0].Name);
    }

    [Fact]
    public void BuildResult_ComputedAtUtcIsSet()
    {
        var before = DateTime.UtcNow;
        var result = LibraryInsightsService.BuildResult(new List<LibraryInsightEntry>());
        var after = DateTime.UtcNow;

        Assert.InRange(result.ComputedAtUtc, before, after);
    }

    [Fact]
    public void BuildResult_RecentLimitedToMaxRecentEntries()
    {
        var now = DateTime.UtcNow;
        var entries = new List<LibraryInsightEntry>();
        for (int i = 0; i < LibraryInsightsService.MaxRecentEntries + 50; i++)
        {
            entries.Add(MakeEntry(
                $"Item{i}",
                "movies",
                1000 + i,
                createdUtc: now.AddDays(-1).AddMinutes(-i),
                changeType: "added"));
        }

        var result = LibraryInsightsService.BuildResult(entries);

        Assert.Equal(LibraryInsightsService.MaxRecentEntries + 50, result.RecentTotalCount);
        Assert.Equal(LibraryInsightsService.MaxRecentEntries, result.Recent.Count);
    }

    // -- Constants --------------------------------------------------

    [Fact]
    public void Constants_RecentDaysWindow_IsPositive()
    {
        Assert.True(LibraryInsightsService.RecentDaysWindow > 0);
    }

    [Fact]
    public void Constants_TopLargestPerType_IsPositive()
    {
        Assert.True(LibraryInsightsService.TopLargestPerType > 0);
    }

    [Fact]
    public void Constants_AddedVsChangedThresholdHours_IsPositive()
    {
        Assert.True(LibraryInsightsService.AddedVsChangedThresholdHours > 0);
    }

    // -- ComputeInsightsAsync integration tests ---------------------

    [Fact]
    public async Task ComputeInsightsAsync_ScansDirectoriesAndReturnsEntries()
    {
        // Arrange — create a real temp directory so Directory.GetCreationTimeUtc works
        using var tempDir = new TempDirectory();
        tempDir.CreateSubDirectory("My Movie (2025)");
        tempDir.CreateFile("My Movie (2025)/movie.mkv", 500_000);

        var service = CreateServiceWithSingleLibrary(tempDir.Path, "Movies", CollectionTypeOptions.movies);

        // Act
        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        // Assert
        Assert.NotEmpty(result.Largest);
        Assert.Contains(result.Largest, e => e.Name == "My Movie (2025)");
        Assert.True(result.Largest[0].Size > 0);
        Assert.Equal("Movies", result.Largest[0].LibraryName);
    }

    [Fact]
    public async Task ComputeInsightsAsync_SkipsTrickplayDirectories()
    {
        using var tempDir = new TempDirectory();
        tempDir.CreateSubDirectory("trickplay");
        tempDir.CreateFile("trickplay/some.bin", 1000);
        tempDir.CreateSubDirectory("Real Movie");
        tempDir.CreateFile("Real Movie/movie.mkv", 100_000);

        var service = CreateServiceWithSingleLibrary(tempDir.Path, "Movies", CollectionTypeOptions.movies);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        Assert.DoesNotContain(result.Largest, e =>
            string.Equals(e.Name, "trickplay", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Largest, e => e.Name == "Real Movie");
    }

    [Fact]
    public async Task ComputeInsightsAsync_SkipsTrashFolderDirectories()
    {
        using var tempDir = new TempDirectory();
        tempDir.CreateSubDirectory(".jellyfin-helper-trash");
        tempDir.CreateFile(".jellyfin-helper-trash/deleted.mkv", 1000);
        tempDir.CreateSubDirectory("Real Movie");
        tempDir.CreateFile("Real Movie/movie.mkv", 100_000);

        var service = CreateServiceWithSingleLibrary(
            tempDir.Path, "Movies", CollectionTypeOptions.movies,
            trashFolderPath: ".jellyfin-helper-trash");

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        Assert.DoesNotContain(result.Largest, e =>
            string.Equals(e.Name, ".jellyfin-helper-trash", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Largest, e => e.Name == "Real Movie");
    }

    [Fact]
    public async Task ComputeInsightsAsync_SkipsMusicLibraries()
    {
        using var tempDir = new TempDirectory();
        tempDir.CreateSubDirectory("Album");
        tempDir.CreateFile("Album/track.flac", 50_000);

        var service = CreateServiceWithSingleLibrary(tempDir.Path, "Music", CollectionTypeOptions.music);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        Assert.Empty(result.Largest);
    }

    [Fact]
    public async Task ComputeInsightsAsync_HandlesIOException_Gracefully()
    {
        // Arrange — set up a location that triggers IOException on GetDirectories
        var libraryManager = TestMockFactory.CreateLibraryManager();
        var fileSystem = TestMockFactory.CreateFileSystem();
        var configHelper = TestMockFactory.CreateCleanupConfigHelper();
        var pluginLog = TestMockFactory.CreatePluginLogService();
        var logger = TestMockFactory.CreateLogger<LibraryInsightsService>();

        var fakeLocation = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new VirtualFolderInfo
            {
                Name = "Movies",
                Locations = [fakeLocation],
                CollectionType = CollectionTypeOptions.movies
            }
        });

        fileSystem.Setup(fs => fs.GetDirectories(fakeLocation, It.IsAny<bool>()))
            .Throws(new IOException("Access denied"));

        var service = new LibraryInsightsService(
            libraryManager.Object, fileSystem.Object, configHelper.Object, pluginLog, logger.Object);

        // Act — should not throw
        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        // Assert
        Assert.Empty(result.Largest);
        Assert.Empty(result.Recent);
    }

    [Fact]
    public async Task ComputeInsightsAsync_EmptyLibrary_ReturnsEmptyResult()
    {
        using var tempDir = new TempDirectory();
        // Empty directory — no subdirectories, no files

        var service = CreateServiceWithSingleLibrary(tempDir.Path, "Movies", CollectionTypeOptions.movies);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        Assert.Empty(result.Largest);
        Assert.Empty(result.Recent);
        Assert.Equal(0, result.LargestTotalSize);
    }

    [Fact]
    public async Task ComputeInsightsAsync_CollectsLooseVideoFiles()
    {
        using var tempDir = new TempDirectory();
        tempDir.CreateFile("standalone_movie.mp4", 200_000);

        var service = CreateServiceWithSingleLibrary(tempDir.Path, "Movies", CollectionTypeOptions.movies);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        Assert.Contains(result.Largest, e => e.Name == "standalone_movie");
    }

    [Fact]
    public async Task ComputeInsightsAsync_CancellationToken_IsRespected()
    {
        using var tempDir = new TempDirectory();
        tempDir.CreateSubDirectory("Movie1");
        tempDir.CreateFile("Movie1/movie.mkv", 100_000);

        var service = CreateServiceWithSingleLibrary(tempDir.Path, "Movies", CollectionTypeOptions.movies);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ComputeInsightsAsync(cts.Token));
    }

    // -- Helper: create service with real filesystem for a single library --

    private static LibraryInsightsService CreateServiceWithSingleLibrary(
        string locationPath,
        string libraryName,
        CollectionTypeOptions? collectionType,
        string? trashFolderPath = null)
    {
        var libraryManager = TestMockFactory.CreateLibraryManager();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new VirtualFolderInfo
            {
                Name = libraryName,
                Locations = [locationPath],
                CollectionType = collectionType
            }
        });

        // Use a real-filesystem-backed IFileSystem mock that delegates to actual IO
        var fileSystem = new Mock<MediaBrowser.Model.IO.IFileSystem>();
        fileSystem.Setup(fs => fs.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, bool>((path, _) =>
            {
                if (!Directory.Exists(path)) return [];
                return Directory.GetDirectories(path)
                    .Select(d => new FileSystemMetadata { FullName = d, IsDirectory = true })
                    .ToArray();
            });
        fileSystem.Setup(fs => fs.GetFiles(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, bool>((path, _) =>
            {
                if (!Directory.Exists(path)) return [];
                return Directory.GetFiles(path)
                    .Select(f => new FileSystemMetadata
                    {
                        FullName = f,
                        IsDirectory = false,
                        Length = new FileInfo(f).Length
                    })
                    .ToArray();
            });

        var config = new PluginConfiguration();
        if (trashFolderPath != null)
        {
            config.TrashFolderPath = trashFolderPath;
        }

        var configHelper = TestMockFactory.CreateCleanupConfigHelper(config);
        var pluginLog = TestMockFactory.CreatePluginLogService();
        var logger = TestMockFactory.CreateLogger<LibraryInsightsService>();

        return new LibraryInsightsService(
            libraryManager.Object, fileSystem.Object, configHelper.Object, pluginLog, logger.Object);
    }

    // -- TempDirectory helper for filesystem-based tests --

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "jfh-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateSubDirectory(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Subdirectory name must be a non-empty relative path.", nameof(name));
            }

            if (System.IO.Path.IsPathRooted(name))
            {
                throw new ArgumentException("Subdirectory name must be a relative path.", nameof(name));
            }

            var dir = SafeCombinePath(name, nameof(name));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string CreateFile(string relativePath, long size)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("File path must be a non-empty relative path.", nameof(relativePath));
            }

            if (size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "File size cannot be negative.");
            }

            if (System.IO.Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("File path must be a relative path.", nameof(relativePath));
            }

            var fullPath = SafeCombinePath(relativePath, nameof(relativePath));
            var dir = System.IO.Path.GetDirectoryName(fullPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write a file of the requested size
            using var fs = File.Create(fullPath);
            fs.SetLength(size);

            return fullPath;
        }

        private string SafeCombinePath(string relativePath, string paramName)
        {
            var basePath = System.IO.Path.GetFullPath(Path);
            if (!basePath.EndsWith(System.IO.Path.DirectorySeparatorChar))
            {
                basePath += System.IO.Path.DirectorySeparatorChar;
            }

            var candidate = System.IO.Path.GetFullPath(System.IO.Path.Join(basePath, relativePath));
            if (!candidate.StartsWith(basePath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Path must stay within the temp directory.", paramName);
            }

            return candidate;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best effort cleanup
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup
            }
        }
    }

    // -- Helpers ----------------------------------------------------

    private static LibraryInsightEntry MakeEntry(
        string name,
        string collectionType,
        long size,
        DateTime? createdUtc = null,
        DateTime? modifiedUtc = null,
        string? libraryName = null,
        string changeType = "added")
    {
        var created = createdUtc ?? DateTime.UtcNow.AddDays(-10);
        return new LibraryInsightEntry
        {
            Name = name,
            Size = size,
            CreatedUtc = created,
            ModifiedUtc = modifiedUtc ?? created,
            LibraryName = libraryName ?? "TestLib",
            CollectionType = collectionType,
            ChangeType = changeType
        };
    }
}