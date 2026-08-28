using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

/// <summary>
///     Extended tests for RecommendationCacheService that cover the defensive branches missed by RecommendationCacheServiceTests: null-argument guard, directory auto-creation when the DataPath does not yet exist, and load of a file containing literal "null".
/// </summary>
public sealed class RecommendationCacheServiceExtendedTests : IDisposable
{
    private readonly string _tempDir;

    public RecommendationCacheServiceExtendedTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-rec-cache-x-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void SaveResults_NullArgument_Throws()
    {
        // ArgumentNullException.ThrowIfNull must fire - otherwise a null argument would
        // silently write a "null" JSON literal to the cache file, corrupting the next Load.
        var service = CreateService(_tempDir);
        Assert.Throws<ArgumentNullException>(() => service.SaveResults(null!));
    }

    [Fact]
    public void SaveResults_NonExistentDirectory_CreatesItAutomatically()
    {
        // BUG GUARD: the service must auto-create the DataPath directory if it does not exist. Some Jellyfin installations start with an empty data folder tree that only gets materialised by the first cache write.
        var nested = Path.Join(_tempDir, "does", "not", "exist", "yet");
        Assert.False(Directory.Exists(nested));

        var service = CreateService(nested);
        var results = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "u" }
        };

        service.SaveResults(results);

        Assert.True(Directory.Exists(nested));
        var loaded = service.LoadResults();
        Assert.NotNull(loaded);
        Assert.Single(loaded!);
    }

    [Fact]
    public void LoadResults_FileContainsLiteralNull_ReturnsNullWithoutThrowing()
    {
        // BUG GUARD: JsonSerializer.Deserialize returns null for the literal "null" JSON value. The service must handle this without throwing (it logs a warning) - the caller then treats it as "no cache".
        var service = CreateService(_tempDir);
        // Seed an empty save so the cache file exists at the right path.
        service.SaveResults(new Collection<RecommendationResult>());
        var cacheFile = Directory.GetFiles(_tempDir, "*.json").Single();
        File.WriteAllText(cacheFile, "null");

        var result = service.LoadResults();

        Assert.Null(result);
    }

    [Fact]
    public void LoadResults_FileContainsEmptyArray_ReturnsEmptyList()
    {
        // Empty array is a valid state - must return an empty list, not null.
        var service = CreateService(_tempDir);
        service.SaveResults(new Collection<RecommendationResult>());
        var cacheFile = Directory.GetFiles(_tempDir, "*.json").Single();
        File.WriteAllText(cacheFile, "[]");

        var result = service.LoadResults();

        Assert.NotNull(result);
        Assert.Empty(result!);
    }
}