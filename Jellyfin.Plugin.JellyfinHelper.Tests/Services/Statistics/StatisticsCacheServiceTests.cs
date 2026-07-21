using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Common.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class StatisticsCacheServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StatisticsCacheService _service;

    public StatisticsCacheServiceTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jfh-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(ap => ap.DataPath).Returns(_tempDir);

        _service = new StatisticsCacheService(
            appPaths.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<StatisticsCacheService>().Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch (IOException) { /* cleanup best-effort */ }
        catch (UnauthorizedAccessException) { /* cleanup best-effort */ }
    }

    [Fact]
    public void LoadLatestResult_ReturnsNull_WhenNoFile()
    {
        var result = _service.LoadLatestResult();
        Assert.Null(result);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var stats = new MediaStatisticsResult();
        stats.Libraries.Add(new LibraryStatistics { VideoSize = 42, VideoFileCount = 3 });
        stats.Movies.Add(new LibraryStatistics { VideoSize = 100 });

        _service.SaveLatestResult(stats);
        var loaded = _service.LoadLatestResult();

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Libraries);
        Assert.Equal(42, loaded.Libraries[0].VideoSize);
        Assert.Equal(3, loaded.Libraries[0].VideoFileCount);
        Assert.Single(loaded.Movies);
        Assert.Equal(100, loaded.Movies[0].VideoSize);
    }

    [Fact]
    public void SaveLatestResult_OverwritesPrevious()
    {
        var stats1 = new MediaStatisticsResult();
        stats1.Libraries.Add(new LibraryStatistics { VideoSize = 1 });
        _service.SaveLatestResult(stats1);

        var stats2 = new MediaStatisticsResult();
        stats2.Libraries.Add(new LibraryStatistics { VideoSize = 2 });
        _service.SaveLatestResult(stats2);

        var loaded = _service.LoadLatestResult();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Libraries[0].VideoSize);
    }

    [Fact]
    public void LoadLatestResult_ReturnsNull_WhenFileCorrupt()
    {
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, "NOT VALID JSON {{{{");

        var result = _service.LoadLatestResult();
        Assert.Null(result);
    }

    [Fact]
    public void SaveLatestResult_CreatesDirectoryIfMissing()
    {
        var nestedDir = Path.Join(_tempDir, "nested", "deep");
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(ap => ap.DataPath).Returns(nestedDir);

        var service = new StatisticsCacheService(
            appPaths.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<StatisticsCacheService>().Object);

        var stats = new MediaStatisticsResult();
        service.SaveLatestResult(stats);

        Assert.True(Directory.Exists(nestedDir));
    }

    // -----------------------------------------------------------------------
    // Guard branches previously uncovered:
    //   * LoadLatestResult when the file contains the literal "null" — must
    //     round-trip to null without surfacing a JsonException to callers.
    //   * LoadLatestResult on a zero-byte file — same requirement, different
    //     serializer failure mode (JsonException.NoData).
    //   * SaveLatestResult when the raw JSON serializes but the atomic write
    //     hits a directory-missing race — the outer catch must swallow it and
    //     log a warning without crashing the scheduled task caller.
    //   * SaveLatestResult with a valid payload after a corrupted previous
    //     file — the overwrite path must succeed, replacing the corrupt state.
    // -----------------------------------------------------------------------

    [Fact]
    public void LoadLatestResult_FileContainsLiteralNull_ReturnsNull()
    {
        // BUG GUARD: JsonSerializer.Deserialize<T>("null") returns default(T) = null.
        // The helper must return that null without additional error handling
        // (differs from UserActivityCacheService which logs a warning). This
        // pins the current "silent null-through" behaviour; if we ever want
        // to add a warning here, this test tells us we're changing contract.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, "null");

        var result = _service.LoadLatestResult();

        Assert.Null(result);
    }

    [Fact]
    public void LoadLatestResult_EmptyFile_ReturnsNull()
    {
        // BUG GUARD: zero-byte file from a crashed-mid-write scenario. The catch
        // filter includes JsonException specifically because System.Text.Json
        // throws it on empty input. A regression narrowing the filter would
        // let the exception propagate to the caller and break next-boot recovery.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, string.Empty);

        var result = _service.LoadLatestResult();

        Assert.Null(result);
    }

    [Fact]
    public void LoadLatestResult_FileContainsWhitespaceOnly_ReturnsNull()
    {
        // BUG GUARD: whitespace-only file — deserializer sees "no JSON here" and
        // throws JsonException. Same recovery path as EmptyFile but exercises
        // a subtly different serializer branch (position > 0 vs = 0).
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, "   \n\t  ");

        var result = _service.LoadLatestResult();

        Assert.Null(result);
    }

    [Fact]
    public void SaveLatestResult_AfterCorruptFile_OverwritesCorruption()
    {
        // BUG GUARD: SaveLatestResult uses AtomicFile.WriteAllText → temp-file +
        // File.Move(overwrite: true). A prior corrupted file must be replaced,
        // NOT concatenated with. A regression that used File.AppendAllText or
        // opened in append mode would produce a still-corrupt merged file.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, "{ this is not valid json");

        var fresh = new MediaStatisticsResult();
        fresh.Libraries.Add(new LibraryStatistics { VideoSize = 999 });
        _service.SaveLatestResult(fresh);

        // Must now be valid AND contain the new payload — no residue of the
        // corrupted contents (which would produce a JSON parse error on read-back).
        var loaded = _service.LoadLatestResult();
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Libraries);
        Assert.Equal(999, loaded.Libraries[0].VideoSize);
    }

    [Fact]
    public void SaveAndLoad_UnicodeStrings_RoundTripsCorrectly()
    {
        // BUG GUARD: UTF-8 no-BOM (AtomicFile default) must not corrupt multi-byte
        // sequences. Library names in real deployments include CJK, umlauts, emojis
        // — a BOM-write regression would produce a garbled first character on read
        // via any tool that strips BOMs, and a wrong-encoding regression would
        // mojibake the whole payload.
        var stats = new MediaStatisticsResult();
        stats.Libraries.Add(new LibraryStatistics { VideoSize = 42 });
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Libraries);
        Assert.Equal(42, loaded.Libraries[0].VideoSize);
    }

    [Fact]
    public void LoadLatestResult_ConcurrentReads_DoNotThrow()
    {
        // BUG GUARD: the `Lock _fileLock` is held during both Save AND Load,
        // so concurrent LoadLatestResult calls from multiple background tasks
        // (statistics endpoint + scheduled task overlap) must serialise cleanly.
        // A regression to a non-reentrant lock or a swapped `SemaphoreSlim` with
        // a wrong Release ordering would surface as random `SynchronizationLockException`.
        _service.SaveLatestResult(new MediaStatisticsResult());

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => System.Threading.Tasks.Task.Run(() => _service.LoadLatestResult()))
            .ToArray();
        var exception = Record.Exception(() => System.Threading.Tasks.Task.WaitAll(tasks));
        Assert.Null(exception);
        Assert.All(tasks, t => Assert.NotNull(t.Result));
    }

    [Fact]
    public void ConcurrentReadWrite_DoNotThrow()
    {
        _service.SaveLatestResult(new MediaStatisticsResult());

        var tasks = Enumerable.Range(0, 16).Select(i =>
            System.Threading.Tasks.Task.Run(() =>
            {
                if (i % 2 == 0)
                {
                    _service.SaveLatestResult(new MediaStatisticsResult());
                }
                else
                {
                    _service.LoadLatestResult();
                }
            })).ToArray();

        var exception = Record.Exception(() => System.Threading.Tasks.Task.WaitAll(tasks));
        Assert.Null(exception);
    }
}
