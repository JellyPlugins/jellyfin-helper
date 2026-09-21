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
    public void LoadLatestResult_InvalidDataPath_ReturnsNullInsteadOfThrowing()
    {
        // A DataPath the OS rejects (embedded null) must degrade to null like Save does.
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(ap => ap.DataPath).Returns("\0invalid");

        var service = new StatisticsCacheService(
            appPaths.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<StatisticsCacheService>().Object);

        Assert.Null(service.LoadLatestResult());
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

    // Guard branches: * LoadLatestResult when the file contains the literal "null" - must round-trip to null without surfacing a JsonException to callers.

    [Fact]
    public void LoadLatestResult_FileContainsLiteralNull_ReturnsNull()
    {
        // BUG GUARD: JsonSerializer.Deserialize<T>("null") returns default(T) = null. The helper must return that null without additional error handling (differs from UserActivityCacheService which logs a warning).
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, "null");

        var result = _service.LoadLatestResult();

        Assert.Null(result);
    }

    [Fact]
    public void LoadLatestResult_EmptyFile_ReturnsNull()
    {
        // BUG GUARD: zero-byte file from a crashed-mid-write scenario. The catch filter includes JsonException specifically because System.Text.Json throws it on empty input.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, string.Empty);

        var result = _service.LoadLatestResult();

        Assert.Null(result);
    }

    [Fact]
    public void LoadLatestResult_FileContainsWhitespaceOnly_ReturnsNull()
    {
        // BUG GUARD: whitespace-only file - deserializer sees "no JSON here" and throws JsonException. Same recovery path as EmptyFile but exercises a subtly different serializer branch (position > 0 vs = 0).
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, "   \n\t  ");

        var result = _service.LoadLatestResult();

        Assert.Null(result);
    }

    [Fact]
    public void SaveLatestResult_AfterCorruptFile_OverwritesCorruption()
    {
        // BUG GUARD: SaveLatestResult uses AtomicFile.WriteAllText -> temp-file + File.Move(overwrite: true). A prior corrupted file must be replaced, NOT concatenated with.
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, "{ this is not valid json");

        var fresh = new MediaStatisticsResult();
        fresh.Libraries.Add(new LibraryStatistics { VideoSize = 999 });
        _service.SaveLatestResult(fresh);

        // Must now be valid AND contain the new payload - no residue of the
        // corrupted contents (which would produce a JSON parse error on read-back).
        var loaded = _service.LoadLatestResult();
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Libraries);
        Assert.Equal(999, loaded.Libraries[0].VideoSize);
    }

    [Fact]
    public void SaveAndLoad_UnicodeStrings_RoundTripsCorrectly()
    {
        // BUG GUARD: UTF-8 no-BOM (AtomicFile default) must not corrupt multi-byte sequences.
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
        // BUG GUARD: the `Lock _fileLock` is held during both Save AND Load, so concurrent LoadLatestResult calls from multiple background tasks (statistics endpoint + scheduled task overlap) must serialise cleanly.
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

    [Fact]
    public void SaveLatestResult_WriteFails_SwallowsExceptionAndDoesNotThrow()
    {
        // A directory sitting at the exact target file name makes AtomicFile's final File.Move throw IOException.
        Directory.CreateDirectory(Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json"));

        Assert.Null(Record.Exception(() => _service.SaveLatestResult(new MediaStatisticsResult())));
    }

    [Fact]
    public void LoadLatestResult_LegacyBitrateTiers_AreMigratedToNewLabels()
    {
        // A cache written before the 7-tier bitrate rework used the old 5-tier labels. On load
        // these must be remapped so old data does not silently vanish from the UI after upgrade.
        var stats = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideoBitrateTiers["2-5 Mbps"] = 3;
        lib.VideoBitrateTierSizes["2-5 Mbps"] = 3_000;
        lib.VideoBitrateTierPaths["2-5 Mbps"] = new System.Collections.ObjectModel.Collection<string> { "/a.mkv", "/b.mkv" };
        stats.Libraries.Add(lib);
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        var loadedLib = loaded!.Libraries[0];
        Assert.False(loadedLib.VideoBitrateTiers.ContainsKey("2-5 Mbps"));
        Assert.Equal(3, loadedLib.VideoBitrateTiers["2–4 Mbps"]);
        Assert.Equal(3_000, loadedLib.VideoBitrateTierSizes["2–4 Mbps"]);
        Assert.Equal(2, loadedLib.VideoBitrateTierPaths["2–4 Mbps"].Count);
        Assert.Contains("/a.mkv", loadedLib.VideoBitrateTierPaths["2–4 Mbps"]);
    }

    [Fact]
    public void LoadLatestResult_CacheWithoutVideoBitrates_LoadsWithEmptyMap()
    {
        // Caches written before per file bitrates existed carry no VideoBitrates key.
        // Loading them must yield an empty map instead of failing, so the explorer
        // can show its rescan hint until the next scan fills the values.
        var legacyJson = "{\"LibraryName\":\"Movies\",\"VideoBitrateTiers\":{\"8–16 Mbps\":1}}";
        var lib = System.Text.Json.JsonSerializer.Deserialize<LibraryStatistics>(legacyJson);

        Assert.NotNull(lib);
        Assert.NotNull(lib!.VideoBitrates);
        Assert.Empty(lib.VideoBitrates);
    }

    [Fact]
    public void LoadLatestResult_CacheWithoutTrackLabels_LoadsWithEmptyMaps()
    {
        // Caches written before per file track labels existed carry neither key.
        // Loading them must yield empty maps instead of failing, so the detail
        // cards fall back to the plain language lists until the next scan.
        var legacyJson = "{\"LibraryName\":\"Movies\",\"AudioLanguages\":{\"German\":1}}";
        var lib = System.Text.Json.JsonSerializer.Deserialize<LibraryStatistics>(legacyJson);

        Assert.NotNull(lib);
        Assert.NotNull(lib!.AudioTrackLabels);
        Assert.Empty(lib.AudioTrackLabels);
        Assert.NotNull(lib.SubtitleTrackLabels);
        Assert.Empty(lib.SubtitleTrackLabels);
    }

    [Fact]
    public void LoadLatestResult_TwoLegacyBitrateTiers_MergeIntoSameNewTier()
    {
        // "5-10 Mbps" and legacy overlap must accumulate rather than overwrite when they land in
        // the same new bucket alongside a value already present under the new label.
        var stats = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideoBitrateTiers["10-20 Mbps"] = 2;
        lib.VideoBitrateTierSizes["10-20 Mbps"] = 2_000;
        lib.VideoBitrateTiers["8–16 Mbps"] = 5; // already-migrated data coexisting in the same cache
        lib.VideoBitrateTierSizes["8–16 Mbps"] = 5_000;
        stats.Libraries.Add(lib);
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        var loadedLib = loaded!.Libraries[0];
        Assert.False(loadedLib.VideoBitrateTiers.ContainsKey("10-20 Mbps"));
        Assert.Equal(7, loadedLib.VideoBitrateTiers["8–16 Mbps"]);
        Assert.Equal(7_000, loadedLib.VideoBitrateTierSizes["8–16 Mbps"]);
    }

    [Fact]
    public void LoadLatestResult_NoLegacyBitrateTiers_LeavesDataUntouched()
    {
        var stats = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideoBitrateTiers["4–8 Mbps"] = 9;
        stats.Libraries.Add(lib);
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        Assert.Equal(9, loaded!.Libraries[0].VideoBitrateTiers["4–8 Mbps"]);
    }

    [Fact]
    public void LoadLatestResult_LegacyBitrateTierPaths_MergeIntoExistingNewTierPaths()
    {
        // A cache can already contain paths under the new label (e.g. a mixed old/new dataset).
        // Legacy paths must be appended to that existing list, not discarded or used to replace it.
        var stats = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideoBitrateTierPaths["10-20 Mbps"] = new System.Collections.ObjectModel.Collection<string> { "/legacy.mkv" };
        lib.VideoBitrateTierPaths["8–16 Mbps"] = new System.Collections.ObjectModel.Collection<string> { "/existing.mkv" };
        stats.Libraries.Add(lib);
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        var loadedLib = loaded!.Libraries[0];
        Assert.False(loadedLib.VideoBitrateTierPaths.ContainsKey("10-20 Mbps"));
        Assert.Equal(2, loadedLib.VideoBitrateTierPaths["8–16 Mbps"].Count);
        Assert.Contains("/legacy.mkv", loadedLib.VideoBitrateTierPaths["8–16 Mbps"]);
        Assert.Contains("/existing.mkv", loadedLib.VideoBitrateTierPaths["8–16 Mbps"]);
    }

    [Fact]
    public void LoadLatestResult_LegacyWatchedBuckets_MergeIntoWatched()
    {
        // Pre-binary-Watched/Never-watched caches used "1 user"/"2-3 users"/"4+ users". These must
        // collapse into the single "Watched" bucket so upgraded servers do not lose watched counts.
        var stats = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.WatchedTiers["1 user"] = 2;
        lib.WatchedTiers["2–3 users"] = 3;
        lib.WatchedTiers["4+ users"] = 1;
        lib.WatchedTierSizes["1 user"] = 1_000;
        lib.WatchedTierPaths["1 user"] = new System.Collections.ObjectModel.Collection<string> { "/a.mkv" };
        lib.WatchedTierPaths["2–3 users"] = new System.Collections.ObjectModel.Collection<string> { "/b.mkv" };
        stats.Libraries.Add(lib);
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        var loadedLib = loaded!.Libraries[0];
        Assert.False(loadedLib.WatchedTiers.ContainsKey("1 user"));
        Assert.False(loadedLib.WatchedTiers.ContainsKey("2–3 users"));
        Assert.False(loadedLib.WatchedTiers.ContainsKey("4+ users"));
        Assert.Equal(6, loadedLib.WatchedTiers["Watched"]);
        Assert.Equal(1_000, loadedLib.WatchedTierSizes["Watched"]);
        Assert.Equal(2, loadedLib.WatchedTierPaths["Watched"].Count);
    }

    [Fact]
    public void LoadLatestResult_LegacyWatchedBucketsWithExistingWatchedEntry_Accumulates()
    {
        // A cache can already contain a "Watched" bucket (mixed old/new data) - legacy counts must
        // add to it, not overwrite it.
        var stats = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.WatchedTiers["Watched"] = 10;
        lib.WatchedTiers["1 user"] = 4;
        lib.WatchedTierPaths["Watched"] = new System.Collections.ObjectModel.Collection<string> { "/existing.mkv" };
        lib.WatchedTierPaths["1 user"] = new System.Collections.ObjectModel.Collection<string> { "/legacy.mkv" };
        stats.Libraries.Add(lib);
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        var loadedLib = loaded!.Libraries[0];
        Assert.Equal(14, loadedLib.WatchedTiers["Watched"]);
        Assert.Equal(2, loadedLib.WatchedTierPaths["Watched"].Count);
        Assert.Contains("/existing.mkv", loadedLib.WatchedTierPaths["Watched"]);
        Assert.Contains("/legacy.mkv", loadedLib.WatchedTierPaths["Watched"]);
    }

    [Fact]
    public void LoadLatestResult_NoLegacyWatchedBuckets_LeavesNeverWatchedUntouched()
    {
        var stats = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.WatchedTiers["Never watched"] = 5;
        stats.Libraries.Add(lib);
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        Assert.Equal(5, loaded!.Libraries[0].WatchedTiers["Never watched"]);
        Assert.False(loaded.Libraries[0].WatchedTiers.ContainsKey("Watched"));
    }

    [Fact]
    public void LoadLatestResult_LegacyBitrateTiers_MigratesEveryCategoryCollection()
    {
        // Category collections deserialize into separate instances, so the migration
        // must cover Movies/TvShows/etc. and not just Libraries.
        var stats = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.VideoBitrateTiers["2-5 Mbps"] = 3;
        var movies = new LibraryStatistics();
        movies.VideoBitrateTiers["> 40 Mbps"] = 2;
        stats.Libraries.Add(lib);
        stats.Movies.Add(movies);
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        Assert.False(loaded!.Libraries[0].VideoBitrateTiers.ContainsKey("2-5 Mbps"));
        Assert.Equal(3, loaded.Libraries[0].VideoBitrateTiers["2–4 Mbps"]);
        Assert.False(loaded.Movies[0].VideoBitrateTiers.ContainsKey("> 40 Mbps"));
        Assert.Equal(2, loaded.Movies[0].VideoBitrateTiers["32–60 Mbps"]);
    }

    [Fact]
    public void LoadLatestResult_LegacyWatchedBuckets_MigratesEveryCategoryCollection()
    {
        var stats = new MediaStatisticsResult();
        var lib = new LibraryStatistics();
        lib.WatchedTiers["1 user"] = 4;
        var tv = new LibraryStatistics();
        tv.WatchedTiers["2–3 users"] = 6;
        stats.Libraries.Add(lib);
        stats.TvShows.Add(tv);
        _service.SaveLatestResult(stats);

        var loaded = _service.LoadLatestResult();

        Assert.False(loaded!.Libraries[0].WatchedTiers.ContainsKey("1 user"));
        Assert.Equal(4, loaded.Libraries[0].WatchedTiers["Watched"]);
        Assert.False(loaded.TvShows[0].WatchedTiers.ContainsKey("2–3 users"));
        Assert.Equal(6, loaded.TvShows[0].WatchedTiers["Watched"]);
    }
}
