using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

/// <summary>
///     Error-handling tests for RecommendationCacheService that drive the broad save-side catch and the load-side IO catch by holding the on-disk cache file open with an exclusive lock.
/// </summary>
public sealed class RecommendationCacheServiceErrorHandlingTests : IDisposable
{
    private readonly string _tempDir;

    public RecommendationCacheServiceErrorHandlingTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-rec-cache-err-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private RecommendationCacheService CreateService(string dataPath)
    {
        var pathsMock = new Mock<IApplicationPaths>();
        pathsMock.Setup(p => p.DataPath).Returns(dataPath);
        var log = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<RecommendationCacheService>>();
        return new RecommendationCacheService(pathsMock.Object, log.Object, logger.Object);
    }

    [Fact]
    public void SaveResults_TargetFileLockedExclusively_SwallowsIoErrorAndDoesNotThrow()
    {
        // Seed a real cache so the destination exists; the second save's File.Replace on the locked destination fails every retry, and the final IOException must be swallowed by the broad catch instead of taking down the scheduled-task caller.
        var service = CreateService(_tempDir);
        var seed = new Collection<RecommendationResult>
        {
            new()
            {
                UserId = Guid.NewGuid(),
                UserName = "Seed",
                Recommendations = new Collection<RecommendedItem>
                {
                    new() { Name = "Seed Movie", Score = 0.5 }
                }
            }
        };
        service.SaveResults(seed);

        var cacheFile = Directory.GetFiles(_tempDir, "*.json").Single();
        var originalContent = File.ReadAllText(cacheFile);

        var results = new Collection<RecommendationResult>
        {
            new()
            {
                UserId = Guid.NewGuid(),
                UserName = "Later",
                Recommendations = new Collection<RecommendedItem>
                {
                    new() { Name = "Later Movie", Score = 0.9 }
                }
            }
        };

        if (OperatingSystem.IsWindows())
        {
            // On Windows an open FileShare.None handle makes AtomicFile's File.Replace onto the
            // locked destination throw IOException on every retry, exercising the broad save-side catch.
            using (new FileStream(cacheFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var exception = Record.Exception(() => service.SaveResults(results));
                Assert.Null(exception);
            }

            // The failed write must not have clobbered the previously-saved cache.
            Assert.Equal(originalContent, File.ReadAllText(cacheFile));
        }
        else
        {
            // POSIX has no mandatory locking, so a FileShare.None handle does NOT block a replace - the Windows path would let the save succeed on Linux.
            File.Delete(cacheFile);
            Directory.CreateDirectory(cacheFile);

            var exception = Record.Exception(() => service.SaveResults(results));
            Assert.Null(exception);

            // The save failed and swallowed the error: the blocking directory is untouched and no
            // stray cache file was written in its place.
            Assert.True(Directory.Exists(cacheFile));
            Assert.False(File.Exists(cacheFile));
        }
    }

    [Fact]
    public void LoadResults_CacheFileLockedExclusively_ReturnsNull()
    {
        // File.Exists is true, but File.ReadAllText throws IOException under an exclusive lock;
        // the read failure must degrade to null ("no cache") rather than propagate.
        var service = CreateService(_tempDir);
        service.SaveResults(new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "u" }
        });

        var cacheFile = Directory.GetFiles(_tempDir, "*.json").Single();

        using (new FileStream(cacheFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(service.LoadResults());
        }
    }
}
